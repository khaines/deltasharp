using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using PqListField = Parquet.Schema.ListField;
using PqMapField = Parquet.Schema.MapField;
using PqStructField = Parquet.Schema.StructField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Reconstructs the three single-level nested Parquet shapes (#571) — a <b>struct of scalars</b>, an
/// <b>array of a scalar</b>, and a <b>map of scalar→scalar</b> — from the raw Dremel repetition/definition
/// levels Parquet.Net 6.0.3 exposes (<see cref="ParquetRowGroupReader.ReadRawAsync{T}"/>), into the
/// immutable nested reference vectors <see cref="StructColumnVector"/>/<see cref="ListColumnVector"/>/
/// <see cref="MapColumnVector"/> (#570). Parquet.Net 6.0.3 offers no reconstructed nested read (no
/// <c>DataColumn</c> type), so the reader assembles the container structure itself from the leaf columns'
/// packed values + definition levels + repetition levels.
/// </summary>
/// <remarks>
/// <para><b>Null-correctness at every level.</b> The reassembly distinguishes a null struct from a present
/// struct with a null field, a null list from an empty list from a list with a null element, and a null map
/// from an empty map from a map with a null value — the same distinctions Spark preserves. It reads the
/// standard 3-level LIST (<c>list/element</c>) and 3-level MAP (<c>key_value/{key,value}</c>) shapes.</para>
/// <para><b>Fail-closed parity.</b> Any nested type nested WITHIN one of the three shapes (array-of-struct,
/// struct-of-list, map-of-map, …), a physical/type disagreement, or a non-required map key surfaces a
/// deterministic <see cref="DeltaStorageException"/> (never a silent/partial/wrong read).</para>
/// <para><b>Eager-decode ceiling.</b> Each leaf's declared value count is bounded against the reader's
/// <see cref="ParquetDecodeLimits"/> BEFORE the transient value/level buffers are allocated, so a crafted
/// footer cannot drive an out-of-memory allocation (mirrors <see cref="ParquetFileReader.EnsureDecodeCeiling"/>
/// for the flat path, which additionally aggregates these leaves' declared bytes).</para>
/// </remarks>
internal static class NestedParquetColumnReader
{
    /// <summary>
    /// Structurally validates that <paramref name="fileField"/> matches the requested nested
    /// <paramref name="requestedType"/> (correct container kind, every requested leaf present and its physical
    /// type an EXACT match — no widening for nested leaves) WITHOUT reading any data page, so a schema
    /// disagreement fails before any batch is yielded (mirrors the flat path's up-front validation).
    /// </summary>
    /// <exception cref="DeltaStorageException">The shapes disagree
    /// (<see cref="StorageErrorKind.SchemaMismatch"/>) or the requested shape nests further than this
    /// increment supports (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public static void ValidateShape(Field fileField, DataType requestedType, string columnName)
    {
        switch (requestedType)
        {
            case StructType structType:
                if (fileField is not PqStructField fileStruct)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{columnName}': requested a struct but the file column is not a struct.");
                }

                foreach (StructField field in structType)
                {
                    _ = ResolveStructField(fileStruct, field, columnName);
                }

                break;
            case ArrayType arrayType:
                if (fileField is not PqListField fileList)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{columnName}': requested an array but the file column is not a list.");
                }

                _ = ExpectScalarLeaf(fileList.Item, arrayType.ElementType, $"array column '{columnName}' element");
                break;
            case MapType mapType:
                if (fileField is not PqMapField fileMap)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{columnName}': requested a map but the file column is not a map.");
                }

                EnsureRequiredMapKey(fileMap, columnName);
                _ = ExpectScalarLeaf(fileMap.Key, mapType.KeyType, $"map column '{columnName}' key");
                _ = ExpectScalarLeaf(fileMap.Value, mapType.ValueType, $"map column '{columnName}' value");
                break;
            default:
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet nested read for column '{columnName}' of type '{requestedType.SimpleString}' "
                    + "is not supported.");
        }
    }

    /// <summary>Collects every leaf <see cref="DataField"/> reachable under <paramref name="field"/> (the
    /// three nested shapes), so the reader can add each leaf's declared footprint to the eager-decode
    /// ceiling.</summary>
    public static void CollectLeafFields(Field field, List<DataField> into)
    {
        switch (field)
        {
            case DataField dataField:
                into.Add(dataField);
                break;
            case PqStructField structField:
                foreach (Field child in structField.Fields)
                {
                    CollectLeafFields(child, into);
                }

                break;
            case PqListField listField:
                CollectLeafFields(listField.Item, into);
                break;
            case PqMapField mapField:
                CollectLeafFields(mapField.Key, into);
                CollectLeafFields(mapField.Value, into);
                break;
            default:
                break;
        }
    }

    /// <summary>Reconstructs the requested nested column for one row group into an immutable nested vector.</summary>
    /// <exception cref="DeltaStorageException">The shape/type disagrees, nests further than supported, or a
    /// leaf declares a value count exceeding the eager-decode ceiling (fail closed).</exception>
    public static async ValueTask<ColumnVector> ReadAsync(
        ParquetRowGroupReader rowGroup,
        Field fileField,
        DataType requestedType,
        int rowCount,
        string columnName,
        ParquetDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        return requestedType switch
        {
            StructType structType => await ReadStructAsync(
                rowGroup, ExpectStruct(fileField, columnName), structType, rowCount, columnName, limits, cancellationToken)
                .ConfigureAwait(false),
            ArrayType arrayType => await ReadListAsync(
                rowGroup, ExpectList(fileField, columnName), arrayType, rowCount, columnName, limits, cancellationToken)
                .ConfigureAwait(false),
            MapType mapType => await ReadMapAsync(
                rowGroup, ExpectMap(fileField, columnName), mapType, rowCount, columnName, limits, cancellationToken)
                .ConfigureAwait(false),
            _ => throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for column '{columnName}' of type '{requestedType.SimpleString}' "
                + "is not supported."),
        };
    }

    // ----- struct -----

    private static async ValueTask<ColumnVector> ReadStructAsync(
        ParquetRowGroupReader rowGroup,
        PqStructField fileStruct,
        StructType requested,
        int rowCount,
        string columnName,
        ParquetDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        // A struct's own definition level: a row whose definition level is BELOW this marks a NULL struct
        // (vs a present struct with a null field, whose level sits at/above this but below the field's max).
        int structMaxDef = fileStruct.MaxDefinitionLevel;
        var children = new ColumnVector[requested.Count];
        int[]? drivingDef = null;
        for (int i = 0; i < requested.Count; i++)
        {
            StructField field = requested[i];
            DataField leaf = ResolveStructField(fileStruct, field, columnName);

            // A struct field is one value per top-level row (max repetition 0), so its leaf declares exactly
            // rowCount values with definition levels aligned to rows — the field child is built with a present
            // floor of 0 (every row yields a cell: a value, a null field, or a null belonging to a null struct).
            (MutableColumnVector child, int[]? def, _, int numValues) = await ReadScalarLeafAsync(
                rowGroup, leaf, field.DataType, presentFloor: 0, limits, cancellationToken).ConfigureAwait(false);
            if (numValues != rowCount)
            {
                throw DeltaStorageException.CorruptData(
                    $"Struct column '{columnName}' field '{field.Name}' declares {numValues} values for a "
                    + $"{rowCount}-row group (a struct field must be one value per row).");
            }

            children[i] = child;
            drivingDef ??= def;
        }

        bool[]? nulls = null;
        if (structMaxDef > 0 && drivingDef is not null)
        {
            // A nullable struct: every child path runs through the optional struct, so any field's definition
            // levels distinguish the null-struct rows (definition level < the struct's own level).
            nulls = new bool[rowCount];
            for (int r = 0; r < rowCount; r++)
            {
                nulls[r] = drivingDef[r] < structMaxDef;
            }
        }

        return new StructColumnVector(requested, children, nulls is null ? default : nulls.AsSpan());
    }

    // ----- array (3-level LIST) -----

    private static async ValueTask<ColumnVector> ReadListAsync(
        ParquetRowGroupReader rowGroup,
        PqListField fileList,
        ArrayType requested,
        int rowCount,
        string columnName,
        ParquetDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        DataField elementLeaf = ExpectScalarLeaf(fileList.Item, requested.ElementType, $"array column '{columnName}' element");
        int listMaxDef = fileList.MaxDefinitionLevel;

        // The element child collects one cell per PRESENT element slot (a real value OR a null element),
        // skipping the placeholder slots a null/empty list emits (definition level below the list's own level).
        (MutableColumnVector elements, int[]? def, int[]? rep, int numValues) = await ReadScalarLeafAsync(
            rowGroup, elementLeaf, requested.ElementType, presentFloor: listMaxDef, limits, cancellationToken)
            .ConfigureAwait(false);

        var offsets = new int[checked(rowCount + 1)];
        var nulls = new bool[rowCount];
        int total = BuildRepeatedStructure(def, rep, numValues, listMaxDef, rowCount, offsets, nulls, columnName);
        if (total != elements.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Array column '{columnName}' reassembled {total} element slot(s) but the element child has "
                + $"{elements.Length}.");
        }

        return new ListColumnVector(requested, elements, offsets.AsSpan(), nulls.AsSpan());
    }

    // ----- map (3-level MAP) -----

    private static async ValueTask<ColumnVector> ReadMapAsync(
        ParquetRowGroupReader rowGroup,
        PqMapField fileMap,
        MapType requested,
        int rowCount,
        string columnName,
        ParquetDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        EnsureRequiredMapKey(fileMap, columnName);
        DataField keyLeaf = ExpectScalarLeaf(fileMap.Key, requested.KeyType, $"map column '{columnName}' key");
        DataField valueLeaf = ExpectScalarLeaf(fileMap.Value, requested.ValueType, $"map column '{columnName}' value");
        int mapMaxDef = fileMap.MaxDefinitionLevel;

        // Keys drive the entry structure. A required key's max definition level equals the map's own level
        // (enforced above), so every referenced key slot carries a real value — keys are never null, matching
        // MapType's structural invariant.
        (MutableColumnVector keys, int[]? keyDef, int[]? keyRep, int keyNumValues) = await ReadScalarLeafAsync(
            rowGroup, keyLeaf, requested.KeyType, presentFloor: mapMaxDef, limits, cancellationToken)
            .ConfigureAwait(false);

        // The value child is parallel to the key child: driven by the SAME present floor, its own definition
        // levels distinguish a present-but-null value from a real value.
        (MutableColumnVector values, _, _, _) = await ReadScalarLeafAsync(
            rowGroup, valueLeaf, requested.ValueType, presentFloor: mapMaxDef, limits, cancellationToken)
            .ConfigureAwait(false);
        if (keys.Length != values.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' reassembled {keys.Length} key(s) but {values.Length} value(s); a "
                + "map's key and value children must be parallel.");
        }

        var offsets = new int[checked(rowCount + 1)];
        var nulls = new bool[rowCount];
        int total = BuildRepeatedStructure(keyDef, keyRep, keyNumValues, mapMaxDef, rowCount, offsets, nulls, columnName);
        if (total != keys.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' reassembled {total} entry slot(s) but the key child has {keys.Length}.");
        }

        return new MapColumnVector(requested, keys, values, offsets.AsSpan(), nulls.AsSpan());
    }

    // Reconstructs the per-row offsets + null flags for a repeated column (list/map) from its driving leaf's
    // definition + repetition levels, distinguishing null container / empty container / present container.
    // Returns the total number of PRESENT child cells (== offsets[^1]), so the caller can cross-check the
    // reassembled child length. A repetition level of 0 opens a new top-level row; a definition level at or
    // above the container's own level counts a present child cell; one below the container-minus-one level
    // (i.e., not even the empty-container placeholder) marks a NULL container.
    private static int BuildRepeatedStructure(
        int[]? def, int[]? rep, int numValues, int containerMaxDef, int rowCount, int[] offsets, bool[] nulls,
        string columnName)
    {
        // A top-level repeated column always carries both level streams (its max repetition and definition
        // levels are >= 1); their absence is a malformed footer.
        if (def is null || rep is null)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested column '{columnName}' is missing the repetition/definition levels required to "
                + "reconstruct its structure.");
        }

        int emptyContainerDef = containerMaxDef - 1;
        int row = -1;
        int elements = 0;
        offsets[0] = 0;
        for (int i = 0; i < numValues; i++)
        {
            if (rep[i] == 0)
            {
                // A new top-level row: close the previous row's offset window, then open this row (assumed
                // present until a below-empty definition level proves it null).
                if (row >= 0)
                {
                    offsets[row + 1] = elements;
                }

                row++;
                if (row >= rowCount)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' declares more rows than the row group's {rowCount}.");
                }

                nulls[row] = false;
            }

            if (row < 0)
            {
                // The first level slot must open a row (repetition level 0); a leading non-zero is corrupt.
                throw DeltaStorageException.CorruptData(
                    $"Nested column '{columnName}' begins with a non-zero repetition level (corrupt levels).");
            }

            int d = def[i];
            if (d >= containerMaxDef)
            {
                elements++;
            }
            else if (d < emptyContainerDef)
            {
                nulls[row] = true;
            }
        }

        if (row >= 0)
        {
            offsets[row + 1] = elements;
        }

        if (row + 1 != rowCount)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested column '{columnName}' reconstructed {row + 1} row(s) but the row group declares {rowCount}.");
        }

        return elements;
    }

    // ----- leaf decode (raw Dremel -> child vector) -----

    // Reads one scalar leaf's packed values + definition/repetition levels and materializes the child cells,
    // returning the child plus the raw level streams for the caller's structural pass. Dispatches the physical
    // read type T and the value->lane conversion per requested scalar type (mirroring the flat reader's
    // ReadColumnAsync, minus widening — nested leaves require an EXACT physical match).
    private static ValueTask<(MutableColumnVector Child, int[]? Def, int[]? Rep, int NumValues)> ReadScalarLeafAsync(
        ParquetRowGroupReader rowGroup,
        DataField leaf,
        DataType scalarType,
        int presentFloor,
        ParquetDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        switch (scalarType)
        {
            case BooleanType:
                return ReadLeafAsync<bool>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case ByteType:
                return ReadLeafAsync<sbyte>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(unchecked((byte)x)), cancellationToken);
            case ShortType:
                return ReadLeafAsync<short>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case IntegerType:
                return ReadLeafAsync<int>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case LongType:
                return ReadLeafAsync<long>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case FloatType:
                return ReadLeafAsync<float>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case DoubleType:
                return ReadLeafAsync<double>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case DateType:
                return ReadLeafAsync<DateTime>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochDay(x)), cancellationToken);
            case TimestampType or TimestampNtzType:
                return ReadLeafAsync<DateTime>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochMicros(x)), cancellationToken);
            case DecimalType decimalType:
                ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(decimalType);
                return ReadLeafAsync<decimal>(rowGroup, leaf, scalarType, presentFloor, limits,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, decimalType, x, factors), cancellationToken);
            case StringType:
                return ReadLeafAsync<ReadOnlyMemory<char>>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendBytes(Encoding.UTF8.GetBytes(new string(x.Span))), cancellationToken);
            case BinaryType:
                return ReadLeafAsync<ReadOnlyMemory<byte>>(rowGroup, leaf, scalarType, presentFloor, limits,
                    static (v, x) => v.AppendBytes(x.Span), cancellationToken);
            default:
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet nested read for leaf type '{scalarType.SimpleString}' is not supported.");
        }
    }

    private static async ValueTask<(MutableColumnVector Child, int[]? Def, int[]? Rep, int NumValues)> ReadLeafAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField leaf,
        DataType elementType,
        int presentFloor,
        ParquetDecodeLimits limits,
        Action<MutableColumnVector, T> append,
        CancellationToken cancellationToken)
        where T : struct
    {
        int numValues = LeafNumValues(rowGroup, leaf, limits, Unsafe.SizeOf<T>());
        var values = new T[numValues];
        int[]? def = null;
        int[]? rep = null;
        Memory<int>? defLevels = null;
        Memory<int>? repLevels = null;

        // Parquet.Net requires a null (not empty) Memory when a level stream is absent, AND requires a
        // non-null one when the field declares that level (max level > 0). Passing a null int[] would
        // implicitly become an EMPTY Memory<int> (length 0), which the library rejects — so build the
        // nullable Memory explicitly.
        if (leaf.MaxDefinitionLevel > 0)
        {
            def = new int[numValues];
            defLevels = def;
        }

        if (leaf.MaxRepetitionLevel > 0)
        {
            rep = new int[numValues];
            repLevels = rep;
        }

        try
        {
            await rowGroup.ReadRawAsync<T>(leaf, values, defLevels, repLevels, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Parquet.Net's DATE/TIMESTAMP decode throws ArgumentOutOfRangeException for a physical value
            // outside the representable DateTime range — a corrupt/hostile file, mapped to the deterministic
            // CorruptData contract (mirrors the flat reader's ReadValueAsync).
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}': a physical value is outside the representable date/time range.", ex);
        }

        var child = ColumnVectors.Create(elementType, Math.Max(numValues, 1));
        int fieldMaxDef = leaf.MaxDefinitionLevel;
        int packed = 0;
        if (def is null)
        {
            // No definition levels (a fully-required path): every declared value is present.
            for (int i = 0; i < numValues; i++)
            {
                append(child, values[packed++]);
            }
        }
        else
        {
            for (int i = 0; i < numValues; i++)
            {
                int d = def[i];
                if (d < presentFloor)
                {
                    // This level slot belongs to a null/empty parent container: it yields no child cell.
                    continue;
                }

                if (d == fieldMaxDef)
                {
                    // A defined value: consume the next packed value (values are front-filled, defined-only).
                    append(child, values[packed++]);
                }
                else
                {
                    // A present cell whose value is null (definition level between the present floor and the
                    // field's own max): a null field / null element / null map value.
                    child.AppendNull();
                }
            }
        }

        return (child, def, rep, numValues);
    }

    // The leaf's declared value count, bounded against the eager-decode ceiling BEFORE the values/level
    // buffers are allocated so a crafted NumValues cannot drive an out-of-memory allocation. Unlike the flat
    // path (one value per row, bounded by the row count), a repeated leaf can declare more values than rows,
    // so this bounds the ACTUAL transient (values + definition + repetition buffers) by the leaf's own count.
    private static int LeafNumValues(ParquetRowGroupReader rowGroup, DataField leaf, ParquetDecodeLimits limits, int elementWidth)
    {
        global::Parquet.Meta.ColumnChunk? chunk = rowGroup.GetMetadata(leaf);
        long numValues = chunk?.MetaData?.NumValues
            ?? throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' has no column-chunk metadata (a stripped/absent footer).");
        if (numValues < 0)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' declares a negative value count ({numValues}).");
        }

        long perSlotBytes = elementWidth
            + (leaf.MaxDefinitionLevel > 0 ? sizeof(int) : 0)
            + (leaf.MaxRepetitionLevel > 0 ? sizeof(int) : 0);
        if (perSlotBytes > 0 && numValues > limits.MaxRowGroupDecodedBytes / perSlotBytes)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' declares {numValues} values, whose eager decode would exceed the "
                + $"{limits.MaxRowGroupDecodedBytes}-byte ceiling.");
        }

        if (numValues > int.MaxValue)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' declares {numValues} values, exceeding Int32.MaxValue.");
        }

        return (int)numValues;
    }

    // ----- shape resolution + physical-type validation (no widening for nested leaves) -----

    private static PqStructField ExpectStruct(Field fileField, string columnName) =>
        fileField as PqStructField
        ?? throw DeltaStorageException.SchemaMismatch(
            $"Column '{columnName}': requested a struct but the file column is not a struct.");

    private static PqListField ExpectList(Field fileField, string columnName) =>
        fileField as PqListField
        ?? throw DeltaStorageException.SchemaMismatch(
            $"Column '{columnName}': requested an array but the file column is not a list.");

    private static PqMapField ExpectMap(Field fileField, string columnName) =>
        fileField as PqMapField
        ?? throw DeltaStorageException.SchemaMismatch(
            $"Column '{columnName}': requested a map but the file column is not a map.");

    private static void EnsureRequiredMapKey(PqMapField fileMap, string columnName)
    {
        // A standard MapType key is required (its max definition level equals the map's own level). A file map
        // with an OPTIONAL key could carry a null key, which MapType forbids — fail closed rather than risk it.
        if (fileMap.Key.MaxDefinitionLevel != fileMap.MaxDefinitionLevel)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Map column '{columnName}': the file map key is nullable, but MapType keys must be non-null.");
        }
    }

    private static DataField ResolveStructField(PqStructField fileStruct, StructField requested, string columnName)
    {
        Field? match = null;
        foreach (Field candidate in fileStruct.Fields)
        {
            if (string.Equals(candidate.Name, requested.Name, StringComparison.Ordinal))
            {
                match = candidate;
                break;
            }
        }

        if (match is null)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Struct column '{columnName}' is missing requested field '{requested.Name}' in the file.");
        }

        return ExpectScalarLeaf(match, requested.DataType, $"struct column '{columnName}' field '{requested.Name}'");
    }

    private static DataField ExpectScalarLeaf(Field fileField, DataType requestedScalar, string context)
    {
        if (requestedScalar is ArrayType or MapType or StructType)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for {context}: a nested type within a nested type "
                + $"('{requestedScalar.SimpleString}') is not supported.");
        }

        if (fileField is not DataField leaf)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for {context}: the file column is itself nested, which is not supported.");
        }

        ValidateLeafPhysicalType(leaf, requestedScalar, context);
        return leaf;
    }

    // An EXACT physical-type match (no type widening for nested leaves — that is #546's scope). Mirrors the
    // flat reader's ValidateFileField annotation checks (DATE vs micros TIMESTAMP, decimal precision/scale,
    // timestamp lane passthrough) but skips nullability enforcement (nested value/element/field nullability is
    // advisory per #570).
    private static void ValidateLeafPhysicalType(DataField leaf, DataType requested, string context)
    {
        bool matches = requested switch
        {
            BooleanType => leaf.ClrType == typeof(bool),
            ByteType => leaf.ClrType == typeof(sbyte),
            ShortType => leaf.ClrType == typeof(short),
            IntegerType => leaf.ClrType == typeof(int),
            LongType => leaf.ClrType == typeof(long),
            FloatType => leaf.ClrType == typeof(float),
            DoubleType => leaf.ClrType == typeof(double),
            StringType => leaf.ClrType == typeof(string),
            BinaryType => leaf.ClrType == typeof(byte[]),
            DateType => leaf is DateTimeDataField { DateTimeFormat: DateTimeFormat.Date },
            TimestampType or TimestampNtzType =>
                leaf is DateTimeDataField timestamp && timestamp.DateTimeFormat != DateTimeFormat.Date,
            DecimalType decimalType =>
                leaf is DecimalDataField decimalLeaf
                && decimalLeaf.Precision == decimalType.Precision
                && decimalLeaf.Scale == decimalType.Scale,
            _ => false,
        };

        if (!matches)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Parquet nested read for {context}: the file physical type '{leaf.ClrType.Name}' does not match "
                + $"the requested '{requested.SimpleString}'.");
        }
    }
}
