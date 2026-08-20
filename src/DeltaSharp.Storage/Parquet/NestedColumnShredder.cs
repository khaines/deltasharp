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
    public static Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        Field field,
        StructField schemaField,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rowGroup);
        return ShredAsync(rowGroup, field, schemaField, segments, rowCount, cancellationToken);
    }

    /// <summary>
    /// Runs the ENTIRE shredding pipeline for <paramref name="schemaField"/> — level computation, the §2.4a
    /// required-lane value guards, the §2.3c per-leaf <see cref="NestedLevelGuard"/> and both cross-leaf
    /// guards — WITHOUT writing anything. This is the design's §2.9 N9 pre-pass: <c>ParquetWriter.CreateAsync</c>
    /// emits the <c>PAR1</c> magic and a stream position the moment it is called, so a guard that fires after
    /// it has already published bytes. Running the identical computation first makes every nested reject
    /// fail closed BEFORE the first byte.
    /// </summary>
    public static Task ValidateColumnAsync(
        Field field,
        StructField schemaField,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        CancellationToken cancellationToken) =>
        ShredAsync(rowGroup: null, field, schemaField, segments, rowCount, cancellationToken);

    private static async Task ShredAsync(
        ParquetRowGroupWriter? rowGroup,
        Field field,
        StructField schemaField,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(schemaField);
        ArgumentNullException.ThrowIfNull(segments);
        string label = DiagnosticText.Sanitize(schemaField.Name);
        try
        {
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
        catch (Exception ex) when (IsForeignVectorFault(ex))
        {
            // §2.8 (Architect): EVERY nested-ColumnVector interaction on the write path — IsNull,
            // RawElementSpan/RawEntrySpan, GetValue<T>, GetBytes, Child(ordinal) — is covered by this single
            // boundary, not just the child resolution. A foreign or inconsistent vector implementation
            // therefore leaves the write door as a typed, bounded UnsupportedFeature rather than as a raw
            // library exception carrying an unbounded message.
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested write for column '{label}' of kind "
                + $"'{DiagnosticText.DescribeType(schemaField.DataType)}': the column vector could not be "
                + "shredded into its Parquet leaves.");
        }
    }

    private static bool IsForeignVectorFault(Exception ex) =>
        ex is NotSupportedException or InvalidOperationException or IndexOutOfRangeException
            or ArgumentException or NullReferenceException
        && ex is not DeltaStorageException;

    // ----- struct -----

    private static async Task WriteStructAsync(
        ParquetRowGroupWriter? rowGroup,
        PqStructField parquetStruct,
        StructType structType,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
    {
        int childCount = structType.Count;
        var leaves = new DataField[childCount];
        var defs = new int[childCount][];

        // N4-a slot provenance (B2): the number of level slots a struct lane emits is derived HERE from the
        // segments the row group actually covers, never from `rowCount`. Comparing a `rowCount`-derived slice
        // length against `rowCount` is the tautology the council found; comparing this independently walked
        // total against it is a real invariant that a dropped/duplicated segment breaks.
        int slots = TotalSegmentRows(segments, label);
        int structMaxDef = -1;
        try
        {
            for (int i = 0; i < childCount; i++)
            {
                DataField leaf = ExpectLeaf(parquetStruct.Fields[i], label);
                leaves[i] = leaf;
                EnsureLeafRepetition(leaf, structType[i].Nullable, label, $"field {i}");

                // §2.3c N4-c / S4: the container level comes from ONE shared helper off the schema-attached
                // leaf, so the encoder below and NestedLevelGuard can never disagree about where the
                // container/leaf boundary sits.
                int childContainerMaxDef = NestedLevelGuard.ContainerMaxDefinitionLevel(leaf);
                if (i == 0)
                {
                    structMaxDef = childContainerMaxDef;
                }
                else if (childContainerMaxDef != structMaxDef)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Struct column '{label}': child leaf {i} reports container definition level "
                        + $"{childContainerMaxDef} but child leaf 0 reports {structMaxDef}; every child of one "
                        + "struct shares the struct's definition level.");
                }

                defs[i] = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
                int emitted = ComputeStructLevels(
                    segments, i, structMaxDef, leaf.MaxDefinitionLevel, label, defs[i].AsSpan(0, slots));
                if (emitted != rowCount)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Struct column '{label}' field {i}: the shredder emitted {emitted} definition level "
                        + $"slot(s) for {rowCount} logical row(s); an unrepeated leaf emits exactly one slot "
                        + "per row.");
                }
            }

            // §2.3c cross-leaf, PRE-write: every child must agree, at every row, on whether the struct is
            // null. A per-leaf guard passes a struct where child A emits def < structMaxDef at a row where
            // sibling B emits def == maxDef — the file persists silently, DeltaSharp reads an availability
            // error and Spark reads WRONG rows.
            ValidateStructNullParity(defs, childCount, structMaxDef, rowCount, label);

            for (int i = 0; i < childCount; i++)
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
    /// Returns the number of slots it emitted, so the caller can check that count against the row count
    /// INDEPENDENTLY of the buffer it was handed.
    /// </summary>
    private static int ComputeStructLevels(
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
                if (slot >= def.Length)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Struct column '{label}' field {ordinal}: the segments describe more rows than the "
                        + "level buffer was sized for.");
                }

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

        return slot;
    }

    // ----- array (3-level LIST) -----

    private static async Task WriteListAsync(
        ParquetRowGroupWriter? rowGroup,
        PqListField parquetList,
        ArrayType arrayType,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
    {
        DataField leaf = ExpectLeaf(parquetList.Item, label);
        EnsureLeafRepetition(leaf, arrayType.ContainsNull, label, "element");

        // S4/§2.3c N4-c: the container level is derived from the SCHEMA-ATTACHED leaf through the guard's own
        // helper, so encoder and guard share one source of truth.
        int containerMaxDef = NestedLevelGuard.ContainerMaxDefinitionLevel(leaf);
        int slots = CountListSlots(segments, label);

        int[] def = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] rep = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        try
        {
            int emitted = ComputeListLevels(
                segments, containerMaxDef, leaf.MaxDefinitionLevel, label,
                def.AsSpan(0, slots), rep.AsSpan(0, slots));
            if (emitted != slots)
            {
                throw DeltaStorageException.CorruptData(
                    $"Array column '{label}': the shredder emitted {emitted} level slot(s) but the raw offsets "
                    + $"describe {slots}.");
            }
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
    private static int ComputeListLevels(
        IReadOnlyList<ColumnSegment> segments,
        int containerMaxDef,
        int leafMaxDef,
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

        return slot;
    }

    // ----- map (3-level MAP) -----

    private static async Task WriteMapAsync(
        ParquetRowGroupWriter? rowGroup,
        PqMapField parquetMap,
        MapType mapType,
        IReadOnlyList<ColumnSegment> segments,
        int rowCount,
        string label,
        CancellationToken cancellationToken)
    {
        DataField keyLeaf = ExpectLeaf(parquetMap.Key, label);
        DataField valueLeaf = ExpectLeaf(parquetMap.Value, label);
        EnsureLeafRepetition(keyLeaf, declaredNullable: false, label, "key");
        EnsureLeafRepetition(valueLeaf, mapType.ValueContainsNull, label, "value");

        // S4/§2.3c N4-c: one shared derivation, cross-checked between the two leaves of the same key_value
        // group — a disagreement means the two leaves are not siblings in the footer the guard will bound against.
        int mapMaxDef = NestedLevelGuard.ContainerMaxDefinitionLevel(keyLeaf);
        int valueContainerMaxDef = NestedLevelGuard.ContainerMaxDefinitionLevel(valueLeaf);
        if (valueContainerMaxDef != mapMaxDef)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{label}': the key leaf reports container definition level {mapMaxDef} but the "
                + $"value leaf reports {valueContainerMaxDef}; both share one repeated key_value group.");
        }

        int slots = CountMapSlots(segments, label);

        int[] keyDef = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] valueDef = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] keyRep = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        int[] valueRep = ArrayPool<int>.Shared.Rent(Math.Max(slots, 1));
        try
        {
            int emitted = ComputeMapLevels(
                segments, mapMaxDef, keyLeaf.MaxDefinitionLevel, valueLeaf.MaxDefinitionLevel, label,
                keyDef.AsSpan(0, slots), valueDef.AsSpan(0, slots), keyRep.AsSpan(0, slots),
                valueRep.AsSpan(0, slots));
            if (emitted != slots)
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{label}': the shredder emitted {emitted} level slot(s) but the raw offsets "
                    + $"describe {slots}.");
            }

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
    private static int ComputeMapLevels(
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

        return slot;
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

    internal static int CheckSlotBound(long slots, string label)
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

    // The present cells of a leaf, in the SAME order the level computation emits them, pushed at a struct
    // VISITOR so every lane (count / copy / measure / transcode) walks the vectors exactly once with no
    // intermediate materialization (§2.3b, B3) and no generic virtual dispatch.
    //
    // ForEachPresent RETURNS the number of cells it visited. That count is derived purely from the source
    // vectors' null masks — never from the level stream — and it is what §2.3c's packed-values clause is
    // checked against (B1). Deriving both sides of that clause from the levels made it `f(x) == f(x)`: it
    // could never fire, and an under-filling collector would have published uninitialized pooled memory.
    private interface IPresentVisitor
    {
        void Visit(ColumnVector child, int index);
    }

    private interface IValueSource
    {
        int ForEachPresent<TVisitor>(ref TVisitor visitor, string label)
            where TVisitor : struct, IPresentVisitor;
    }

    private readonly struct StructValueSource(IReadOnlyList<ColumnSegment> segments, int ordinal) : IValueSource
    {
        public int ForEachPresent<TVisitor>(ref TVisitor visitor, string label)
            where TVisitor : struct, IPresentVisitor
        {
            int count = 0;
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
                        visitor.Visit(child, row);
                        count++;
                    }
                }
            }

            return count;
        }
    }

    private readonly struct ListValueSource(IReadOnlyList<ColumnSegment> segments) : IValueSource
    {
        public int ForEachPresent<TVisitor>(ref TVisitor visitor, string label)
            where TVisitor : struct, IPresentVisitor
        {
            int count = 0;
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
                            visitor.Visit(elements, start + e);
                            count++;
                        }
                    }
                }
            }

            return count;
        }
    }

    private readonly struct MapValueSource(IReadOnlyList<ColumnSegment> segments, bool keys) : IValueSource
    {
        public int ForEachPresent<TVisitor>(ref TVisitor visitor, string label)
            where TVisitor : struct, IPresentVisitor
        {
            int count = 0;
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
                            visitor.Visit(child, start + e);
                            count++;
                        }
                    }
                }
            }

            return count;
        }
    }

    // ----- present-cell visitors -----

    /// <summary>Visits nothing: used by the §2.9 N9 pre-pass, which needs the present-cell COUNT (to feed the
    /// §2.3c packed-values clause) but must not materialize a single value.</summary>
    private struct CountingVisitor : IPresentVisitor
    {
        public readonly void Visit(ColumnVector child, int index)
        {
        }
    }

    /// <summary>Copies each present fixed-width cell into the caller-owned buffer, BOUNDS-CHECKING its own
    /// writes: an over-supplying source fails closed on the typed contract instead of raising a raw
    /// <see cref="IndexOutOfRangeException"/> (B1).</summary>
    private struct ValueCollector<T> : IPresentVisitor
    {
        private readonly T[] _destination;
        private readonly int _capacity;
        private readonly Func<ColumnVector, int, T> _read;
        private readonly string _label;
        private int _count;

        internal ValueCollector(T[] destination, int capacity, Func<ColumnVector, int, T> read, string label)
        {
            _destination = destination;
            _capacity = capacity;
            _read = read;
            _label = label;
            _count = 0;
        }

        public void Visit(ColumnVector child, int index)
        {
            if (_count >= _capacity)
            {
                throw ValueOverflow(_label, _capacity);
            }

            _destination[_count++] = _read(child, index);
        }
    }

    /// <summary>Accumulates Σ present UTF-8 byte length under <c>checked</c>. This IS the §2.3b upper bound on
    /// the transcoded UTF-16 scratch (a UTF-16 char count never exceeds the UTF-8 byte count), so the string
    /// lane needs no decode pre-pass and materializes nothing.</summary>
    private struct ByteLengthVisitor : IPresentVisitor
    {
        private long _total;

        internal readonly long Total => _total;

        public void Visit(ColumnVector child, int index) =>
            _total = checked(_total + child.GetBytes(index).Length);
    }

    /// <summary>Transcodes UTF-8 → UTF-16 straight into the exactly-sized, NEVER-grown per-leaf scratch and
    /// hands out views into it.</summary>
    private struct CharTranscodeVisitor : IPresentVisitor
    {
        private readonly char[] _scratch;
        private readonly int _budget;
        private readonly ReadOnlyMemory<char>[] _destination;
        private readonly int _capacity;
        private readonly string _label;
        private int _count;
        private int _position;

        internal CharTranscodeVisitor(
            char[] scratch, int budget, ReadOnlyMemory<char>[] destination, int capacity, string label)
        {
            _scratch = scratch;
            _budget = budget;
            _destination = destination;
            _capacity = capacity;
            _label = label;
            _count = 0;
            _position = 0;
        }

        public void Visit(ColumnVector child, int index)
        {
            if (_count >= _capacity)
            {
                throw ValueOverflow(_label, _capacity);
            }

            ReadOnlySpan<byte> utf8 = child.GetBytes(index);
            if (_position + utf8.Length > _budget)
            {
                throw ScratchOverflow(_label);
            }

            int written = Encoding.UTF8.GetChars(utf8, _scratch.AsSpan(_position));
            _destination[_count++] = new ReadOnlyMemory<char>(_scratch, _position, written);
            _position += written;
        }
    }

    /// <summary>The binary lane's counterpart to <see cref="CharTranscodeVisitor"/> — the same ownership rules,
    /// minus the transcode.</summary>
    private struct ByteCopyVisitor : IPresentVisitor
    {
        private readonly byte[] _scratch;
        private readonly int _budget;
        private readonly ReadOnlyMemory<byte>[] _destination;
        private readonly int _capacity;
        private readonly string _label;
        private int _count;
        private int _position;

        internal ByteCopyVisitor(
            byte[] scratch, int budget, ReadOnlyMemory<byte>[] destination, int capacity, string label)
        {
            _scratch = scratch;
            _budget = budget;
            _destination = destination;
            _capacity = capacity;
            _label = label;
            _count = 0;
            _position = 0;
        }

        public void Visit(ColumnVector child, int index)
        {
            if (_count >= _capacity)
            {
                throw ValueOverflow(_label, _capacity);
            }

            ReadOnlySpan<byte> payload = child.GetBytes(index);
            if (_position + payload.Length > _budget)
            {
                throw ScratchOverflow(_label);
            }

            payload.CopyTo(_scratch.AsSpan(_position));
            _destination[_count++] = new ReadOnlyMemory<byte>(_scratch, _position, payload.Length);
            _position += payload.Length;
        }
    }

    private static DeltaStorageException ValueOverflow(string label, int capacity) =>
        DeltaStorageException.CorruptData(
            $"Nested column '{label}': the column vector supplied more than the {capacity} present value(s) "
            + "its definition levels describe.");

    private static DeltaStorageException ScratchOverflow(string label) =>
        DeltaStorageException.CorruptData(
            $"Nested column '{label}': the variable-width leaf's payloads exceed the transcode buffer measured "
            + "for them.");

    // ----- leaf write dispatch (AOT-safe: closed generic instantiations, never MakeGenericMethod) -----

    private static Task WriteLeafAsync<TSource>(
        ParquetRowGroupWriter? rowGroup,
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
        if (rowGroup is null)
        {
            // §2.9 N9 pre-pass: run the guard with an INDEPENDENTLY derived present-cell count, but touch no
            // value and rent no value buffer.
            var counter = default(CountingVisitor);
            int present = source.ForEachPresent(ref counter, label);
            RunLevelGuard(leaf, def, rep, present, rowCount, label);
            return Task.CompletedTask;
        }

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
            // Belt and braces (B1): the guard below refuses an under-filled buffer, and the buffer is cleared
            // first so a pooled array's previous tenant's payload can never reach the writer even if it did.
            values.AsSpan(0, valueCount).Clear();
            var collector = new ValueCollector<T>(values, valueCount, read, label);
            int collected = source.ForEachPresent(ref collector, label);
            await WriteAllPartsAsync<T>(
                rowGroup, leaf, values.AsMemory(0, valueCount), def, rep, rowCount, collected, label,
                cancellationToken).ConfigureAwait(false);
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
    // into a per-leaf pooled char[] and hands ReadOnlyMemory<char> VIEWS into it. TWO span walks, no
    // materialization (B3): the first sums the present payloads' UTF-8 byte lengths — a valid and exact upper
    // bound on the char count — and the second decodes straight into the resulting scratch. The scratch is
    // NEVER grown mid-leaf: an Array.Resize/re-rent partway through would strand already-handed-out views in
    // the abandoned array (silent garbage). Both pools are returned with clearArray:true — mandatory for the
    // REFERENCE-bearing element array, which would otherwise retain the char[] (and the user payload it
    // carries) across rents.
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
        int budget = MeasurePayloadBytes(source, label);
        char[] scratch = ArrayPool<char>.Shared.Rent(Math.Max(budget, 1));
        ReadOnlyMemory<char>[] values = ArrayPool<ReadOnlyMemory<char>>.Shared.Rent(Math.Max(valueCount, 1));
        try
        {
            values.AsSpan(0, valueCount).Clear();
            var transcoder = new CharTranscodeVisitor(scratch, budget, values, valueCount, label);
            int collected = source.ForEachPresent(ref transcoder, label);
            await WriteAllPartsAsync<ReadOnlyMemory<char>>(
                rowGroup, leaf, values.AsMemory(0, valueCount), def, rep, rowCount, collected, label,
                cancellationToken).ConfigureAwait(false);
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
        int budget = MeasurePayloadBytes(source, label);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Max(budget, 1));
        ReadOnlyMemory<byte>[] values = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(valueCount, 1));
        try
        {
            values.AsSpan(0, valueCount).Clear();
            var copier = new ByteCopyVisitor(scratch, budget, values, valueCount, label);
            int collected = source.ForEachPresent(ref copier, label);
            await WriteAllPartsAsync<ReadOnlyMemory<byte>>(
                rowGroup, leaf, values.AsMemory(0, valueCount), def, rep, rowCount, collected, label,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(values, clearArray: true);
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }
    }

    // The §2.3b size pass. `checked` accumulation into a long, then an explicit ceiling: a single leaf whose
    // payloads exceed 2 GiB cannot be transcoded into one contiguous scratch, and that is an
    // UnsupportedFeature on the typed contract — never a raw OverflowException escaping the write door.
    private static int MeasurePayloadBytes<TSource>(TSource source, string label)
        where TSource : struct, IValueSource
    {
        var lengths = default(ByteLengthVisitor);
        try
        {
            _ = source.ForEachPresent(ref lengths, label);
        }
        catch (OverflowException)
        {
            throw PayloadTooLarge(label);
        }

        long total = lengths.Total;
        return total > int.MaxValue ? throw PayloadTooLarge(label) : (int)total;
    }

    private static DeltaStorageException PayloadTooLarge(string label) =>
        DeltaStorageException.UnsupportedFeature(
            $"Nested column '{label}': the variable-width leaf's payloads exceed the 2 GiB a single Parquet "
            + "leaf write can stage in one buffer.");

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
        int collected,
        string label,
        CancellationToken cancellationToken)
        where T : struct
    {
        RunLevelGuard(leaf, def, rep, collected, rowCount, label);
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

    // The one place the §2.3c per-leaf guard is invoked, from BOTH the write lane and the N9 pre-pass, so the
    // two can never diverge. `collected` is the SOURCE-derived present-cell count (B1).
    private static void RunLevelGuard(
        DataField leaf,
        ReadOnlyMemory<int> def,
        ReadOnlyMemory<int>? rep,
        int collected,
        int rowCount,
        string label) =>
        NestedLevelGuard.Validate(
            leaf, def.Span, rep.HasValue ? rep.Value.Span : ReadOnlySpan<int>.Empty, rep.HasValue,
            collected, rowCount, label);

    // ----- cross-leaf guards (§2.3c) -----

    /// <summary>
    /// §2.3c cross-leaf struct clause: all children of an OPTIONAL struct must agree, at every row, on
    /// whether the struct itself is null. <b>internal</b> so it can be driven directly by fault-injected
    /// negatives — a guard with no negative test is indistinguishable from a no-op (B4).
    /// </summary>
    internal static void ValidateStructNullParity(
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

    /// <summary>
    /// §2.3c cross-leaf map clause: a 3-level map nests key and value in ONE repeated <c>key_value</c> group,
    /// so their repetition streams must be IDENTICAL and their definition streams must agree on every
    /// null/empty/absent slot. <b>internal</b> for the same reason as
    /// <see cref="ValidateStructNullParity"/> (B4).
    /// </summary>
    internal static void ValidateMapParallelLevels(
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

    // The logical row count the row group's segments describe, derived from the SEGMENTS — the independent
    // side of the struct lane's N4-a slot-count clause (B2).
    private static int TotalSegmentRows(IReadOnlyList<ColumnSegment> segments, string label)
    {
        long rows = 0;
        foreach (ColumnSegment segment in segments)
        {
            rows = checked(rows + segment.Length);
        }

        return CheckSlotBound(rows, label);
    }

    // #730/S2, per NESTED leaf: the mapped Parquet leaf's repetition (OPTIONAL vs REQUIRED) must equal the
    // Delta schema's declared nullability for that leaf. A divergence is precisely the footer↔log
    // contradiction #730 exists to remove — writing it would publish a file whose repetition contradicts the
    // committed schemaString — so it is an UNCONDITIONAL typed reject, never a Debug.Assert that vanishes in
    // Release.
    private static void EnsureLeafRepetition(
        DataField leaf, bool declaredNullable, string label, string context)
    {
        if (leaf.IsNullable != declaredNullable)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested column '{label}' {context}: the mapped Parquet leaf is "
                + $"{(leaf.IsNullable ? "OPTIONAL" : "REQUIRED")} but the declared Delta schema says "
                + $"{(declaredNullable ? "nullable" : "non-nullable")}; writing it would publish a footer that "
                + "contradicts the committed schema (#730).");
        }
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
