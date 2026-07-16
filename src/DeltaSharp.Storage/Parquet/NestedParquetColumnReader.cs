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
/// <para><b>Structural def/rep validation (the complete enforced invariant set).</b> Because Parquet.Net
/// exposes only the raw Dremel levels, the reader treats them as UNTRUSTED and validates every structural
/// invariant a well-formed stream must satisfy, failing closed with <see cref="StorageErrorKind.CorruptData"/>
/// so no crafted def/rep stream can silently mis-decode (produce wrong-but-plausible values) for the three
/// shapes. The enforced set:
/// <list type="bullet">
///   <item><description><b>Every leaf.</b> Each reconstructed definition level lies in <c>[0, leaf max def]</c>
///   and each repetition level in <c>[0, leaf max rep]</c> (<see cref="ValidateLevelRange"/>, covering BOTH
///   streams); the declared value count is ceiling-bounded and non-negative; the def and rep arrays are
///   allocated to the leaf's own value count, so they are equal-length and value-count-aligned by
///   construction.</description></item>
///   <item><description><b>List.</b> Both level streams are present; the first slot opens a row (repetition
///   0); a row opened as an empty or null list admits no continuation, and every continuation (repetition
///   &gt; 0) slot is a genuine element occurrence (state-transition legality); the reconstructed row count
///   equals the row group's; the reassembled element count equals the element child's length; and the
///   element-slot count is at least the row count (defense in depth).</description></item>
///   <item><description><b>Struct.</b> Every field declares exactly one value per row (so all fields share
///   the row count and therefore one another's length); and all fields agree, per row, on whether the struct
///   is null (cross-field definition parity — a field claiming "present" under a null-struct row is
///   rejected).</description></item>
///   <item><description><b>Map.</b> The key is required/non-null; the key and value leaves share an IDENTICAL
///   repetition stream (<see cref="ValidateParallelRepetition"/>) AND agree slot-by-slot on entry presence
///   (<see cref="ValidateParallelDefinition"/> — the value's def-entry level matches the key's), so the value
///   child pairs positionally with the key-driven entries; the shared entry structure obeys the list
///   state-transition rules (the key stream drives <see cref="BuildRepeatedStructure"/>); the key and value
///   child lengths match; the reassembled entry count equals the key child length; and the entry-slot count
///   is at least the row count.</description></item>
/// </list>
/// With this set the three shapes are fully validated against structurally-invalid def/rep streams: the
/// value/element/entry reconstruction is a pure positional consequence of levels that are all range-checked,
/// length-aligned, cross-leaf-consistent (map), cross-field-consistent (struct), and state-transition-legal
/// (list/map) — leaving no residual class that decodes to silent wrong data.</para>
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

                if (structType.Count == 0)
                {
                    // Defensive parity with EnsureReadSupported: a zero-field struct has no leaf to drive the
                    // row count and would reconstruct a length-0 vector — fail closed on the contract.
                    throw DeltaStorageException.UnsupportedFeature(
                        $"Parquet nested read for struct column '{columnName}': a zero-field struct is not supported.");
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
        int[]?[] fieldDefs = new int[requested.Count][]; // each field's definition-level stream (null if required)
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
            fieldDefs[i] = def;
        }

        bool[]? nulls = BuildStructNullMask(fieldDefs, structMaxDef, rowCount, columnName);
        return new StructColumnVector(requested, children, nulls is null ? default : nulls.AsSpan());
    }

    // Builds an optional struct's per-row null mask from its fields' definition-level streams, validating that
    // every field AGREES on the struct's presence at each row (F2). A well-formed optional struct emits, for
    // each field, a definition level below the struct's own level IFF the struct is absent — so a crafted
    // stream where one field says "null struct" (def < structMaxDef) while another says "present" at the SAME
    // row would otherwise decode a PHANTOM field value under a null struct. Returns null when the struct is
    // required (no null mask) or carries no definition streams. Internal so a direct unit test can pin the
    // cross-field parity guard with crafted field-def streams that the released Parquet.Net write door (which
    // derives definition levels from value nullability, never below the field's own null level) cannot author.
    internal static bool[]? BuildStructNullMask(
        int[]?[] fieldDefs, int structMaxDef, int rowCount, string columnName)
    {
        if (structMaxDef <= 0)
        {
            // A required struct: no null mask (every row is present).
            return null;
        }

        // A nullable struct: every field child runs through the optional struct, so a field's definition level
        // below the struct's own level marks a NULL-struct row. Drive the null mask from any field that
        // carries a definition stream.
        int[]? drivingDef = null;
        foreach (int[]? d in fieldDefs)
        {
            if (d is not null)
            {
                drivingDef = d;
                break;
            }
        }

        if (drivingDef is null)
        {
            return null;
        }

        var nulls = new bool[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            bool structNull = drivingDef[r] < structMaxDef;

            // F2 (crafted-Dremel): validate the cross-field parity and fail closed rather than trust a single
            // driving field — every field must agree with the struct's null-ness at this row.
            for (int f = 0; f < fieldDefs.Length; f++)
            {
                int[]? fieldDef = fieldDefs[f];
                if (fieldDef is null)
                {
                    // A field inside a nullable struct always carries a definition stream (its max def >=
                    // structMaxDef >= 1); a null stream would need a max def of 0, impossible under an optional
                    // parent — so there is nothing to cross-check.
                    continue;
                }

                if ((fieldDef[r] < structMaxDef) != structNull)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Struct column '{columnName}' fields disagree on the struct's presence at row "
                        + $"{r} (a corrupt/crafted definition stream): all fields of an optional struct "
                        + "must agree on whether the struct is null.");
                }
            }

            nulls[r] = structNull;
        }

        return nulls;
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

        // A1 (defense in depth): every top-level row emits at least one element-level slot (a real element, or
        // a placeholder for a null/empty list), so the element leaf's declared value count is >= the row count.
        // A smaller count cannot describe rowCount rows — reject BEFORE allocating the rowCount-scaled
        // offsets/nulls (this also transitively bounds that allocation, since numValues is ceiling-bounded).
        if (numValues < rowCount)
        {
            throw DeltaStorageException.CorruptData(
                $"Array column '{columnName}' declares {numValues} element slot(s) for a {rowCount}-row group, "
                + "but a repeated column emits at least one level slot per row.");
        }

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
        // levels distinguish a present-but-null value from a real value. Capture the value repetition AND
        // definition streams (F1 rep, R6 def): a well-formed 3-level map nests the key and value in the SAME
        // repeated key_value group, so their repetition levels are identical AND they agree, slot-by-slot, on
        // whether an entry is present.
        (MutableColumnVector values, int[]? valueDef, int[]? valueRep, _) = await ReadScalarLeafAsync(
            rowGroup, valueLeaf, requested.ValueType, presentFloor: mapMaxDef, limits, cancellationToken)
            .ConfigureAwait(false);

        // F1: the value child is consumed positionally against the KEY-driven entry structure (offsets/nulls
        // below come from the key leaf alone). If a crafted/corrupt file gave the value a divergent per-entry
        // distribution — a different repetition stream at the SAME total count — the positional pairing would
        // silently mis-assign values across rows/keys (a WRONG, not merely failed, read). Reject any rep
        // divergence BEFORE reconstructing.
        ValidateParallelRepetition(keyRep, valueRep, columnName);

        // R6: the value's DEFINITION levels are the second half of the cross-leaf contract. The value child is
        // front-filled from the slots where valueDef >= mapMaxDef and paired positionally against the entries
        // the KEY structure marks present (keyDef >= mapMaxDef). A crafted stream where key and value disagree
        // on entry presence at a slot — passing rep-parity and level-range — would mis-pair values across the
        // map. Validate ENTRY-PRESENCE parity (not raw def equality: a present value legitimately has a HIGHER
        // def than the required key, distinguishing null vs non-null above the map's own level). Only REP is
        // shared identically; the value's own def still distinguishes null values during its leaf reconstruction.
        ValidateParallelDefinition(keyDef, valueDef, mapMaxDef, columnName);

        if (keys.Length != values.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' reassembled {keys.Length} key(s) but {values.Length} value(s); a "
                + "map's key and value children must be parallel.");
        }

        // A1 (defense in depth): the key leaf drives the entry structure and emits at least one level slot per
        // row (a placeholder for a null/empty map), so its declared value count is >= the row count. Reject a
        // smaller count BEFORE allocating the rowCount-scaled offsets/nulls (bounding that allocation, since
        // keyNumValues is ceiling-bounded).
        if (keyNumValues < rowCount)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' declares {keyNumValues} entry slot(s) for a {rowCount}-row group, "
                + "but a repeated column emits at least one level slot per row.");
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
    // (i.e., not even the empty-container placeholder) marks a NULL container. Internal so a direct unit test
    // can pin the F2 state-transition guard with crafted def/rep streams that the released Parquet.Net write
    // door (which derives definition levels from value nullability, never below the element's own null level)
    // cannot author.
    internal static int BuildRepeatedStructure(
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
        bool rowComplete = false; // F2: the current row opened as an empty/null container -> no continuation
        offsets[0] = 0;
        for (int i = 0; i < numValues; i++)
        {
            int d = def[i];
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

                // F2: an empty or null container occupies a SINGLE level slot with no element occurrence
                // (definition level below the container's own level), so it admits no continuation.
                rowComplete = d < containerMaxDef;
            }
            else
            {
                // A continuation slot (repetition level > 0) of the current row.
                if (row < 0)
                {
                    // The first level slot must open a row (repetition level 0); a leading non-zero is corrupt.
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' begins with a non-zero repetition level (corrupt levels).");
                }

                // F2 (crafted-Dremel): a continuation is legal only when the row is an active element-bearing
                // container (its opening slot was an element) AND this slot is itself an element occurrence
                // (definition level at/above the container's own level). Continuing a row whose container was
                // empty/null — e.g. def=[1,2]/rep=[0,1], an empty-list marker then a continuation — or a
                // placeholder masquerading as a continuation would otherwise reconstruct a PHANTOM element
                // into an empty/null container. Fail closed rather than silently decode wrong-but-plausible data.
                if (rowComplete || d < containerMaxDef)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' continues row {row} (repetition level {rep[i]}) after an "
                        + $"empty/null-container marker (definition level {d}); an empty or null repeated "
                        + "container has no continuation.");
                }
            }

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
                    static (v, x) =>
                    {
                        // Encode straight from the source chars into a single right-sized buffer — no
                        // intermediate string allocation per element (balanced F2). The 2-arg span overload
                        // is the only ReadOnlySpan<char> form the framework exposes (there is no byte[]-
                        // returning single-arg overload), and AppendBytes copies into the vector's own store.
                        ReadOnlySpan<char> chars = x.Span;
                        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(chars)];
                        Encoding.UTF8.GetBytes(chars, bytes);
                        v.AppendBytes(bytes);
                    }, cancellationToken);
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
        int numValues = LeafNumValues(
            rowGroup, leaf, limits, Unsafe.SizeOf<T>(), variableWidth: elementType is StringType or BinaryType);
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
            // CorruptData contract (mirrors the flat reader's ReadValueAsync). Named by the requested leaf type
            // so the message is accurate for whichever leaf raised it (not hard-coded to date/time).
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' of type '{elementType.SimpleString}' has a physical value outside "
                + "its representable range.", ex);
        }

        // A5: reject any reconstructed Dremel level outside its declared range BEFORE interpreting it. A
        // crafted level would otherwise be silently coerced — a definition level above the field max reads as a
        // spurious present-null, a repetition level above the max mis-nests a row — a WRONG (not merely failed)
        // read. The value/structure passes below can then trust the levels.
        ValidateLevelRange(def, leaf.MaxDefinitionLevel, leaf.Path.ToString(), "definition");
        ValidateLevelRange(rep, leaf.MaxRepetitionLevel, leaf.Path.ToString(), "repetition");

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

    // Validates that every reconstructed Dremel level in <paramref name="levels"/> falls in the closed range
    // [0, <paramref name="maxLevel"/>] declared by the leaf's schema — a level outside it is a corrupt or
    // hostile page, failed closed rather than silently coerced (A5). Internal so the guard can be pinned by a
    // direct unit test (an out-of-range level cannot be produced by any conforming Parquet writer, so this is
    // otherwise unreachable through the public read door). The unsigned compare rejects negatives too.
    internal static void ValidateLevelRange(int[]? levels, int maxLevel, string leafPath, string kind)
    {
        if (levels is null)
        {
            return;
        }

        for (int i = 0; i < levels.Length; i++)
        {
            if ((uint)levels[i] > (uint)maxLevel)
            {
                throw DeltaStorageException.CorruptData(
                    $"Nested leaf '{leafPath}' has a {kind} level {levels[i]} outside the valid range "
                    + $"[0, {maxLevel}].");
            }
        }
    }

    // Validates that a map's key and value leaves share an IDENTICAL repetition-level stream — the structural
    // contract of a well-formed 3-level Parquet map, whose key and value live in the SAME repeated key_value
    // group and therefore repeat in lockstep. The reader consumes the value child positionally against the
    // key-driven entry structure, so a value stream with a different per-entry distribution (even at the same
    // total count) would silently mis-pair values across rows/keys — fail closed instead (F1). Only REPETITION
    // is compared: definition levels legitimately differ (an optional value may be null where the required key
    // is present). Internal so the guard can be pinned by a direct unit test as well as through the read door.
    internal static void ValidateParallelRepetition(int[]? keyRep, int[]? valueRep, string columnName)
    {
        int keyLen = keyRep?.Length ?? 0;
        int valueLen = valueRep?.Length ?? 0;
        if (keyLen != valueLen)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' key and value leaves carry {keyLen} and {valueLen} repetition "
                + "level(s); a well-formed map shares one repeated group, so they must be equal.");
        }

        if (keyRep is null || valueRep is null)
        {
            // Both null (a degenerate non-repeated map, impossible for a real MapField whose leaves have a
            // max repetition level >= 1) — vacuously parallel.
            return;
        }

        for (int i = 0; i < keyLen; i++)
        {
            if (keyRep[i] != valueRep[i])
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{columnName}' key and value repetition levels diverge at slot {i} "
                    + $"({keyRep[i]} vs {valueRep[i]}); the value stream would mis-pair entries across rows.");
            }
        }
    }

    // Validates that a map's key and value leaves AGREE, slot-by-slot, on whether each key_value slot is a
    // PRESENT entry — the DEFINITION-level analog of ValidateParallelRepetition (R6). A well-formed 3-level map
    // emits, for every slot, a key and value definition level that both sit at/above the map's own level (a
    // present entry) or both below it (an empty-map / null-map placeholder). The reader front-fills the value
    // child from the slots where valueDef >= mapMaxDef and pairs it positionally against the KEY-driven entry
    // structure, so a crafted stream where key and value disagree on entry presence (e.g. keyDef=[2,1] /
    // valueDef=[1,2]) — which passes rep-parity and level-range — would silently mis-pair values across the
    // map. Compare ENTRY-PRESENCE (>= mapMaxDef), NOT raw equality: a present value legitimately has a HIGHER
    // def than the required key (a nullable value distinguishes null vs non-null above the map's own level).
    // Fail closed on any length or presence disagreement, BEFORE reconstruction. Internal so the guard can be
    // pinned by a direct unit test (the released Parquet.Net write door derives definition levels from value
    // nullability, so a key/value entry-presence divergence is not authorable end-to-end).
    internal static void ValidateParallelDefinition(int[]? keyDef, int[]? valueDef, int mapMaxDef, string columnName)
    {
        int keyLen = keyDef?.Length ?? 0;
        int valueLen = valueDef?.Length ?? 0;
        if (keyLen != valueLen)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' key and value leaves carry {keyLen} and {valueLen} definition "
                + "level(s); a well-formed map shares one key_value group, so they must be equal.");
        }

        if (keyDef is null || valueDef is null)
        {
            // Both null (a degenerate non-optional map, impossible for a real MapField whose leaves have a max
            // definition level >= the map's own level >= 1) — vacuously parallel.
            return;
        }

        for (int i = 0; i < keyLen; i++)
        {
            if ((keyDef[i] >= mapMaxDef) != (valueDef[i] >= mapMaxDef))
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{columnName}' key and value definition levels disagree on entry presence at "
                    + $"slot {i} (key {keyDef[i]}, value {valueDef[i]}, map level {mapMaxDef}); the value stream "
                    + "would mis-pair entries across the map.");
            }
        }
    }
    // buffers are allocated so a crafted NumValues cannot drive an out-of-memory allocation. Unlike the flat
    // path (one value per row, bounded by the row count), a repeated leaf can declare more values than rows,
    // so this bounds the ACTUAL transient (values + definition + repetition buffers) by the leaf's own count.
    private static int LeafNumValues(
        ParquetRowGroupReader rowGroup, DataField leaf, ParquetDecodeLimits limits, int elementWidth, bool variableWidth)
    {
        global::Parquet.Meta.ColumnMetaData meta = rowGroup.GetMetadata(leaf)?.MetaData
            ?? throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' has no column-chunk metadata (a stripped/absent footer).");
        long numValues = meta.NumValues;
        if (numValues < 0)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{leaf.Path}' declares a negative value count ({numValues}).");
        }

        long perSlotBytes = elementWidth
            + (leaf.MaxDefinitionLevel > 0 ? sizeof(int) : 0)
            + (leaf.MaxRepetitionLevel > 0 ? sizeof(int) : 0)
            // F2: fold in the reconstructed #570 child ColumnVector each leaf materializes AFTER the raw
            // decode — it holds up to numValues values (elementWidth each) plus a per-value null-mask slot.
            // Without this term a leaf whose RAW decode fits the ceiling could still overshoot it by
            // ~elementWidth per value during reconstruction, so the ceiling would not bound the true peak.
            // Charge a full null-mask byte per value (>= the bitmap's actual per-value bit) to never
            // under-count; the default 4 GiB ceiling stays harmless for real row groups.
            + elementWidth + 1;

        // R5-F1: for a VARIABLE-width leaf (string/binary) elementWidth is only the child's per-value HANDLE
        // (offset/length) — the reconstructed child ALSO copies the decoded UTF-8/byte payload into a byte
        // store that grows by DOUBLING (ManagedVariableWidthColumnVector: newCapacity = max(required,
        // _data.Length * 2)), so its peak is up to 2x the copied payload. TotalUncompressedSize upper-bounds
        // that payload (it also carries per-value length prefixes + level/page-header overhead), so 2x it
        // conservatively bounds the byte-store peak. Fixed-width leaves budget nothing here (their value
        // already fits in elementWidth). (Residual, shared with the flat reader and pre-existing: a
        // dictionary-encoded column whose values REPEAT can materialize more child bytes than its
        // TotalUncompressedSize; a general per-value payload bound needs page-level decode, out of scope here.)
        long payloadBytes = 0;
        if (variableWidth)
        {
            long uncompressed = Math.Max(meta.TotalUncompressedSize, 0);
            // Saturate the doubling so a hostile footer's enormous TotalUncompressedSize cannot overflow the
            // 64-bit budget (it breaches the ceiling either way, via the negative-remaining branch below).
            payloadBytes = uncompressed > long.MaxValue / 2 ? long.MaxValue : 2 * uncompressed;
        }

        // The eager transient is (numValues * perSlotBytes) + payloadBytes. Check overflow-safely: reject if
        // the payload budget alone breaches the ceiling (remaining < 0), else if the per-slot transient
        // breaches the REMAINING budget.
        long remaining = limits.MaxRowGroupDecodedBytes - payloadBytes;
        if (remaining < 0 || (perSlotBytes > 0 && numValues > remaining / perSlotBytes))
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
        // Defensive parity, guaranteed unreachable through the public read door: Parquet.Net's MapField
        // constructor itself throws ("map's key cannot be nullable"), so a MapField loaded from any file
        // always has a required key (its max definition level equals the map's own level). The guard is kept
        // as an explicit local invariant so a future decode path — or a library change — that produced a
        // nullable key would still fail closed rather than risk a null MapType key.
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
