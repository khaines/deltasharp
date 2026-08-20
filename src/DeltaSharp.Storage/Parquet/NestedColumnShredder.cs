using System.Buffers;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using PqListField = Parquet.Schema.ListField;
using PqMapField = Parquet.Schema.MapField;
using PqStructField = Parquet.Schema.StructField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Shreds one single-level nested <see cref="ColumnVector"/> (a <c>struct&lt;scalars&gt;</c>, an
/// <c>array&lt;scalar&gt;</c>, or a <c>map&lt;scalar,scalar&gt;</c>) into its per-leaf Dremel encoding —
/// packed present values plus explicit definition and repetition levels — and writes each leaf through
/// Parquet.Net 6.1.0's <c>ParquetRowGroupWriter.WriteAllPartsAsync&lt;T&gt;</c>. This is the exact inverse of
/// <see cref="NestedParquetColumnReader"/> (#571/#584); the design's normative level tables (§2.3) are
/// transcribed in <see cref="ComputeStructLevels"/>/<see cref="ComputeListLevels"/>/
/// <see cref="ComputeMapLevels"/>.
/// </summary>
/// <remarks>
/// <para><b>Buffer ownership (BL-10).</b> No pooled array ever escapes: every value/level buffer is rented
/// from an <see cref="ArrayPool{T}"/> under <c>try</c>/<c>finally</c>, sliced EXACTLY
/// (<c>AsMemory(0, count)</c>) for the awaited write, and returned with <c>clearArray:true</c> only AFTER the
/// awaited <c>WriteAllPartsAsync</c> has completed.</para>
/// <para><b>Accessor rule (A1/NEW-6/B-2/B-5).</b> The child is resolved ONCE via
/// <see cref="StructColumnVector.Child(int)"/>/<see cref="ListColumnVector.Elements"/>/
/// <see cref="MapColumnVector.Keys"/>/<see cref="MapColumnVector.Values"/> — never the per-row
/// <c>ElementsAt</c>/<c>KeysAt</c>/<c>ValuesAt</c>, which allocate a vector per row. Those accessors are
/// ALREADY row/element-aligned to the vector's window, so the shredder does NOT re-rebase them; what IS
/// rebased is the raw offset arithmetic, which the vectors expose as
/// <see cref="ListColumnVector.RawElementSpan"/>/<see cref="MapColumnVector.RawEntrySpan"/> (element-base
/// relative, so it indexes the pre-sliced child view directly). Element/entry counts therefore derive from the
/// RAW offsets — never from <c>Elements.Length</c>, which over-counts on a mutable vector carrying an
/// uncommitted tail.</para>
/// <para><b>AOT (N-6).</b> Leaf writes dispatch through an explicit <c>switch</c> over the DeltaSharp
/// <see cref="DataType"/> onto CLOSED <c>WriteAllPartsAsync&lt;T&gt;</c> instantiations — never
/// <c>MakeGenericMethod</c> — so every instantiation is statically rooted for the NativeAOT gate.</para>
/// <para><b>Diagnostics (§2.8).</b> Every nested-vector interaction is wrapped so no raw
/// <see cref="NotSupportedException"/>/<see cref="InvalidOperationException"/> and no
/// <c>DataType.SimpleString</c> of a nested type escapes: each becomes a typed
/// <see cref="StorageErrorKind.UnsupportedFeature"/> carrying only the sanitized column label and a bounded
/// KIND.</para>
/// </remarks>
internal static class NestedColumnShredder
{
    /// <summary>A contiguous run of logical rows of one input batch's (already selection-resolved) column
    /// vector that the row group being written covers.</summary>
    internal readonly record struct ColumnSegment(ColumnVector Vector, int Start, int Length);

    /// <summary>The per-row-group ceiling on a single leaf's total level slots — the pathological fan-out
    /// bound (Quality N4). A row group is capped at <c>ParquetFileWriter.RowGroupRowLimit</c> logical rows, so
    /// a leaf whose slot count reaches this bound describes an implausibly wide nested fan-out and fails
    /// closed rather than driving an unbounded rent.</summary>
    internal const int MaxLeafSlotsPerRowGroup = 1 << 28;

    /// <summary>
    /// Shreds <paramref name="schemaField"/>'s nested column over <paramref name="segments"/> and writes every
    /// leaf into <paramref name="rowGroup"/>.
    /// </summary>
    /// <exception cref="DeltaStorageException">The column's shape is out of scope
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>), a REQUIRED nested leaf holds a null, or a computed
    /// level stream violates the §2.3c pre-write invariants (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    public static async Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        Field field,
        StructField schemaField,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        CancellationToken cancellationToken)
    {
        string label = DiagnosticText.Sanitize(schemaField.Name);
        switch (schemaField.DataType)
        {
            case StructType structType when field is PqStructField parquetStruct:
                await WriteStructAsync(rowGroup, parquetStruct, structType, segments, rowCount, label, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ArrayType arrayType when field is PqListField parquetList:
                await WriteListAsync(rowGroup, parquetList, arrayType, segments, rowCount, label, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case MapType mapType when field is PqMapField parquetMap:
                await WriteMapAsync(rowGroup, parquetMap, mapType, segments, rowCount, label, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                // F10: a typed default arm. Reached only if the Parquet field and the declared type disagree
                // on kind — a DeltaSharp bug, not a data condition — so it fails closed on the bounded KIND
                // rather than rendering a nested SimpleString.
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet nested write for column '{label}' of kind "
                    + $"'{DiagnosticText.DescribeType(schemaField.DataType)}': the mapped Parquet field does "
                    + "not match the declared nested shape.");
        }
    }

    // ----- struct -----

    private static async Task WriteStructAsync(
        ParquetRowGroupWriter rowGroup,
        PqStructField parquetStruct,
        StructType structType,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
    {
        int structMaxDef = parquetStruct.MaxDefinitionLevel;
        var leaves = new DataField[structType.Count];
        var defs = new int[structType.Count][];
        try
        {
            for (int i = 0; i < structType.Count; i++)
            {
                leaves[i] = ExpectLeaf(parquetStruct.Fields[i], label);
                defs[i] = ArrayPool<int>.Shared.Rent(Math.Max(rowCount, 1));
                ComputeStructLevels(
                    segments, i, structMaxDef, leaves[i].MaxDefinitionLevel, label,
                    defs[i].AsSpan(0, rowCount));
            }

            // §2.3c cross-leaf, PRE-write: every child must agree, at every row, on whether the struct is
            // null. A per-leaf guard passes a struct where child A emits def < structMaxDef at a row where
            // sibling B emits def == maxDef — the file persists silently, DeltaSharp reads an availability
            // error and Spark reads WRONG rows.
            ValidateStructNullParity(defs, structType.Count, structMaxDef, rowCount, label);

            for (int i = 0; i < structType.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteLeafAsync(
                    rowGroup, leaves[i], structType[i].DataType, defs[i].AsMemory(0, rowCount), rep: null,
                    rowCount, label, new StructValueSource(segments, i), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (int[]? buffer in defs)
            {
                if (buffer is not null)
                {
                    ArrayPool<int>.Shared.Return(buffer, clearArray: true);
                }
            }
        }
    }

    /// <summary>
    /// The NORMATIVE struct level table (design §2.3). For an OPTIONAL struct (<c>structMaxDef</c> == 1) each
    /// child leaf emits exactly one definition level per logical row and NO repetition stream:
    /// <list type="bullet">
    ///   <item><description>null struct → <c>0</c> (below <c>structMaxDef</c>, for EVERY child — including a
    ///   REQUIRED child whose own max def is <c>structMaxDef</c>);</description></item>
    ///   <item><description>present struct, null field → <c>structMaxDef</c> (impossible, and rejected, for a
    ///   REQUIRED child — §2.4a);</description></item>
    ///   <item><description>present value → the leaf's own <c>MaxDefinitionLevel</c>.</description></item>
    /// </list>
    /// </summary>
    private static void ComputeStructLevels(
        IReadOnlyList<ColumnSegment> segments,
        int ordinal,
        int structMaxDef,
        int leafMaxDef,
        string label,
        Span<int> def)
    {
        int slot = 0;
        foreach (ColumnSegment segment in segments)
        {
            StructColumnVector vector = ExpectStructVector(segment.Vector, label);
            ColumnVector child = ResolveChild(() => vector.Child(ordinal), label, "struct field");
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                if (vector.IsNull(row))
                {
                    def[slot++] = 0;
                    continue;
                }

                if (child.IsNull(row))
                {
                    // §2.4a required-lane value guard: a REQUIRED child (leafMaxDef == structMaxDef) has no
                    // level at which to express its own null, so a null here would silently become a null
                    // STRUCT on read. Fail closed BEFORE any byte is written.
                    if (leafMaxDef <= structMaxDef)
                    {
                        throw DeltaStorageException.CorruptData(
                            $"Struct column '{label}' field {ordinal} is declared non-nullable but holds a "
                            + $"null at row {row}.");
                    }

                    def[slot++] = structMaxDef;
                    continue;
                }

                def[slot++] = leafMaxDef;
            }
        }
    }

    // ----- array (3-level LIST) -----

    private static async Task WriteListAsync(
        ParquetRowGroupWriter rowGroup,
        PqListField parquetList,
        ArrayType arrayType,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
    {
        DataField leaf = ExpectLeaf(parquetList.Item, label);
        int containerMaxDef = parquetList.MaxDefinitionLevel;
        int slots = CountListSlots(segments, label);

        int[] def = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] rep = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        try
        {
            ComputeListLevels(
                segments, containerMaxDef, leaf.MaxDefinitionLevel, arrayType, label,
                def.AsSpan(0, slots), rep.AsSpan(0, slots));
            await WriteLeafAsync(
                rowGroup, leaf, arrayType.ElementType, def.AsMemory(0, slots), rep.AsMemory(0, slots),
                rowCount, label, new ListValueSource(segments), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(def, clearArray: true);
            ArrayPool<int>.Shared.Return(rep, clearArray: true);
        }
    }

    /// <summary>
    /// The NORMATIVE list level table (design §2.3). For an OPTIONAL list (<c>containerMaxDef</c> == 2,
    /// <c>emptyContainerDef</c> == 1) the element leaf emits, per logical row:
    /// <list type="bullet">
    ///   <item><description>null list → one slot, <c>def 0</c>, <c>rep 0</c>;</description></item>
    ///   <item><description>empty list → one slot, <c>def containerMaxDef - 1</c>, <c>rep 0</c>;</description></item>
    ///   <item><description>otherwise one slot per element: <c>rep</c> is <c>0</c> for the row's first element
    ///   and <c>1</c> for each subsequent one; <c>def</c> is <c>containerMaxDef</c> for a null element and the
    ///   leaf's own <c>MaxDefinitionLevel</c> for a present value (a REQUIRED element drops one level, which
    ///   makes a null element unrepresentable — §2.4a rejects it).</description></item>
    /// </list>
    /// </summary>
    private static void ComputeListLevels(
        IReadOnlyList<ColumnSegment> segments,
        int containerMaxDef,
        int leafMaxDef,
        ArrayType arrayType,
        string label,
        Span<int> def,
        Span<int> rep)
    {
        int slot = 0;
        foreach (ColumnSegment segment in segments)
        {
            ListColumnVector vector = ExpectListVector(segment.Vector, label);
            ColumnVector elements = ResolveChild(() => vector.Elements, label, "array element");
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                (int start, int length) = vector.RawElementSpan(row);
                if (vector.IsNull(row))
                {
                    // A null list occupies ONE level slot and emits no value — even when it physically
                    // retains elements (only offset monotonicity is enforced, so a masked row can carry a
                    // non-zero span). Those retained bytes never reach disk.
                    def[slot] = 0;
                    rep[slot++] = 0;
                    continue;
                }

                if (length == 0)
                {
                    def[slot] = containerMaxDef - 1;
                    rep[slot++] = 0;
                    continue;
                }

                for (int e = 0; e < length; e++)
                {
                    bool elementNull = elements.IsNull(start + e);
                    if (elementNull && leafMaxDef <= containerMaxDef)
                    {
                        // §2.4a required-lane value guard: a REQUIRED element cannot express its own null.
                        throw DeltaStorageException.CorruptData(
                            $"Array column '{label}' declares a non-nullable element but holds a null element "
                            + $"at row {row}.");
                    }

                    def[slot] = elementNull ? containerMaxDef : leafMaxDef;
                    rep[slot++] = e == 0 ? 0 : 1;
                }
            }
        }

        _ = arrayType;
    }

    // ----- map (3-level MAP) -----

    private static async Task WriteMapAsync(
        ParquetRowGroupWriter rowGroup,
        PqMapField parquetMap,
        MapType mapType,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
    {
        DataField keyLeaf = ExpectLeaf(parquetMap.Key, label);
        DataField valueLeaf = ExpectLeaf(parquetMap.Value, label);
        int mapMaxDef = parquetMap.MaxDefinitionLevel;
        int slots = CountMapSlots(segments, label);

        int[] keyDef = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] valueDef = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] keyRep = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] valueRep = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        try
        {
            ComputeMapLevels(
                segments, mapMaxDef, keyLeaf.MaxDefinitionLevel, valueLeaf.MaxDefinitionLevel, label,
                keyDef.AsSpan(0, slots), valueDef.AsSpan(0, slots), keyRep.AsSpan(0, slots),
                valueRep.AsSpan(0, slots));

            // §2.3c cross-leaf, PRE-write: a 3-level map nests key and value in ONE repeated key_value group,
            // so their repetition streams must be IDENTICAL and their definition streams must agree on every
            // null/empty/absent slot. A per-leaf guard passes a mis-paired map (keys [0,1,0] / values [0,0,1])
            // that persists silently and mis-pairs entries across rows on read.
            ValidateMapParallelLevels(
                keyDef.AsSpan(0, slots), valueDef.AsSpan(0, slots), keyRep.AsSpan(0, slots),
                valueRep.AsSpan(0, slots), mapMaxDef, label);

            await WriteLeafAsync(
                rowGroup, keyLeaf, mapType.KeyType, keyDef.AsMemory(0, slots), keyRep.AsMemory(0, slots),
                rowCount, label, new MapValueSource(segments, keys: true), cancellationToken).ConfigureAwait(false);
            await WriteLeafAsync(
                rowGroup, valueLeaf, mapType.ValueType, valueDef.AsMemory(0, slots), valueRep.AsMemory(0, slots),
                rowCount, label, new MapValueSource(segments, keys: false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(keyDef, clearArray: true);
            ArrayPool<int>.Shared.Return(valueDef, clearArray: true);
            ArrayPool<int>.Shared.Return(keyRep, clearArray: true);
            ArrayPool<int>.Shared.Return(valueRep, clearArray: true);
        }
    }

    /// <summary>
    /// The NORMATIVE map level table (design §2.3, per the reader's <c>ValidateParallelDefinition</c> R6+R7
    /// and <c>ValidateParallelRepetition</c>). For an OPTIONAL map (<c>mapMaxDef</c> == 2) the key and value
    /// leaves share ONE repetition stream and emit, per logical row:
    /// <list type="bullet">
    ///   <item><description>null map → one slot, key def <c>0</c>, value def <c>0</c>, rep <c>0</c>;</description></item>
    ///   <item><description>empty map → one slot, key def <c>1</c>, value def <c>1</c>, rep <c>0</c>;</description></item>
    ///   <item><description>otherwise one slot per entry: rep <c>0</c> for the row's first entry and <c>1</c>
    ///   thereafter; key def is always <c>mapMaxDef</c> (keys are REQUIRED and never null); value def is
    ///   <c>mapMaxDef</c> for a null value and the value leaf's own <c>MaxDefinitionLevel</c> for a present
    ///   one.</description></item>
    /// </list>
    /// Keys are non-null over the REFERENCED range only — a null key in an unreferenced tail of a sliced
    /// vector belongs to no row and must not over-reject (NEW-8/BL-13).
    /// </summary>
    private static void ComputeMapLevels(
        IReadOnlyList<ColumnSegment> segments,
        int mapMaxDef,
        int keyMaxDef,
        int valueMaxDef,
        string label,
        Span<int> keyDef,
        Span<int> valueDef,
        Span<int> keyRep,
        Span<int> valueRep)
    {
        int slot = 0;
        foreach (ColumnSegment segment in segments)
        {
            MapColumnVector vector = ExpectMapVector(segment.Vector, label);
            ColumnVector keys = ResolveChild(() => vector.Keys, label, "map key");
            ColumnVector values = ResolveChild(() => vector.Values, label, "map value");
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                (int start, int length) = vector.RawEntrySpan(row);
                if (vector.IsNull(row))
                {
                    keyDef[slot] = 0;
                    valueDef[slot] = 0;
                    keyRep[slot] = 0;
                    valueRep[slot++] = 0;
                    continue;
                }

                if (length == 0)
                {
                    keyDef[slot] = mapMaxDef - 1;
                    valueDef[slot] = mapMaxDef - 1;
                    keyRep[slot] = 0;
                    valueRep[slot++] = 0;
                    continue;
                }

                for (int e = 0; e < length; e++)
                {
                    if (keys.IsNull(start + e))
                    {
                        // §2.4a required-lane value guard, scoped to the REFERENCED entry range: MapType keys
                        // are structurally non-null and the Parquet key leaf is REQUIRED, so a null key has no
                        // level at which to be expressed.
                        throw DeltaStorageException.CorruptData(
                            $"Map column '{label}' holds a null key at row {row}; map keys must not be null.");
                    }

                    bool valueNull = values.IsNull(start + e);
                    if (valueNull && valueMaxDef <= mapMaxDef)
                    {
                        throw DeltaStorageException.CorruptData(
                            $"Map column '{label}' declares a non-nullable value but holds a null value at "
                            + $"row {row}.");
                    }

                    keyDef[slot] = keyMaxDef;
                    valueDef[slot] = valueNull ? mapMaxDef : valueMaxDef;
                    keyRep[slot] = e == 0 ? 0 : 1;
                    valueRep[slot++] = e == 0 ? 0 : 1;
                }
            }
        }
    }

    // ----- slot counting (raw offsets only — never Elements.Length, B-5) -----

    private static int CountListSlots(IReadOnlyList<ColumnSegment> segments, string label)
    {
        long slots = 0;
        foreach (ColumnSegment segment in segments)
        {
            ListColumnVector vector = ExpectListVector(segment.Vector, label);
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                (_, int length) = vector.RawElementSpan(row);
                slots = checked(slots + (vector.IsNull(row) || length == 0 ? 1 : length));
            }
        }

        return CheckSlotBound(slots, label);
    }

    private static int CountMapSlots(IReadOnlyList<ColumnSegment> segments, string label)
    {
        long slots = 0;
        foreach (ColumnSegment segment in segments)
        {
            MapColumnVector vector = ExpectMapVector(segment.Vector, label);
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                (_, int length) = vector.RawEntrySpan(row);
                slots = checked(slots + (vector.IsNull(row) || length == 0 ? 1 : length));
            }
        }

        return CheckSlotBound(slots, label);
    }

    private static int CheckSlotBound(long slots, string label)
    {
        if (slots > MaxLeafSlotsPerRowGroup)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Nested column '{label}' would emit {slots} Dremel level slot(s) in one row group, exceeding "
                + $"the supported bound of {MaxLeafSlotsPerRowGroup}.");
        }

        return (int)slots;
    }

    // ----- value sources (present cells, in slot order) -----

    // The present cells of a leaf, in the SAME order the level computation emits them. Deliberately a small
    // struct hierarchy rather than a shared structural walk: "which cells are present" is a far simpler
    // predicate than the level tables, so re-deriving it here does not duplicate the risky logic — and the
    // §2.3c `count(def == maxDef) == values.Length` clause cross-checks the two against each other before any
    // byte is written.
    private interface IValueSource
    {
        void Collect<T>(Span<T> destination, Func<ColumnVector, int, T> read, string label);
    }

    private readonly struct StructValueSource(IReadOnlyList<ColumnSegment> segments, int ordinal) : IValueSource
    {
        public void Collect<T>(Span<T> destination, Func<ColumnVector, int, T> read, string label)
        {
            int index = 0;
            foreach (ColumnSegment segment in segments)
            {
                StructColumnVector vector = ExpectStructVector(segment.Vector, label);
                int childOrdinal = ordinal;
                ColumnVector child = ResolveChild(() => vector.Child(childOrdinal), label, "struct field");
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (!vector.IsNull(row) && !child.IsNull(row))
                    {
                        destination[index++] = read(child, row);
                    }
                }
            }
        }
    }

    private readonly struct ListValueSource(IReadOnlyList<ColumnSegment> segments) : IValueSource
    {
        public void Collect<T>(Span<T> destination, Func<ColumnVector, int, T> read, string label)
        {
            int index = 0;
            foreach (ColumnSegment segment in segments)
            {
                ListColumnVector vector = ExpectListVector(segment.Vector, label);
                ColumnVector elements = ResolveChild(() => vector.Elements, label, "array element");
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (vector.IsNull(row))
                    {
                        continue;
                    }

                    (int start, int length) = vector.RawElementSpan(row);
                    for (int e = 0; e < length; e++)
                    {
                        if (!elements.IsNull(start + e))
                        {
                            destination[index++] = read(elements, start + e);
                        }
                    }
                }
            }
        }
    }

    private readonly struct MapValueSource(IReadOnlyList<ColumnSegment> segments, bool keys) : IValueSource
    {
        public void Collect<T>(Span<T> destination, Func<ColumnVector, int, T> read, string label)
        {
            int index = 0;
            foreach (ColumnSegment segment in segments)
            {
                MapColumnVector vector = ExpectMapVector(segment.Vector, label);
                MapColumnVector current = vector;
                ColumnVector child = keys
                    ? ResolveChild(() => current.Keys, label, "map key")
                    : ResolveChild(() => current.Values, label, "map value");
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (vector.IsNull(row))
                    {
                        continue;
                    }

                    (int start, int length) = vector.RawEntrySpan(row);
                    for (int e = 0; e < length; e++)
                    {
                        if (!child.IsNull(start + e))
                        {
                            destination[index++] = read(child, start + e);
                        }
                    }
                }
            }
        }
    }

    // ----- leaf write dispatch (AOT-safe: closed generic instantiations, never MakeGenericMethod) -----

    private static Task WriteLeafAsync<TSource>(
        ParquetRowGroupWriter rowGroup,
        DataField leaf,
        DataType leafType,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int rowCount,
        string label,
        TSource source,
        CancellationToken cancellationToken)
        where TSource : struct, IValueSource
    {
        int valueCount = CountAtLevel(def.Span, leaf.MaxDefinitionLevel);
        return leafType switch
        {
            BooleanType => EmitAsync<bool, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => vector.GetValue<bool>(row), cancellationToken),
            ByteType => EmitAsync<sbyte, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => unchecked((sbyte)vector.GetValue<byte>(row)), cancellationToken),
            ShortType => EmitAsync<short, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => vector.GetValue<short>(row), cancellationToken),
            IntegerType => EmitAsync<int, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => vector.GetValue<int>(row), cancellationToken),
            LongType => EmitAsync<long, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => vector.GetValue<long>(row), cancellationToken),
            FloatType => EmitAsync<float, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => vector.GetValue<float>(row), cancellationToken),
            DoubleType => EmitAsync<double, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => vector.GetValue<double>(row), cancellationToken),
            DateType => EmitAsync<DateTime, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => ParquetTypeMapping.EpochDayToDateTime(vector.GetValue<int>(row)),
                cancellationToken),
            TimestampType or TimestampNtzType => EmitAsync<DateTime, TSource>(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
                static (vector, row) => ParquetTypeMapping.EpochMicrosToDateTime(vector.GetValue<long>(row)),
                cancellationToken),
            DecimalType decimalType => EmitDecimalAsync(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source, decimalType, cancellationToken),
            StringType => EmitStringAsync(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source, cancellationToken),
            BinaryType => EmitBinaryAsync(
                rowGroup, leaf, def, rep, rowCount, valueCount, label, source, cancellationToken),
            _ => throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested write for column '{label}': leaf type "
                + $"'{DiagnosticText.DescribeType(leafType)}' is not supported."),
        };
    }

    private static async Task EmitAsync<T, TSource>(
        ParquetRowGroupWriter rowGroup,
        DataField leaf,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int rowCount,
        int valueCount,
        string label,
        TSource source,
        Func<ColumnVector, int, T> read,
        CancellationToken cancellationToken)
        where T : struct
        where TSource : struct, IValueSource
    {
        T[] values = ArrayPool<T>.Shared.Rent(Math.Max(valueCount, 1));
        try
        {
            source.Collect(values.AsSpan(0, valueCount), read, label);
            await WriteAllPartsAsync<T>(
                rowGroup, leaf, values.AsMemory(0, valueCount), def, rep, rowCount, label, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<T>.Shared.Return(values, clearArray: true);
        }
    }

    private static Task EmitDecimalAsync<TSource>(
        ParquetRowGroupWriter rowGroup,
        DataField leaf,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int rowCount,
        int valueCount,
        string label,
        TSource source,
        DecimalType decimalType,
        CancellationToken cancellationToken)
        where TSource : struct, IValueSource =>
        EmitAsync<decimal, TSource>(
            rowGroup, leaf, def, rep, rowCount, valueCount, label, source,
            (vector, row) => ParquetTypeMapping.ReadDecimal(vector, decimalType, row), cancellationToken);

    // §2.3b string lane. The managed vectors store a String as UTF-8, so the shredder TRANSCODES UTF-8→UTF-16
    // into a per-leaf pooled char[] and hands ReadOnlyMemory<char> VIEWS into it. The scratch is sized EXACTLY
    // up front (Σ present values' UTF-8 byte length — a valid upper bound, since a UTF-16 char count never
    // exceeds the UTF-8 byte count) and is NEVER grown mid-leaf: an Array.Resize/re-rent partway through would
    // strand already-handed-out views in the abandoned array (silent garbage, B-3). Both pools are returned
    // with clearArray:true — mandatory for the REFERENCE-bearing element array, which would otherwise retain
    // the char[] across rents.
    private static async Task EmitStringAsync<TSource>(
        ParquetRowGroupWriter rowGroup,
        DataField leaf,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int rowCount,
        int valueCount,
        string label,
        TSource source,
        CancellationToken cancellationToken)
        where TSource : struct, IValueSource
    {
        var spans = new BytesCollector(valueCount);
        source.Collect(spans.Destination, BytesCollector.Read, label);

        char[] scratch = ArrayPool<char>.Shared.Rent(Math.Max(spans.TotalBytes, 1));
        ReadOnlyMemory<char>[] values = ArrayPool<ReadOnlyMemory<char>>.Shared.Rent(Math.Max(valueCount, 1));
        try
        {
            int position = 0;
            for (int i = 0; i < valueCount; i++)
            {
                int written = Encoding.UTF8.GetChars(spans.Bytes[i], scratch.AsSpan(position));
                values[i] = scratch.AsMemory(position, written);
                position += written;
            }

            await WriteAllPartsAsync<ReadOnlyMemory<char>>(
                rowGroup, leaf, values.AsMemory(0, valueCount), def, rep, rowCount, label, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<char>>.Shared.Return(values, clearArray: true);
            ArrayPool<char>.Shared.Return(scratch, clearArray: true);
        }
    }

    // §2.3b binary lane — the same ownership rules as the string lane, minus the transcode.
    private static async Task EmitBinaryAsync<TSource>(
        ParquetRowGroupWriter rowGroup,
        DataField leaf,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int rowCount,
        int valueCount,
        string label,
        TSource source,
        CancellationToken cancellationToken)
        where TSource : struct, IValueSource
    {
        var spans = new BytesCollector(valueCount);
        source.Collect(spans.Destination, BytesCollector.Read, label);

        byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Max(spans.TotalBytes, 1));
        ReadOnlyMemory<byte>[] values = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(valueCount, 1));
        try
        {
            int position = 0;
            for (int i = 0; i < valueCount; i++)
            {
                byte[] payload = spans.Bytes[i];
                payload.CopyTo(scratch.AsSpan(position));
                values[i] = scratch.AsMemory(position, payload.Length);
                position += payload.Length;
            }

            await WriteAllPartsAsync<ReadOnlyMemory<byte>>(
                rowGroup, leaf, values.AsMemory(0, valueCount), def, rep, rowCount, label, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(values, clearArray: true);
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }
    }

    // Materializes the variable-width leaf's present payloads so the exactly-sized scratch above can be sized
    // BEFORE any view is handed out (the scratch must never grow mid-leaf). GetBytes returns a span into the
    // vector's own store, which cannot be retained across the collection, so each payload is copied once here.
    private sealed class BytesCollector
    {
        internal BytesCollector(int count) => Bytes = new byte[count][];

        internal byte[][] Bytes { get; }

        internal Span<byte[]> Destination => Bytes;

        internal int TotalBytes
        {
            get
            {
                int total = 0;
                foreach (byte[] value in Bytes)
                {
                    total = checked(total + value.Length);
                }

                return total;
            }
        }

        internal static Func<ColumnVector, int, byte[]> Read { get; } =
            static (vector, row) => vector.GetBytes(row).ToArray();
    }

    // The single write call site. Runs the §2.3c pre-write level-invariant guard against the SCHEMA-attached
    // DataField, then maps any raw Parquet.Net writer fault onto the typed contract (A4) so no library
    // exception escapes the nested write door.
    private static async Task WriteAllPartsAsync<T>(
        ParquetRowGroupWriter rowGroup,
        DataField leaf,
        ReadOnlyMemory<T> values,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
        where T : struct
    {
        NestedLevelGuard.Validate(
            leaf, def.Span, rep.HasValue ? rep.Value.Span : ReadOnlySpan<int>.Empty, rep.HasValue,
            values.Length, rowCount, label);
        try
        {
            await rowGroup.WriteAllPartsAsync(leaf, values, def, rep, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DeltaStorageException and not OperationCanceledException)
        {
            // A4: Parquet.Net raises raw exceptions (e.g. its own cross-leaf row-count InvalidOperationException)
            // from the write primitive. Map them onto the deterministic CorruptData contract with a bounded
            // message — no library text, no field path.
            throw DeltaStorageException.CorruptData(
                $"Nested column '{label}': the Parquet writer rejected the encoded leaf.", ex);
        }
    }

    // ----- cross-leaf guards (§2.3c) -----

    private static void ValidateStructNullParity(
        int[]?[] defs, int childCount, int structMaxDef, int rowCount, string label)
    {
        if (structMaxDef <= 0 || childCount == 0)
        {
            return;
        }

        for (int row = 0; row < rowCount; row++)
        {
            bool structNull = defs[0]![row] < structMaxDef;
            for (int i = 1; i < childCount; i++)
            {
                if ((defs[i]![row] < structMaxDef) != structNull)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Struct column '{label}': the shredded child leaves disagree on the struct's presence "
                        + $"at row {row}; all children of an optional struct must agree on whether it is null.");
                }
            }
        }
    }

    private static void ValidateMapParallelLevels(
        ReadOnlySpan<int> keyDef,
        ReadOnlySpan<int> valueDef,
        ReadOnlySpan<int> keyRep,
        ReadOnlySpan<int> valueRep,
        int mapMaxDef,
        string label)
    {
        if (keyDef.Length != valueDef.Length || keyRep.Length != valueRep.Length
            || keyDef.Length != keyRep.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{label}': the shredded key and value level streams have different lengths; a "
                + "well-formed map shares one repeated key_value group.");
        }

        for (int i = 0; i < keyDef.Length; i++)
        {
            if (keyRep[i] != valueRep[i])
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{label}': the shredded key and value repetition levels diverge at slot {i}; "
                    + "the value stream would mis-pair entries across rows.");
            }

            bool keyEntry = keyDef[i] >= mapMaxDef;
            bool valueEntry = valueDef[i] >= mapMaxDef;
            if (keyEntry != valueEntry)
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{label}': the shredded key and value definition levels disagree on entry "
                    + $"presence at slot {i}.");
            }

            if (!keyEntry && keyDef[i] != valueDef[i])
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{label}': the shredded key and value definition levels disagree on container "
                    + $"state (null map vs empty map) at slot {i}.");
            }
        }
    }

    private static int CountAtLevel(ReadOnlySpan<int> levels, int level)
    {
        int count = 0;
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] == level)
            {
                count++;
            }
        }

        return count;
    }

    // ----- typed vector/field accessors (§2.8 diagnostic hygiene) -----

    private static DataField ExpectLeaf(Field field, string label) =>
        field as DataField
        ?? throw DeltaStorageException.UnsupportedFeature(
            $"Parquet nested write for column '{label}': a nested type within a nested type is not supported "
            + "(deferred, #585).");

    private static StructColumnVector ExpectStructVector(ColumnVector vector, string label) =>
        vector as StructColumnVector ?? throw MismatchedVector(vector, label, "struct");

    private static ListColumnVector ExpectListVector(ColumnVector vector, string label) =>
        vector as ListColumnVector ?? throw MismatchedVector(vector, label, "array");

    private static MapColumnVector ExpectMapVector(ColumnVector vector, string label) =>
        vector as MapColumnVector ?? throw MismatchedVector(vector, label, "map");

    // F10: a foreign nested vector implementation (an Arrow-imported one, say) exposes none of the structural
    // accessors the shredder needs, so it fails closed on the bounded KIND — never on a raw type name or a
    // nested SimpleString.
    private static DeltaStorageException MismatchedVector(ColumnVector vector, string label, string kind) =>
        DeltaStorageException.UnsupportedFeature(
            $"Parquet nested write for column '{label}': the column is declared '{kind}' but its vector is "
            + $"not a DeltaSharp managed {kind} vector (kind '{DiagnosticText.DescribeType(vector.Type)}').");

    // Every nested-vector structural interaction goes through here so a raw NotSupportedException /
    // InvalidOperationException (a sealed/selected/unsupported vector state) becomes a typed, bounded
    // UnsupportedFeature instead of escaping the write door unclassified (§2.8).
    private static ColumnVector ResolveChild(Func<ColumnVector> accessor, string label, string context)
    {
        try
        {
            return accessor();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested write for column '{label}': the {context} child could not be resolved from "
                + "the nested vector.");
        }
    }
}
