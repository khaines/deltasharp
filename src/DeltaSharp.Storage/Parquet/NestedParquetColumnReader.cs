using System.Runtime.CompilerServices;
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
/// Reconstructs the three single-level nested Parquet shapes (#571) — a <b>struct of scalars</b>, an
/// <b>array of a scalar</b>, and a <b>map of scalar→scalar</b> — from the raw Dremel repetition/definition
/// levels Parquet.Net 6.1.0 exposes (<see cref="ParquetRowGroupReader.ReadRawAsync{T}"/>), into the
/// immutable nested reference vectors <see cref="StructColumnVector"/>/<see cref="ListColumnVector"/>/
/// <see cref="MapColumnVector"/> (#570). Parquet.Net 6.1.0 offers no reconstructed nested read (no
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
///   <item><description><b>Leaf structural levels (schema, at shape resolution).</b> Before any stream is
///   read, every leaf the reader navigates to is checked (<see cref="ValidateLeafStructuralLevels"/>) so its
///   declared <c>MaxRepetitionLevel</c> equals its position's requirement (a struct scalar field 0; a list
///   element / map key / map value 1) and its <c>MaxDefinitionLevel</c> sits in
///   <c>[containerMaxDef, containerMaxDef + 1]</c> — closing the masquerade where a crafted footer declares a
///   scalar leaf repeated (a repeated primitive posing as struct rows) or over-nested (a phantom optional
///   level mis-classifying present vs null cells). Nullability of the leaf's OWN value is enforced by the
///   #813 required-lane guard (<see cref="RejectNullInRequiredNestedLeaf"/>): a required (non-nullable) leaf
///   backed by a physically-OPTIONAL column that materializes a LEAF-ATTRIBUTABLE null (every ancestor
///   container present, only the leaf's own value null — Dremel level <c>leaf max def − 1</c>) fails closed,
///   the nested analogue of #807's flat <c>RejectNullInRequiredLane</c>, extended to ALL leaf types. An
///   ANCESTOR null (a null struct / absent list element / null map entry — a lower definition level) is
///   legitimate and accepted, so container-null nesting still reads.</description></item>
///   <item><description><b>Every leaf (streams).</b> Each reconstructed definition level lies in
///   <c>[0, leaf max def]</c> and each repetition level in <c>[0, leaf max rep]</c>
///   (<see cref="ValidateLevelRange"/>, covering BOTH streams); the declared value count is ceiling-bounded
///   and non-negative; the def and rep arrays are allocated to the leaf's own value count, so they are
///   equal-length and value-count-aligned by construction.</description></item>
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
///   repetition stream (<see cref="ValidateParallelRepetition"/>) AND their definition streams agree,
///   slot-by-slot, on BOTH co-indexed dimensions (<see cref="ValidateParallelDefinition"/>): entry presence
///   (both at/above the map's own level, or both below) so the value child pairs positionally with the
///   key-driven entries, AND — for a non-entry slot — the specific container state (null map vs empty map)
///   so a self-contradictory placeholder fails closed rather than resolving to the key's view; the shared
///   entry structure obeys the list state-transition rules (the key stream drives
///   <see cref="BuildRepeatedStructure"/>); the key and value child lengths match; the reassembled entry
///   count equals the key child length; and the entry-slot count is at least the row count.</description></item>
/// </list>
/// With this set the three shapes are fully validated against both structurally-invalid SCHEMA levels (a
/// crafted leaf whose declared max levels contradict its navigated position) and structurally-invalid def/rep
/// STREAMS: the value/element/entry reconstruction is a pure positional consequence of levels that are all
/// schema-consistent (leaf max levels match the shape), range-checked, length-aligned, cross-leaf-consistent
/// (map), cross-field-consistent (struct), and state-transition-legal (list/map) — leaving no residual class
/// that decodes to silent wrong data.</para>
/// </remarks>
internal static class NestedParquetColumnReader
{
    /// <summary>
    /// The hard cap on the reconstruction recursion depth for a nested-within-nested read (585a, design §2.6).
    /// Set to <b>64</b> = the write cap (<c>ParquetTypeMapping.MaxFooterTypeDepth</c> /
    /// <c>DeltaWriteSchemaEligibility.MaxDepth</c>), so the read cap is <b>≥ every write/log/footer cap</b> and
    /// never over-rejects a schema DeltaSharp can write/admit (no read-after-write parity gap). Checked at
    /// <see cref="ValidateShape"/> / <c>DecodeNode</c> entry BEFORE any allocation or descent, so a maliciously
    /// deep schema fails closed <see cref="StorageErrorKind.UnsupportedFeature"/> deterministically — never a
    /// <see cref="StackOverflowException"/> (which would bypass the fail-closed contract).
    /// </summary>
    internal const int MaxNestedReadDepth = 64;

    // Micros in one UTC day — the scale factor for the date → timestamp_ntz per-leaf promotion (#546),
    // mirroring ParquetFileReader.MicrosPerDay for the flat path.
    private const long MicrosPerDay = 86_400L * 1_000_000L;

    /// <summary>
    /// Structurally validates that <paramref name="fileField"/> matches the requested nested
    /// <paramref name="requestedType"/> to ARBITRARY DEPTH (585a): the correct container kind at every level,
    /// every requested leaf present with an EXACT physical-type match OR — when
    /// <paramref name="allowTypeWideningPromotion"/> is set (name/none mode, any depth — 585b lifted the #546
    /// depth cap) — a Delta-sanctioned NARROWER widening the read path promotes per leaf (#546), and every per-level
    /// structural guard (canonical/required map key, leaf structural-level consistency) applied recursively —
    /// WITHOUT reading any data page, so a schema disagreement fails before any batch is yielded (mirrors the
    /// flat path's up-front validation).
    /// </summary>
    /// <exception cref="DeltaStorageException">The shapes disagree
    /// (<see cref="StorageErrorKind.SchemaMismatch"/>), a leaf type is unsupported or the schema nests deeper
    /// than <see cref="MaxNestedReadDepth"/> (<see cref="StorageErrorKind.UnsupportedFeature"/>), or a crafted
    /// footer declares structurally-inconsistent levels (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    /// <remarks>
    /// Invoked only on the NAME/none-mode branch (id-mode columns route to the separate
    /// <c>Validate*ShapeById</c> helpers, which hardcode <c>promoteLeaf: false</c>), so the
    /// <paramref name="allowTypeWideningPromotion"/> gate composed here is never applied to an id-mode leaf
    /// (#546 §2.4 / §9 O1).
    /// </remarks>
    public static void ValidateShape(
        Field fileField, DataType requestedType, string columnName, bool allowTypeWideningPromotion)
    {
        // #683 message hygiene: `columnName` is a pure DIAGNOSTIC LABEL (never a lookup key) that is echoed
        // into every message this reader raises, including the recursive sub-labels built from it. Sanitize it
        // ONCE at the entry point (control-char strip + length cap) so a crafted/foreign schema name cannot
        // inject line breaks into a structured-log sink or render unbounded.
        columnName = DiagnosticText.Sanitize(columnName);
        ValidateNode(fileField, requestedType, columnName, depth: 0, allowTypeWideningPromotion);
    }

    // 585a — recursive shape/level validator. Keys each container's leaf-structural-level guards off THAT
    // container node's own MaxRepetitionLevel/MaxDefinitionLevel, so it generalizes the single-level guards to
    // any depth; a scalar leaf routes to ExpectScalarLeaf (exact physical type + level guard), a nested child
    // recurses. The map-transposition + canonical-name + required-key guards run at EVERY map node.
    private static void ValidateNode(
        Field fileField, DataType requestedType, string context, int depth, bool allowTypeWideningPromotion)
    {
        if (depth > MaxNestedReadDepth)
        {
            // DoS bound, checked BEFORE any descent (design §2.6): a maliciously deep schema fails closed here,
            // at shape resolution, before a single data page is read — never a StackOverflowException.
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for column '{context}': the requested type nests deeper than the "
                + $"supported limit of {MaxNestedReadDepth} levels.");
        }

        switch (requestedType)
        {
            case StructType structType:
                if (fileField is not PqStructField fileStruct)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{context}': requested a struct but the file column is not a struct.");
                }

                if (structType.Count == 0)
                {
                    // Defensive parity with EnsureReadSupported: a zero-field struct has no leaf to drive the
                    // row count and would reconstruct a length-0 vector — fail closed on the contract.
                    throw DeltaStorageException.UnsupportedFeature(
                        $"Parquet nested read for struct column '{context}': a zero-field struct is not supported.");
                }

                foreach (StructField field in structType)
                {
                    string childLabel = $"struct column '{context}' field '{DiagnosticText.Sanitize(field.Name)}'";
                    if (!TryResolveStructChildNode(fileStruct, field, context, out Field? childNode))
                    {
                        // ABSENT physical name (#857). A REQUIRED absent child cannot be null-filled — fail
                        // closed here (fast, before any decode; mirrors the flat gate), the same
                        // ColumnNotPresentInFile the decode path would raise (§9 Q3). A NULLABLE absent child
                        // is null-filled at decode (ReadStructAsync), so there is nothing in the file to
                        // validate — defer. A DUPLICATE already threw inside the resolver; a PRESENT child
                        // still validates its shape below (AC3), so absence is never conflated with mismatch.
                        if (!field.Nullable)
                        {
                            throw DeltaStorageException.ColumnNotPresentInFile(childLabel);
                        }

                        continue;
                    }

                    ValidateChild(
                        childNode!, field.DataType, fileStruct.MaxRepetitionLevel, fileStruct.MaxDefinitionLevel,
                        childLabel, depth + 1, allowTypeWideningPromotion);
                }

                break;
            case ArrayType arrayType:
                if (fileField is not PqListField fileList)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{context}': requested an array but the file column is not a list.");
                }

                ValidateChild(
                    fileList.Item, arrayType.ElementType, fileList.MaxRepetitionLevel, fileList.MaxDefinitionLevel,
                    $"array column '{context}' element", depth + 1, allowTypeWideningPromotion);
                break;
            case MapType mapType:
                if (fileField is not PqMapField fileMap)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{context}': requested a map but the file column is not a map.");
                }

                // The map key/value transposition + canonical-name guards run at EVERY map node (design §2.6):
                // a map<T,T> with a required value can silently transpose past the type/level guards, so assert
                // the canonical child names and required key on THIS map before its children are validated.
                EnsureCanonicalMapChildNames(fileMap, context);
                EnsureRequiredMapKey(fileMap, context);
                ValidateChild(
                    fileMap.Key, mapType.KeyType, fileMap.MaxRepetitionLevel, fileMap.MaxDefinitionLevel,
                    $"map column '{context}' key", depth + 1, allowTypeWideningPromotion);
                ValidateChild(
                    fileMap.Value, mapType.ValueType, fileMap.MaxRepetitionLevel, fileMap.MaxDefinitionLevel,
                    $"map column '{context}' value", depth + 1, allowTypeWideningPromotion);
                break;
            default:
                // #705 predicate: struct/array/map are handled by the cases above, so this arm fires only for
                // a SCALAR requestedType at a CONTAINER position (a nested column whose top type is scalar) —
                // DescribeType renders it as a bounded atomic literal.
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet nested read for column '{context}' of type '{DiagnosticText.DescribeType(requestedType)}' "
                    + "is not supported.");
        }
    }

    // Validates one requested child (array element / map key/value / struct field) against its file node:
    // a scalar child routes to the exact-physical-type + structural-level leaf guard; a nested child recurses
    // (guarded by MaxNestedReadDepth at ValidateNode entry). `parentMaxRep`/`parentMaxDef` are the IMMEDIATE
    // parent container node's own levels — the thresholds the leaf structural-level guard needs at any depth.
    // `depth` is THIS child's own container depth (its parent's depth + 1). 585b lifted the #546 depth cap on
    // the name-mode promotion gate: a name-mode scalar leaf is promotion-eligible at ANY depth
    // (`allowTypeWideningPromotion`), so a nested-within-nested narrow leaf under a widened schema promotes,
    // composing the read gate with 585a's recursive descent (design §2.5, R1). Id-mode leaves are validated by
    // the separate `Validate*ShapeById` helpers (hardcoded `promoteLeaf: false`) and never reach here.
    private static void ValidateChild(
        Field fileChild, DataType requested, int parentMaxRep, int parentMaxDef, string context, int depth,
        bool allowTypeWideningPromotion)
    {
        if (requested is ArrayType or MapType or StructType)
        {
            ValidateNode(fileChild, requested, context, depth, allowTypeWideningPromotion);
        }
        else
        {
            bool promoteLeaf = allowTypeWideningPromotion;
            _ = ExpectScalarLeaf(fileChild, requested, parentMaxRep, parentMaxDef, context, promoteLeaf);
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
        NestedDecodeBudget budget,
        IReadOnlyDictionary<int, DataField>? byFieldId,
        NestedInteriorIds? interiorIds,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        // #683 message hygiene: `columnName` is a pure DIAGNOSTIC LABEL (never a lookup key) that is echoed
        // into every message this reader raises, including the recursive sub-labels built from it. Sanitize it
        // ONCE at the entry point (control-char strip + length cap) so a crafted/foreign schema name cannot
        // inject line breaks into a structured-log sink or render unbounded.
        columnName = DiagnosticText.Sanitize(columnName);

        // A top-level nested column: parentMaxRep/parentMaxDef = 0 (its owner is the record itself, always
        // present), ownerCells = rowCount, depth = 0.
        return await DecodeNode(
            rowGroup, fileField, requestedType, rowCount, columnName, budget, byFieldId, interiorIds,
            depth: 0, parentMaxRep: 0, parentMaxDef: 0, allowTypeWideningPromotion, cancellationToken)
            .ConfigureAwait(false);
    }

    // 585a — the recursive shredder-inverse. Dispatches on the requested type, reconstructing an arbitrary-depth
    // nested ColumnVector from the raw Dremel levels. `ownerCells` is the number of parent cells that reach this
    // node position (rowCount at the top; the parent container's present-element/entry count one level down);
    // the returned vector's length is the number of cells this node contributes to its parent (a struct child
    // returns parent.Length; a list element / map key/value returns the parent's total element/entry count).
    // `parentMaxRep`/`parentMaxDef` are the IMMEDIATE parent container node's own Dremel levels — the owner-cell
    // boundary + present-parent thresholds a nested repeated level needs (design §2.2).
    private static async ValueTask<ColumnVector> DecodeNode(
        ParquetRowGroupReader rowGroup,
        Field fileField,
        DataType requestedType,
        int ownerCells,
        string columnName,
        NestedDecodeBudget budget,
        IReadOnlyDictionary<int, DataField>? byFieldId,
        NestedInteriorIds? interiorIds,
        int depth,
        int parentMaxRep,
        int parentMaxDef,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        if (depth > MaxNestedReadDepth)
        {
            // DoS bound, checked BEFORE any allocation or descent (design §2.6): decode only ever runs after
            // ValidateShape (which enforces the same bound up front), so this is defense in depth — a
            // deterministic typed rejection, never a StackOverflowException.
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for column '{columnName}': the schema nests deeper than the supported "
                + $"limit of {MaxNestedReadDepth} levels.");
        }

        return requestedType switch
        {
            // #676: a non-null byFieldId routes struct-child binding through the id-keyed containment path
            // (id mode). #839: an id-mode array/map (non-null byFieldId + interiorIds) routes the interior
            // element/key/value leaf binding through the same containment/identity-selection path; name/none
            // mode (null interiorIds) binds the interior positionally. 585a: name/none mode may recurse into a
            // nested interior (DecodeNode), threading ownerCells/depth/parentMaxRep/parentMaxDef. The two
            // interior paths are mutually exclusive — id mode is always a SCALAR interior (nested-within-nested
            // id mode is rejected upstream), so it never recurses.
            StructType structType => await ReadStructAsync(
                rowGroup, ExpectStruct(fileField, columnName), structType, ownerCells, columnName, budget, byFieldId,
                depth, parentMaxRep, parentMaxDef, allowTypeWideningPromotion, cancellationToken).ConfigureAwait(false),
            ArrayType arrayType => await ReadListAsync(
                rowGroup, ExpectList(fileField, columnName), arrayType, ownerCells, columnName, budget,
                byFieldId, interiorIds, depth, parentMaxRep, parentMaxDef, allowTypeWideningPromotion,
                cancellationToken).ConfigureAwait(false),
            MapType mapType => await ReadMapAsync(
                rowGroup, ExpectMap(fileField, columnName), mapType, ownerCells, columnName, budget,
                byFieldId, interiorIds, depth, parentMaxRep, parentMaxDef, allowTypeWideningPromotion,
                cancellationToken).ConfigureAwait(false),
            _ => throw DeltaStorageException.UnsupportedFeature(
                // #705 predicate: struct/array/map are the three explicit switch arms above, so this `_` arm
                // fires only for a SCALAR requestedType at a container position. DescribeType bounds it.
                $"Parquet nested read for column '{columnName}' of type '{DiagnosticText.DescribeType(requestedType)}' "
                + "is not supported."),
        };
    }

    // ----- struct -----

    private static async ValueTask<ColumnVector> ReadStructAsync(
        ParquetRowGroupReader rowGroup,
        PqStructField fileStruct,
        StructType requested,
        int ownerCells,
        string columnName,
        NestedDecodeBudget budget,
        IReadOnlyDictionary<int, DataField>? byFieldId,
        int depth,
        int parentMaxRep,
        int parentMaxDef,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        int rowCount = ownerCells;

        // Charge the struct's OWN per-row null arrays against the shared row-group budget before its fields are
        // read: the reconstruction builds a transient bool[rows] mask AND StructColumnVector copies it into a
        // final validity bitmap (NestedValidity.Build), so charge 2 bytes/row (both live at the copy). The
        // field children are charged separately as they are read — structure + fields stay cumulatively bounded.
        budget.ChargeStructural(rowCount, 2 * sizeof(bool), $"struct column '{columnName}'");

        // A struct's own definition level: a row whose definition level is BELOW this marks a NULL struct
        // (vs a present struct with a null field, whose level sits at/above this but below the field's max).
        int structMaxDef = fileStruct.MaxDefinitionLevel;
        int structMaxRep = fileStruct.MaxRepetitionLevel;

        // A struct nested under a repeated ancestor (structMaxRep > 0) is NOT one-value-per-row: its scalar
        // fields' leaves carry the ancestor's null/empty-container placeholder slots. Such a field is collected
        // with a present floor at the PARENT's own level (parentMaxDef) so exactly one cell is materialized per
        // struct owner cell (a present parent element), and its per-owner-cell def stream is EXTRACTED (clamped
        // at structMaxDef) rather than taken raw. A TOP-LEVEL struct (structMaxRep == 0) keeps the original
        // one-per-row path (presentFloor 0, raw def) — byte-identical, no #571 regression.
        bool underRepeatedAncestor = structMaxRep > 0;
        var children = new ColumnVector[requested.Count];
        // Each field's definition-level stream. It is `null` for a required field (no null mask). For a
        // synthesized ABSENT child (null-filled, #857) it is `null` under a REQUIRED struct (required-field
        // semantics) or a `StructPresenceDefs` clone under a NULLABLE struct (so the cross-field parity guard's
        // INV-PARITY holds against every present sibling).
        int[]?[] fieldDefs = new int[requested.Count][];
        // #857 (Storage F1 / red-team): the struct's presence stream is a property of the STRUCT, not of any
        // one absent child, so it is computed AT MOST ONCE per ReadStructAsync and shared by every absent
        // child — otherwise N absent children would each re-decode AND re-CHARGE the same driving leaf N times,
        // which both wastes I/O and can spuriously exhaust the shared decode budget for a wide drop-then-re-add
        // projection (failing valid data closed). The shared array is read-only to BuildStructNullMask.
        int[]? absentPresenceDefs = null;
        bool absentPresenceComputed = false;
        for (int i = 0; i < requested.Count; i++)
        {
            StructField field = requested[i];

            // #676: id mode binds each child by field_id within the resolved container (containment-scoped,
            // never by name); name/none mode binds by physical name. Id mode supports ONLY top-level
            // struct<scalars> (nested-within-nested column mapping is out of scope, #676/#839), so it is always
            // one-value-per-row.
            if (byFieldId is not null)
            {
                DataField leaf = ResolveStructFieldById(fileStruct, field, byFieldId, columnName);
                // Id-mode nested widening is out of scope (#676/#839, design §9 O1): keep the exact-match
                // requirement — promoteLeaf is never set on an id-mode leaf.
                (MutableColumnVector child, int[]? def, _, int numValues) = await ReadScalarLeafAsync(
                    rowGroup, leaf, field.DataType, presentFloor: 0, budget, promoteLeaf: false, cancellationToken)
                    .ConfigureAwait(false);
                RejectNullInRequiredNestedLeaf(def, leaf, field.DataType, field.Nullable);
                EnsureStructFieldRowCount(numValues, rowCount, columnName, field.Name);
                children[i] = child;
                fieldDefs[i] = def;
                continue;
            }

            string childContext = $"struct column '{columnName}' field '{DiagnosticText.Sanitize(field.Name)}'";
            if (!TryResolveStructChildNode(fileStruct, field, columnName, out Field? resolvedChild))
            {
                // ABSENT physical name (#857, §2.3): a drop-then-re-add mints a FRESH physicalName, so a data
                // file written before the re-add carries NO physical column for the re-added child — genuinely
                // absent, exactly as an additively-added top-level column is absent from an older, narrower
                // file. Reached ONLY when the physical name is absent (a PRESENT-but-mismatched child routed
                // through the resolver as `true` and fails closed below on type/shape — absence and mismatch
                // are never conflated, AC3); a DUPLICATE physical name already threw inside the resolver.
                if (!field.Nullable)
                {
                    // §9 Q3: a REQUIRED absent child cannot be null-filled (a required lane cannot carry the
                    // null the older rows would need) — fail closed, mirroring the flat gate
                    // (nullFillMissingColumns && requestedField.Nullable) at ParquetFileReader. (Defense in
                    // depth: ValidateShape's ValidateNode already fails this case closed with the SAME
                    // ColumnNotPresentInFile before decode; this keeps the decode path fail-closed even if
                    // reached without the up-front shape validation.)
                    throw DeltaStorageException.ColumnNotPresentInFile(childContext);
                }

                // NULLABLE + absent → NULL-FILL (§2.4/§2.5): an all-null child vector for the FULL requested
                // type (scalar OR nested subtree) plus a synthesized per-owner-cell presence stream clamped at
                // structMaxDef so BuildStructNullMask's parity guard is satisfied (INV-PARITY). The presence
                // stream is computed once (memoized) and shared across all absent children of this struct.
                children[i] = SynthesizeAbsentChild(field.DataType, rowCount, budget, childContext, depth + 1);
                if (!absentPresenceComputed)
                {
                    absentPresenceDefs = await StructPresenceDefs(
                        rowGroup, fileStruct, structMaxDef, parentMaxDef, parentMaxRep, rowCount, budget,
                        childContext, cancellationToken).ConfigureAwait(false);
                    absentPresenceComputed = true;
                }

                fieldDefs[i] = absentPresenceDefs;
                continue;
            }

            // PRESENT: unchanged routing — a scalar leaf or a 585a nested recurse. A type/shape disagreement
            // here still fails closed (SchemaMismatch, AC3), never null-fills.
            Field childNode = resolvedChild!;

            if (field.DataType is ArrayType or MapType or StructType)
            {
                // A nested struct child (585a): recurse. The child contributes one cell per struct owner cell.
                // Its driving-leaf def (clamped at structMaxDef, one per owner cell) reports the STRUCT's
                // presence — feed that to the cross-field null-mask parity guard.
                DataField drivingLeaf = FirstDataField(childNode);
                // 585b (R5): read the driving leaf for STRUCTURE ONLY (def/rep) as its OWN physical type
                // (ParquetTypeMapping.ToDataType — the StructPresenceDefs pattern), not the requested (possibly
                // widened) first-scalar type; a widened driving read would fault the raw typed decode. def/rep
                // are type-agnostic (design §2.5 driving-leaf gap).
                DataType drivingType = ParquetTypeMapping.ToDataType(drivingLeaf);
                (_, int[]? drivingDef, int[]? drivingRep, int drivingNumValues) = await ReadScalarLeafAsync(
                    rowGroup, drivingLeaf, drivingType, presentFloor: 0, budget, promoteLeaf: false, cancellationToken)
                    .ConfigureAwait(false);
                fieldDefs[i] = ExtractOwnerCellDefs(
                    drivingDef, drivingRep, drivingNumValues, structMaxDef, parentMaxDef, parentMaxRep, rowCount, childContext);

                // A struct is TRANSPARENT to repetition: its children share the struct's OWN owner cells and
                // parent boundary (even a null-struct row yields a null child cell). So recurse with the
                // struct's parentMaxRep/parentMaxDef UNCHANGED — NOT structMaxRep/structMaxDef.
                //
                // 585b defense-in-depth (#868 Issue 2): this deeper recursion nulls `byFieldId`, so the R2/R3/R4
                // `promoteLeaf` gate's `&& byFieldId is null` conjunct is VACUOUSLY TRUE below. That is SAFE
                // because an id-mode nested-within-nested SHAPE is rejected UPSTREAM at ValidateShape /
                // ExpectScalarLeaf (UnsupportedFeature, "a nested type within a nested type … is not supported")
                // BEFORE decode ever recurses here — so an id-mode read never reaches a deep name-mode promote.
                // (585b removed the prior `depth == 0` layer; the upstream shape gate is the sufficient guard.)
                children[i] = await DecodeNode(
                    rowGroup, childNode, field.DataType, rowCount, childContext, budget, byFieldId: null,
                    interiorIds: null, depth + 1, parentMaxRep, parentMaxDef, allowTypeWideningPromotion,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // A scalar struct field. 585b lifted the #546 depth cap on the name-mode promotion gate: a name-mode
            // scalar child promotes at ANY depth (design §2.5, R2). The `byFieldId is null` conjunct is
            // RETAINED so id-mode never promotes (an id-mode struct child already `continue`d above); a deeper
            // name-mode recursion nulls `byFieldId`, so this conjunct is decisive only at the top-level entry.
            // The up-front ValidateShape/ValidateChild (R1) runs before decode and already admits the same
            // leaves, so this decode-side lift keeps the decode self-consistent with validation (design §2.5).
            bool promoteLeaf = allowTypeWideningPromotion && byFieldId is null;
            DataField scalarLeaf = ExpectScalarLeaf(
                childNode, field.DataType, structMaxRep, structMaxDef, childContext, promoteLeaf);
            int scalarPresentFloor = underRepeatedAncestor ? parentMaxDef : 0;
            (MutableColumnVector scalarChild, int[]? scalarDef, int[]? scalarRep, int scalarNumValues) = await ReadScalarLeafAsync(
                rowGroup, scalarLeaf, field.DataType, scalarPresentFloor, budget, promoteLeaf, cancellationToken)
                .ConfigureAwait(false);
            RejectNullInRequiredNestedLeaf(scalarDef, scalarLeaf, field.DataType, field.Nullable);

            if (underRepeatedAncestor)
            {
                // The child materialized one cell per present parent element (present floor = parentMaxDef);
                // its per-owner-cell def is extracted (clamped at structMaxDef) for the null-mask parity guard.
                if (scalarChild.Length != rowCount)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Struct column '{columnName}' field '{DiagnosticText.Sanitize(field.Name)}' reconstructed "
                        + $"{scalarChild.Length} cell(s) but the struct has {rowCount} owner cell(s).");
                }

                fieldDefs[i] = ExtractOwnerCellDefs(
                    scalarDef, scalarRep, scalarNumValues, structMaxDef, parentMaxDef, parentMaxRep, rowCount, childContext);
            }
            else
            {
                // A top-level struct field is one value per row (byte-identical single-level path).
                EnsureStructFieldRowCount(scalarNumValues, rowCount, columnName, field.Name);
                fieldDefs[i] = scalarDef;
            }

            children[i] = scalarChild;
        }

        bool[]? nulls = BuildStructNullMask(fieldDefs, structMaxDef, rowCount, columnName);
        return new StructColumnVector(requested, children, nulls is null ? default : nulls.AsSpan());
    }

    private static void EnsureStructFieldRowCount(int numValues, int rowCount, string columnName, string fieldName)
    {
        if (numValues != rowCount)
        {
            throw DeltaStorageException.CorruptData(
                $"Struct column '{columnName}' field '{DiagnosticText.Sanitize(fieldName)}' declares {numValues} values for a "
                + $"{rowCount}-row group (a struct field must be one value per row).");
        }
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
                    // parent — so there is nothing to cross-check. (A synthesized ABSENT child, #857, likewise
                    // carries a StructPresenceDefs clone under a nullable struct; it is `null` only under a
                    // REQUIRED struct, where structMaxDef == 0 and this guard has already early-returned above.)
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
        int ownerCells,
        string columnName,
        NestedDecodeBudget budget,
        IReadOnlyDictionary<int, DataField>? byFieldId,
        NestedInteriorIds? interiorIds,
        int depth,
        int parentMaxRep,
        int parentMaxDef,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        // Charge the list's OWN per-owner structural arrays against the shared row-group budget before its
        // element leaf is read. The reconstruction builds a transient offsets int[owners+1] + nulls bool[owners],
        // THEN ListColumnVector COPIES the offsets (NestedValidity.CopyValidatedOffsets) and builds a validity
        // bitmap — transient + final coexist at the copy, so charge 2*(int+bool) = ~10 bytes/owner (the complete
        // per-owner structural set; the element child is charged separately). Structure + child stay bounded.
        budget.ChargeStructural(ownerCells, 2 * (sizeof(int) + sizeof(bool)), $"array column '{columnName}'");

        int listMaxDef = fileList.MaxDefinitionLevel;
        int listMaxRep = fileList.MaxRepetitionLevel;
        string elementContext = $"array column '{columnName}' element";

        int[]? def;
        int[]? rep;
        int numValues;
        ColumnVector elements;
        if (requested.ElementType is ArrayType or MapType or StructType)
        {
            // A nested list element (585a): reconstruct THIS level's offsets/nulls from the element subtree's
            // DRIVING leaf (first leaf under fileList.Item), then recurse into the element type. The driving
            // leaf's raw (def, rep) fully describe this repeated level's structure (design §2.2 DecodeList).
            DataField drivingLeaf = FirstDataField(fileList.Item);
            // 585b (R5): the driving leaf is read for STRUCTURE ONLY (def/rep — the child vector is discarded),
            // so read it as its OWN physical type (ParquetTypeMapping.ToDataType), NOT the requested (possibly
            // WIDENED) first-scalar type — the same pattern StructPresenceDefs uses. Under 585b the requested
            // element may widen a deep leaf; a driving read at the requested wide type against the narrower
            // physical leaf would fault the raw typed decode. def/rep are type-agnostic, so the physical read
            // yields identical structure without promotion (design §2.5 driving-leaf gap).
            DataType drivingType = ParquetTypeMapping.ToDataType(drivingLeaf);
            (_, def, rep, numValues) = await ReadScalarLeafAsync(
                rowGroup, drivingLeaf, drivingType, presentFloor: 0, budget, promoteLeaf: false, cancellationToken)
                .ConfigureAwait(false);
            EnsureRepeatedSlotFloor(numValues, ownerCells, columnName, "element");

            var nestedOffsets = new int[checked(ownerCells + 1)];
            var nestedNulls = new bool[ownerCells];
            int elemCount = BuildRepeatedStructure(
                def, rep, numValues, listMaxDef, listMaxRep, parentMaxDef, parentMaxRep, ownerCells,
                nestedOffsets, nestedNulls, columnName);

            // 585b defense-in-depth (#868 Issue 2): the nested-element recurse nulls `byFieldId`, so the R3
            // `promoteLeaf` gate's `&& byFieldId is null` conjunct is vacuously true below — SAFE because an
            // id-mode nested-within-nested shape is rejected UPSTREAM (UnsupportedFeature) before decode
            // recurses here (see the DecodeStruct site for the full rationale).
            elements = await DecodeNode(
                rowGroup, fileList.Item, requested.ElementType, elemCount, elementContext, budget, byFieldId: null,
                interiorIds: null, depth + 1, listMaxRep, listMaxDef, allowTypeWideningPromotion, cancellationToken)
                .ConfigureAwait(false);

            if (elemCount != elements.Length)
            {
                throw DeltaStorageException.CorruptData(
                    $"Array column '{columnName}' reassembled {elemCount} element slot(s) but the element child has "
                    + $"{elements.Length}.");
            }

            return new ListColumnVector(requested, elements, nestedOffsets.AsSpan(), nestedNulls.AsSpan());
        }

        // A scalar list element. #839: in id mode (non-null byFieldId + interiorIds.ElementId) bind the element
        // leaf by its nested.ids field_id within the container's own interior (identity-selection + containment);
        // name/none mode binds the element positionally (fileList.Item). The interior is ALWAYS scalar in id
        // mode (nested-within-nested id mode is rejected upstream), so this is the only id-mode element path —
        // the recursive branch above is never entered when byFieldId/interiorIds are present.
        //
        // 585b lifted the #546 depth cap on the name-mode promotion gate: a name-mode scalar element promotes
        // at ANY depth, so a nested-within-nested narrow element promotes across a widen (design §2.5, R3). The
        // `byFieldId is null` conjunct is RETAINED so an id-mode element never promotes (§9 O1); deeper
        // name-mode recursion nulls `byFieldId`, so it is decisive only at the top-level entry.
        bool promoteLeaf = allowTypeWideningPromotion && byFieldId is null;
        DataField elementLeaf = byFieldId is not null && interiorIds?.ElementId is long elementId
            ? ExpectScalarLeaf(
                ResolveInteriorLeafById(elementId, ListInteriorLeaves(fileList), byFieldId, elementContext),
                requested.ElementType, listMaxRep, listMaxDef, elementContext, promoteLeaf: false)
            : ExpectScalarLeaf(
                fileList.Item, requested.ElementType, listMaxRep, listMaxDef, elementContext, promoteLeaf);

        // The element child collects one cell per PRESENT element slot (a real value OR a null element),
        // skipping the placeholder slots a null/empty list emits (definition level below the list's own level).
        MutableColumnVector scalarElements;
        (scalarElements, def, rep, numValues) = await ReadScalarLeafAsync(
            rowGroup, elementLeaf, requested.ElementType, presentFloor: listMaxDef, budget, promoteLeaf,
            cancellationToken).ConfigureAwait(false);
        RejectNullInRequiredNestedLeaf(def, elementLeaf, requested.ElementType, requested.ContainsNull);
        elements = scalarElements;

        // A1 (defense in depth): every owner cell emits at least one element-level slot (a real element, or a
        // placeholder for a null/empty list), so the element leaf's declared value count is >= the owner count.
        EnsureRepeatedSlotFloor(numValues, ownerCells, columnName, "element");

        var offsets = new int[checked(ownerCells + 1)];
        var nulls = new bool[ownerCells];
        int total = BuildRepeatedStructure(
            def, rep, numValues, listMaxDef, listMaxRep, parentMaxDef, parentMaxRep, ownerCells, offsets, nulls, columnName);
        if (total != elements.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Array column '{columnName}' reassembled {total} element slot(s) but the element child has "
                + $"{elements.Length}.");
        }

        return new ListColumnVector(requested, elements, offsets.AsSpan(), nulls.AsSpan());
    }

    // A1 (defense in depth): a repeated column emits at least one level slot per owner cell (a real occurrence
    // or a null/empty-container placeholder), so its driving leaf's declared value count is >= the owner count.
    // Reject a smaller count BEFORE allocating the owner-scaled offsets/nulls (bounding that allocation, since
    // numValues is ceiling-bounded).
    private static void EnsureRepeatedSlotFloor(int numValues, int ownerCells, string columnName, string kind)
    {
        if (numValues < ownerCells)
        {
            throw DeltaStorageException.CorruptData(
                $"{(string.Equals(kind, "element", StringComparison.Ordinal) ? "Array" : "Map")} column '{columnName}' "
                + $"declares {numValues} {kind} slot(s) for {ownerCells} owner cell(s), but a repeated column emits "
                + "at least one level slot per owner cell.");
        }
    }

    // ----- map (3-level MAP) -----

    private static async ValueTask<ColumnVector> ReadMapAsync(
        ParquetRowGroupReader rowGroup,
        PqMapField fileMap,
        MapType requested,
        int ownerCells,
        string columnName,
        NestedDecodeBudget budget,
        IReadOnlyDictionary<int, DataField>? byFieldId,
        NestedInteriorIds? interiorIds,
        int depth,
        int parentMaxRep,
        int parentMaxDef,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        // Charge the map's OWN per-owner structural arrays against the shared row-group budget before its
        // key/value leaves are read. Like a list, the reconstruction builds a transient entry-offsets
        // int[owners+1] + nulls bool[owners], THEN MapColumnVector COPIES the offsets and builds a validity
        // bitmap — transient + final coexist, so charge 2*(int+bool) = ~10 bytes/owner (both child leaves are
        // charged separately). Structure + children stay cumulatively bounded.
        budget.ChargeStructural(ownerCells, 2 * (sizeof(int) + sizeof(bool)), $"map column '{columnName}'");

        // The map key/value transposition + canonical-name + required-key guards run at EVERY map node (§2.6).
        EnsureCanonicalMapChildNames(fileMap, columnName);
        EnsureRequiredMapKey(fileMap, columnName);
        int mapMaxDef = fileMap.MaxDefinitionLevel;
        int mapMaxRep = fileMap.MaxRepetitionLevel;
        bool nestedKey = requested.KeyType is ArrayType or MapType or StructType;
        bool nestedValue = requested.ValueType is ArrayType or MapType or StructType;

        // 585b lifted the #546 depth cap on the name-mode promotion gate: a name-mode scalar key/value promotes
        // at ANY depth (design §2.5, R4). The `byFieldId is null` conjunct is RETAINED so an id-mode key/value
        // never promotes (§9 O1); deeper name-mode recursion nulls `byFieldId`, so it is decisive only at the
        // top-level entry.
        bool promoteLeaf = allowTypeWideningPromotion && byFieldId is null;

        // Keys drive the entry structure (the key subtree's driving leaf). A required key's max definition level
        // equals the map's own level, so every referenced key slot carries a real value — keys are never null,
        // matching MapType's structural invariant.
        MutableColumnVector? scalarKeys;
        int[]? keyDef;
        int[]? keyRep;
        int keyNumValues;
        if (nestedKey)
        {
            DataField keyDriving = FirstDataField(fileMap.Key);
            // 585b (R5): driving leaf read for STRUCTURE ONLY — read as its own physical type
            // (ParquetTypeMapping.ToDataType), not the requested (possibly widened) first-scalar type
            // (design §2.5 driving-leaf gap).
            DataType keyDeep = ParquetTypeMapping.ToDataType(keyDriving);
            (_, keyDef, keyRep, keyNumValues) = await ReadScalarLeafAsync(
                rowGroup, keyDriving, keyDeep, presentFloor: 0, budget, promoteLeaf: false, cancellationToken)
                .ConfigureAwait(false);
            scalarKeys = null;
        }
        else
        {
            // #839: in id mode bind the key leaf by its nested.ids field_id within the container's own interior
            // (identity-selection separates key from value, §2.5 step 3); name/none mode binds it positionally
            // (fileMap.Key). Either way ExpectScalarLeaf type/level-validates the selected leaf. The interior is
            // ALWAYS scalar in id mode (nested key/value id mode is rejected upstream), so this non-nested
            // branch is the only id-mode key path.
            string keyContext = $"map column '{columnName}' key";
            DataField keyLeaf = byFieldId is not null && interiorIds?.KeyId is long keyId
                ? ExpectScalarLeaf(
                    ResolveInteriorLeafById(keyId, MapInteriorLeaves(fileMap), byFieldId, keyContext),
                    requested.KeyType, mapMaxRep, mapMaxDef, keyContext, promoteLeaf: false)
                : ExpectScalarLeaf(
                    fileMap.Key, requested.KeyType, mapMaxRep, mapMaxDef, keyContext, promoteLeaf);
            (scalarKeys, keyDef, keyRep, keyNumValues) = await ReadScalarLeafAsync(
                rowGroup, keyLeaf, requested.KeyType, presentFloor: mapMaxDef, budget, promoteLeaf, cancellationToken)
                .ConfigureAwait(false);
        }

        // The value subtree is parallel to the key subtree (same repeated key_value group), so their driving
        // leaves share a repetition stream AND agree, slot-by-slot, on entry presence.
        MutableColumnVector? scalarValues;
        int[]? valueDef;
        int[]? valueRep;
        if (nestedValue)
        {
            DataField valueDriving = FirstDataField(fileMap.Value);
            // 585b (R5): driving leaf read for STRUCTURE ONLY — read as its own physical type
            // (ParquetTypeMapping.ToDataType), not the requested (possibly widened) first-scalar type
            // (design §2.5 driving-leaf gap).
            DataType valueDeep = ParquetTypeMapping.ToDataType(valueDriving);
            (_, valueDef, valueRep, _) = await ReadScalarLeafAsync(
                rowGroup, valueDriving, valueDeep, presentFloor: 0, budget, promoteLeaf: false, cancellationToken)
                .ConfigureAwait(false);
            scalarValues = null;
        }
        else
        {
            // #839: in id mode bind the value leaf by its DISTINCT nested.ids field_id within the container's
            // own interior; name/none mode binds it positionally (fileMap.Value). Interior is always scalar in
            // id mode, so this non-nested branch is the only id-mode value path.
            string valueContext = $"map column '{columnName}' value";
            DataField valueLeaf = byFieldId is not null && interiorIds?.ValueId is long valueId
                ? ExpectScalarLeaf(
                    ResolveInteriorLeafById(valueId, MapInteriorLeaves(fileMap), byFieldId, valueContext),
                    requested.ValueType, mapMaxRep, mapMaxDef, valueContext, promoteLeaf: false)
                : ExpectScalarLeaf(
                    fileMap.Value, requested.ValueType, mapMaxRep, mapMaxDef, valueContext, promoteLeaf);
            (scalarValues, valueDef, valueRep, _) = await ReadScalarLeafAsync(
                rowGroup, valueLeaf, requested.ValueType, presentFloor: mapMaxDef, budget, promoteLeaf,
                cancellationToken).ConfigureAwait(false);
            RejectNullInRequiredNestedLeaf(valueDef, valueLeaf, requested.ValueType, requested.ValueContainsNull);
        }

        // F1/R6/R7: the KEY and VALUE cross-leaf parity checks compare the two leaves slot-for-slot, so they
        // apply ONLY when key AND value are SCALAR siblings in the same key_value group (the single-level 3-level
        // map contract — byte-preserved). When either side is NESTED, its driving leaf is DEEPER (carries extra
        // repetition beyond the map's own level), so a raw slot-for-slot comparison is meaningless; the
        // recursion's own owner-cell reconstruction (each nested child is decoded with ownerCells = entryCount
        // and re-checks that count) supplies the structural cross-check instead.
        if (!nestedKey && !nestedValue)
        {
            // F1: the value child is consumed positionally against the KEY-driven entry structure. A divergent
            // per-entry distribution would silently mis-pair — reject any rep divergence BEFORE reconstructing.
            ValidateParallelRepetition(keyRep, valueRep, columnName);

            // R6/R7: key and value leaves must agree, slot-by-slot, on entry presence at the map's own level.
            ValidateParallelDefinition(keyDef, valueDef, mapMaxDef, columnName);
        }

        // A1 (defense in depth): the key subtree drives the entry structure and emits at least one level slot
        // per owner cell (a placeholder for a null/empty map).
        EnsureRepeatedSlotFloor(keyNumValues, ownerCells, columnName, "entry");

        var offsets = new int[checked(ownerCells + 1)];
        var nulls = new bool[ownerCells];
        int entryCount = BuildRepeatedStructure(
            keyDef, keyRep, keyNumValues, mapMaxDef, mapMaxRep, parentMaxDef, parentMaxRep, ownerCells, offsets, nulls, columnName);

        // 585b defense-in-depth (#868 Issue 2): the key/value recurses below null `byFieldId`, so the R4
        // `promoteLeaf` gate's `&& byFieldId is null` conjunct is vacuously true — SAFE because an id-mode
        // nested-within-nested shape is rejected UPSTREAM (UnsupportedFeature) before decode recurses here
        // (see the DecodeStruct site for the full rationale).
        ColumnVector keys = nestedKey
            ? await DecodeNode(
                rowGroup, fileMap.Key, requested.KeyType, entryCount, $"map column '{columnName}' key", budget,
                byFieldId: null, interiorIds: null, depth + 1, mapMaxRep, mapMaxDef, allowTypeWideningPromotion,
                cancellationToken).ConfigureAwait(false)
            : scalarKeys!;
        ColumnVector values = nestedValue
            ? await DecodeNode(
                rowGroup, fileMap.Value, requested.ValueType, entryCount, $"map column '{columnName}' value", budget,
                byFieldId: null, interiorIds: null, depth + 1, mapMaxRep, mapMaxDef, allowTypeWideningPromotion,
                cancellationToken).ConfigureAwait(false)
            : scalarValues!;

        if (keys.Length != values.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' reassembled {keys.Length} key(s) but {values.Length} value(s); a "
                + "map's key and value children must be parallel.");
        }

        if (entryCount != keys.Length)
        {
            throw DeltaStorageException.CorruptData(
                $"Map column '{columnName}' reassembled {entryCount} entry slot(s) but the key child has {keys.Length}.");
        }

        return new MapColumnVector(requested, keys, values, offsets.AsSpan(), nulls.AsSpan());
    }

    // Reconstructs the per-owner-cell offsets + null flags for ONE repeated level (list/map) from its driving
    // leaf's definition + repetition levels, distinguishing null container / empty container / present
    // container — at ARBITRARY depth (design §2.2, worked trace §2.4). Returns the total number of PRESENT
    // child cells at THIS level (== offsets[^1]), so the caller can cross-check the reassembled child length.
    //
    // Level parameters:
    //   thisMaxDef / thisMaxRep  — the repeated node's OWN Dremel levels (this level's container).
    //   parentMaxDef / parentMaxRep — the IMMEDIATE parent container's levels (the owner-cell boundary).
    //
    // The generalization over the single-level (#571) reader: an owner cell opens at `rep <= parentMaxRep`
    // (the parent's element/row boundary — NOT the hard-wired `rep == 0`), and a new occurrence at THIS level
    // counts iff `rep <= thisMaxRep && def >= thisMaxDef`. Slots with `rep > thisMaxRep` belong to a DEEPER
    // container (a child recursion consumes them) and are excluded from this level's count. For a top-level
    // single repeated level (parentMaxRep = parentMaxDef = 0, thisMaxRep = 1) this reduces EXACTLY to the old
    // `rep == 0` owner boundary + ungated `def >= thisMaxDef` element count — byte-identical, no #571 regression.
    //
    // Internal so a direct unit test can pin the F2 state-transition guard with crafted def/rep streams that
    // the released Parquet.Net write door (which derives definition levels from value nullability, never below
    // the element's own null level) cannot author.
    internal static int BuildRepeatedStructure(
        int[]? def, int[]? rep, int numValues, int thisMaxDef, int thisMaxRep, int parentMaxDef, int parentMaxRep,
        int ownerCells, int[] offsets, bool[] nulls, string columnName)
    {
        // A repeated column always carries both level streams (its max repetition and definition levels are
        // >= 1); their absence is a malformed footer.
        if (def is null || rep is null)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested column '{columnName}' is missing the repetition/definition levels required to "
                + "reconstruct its structure.");
        }

        // A container that is empty (not null) sits ONE level below its own present level: def == thisMaxDef-1
        // is the empty-container marker; def < thisMaxDef-1 is a NULL container (or an ABSENT parent, handled by
        // the owner-open gate below).
        int emptyContainerDef = thisMaxDef - 1;
        int owner = -1;
        int elements = 0;
        bool ownerComplete = false; // F2: the current owner opened as an empty/null container -> no continuation
        offsets[0] = 0;
        for (int i = 0; i < numValues; i++)
        {
            int d = def[i];
            int r = rep[i];

            if (r <= parentMaxRep)
            {
                // A parent-boundary slot: it opens the NEXT owner cell (a new parent element/entry/row). But
                // the parent element itself may be ABSENT here (def < parentMaxDef) — a placeholder emitted by a
                // null/empty grandparent container — in which case NO owner cell exists at this position and the
                // slot is consumed without opening one (it belongs to the parent level's own bookkeeping).
                if (i == 0 && r != 0)
                {
                    // The very first level slot must carry repetition level 0 (the record boundary); a leading
                    // non-zero is corrupt.
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' begins with a non-zero repetition level (corrupt levels).");
                }

                if (d < parentMaxDef)
                {
                    // The parent element is absent at this slot (its own container is null/empty). No owner
                    // cell opens here.
                    continue;
                }

                // The parent element is present: open a new owner cell for it. Close the previous owner's
                // window first.
                if (owner >= 0)
                {
                    offsets[owner + 1] = elements;
                }

                owner++;
                if (owner >= ownerCells)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' declares more owner cells than the expected {ownerCells}.");
                }

                // This owner's container: null if its def is below even the empty-container marker; empty if it
                // sits exactly at the marker; present (element-bearing) at/above its own level.
                nulls[owner] = d < emptyContainerDef;
                ownerComplete = d < thisMaxDef;
            }
            else if (r <= thisMaxRep)
            {
                // A continuation of the CURRENT owner at THIS repeated level (a new occurrence in the same
                // container). Legal only when the owner is an active element-bearing container AND this slot is
                // itself an occurrence at/above this level's own definition. Continuing an empty/null container,
                // or a placeholder masquerading as a continuation, would reconstruct a PHANTOM occurrence.
                if (owner < 0)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' begins with a non-zero repetition level (corrupt levels).");
                }

                if (ownerComplete || d < thisMaxDef)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' continues owner cell {owner} (repetition level {r}) after an "
                        + $"empty/null-container marker (definition level {d}); an empty or null repeated "
                        + "container has no continuation.");
                }
            }
            // else: r > thisMaxRep — a continuation at a DEEPER repeated level. It belongs to a child container
            // nested inside this level's current occurrence; the child recursion consumes it. Excluded from THIS
            // level's owner boundaries AND occurrence count.

            // Count an occurrence at THIS level: a slot at/above this level's own definition that is NOT the
            // business of a deeper level (rep <= thisMaxRep). This is the corrected count (design §2.4): for
            // array<array<int>> the OUTER level counts INNER-LIST occurrences (rep <= 1), NOT leaf values.
            if (r <= thisMaxRep && d >= thisMaxDef)
            {
                elements++;
            }
        }

        if (owner >= 0)
        {
            offsets[owner + 1] = elements;
        }

        if (owner + 1 != ownerCells)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested column '{columnName}' reconstructed {owner + 1} owner cell(s) but the parent declares {ownerCells}.");
        }

        return elements;
    }

    // Extracts a nested struct-child's per-owner-cell definition stream (clamped at the struct's own level) from
    // its driving leaf's raw (def, rep). Used to feed the cross-field null-mask parity guard (BuildStructNullMask)
    // for a struct child that is ITSELF nested (a list/map/struct with no scalar leaf of its own). Each owner
    // cell's def is the opening slot's def clamped at structMaxDef: at/above => the struct is present at that
    // owner; below => the struct is absent there. Owner cells open at the parent boundary (rep <= parentMaxRep &&
    // def >= parentMaxDef), matching BuildRepeatedStructure's owner discipline. A driving leaf with no repeated
    // ancestor carries a null rep (max repetition 0) — every slot is then an owner (repetition level 0); a fully
    // required path carries a null def — every slot is then present at its own max.
    // internal (not private) so the parity-under-repeated-ancestor clamp is directly unit-testable (#857 R2,
    // Quality F1): a null struct owner cell under a repeated ancestor must report def < structMaxDef, not be
    // over-reported as present.
    internal static int[] ExtractOwnerCellDefs(
        int[]? def, int[]? rep, int numValues, int structMaxDef, int parentMaxDef, int parentMaxRep,
        int ownerCells, string columnName)
    {
        var owned = new int[ownerCells];
        int owner = -1;
        for (int i = 0; i < numValues; i++)
        {
            int r = rep is null ? 0 : rep[i];
            int d = def is null ? structMaxDef : def[i];
            if (r <= parentMaxRep)
            {
                if (i == 0 && r != 0)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' begins with a non-zero repetition level (corrupt levels).");
                }

                if (d < parentMaxDef)
                {
                    continue; // parent element absent — no owner cell here
                }

                owner++;
                if (owner >= ownerCells)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Nested column '{columnName}' declares more owner cells than the expected {ownerCells}.");
                }

                owned[owner] = Math.Min(d, structMaxDef);
            }
        }

        if (owner + 1 != ownerCells)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested column '{columnName}' reconstructed {owner + 1} owner cell(s) but the parent declares {ownerCells}.");
        }

        return owned;
    }

    // ----- leaf decode (raw Dremel -> child vector) -----

    // Reads one scalar leaf's packed values + definition/repetition levels and materializes the child cells,
    // returning the child plus the raw level streams for the caller's structural pass. Dispatches the physical
    // read type T and the value->lane conversion per requested scalar type (mirroring the flat reader's
    // ReadColumnAsync). When <paramref name="promoteLeaf"/> is set and the leaf's PHYSICAL type is a NARROWER
    // Delta-sanctioned widening of the requested scalar, reads the narrow values and PROMOTES each into the
    // requested (wide) lane — the exact per-leaf type widening of #546 (the scalar promotion the flat path
    // applies in ReadPromotedColumnAsync, now per nested leaf, gated by the container-depth-composed leaf gate).
    private static ValueTask<(MutableColumnVector Child, int[]? Def, int[]? Rep, int NumValues)> ReadScalarLeafAsync(
        ParquetRowGroupReader rowGroup,
        DataField leaf,
        DataType scalarType,
        int presentFloor,
        NestedDecodeBudget budget,
        bool promoteLeaf,
        CancellationToken cancellationToken)
    {
        // Per-leaf type-widening promotion (#546). The same allowlist the flat path uses
        // (TypeWidening.IsSanctionedWidening, including the integral→decimal fit guard), gated identically by
        // the caller's depth-composed promoteLeaf. ValidateLeafPhysicalType (up-front) already proved the pair
        // is a sanctioned widening; this re-check disambiguates a same-physical pair whose logical types differ
        // but is NOT a widening (a native micros leaf read as timestamp_ntz has physical timestamp ≠ requested
        // timestamp_ntz yet takes the identity micros read, not promotion — #533).
        if (promoteLeaf
            && ParquetTypeMapping.TryToDataType(leaf, out DataType? physicalType)
            && !physicalType.Equals(scalarType)
            && TypeWidening.IsSanctionedWidening(physicalType, scalarType))
        {
            return ReadPromotedLeafAsync(
                rowGroup, leaf, physicalType, scalarType, presentFloor, budget, cancellationToken);
        }

        switch (scalarType)
        {
            case BooleanType:
                return ReadLeafAsync<bool>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case ByteType:
                return ReadLeafAsync<sbyte>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(unchecked((byte)x)), cancellationToken);
            case ShortType:
                return ReadLeafAsync<short>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case IntegerType:
                return ReadLeafAsync<int>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case LongType:
                return ReadLeafAsync<long>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case FloatType:
                return ReadLeafAsync<float>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case DoubleType:
                return ReadLeafAsync<double>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(x), cancellationToken);
            case DateType:
                return ReadLeafAsync<DateTime>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochDay(x)), cancellationToken);
            case TimestampType or TimestampNtzType:
                return ReadLeafAsync<DateTime>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochMicros(x)), cancellationToken);
            case DecimalType decimalType:
                ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(decimalType);
                return ReadLeafAsync<decimal>(rowGroup, leaf, scalarType, presentFloor, budget,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, decimalType, x, factors), cancellationToken);
            case StringType:
                return ReadLeafAsync<ReadOnlyMemory<char>>(rowGroup, leaf, scalarType, presentFloor, budget,
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
                return ReadLeafAsync<ReadOnlyMemory<byte>>(rowGroup, leaf, scalarType, presentFloor, budget,
                    static (v, x) => v.AppendBytes(x.Span), cancellationToken);
            default:
                // #705 predicate: every atomic leaf kind is a case above, so this arm fires for a scalar the
                // decoder does not decode here (or, defensively, a non-atomic type reaching the leaf path).
                // DescribeType bounds either — a raw SimpleString would recurse for the non-atomic case.
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet nested read for leaf type '{DiagnosticText.DescribeType(scalarType)}' is not supported.");
        }
    }

    // Reads a nested leaf's NARROW physical values and promotes each into the requested WIDE lane (#546) — the
    // per-leaf mirror of ParquetFileReader.ReadPromotedColumnAsync. The dispatch is by (physical, requested);
    // ValidateLeafPhysicalType already proved the pair is a sanctioned widening, so every arm is a lossless
    // promotion: an integral sign-extend (byte/short/int → wider integral), float→double, a grow-only decimal
    // rescale, a cross-family integral→double / integral→decimal (#535), or date→timestamp_ntz (#533). The
    // child vector ReadLeafAsync allocates is the REQUESTED (wide) type, so every converting append targets the
    // requested storage width. The decimal/integral→decimal arms capture the requested decimal + hoisted scale
    // factors (as the exact-match decimal case above already does); the rest are non-capturing.
    private static ValueTask<(MutableColumnVector Child, int[]? Def, int[]? Rep, int NumValues)> ReadPromotedLeafAsync(
        ParquetRowGroupReader rowGroup,
        DataField leaf,
        DataType physicalType,
        DataType requestedScalar,
        int presentFloor,
        NestedDecodeBudget budget,
        CancellationToken cancellationToken)
    {
        if (requestedScalar is DecimalType requestedDecimal)
        {
            ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(requestedDecimal);

            // decimal(p,s) → decimal(p',s') grow-only: read at the file's scale, rescale into the requested lane.
            if (physicalType is DecimalType)
            {
                return ReadLeafAsync<decimal>(rowGroup, leaf, requestedDecimal, presentFloor, budget,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, requestedDecimal, x, factors), cancellationToken);
            }

            // Cross-family integral → decimal (#535): read the narrow integral and widen into the decimal lane.
            // ValidateLeafPhysicalType proved the decimal holds the full integral range (its integer-digit
            // capacity p − s ≥ the source's Parquet-physical digits), so AppendDecimal never truncates.
            return physicalType switch
            {
                ByteType => ReadLeafAsync<sbyte>(rowGroup, leaf, requestedDecimal, presentFloor, budget,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, requestedDecimal, x, factors), cancellationToken),
                ShortType => ReadLeafAsync<short>(rowGroup, leaf, requestedDecimal, presentFloor, budget,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, requestedDecimal, x, factors), cancellationToken),
                IntegerType => ReadLeafAsync<int>(rowGroup, leaf, requestedDecimal, presentFloor, budget,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, requestedDecimal, x, factors), cancellationToken),
                _ => ReadLeafAsync<long>(rowGroup, leaf, requestedDecimal, presentFloor, budget,
                    (v, x) => ParquetTypeMapping.AppendDecimal(v, requestedDecimal, x, factors), cancellationToken),
            };
        }

        if (requestedScalar is DoubleType)
        {
            // float → double, or cross-family byte/short/int → double (#535). long → double is lossy and NOT
            // sanctioned, so the physical is never long here.
            return physicalType switch
            {
                FloatType => ReadLeafAsync<float>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                    static (v, x) => v.AppendValue((double)x), cancellationToken),
                ByteType => ReadLeafAsync<sbyte>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                    static (v, x) => v.AppendValue((double)x), cancellationToken),
                ShortType => ReadLeafAsync<short>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                    static (v, x) => v.AppendValue((double)x), cancellationToken),
                _ => ReadLeafAsync<int>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                    static (v, x) => v.AppendValue((double)x), cancellationToken),
            };
        }

        if (requestedScalar is TimestampNtzType && physicalType is DateType)
        {
            // date → timestamp_ntz (#533): promote each epoch-day to epoch-micros at midnight of the date
            // (days × MicrosPerDay, timezone-less — no session offset), mirroring the flat path. The multiply
            // is `checked`: any epoch-day a Parquet DATE can materialize keeps the product ≪ long.MaxValue, so
            // it never throws in practice but fails loud (→ CorruptData) rather than wrapping on a hostile value.
            return ReadLeafAsync<DateTime>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                static (v, x) => v.AppendValue(checked((long)ParquetTypeMapping.DateTimeToEpochDay(x) * MicrosPerDay)),
                cancellationToken);
        }

        // Integral widening: byte(sbyte) → short → int → long. The requested lane is the upcast target; the
        // file's physical width is the read buffer's element type.
        switch (requestedScalar)
        {
            case ShortType:
                // Only byte → short reaches here (a sanctioned narrower integral).
                return ReadLeafAsync<sbyte>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                    static (v, x) => v.AppendValue((short)x), cancellationToken);

            case IntegerType:
                return physicalType is ByteType
                    ? ReadLeafAsync<sbyte>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                        static (v, x) => v.AppendValue((int)x), cancellationToken)
                    : ReadLeafAsync<short>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                        static (v, x) => v.AppendValue((int)x), cancellationToken);

            case LongType:
                return physicalType switch
                {
                    ByteType => ReadLeafAsync<sbyte>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                        static (v, x) => v.AppendValue((long)x), cancellationToken),
                    ShortType => ReadLeafAsync<short>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                        static (v, x) => v.AppendValue((long)x), cancellationToken),
                    _ => ReadLeafAsync<int>(rowGroup, leaf, requestedScalar, presentFloor, budget,
                        static (v, x) => v.AppendValue((long)x), cancellationToken),
                };

            default:
                // Unreachable: ValidateLeafPhysicalType only admits the sanctioned widenings handled above.
                throw DeltaStorageException.SchemaMismatch(
                    $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}': cannot promote physical type "
                    + $"'{DiagnosticText.DescribeType(physicalType)}' to requested "
                    + $"'{DiagnosticText.DescribeType(requestedScalar)}'.");
        }
    }

    private static async ValueTask<(MutableColumnVector Child, int[]? Def, int[]? Rep, int NumValues)> ReadLeafAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField leaf,
        DataType elementType,
        int presentFloor,
        NestedDecodeBudget budget,
        Action<MutableColumnVector, T> append,
        CancellationToken cancellationToken)
        where T : struct
    {
        int numValues = LeafNumValues(
            rowGroup, leaf, budget, Unsafe.SizeOf<T>(), variableWidth: elementType is StringType or BinaryType);
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
            // CorruptData contract (mirrors the flat reader's ReadValueAsync). The file-derived leaf path is echoed
            // through DiagnosticText.Sanitize (#665) — it is bounded to the requested schema when reachable (nested
            // reads under column-mapping id mode fail closed before decode), so this closes only the residual
            // log-injection vector. Named by the requested leaf type
            // so the message is accurate for whichever leaf raised it (not hard-coded to date/time).
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}' of type '{DiagnosticText.DescribeType(elementType)}' has a physical value outside "
                + "its representable range.", ex);
        }

        // A5: reject any reconstructed Dremel level outside its declared range BEFORE interpreting it. A
        // crafted level would otherwise be silently coerced — a definition level above the field max reads as a
        // spurious present-null, a repetition level above the max mis-nests a row — a WRONG (not merely failed)
        // read. The value/structure passes below can then trust the levels.
        ValidateLevelRange(def, leaf.MaxDefinitionLevel, leaf.Path.ToString(), "definition");
        ValidateLevelRange(rep, leaf.MaxRepetitionLevel, leaf.Path.ToString(), "repetition");

        var child = CreateLeafVector(
            elementType, Math.Max(numValues, 1),
            // Defense-in-depth twin of the absent-child site (#863): ReadLeafAsync<T> is dispatched only for
            // SUPPORTED physical scalars (T : struct), and a NullType/void leaf against a PRESENT physical leaf
            // fails closed earlier (ValidateLeafPhysicalType SchemaMismatch / the leaf-decode UnsupportedFeature
            // dispatch default), so ColumnVectors.Create never rejects `elementType` here today — CreateLeafVector
            // keeps this site fail-closed-typed if that ever changes.
            $"nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}'");
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

    // #813: a required (non-nullable) nested leaf lane must not silently absorb a LEAF-ATTRIBUTABLE null — a
    // null where every ancestor container is PRESENT and only the leaf's own value is null. In Dremel terms
    // that is a physically-OPTIONAL leaf (<paramref name="leaf"/>.<c>IsNullable</c>) whose reconstructed
    // definition level is exactly one below its max (<c>MaxDefinitionLevel - 1</c>): the def level truncates at
    // the FIRST undefined optional from the root, so a value one below max means every shallower optional is
    // defined and only the DEEPEST — the leaf's own — is not. A LOWER level is an ANCESTOR null (a null struct
    // / absent list element / null map entry), which is legitimate and must NOT be rejected. Extends the flat
    // guard <c>RejectNullInRequiredLane</c> (#807) Dremel-aware into the nested path, covering ALL leaf types
    // (the flat schema-level check has no nested analogue — <see cref="NestedParquetColumnReader"/>'s leaf
    // nullability was advisory). Path-free (#653/#665): the message names only the sanitized leaf path + type.
    private static void RejectNullInRequiredNestedLeaf(
        int[]? def, DataField leaf, DataType requestedType, bool requestedNullable)
    {
        // A nullable request accepts nulls; a physically-REQUIRED leaf can only be nulled by an ancestor (never
        // leaf-attributable); no definition stream means a fully-required path (no nulls at all).
        if (requestedNullable || !leaf.IsNullable || def is null)
        {
            return;
        }

        int leafNullLevel = leaf.MaxDefinitionLevel - 1;
        foreach (int d in def)
        {
            if (d == leafNullLevel)
            {
                throw DeltaStorageException.SchemaMismatch(
                    $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}': a required (non-nullable) "
                    + $"{DiagnosticText.DescribeType(requestedType)} leaf materialized a NULL from a physically "
                    + "OPTIONAL Parquet column while every ancestor container was present; the read-time "
                    + "required-lane guard (#813, extending #807) rejects rather than silently null-fill a "
                    + "non-nullable nested lane.");
            }
        }
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
                    $"Nested leaf '{DiagnosticText.Sanitize(leafPath)}' has a {kind} level {levels[i]} outside the valid range "
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
        // #683 message hygiene: `columnName` is a pure DIAGNOSTIC LABEL (never a lookup key) that is echoed
        // into every message this reader raises, including the recursive sub-labels built from it. Sanitize it
        // ONCE at the entry point (control-char strip + length cap) so a crafted/foreign schema name cannot
        // inject line breaks into a structured-log sink or render unbounded.
        columnName = DiagnosticText.Sanitize(columnName);

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
    // Validates that a map's key and value leaves AGREE, slot-by-slot, on their DEFINITION-level state — the
    // definition-level analog of ValidateParallelRepetition (R6/R7). A well-formed 3-level map emits, for every
    // slot, key and value definition levels that agree on the slot's meaning. There are two co-indexed
    // dimensions:
    //   1. Entry-presence (R6): both leaves must sit at/above the map's own level (a present entry) or both
    //      below it (a non-entry placeholder). Compare presence (>= mapMaxDef), NOT raw equality, because a
    //      present-but-null value legitimately carries a HIGHER def than the required key (distinguishing null
    //      vs non-null above the map's own level). The reader front-fills the value child from the slots where
    //      valueDef >= mapMaxDef and pairs it positionally against the KEY-driven entry structure, so a stream
    //      where the leaves disagree on presence (e.g. keyDef=[2,1] / valueDef=[1,2]) would mis-pair values.
    //   2. Container-state (R7): when BOTH sit below mapMaxDef the slot is a non-entry placeholder whose
    //      SPECIFIC state must still agree — null-map (def 0) vs empty-map (def 1). A crafted stream where the
    //      key says empty and the value says null (keyDef=[1] / valueDef=[0]) is self-contradictory; fail
    //      closed rather than silently resolve it to the key's (authoritative) view.
    // Together with ValidateParallelRepetition (identical rep streams) these fully constrain the two co-indexed
    // map leaves: both the entry set AND the non-entry container state must agree at every slot. Fail closed on
    // any length, presence, or container-state disagreement, BEFORE reconstruction. Internal so the guard can
    // be pinned by a direct unit test (the released Parquet.Net write door derives definition levels from value
    // nullability, so a key/value definition divergence is not authorable end-to-end).
    internal static void ValidateParallelDefinition(int[]? keyDef, int[]? valueDef, int mapMaxDef, string columnName)
    {
        // #683 message hygiene: `columnName` is a pure DIAGNOSTIC LABEL (never a lookup key) that is echoed
        // into every message this reader raises, including the recursive sub-labels built from it. Sanitize it
        // ONCE at the entry point (control-char strip + length cap) so a crafted/foreign schema name cannot
        // inject line breaks into a structured-log sink or render unbounded.
        columnName = DiagnosticText.Sanitize(columnName);

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
            bool keyEntry = keyDef[i] >= mapMaxDef;
            bool valueEntry = valueDef[i] >= mapMaxDef;
            if (keyEntry != valueEntry)
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{columnName}' key and value definition levels disagree on entry presence at "
                    + $"slot {i} (key {keyDef[i]}, value {valueDef[i]}, map level {mapMaxDef}); the value stream "
                    + "would mis-pair entries across the map.");
            }

            // R7 container-state parity: when BOTH sit below the map's own level the slot is a non-entry
            // placeholder, but the SPECIFIC state must still agree — null-map (def 0) vs empty-map (def 1). A
            // crafted stream where the key says empty and the value says null (or vice-versa) is
            // self-contradictory; fail closed rather than silently resolve it to the key's authoritative view.
            if (!keyEntry && keyDef[i] != valueDef[i])
            {
                throw DeltaStorageException.CorruptData(
                    $"Map column '{columnName}' key and value definition levels disagree on container state at "
                    + $"slot {i} (key {keyDef[i]}, value {valueDef[i]}, map level {mapMaxDef}); a non-entry row "
                    + "(null map vs empty map) must be identical on both leaves.");
            }
        }
    }
    // The ROW-GROUP-WIDE eager-decode budget shared across EVERY nested column of one row-group read (and
    // every leaf + container structure within each): the reader's MaxRowGroupDecodedBytes ceiling, drawn down
    // by each leaf's reconstruction transient AND each container's own per-row structural arrays (list/map
    // offsets + null masks, struct null masks), so the COMBINED reconstruction peak stays bounded. The flat
    // reader's EnsureDecodeCeiling sums the projected chunks' declared UncompressedBytes CUMULATIVELY across
    // all columns; this mirrors that for the nested reconstruction overhead (the #570 child ColumnVector each
    // leaf materializes, plus the container structure) which the declared-bytes aggregate does not model.
    // Per-leaf-only, or a fresh budget per column, would let a K-field struct — or K nested columns — allocate
    // up to K x the ceiling. Created ONCE per row group by ParquetFileReader and passed to each ReadAsync.
    internal sealed class NestedDecodeBudget
    {
        private long _remaining;

        public NestedDecodeBudget(long ceiling)
        {
            Ceiling = ceiling;
            _remaining = ceiling;
        }

        public long Ceiling { get; }

        // Draws this leaf's transient (payloadBytes + numValues * perSlotBytes) down from the shared budget,
        // failing closed if the CUMULATIVE total across the row group's nested columns breaches the ceiling.
        // Overflow-safe: the payload is subtracted first (a saturated payload underflows to < 0 and is caught),
        // then the per-slot product is bounded by division against what REMAINS, so the subsequent subtraction
        // cannot drive _remaining below 0 or overflow (numValues * perSlotBytes <= _remaining <= Ceiling).
        public void Charge(long payloadBytes, long numValues, long perSlotBytes, DataField leaf)
        {
            _remaining -= payloadBytes;
            if (_remaining < 0 || (perSlotBytes > 0 && numValues > _remaining / perSlotBytes))
            {
                throw DeltaStorageException.CorruptData(
                    $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}' declares {numValues} values, whose eager decode would exceed the "
                    + $"{Ceiling}-byte ceiling.");
            }

            _remaining -= numValues * perSlotBytes;
        }

        // Draws a nested container's OWN per-row reconstructed structural arrays down from the shared budget so
        // they are charged alongside the leaf values rather than escaping the ceiling. The caller passes the
        // COMPLETE per-row structural width — for a list/map both the TRANSIENT offsets+nulls and the final
        // copied offsets + validity bitmap the ColumnVector materializes (they coexist at the copy); for a
        // struct the transient null mask + final validity bitmap. rowCount is already <= int.MaxValue and
        // perRowBytes is a tiny constant, so the product cannot overflow; fails closed on breach.
        public void ChargeStructural(int rowCount, long perRowBytes, string context)
        {
            _remaining -= rowCount * perRowBytes;
            if (_remaining < 0)
            {
                throw DeltaStorageException.CorruptData(
                    $"{context} reconstructs a {rowCount}-row structure whose eager decode would exceed the "
                    + $"{Ceiling}-byte ceiling.");
            }
        }
    }

    // buffers are allocated so a crafted NumValues cannot drive an out-of-memory allocation. Unlike the flat
    // path (one value per row, bounded by the row count), a repeated leaf can declare more values than rows,
    // so this bounds the ACTUAL transient (values + definition + repetition buffers) by the leaf's own count,
    // charged against the COLUMN-WIDE budget so the leaves' COMBINED reconstruction peak stays under the ceiling.
    private static int LeafNumValues(
        ParquetRowGroupReader rowGroup, DataField leaf, NestedDecodeBudget budget, int elementWidth, bool variableWidth)
    {
        global::Parquet.Meta.ColumnMetaData meta = rowGroup.GetMetadata(leaf)?.MetaData
            ?? throw DeltaStorageException.CorruptData(
                $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}' has no column-chunk metadata (a stripped/absent footer).");
        long numValues = meta.NumValues;
        if (numValues < 0)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}' declares a negative value count ({numValues}).");
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

        // The eager transient is (numValues * perSlotBytes) + payloadBytes. Draw it down from the COLUMN-WIDE
        // budget (shared across this nested column's leaves) so their COMBINED peak — not merely each leaf
        // independently — stays within the eager-decode ceiling; fails closed (overflow-safely) on breach.
        budget.Charge(payloadBytes, numValues, perSlotBytes, leaf);

        if (numValues > int.MaxValue)
        {
            throw DeltaStorageException.CorruptData(
                $"Nested leaf '{DiagnosticText.Sanitize(leaf.Path.ToString())}' declares {numValues} values, exceeding Int32.MaxValue.");
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

    // Parquet.Net binds a map's key_value children POSITIONALLY (MapField.Assign: first child → Key, second →
    // Value, in ThriftFooter declaration order), NOT by name. A map<T,T> with a required value therefore
    // silently TRANSPOSES key/value past the type/level/EnsureRequiredMapKey guards (both children same
    // physical type, both REQUIRED, identical rep/def). This guard asserts the canonical child names before
    // the children are consumed so a non-canonical / transposed map fails closed (#676 §2.5). Map-only: a
    // single-child list has no transposition hazard, and a list/element-name check would fail-close legitimate
    // legacy-shaped foreign lists. Mode-independent (also closes the pre-existing none-mode #571 exposure).
    private static void EnsureCanonicalMapChildNames(PqMapField fileMap, string columnName)
    {
        if (!string.Equals(fileMap.Key.Name, "key", StringComparison.Ordinal)
            || !string.Equals(fileMap.Value.Name, "value", StringComparison.Ordinal))
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Map column '{columnName}': the file map key_value children are not named the canonical "
                + "'key'/'value'; Parquet.Net binds them positionally, so a non-canonically-named map cannot be "
                + "read safely (its key and value may be transposed).");
        }
    }

    // Resolves a struct child to its RAW file Field (NAME/none mode) — DUPLICATE-intolerant, non-throwing on
    // ABSENCE — WITHOUT collapsing it to a scalar leaf. A scalar child then routes to ExpectScalarLeaf; a
    // nested child recurses through DecodeNode/ValidateNode. This is the name-mode sibling of
    // ResolveStructFieldById. Three outcomes (#857 §2.3):
    //   • EXACTLY ONE file field with the requested physical name → `childNode = match; return true` (present).
    //   • MORE THAN ONE → still THROWS SchemaMismatch (an ambiguous/foreign shape; a DUPLICATE is NOT absence
    //     and must never be treated as one — the duplicate-child guard stays intact inside the resolver).
    //   • NONE → `childNode = null; return false` (genuinely ABSENT — the CALLER decides null-fill vs
    //     fail-closed, so absence is never conflated with a present-but-mismatched child).
    private static bool TryResolveStructChildNode(
        PqStructField fileStruct, StructField requested, string columnName, out Field? childNode)
    {
        Field? match = null;
        foreach (Field candidate in fileStruct.Fields)
        {
            if (string.Equals(candidate.Name, requested.Name, StringComparison.Ordinal))
            {
                if (match is not null)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Struct column '{columnName}' has more than one file field named "
                        + $"'{DiagnosticText.Sanitize(requested.Name)}'; a duplicate struct child name cannot be resolved safely.");
                }

                match = candidate;
            }
        }

        childNode = match;
        return match is not null;
    }

    // §2.4 (#857): synthesizes an ABSENT nullable struct child's per-owner-cell definition stream so the
    // cross-field null-mask parity guard (BuildStructNullMask) sees the SAME struct presence the present
    // siblings report (INV-PARITY). Struct presence is a property of the STRUCT, not of any one child, so it
    // is read from the file struct's OWN driving leaf (FirstDataField) — PROJECTION-INDEPENDENT: correct even
    // when the absent child is the ONLY projected field (there is then no requested sibling to drive the
    // mask). The clone is clamped at structMaxDef by the SAME ExtractOwnerCellDefs a present sibling under a
    // repeated ancestor uses; for a TOP-LEVEL struct (parentMaxRep == 0) that call reduces to
    // min(def[r], structMaxDef) per owner cell (and additionally reconciles a repeated FIRST-child driving
    // leaf and the owner-cell count), so parity holds by construction in BOTH null-mask paths. A REQUIRED
    // struct (structMaxDef == 0) has no null mask — BuildStructNullMask early-returns — so `null` is returned
    // (required-field semantics) and NO extra leaf is read. The one driving-leaf read is charged against the
    // shared budget by the existing ReadScalarLeafAsync path.
    private static async ValueTask<int[]?> StructPresenceDefs(
        ParquetRowGroupReader rowGroup,
        PqStructField fileStruct,
        int structMaxDef,
        int parentMaxDef,
        int parentMaxRep,
        int rowCount,
        NestedDecodeBudget budget,
        string context,
        CancellationToken cancellationToken)
    {
        if (structMaxDef == 0)
        {
            // A REQUIRED struct: no null mask exists (every owner cell is present); required-field semantics.
            return null;
        }

        // FirstDataField throws CorruptData for a zero-leaf struct; a struct PRESENT in the file always has
        // >= 1 physical leaf, so this only fires on a corrupt/empty footer — fail-closed, correct.
        DataField driving = FirstDataField(fileStruct);

        // The driving leaf's DeltaSharp scalar type is derived from its OWN physical type (we discard its
        // values and consume only its Dremel levels), so the physical read dispatch is exact. It is read for
        // structure only (values discarded), so it is NEVER promoted (#546): promoteLeaf: false. Note this is
        // defensive symmetry, not load-bearing — because drivingScalar == the leaf's own physical type, the
        // promotion predicate (!physicalType.Equals(scalarType) && IsSanctionedWidening) is structurally
        // UNREACHABLE here regardless of the flag (RFL-864 merge round F3: flipping it is an equivalent mutant).
        DataType drivingScalar = ParquetTypeMapping.ToDataType(driving);
        (_, int[]? def, int[]? rep, int numValues) = await ReadScalarLeafAsync(
            rowGroup, driving, drivingScalar, presentFloor: 0, budget, promoteLeaf: false, cancellationToken)
            .ConfigureAwait(false);

        return ExtractOwnerCellDefs(
            def, rep, numValues, structMaxDef, parentMaxDef, parentMaxRep, rowCount, context);
    }

    // §2.5/§2.6 (#857): builds an all-null child vector for an ABSENT nullable struct child of ANY requested
    // type — scalar OR nested (struct/array/map, now decodable via 585a's recursive DecodeNode). An absent
    // physical column means the ENTIRE subtree is absent, so EVERY cell is null and NO interior physical leaf
    // is read (it NEVER recurses into DecodeNode/ReadScalarLeafAsync), keeping a deeply nested absent subtree
    // O(rows), not O(rows x subtree-leaves). The vector is charged against the shared NestedDecodeBudget
    // consistent with ChargeStructural so a wide absent-child projection cannot escape the row-group decode
    // ceiling (§2.6, §6 DoS).
    private static ColumnVector SynthesizeAbsentChild(
        DataType type, int rowCount, NestedDecodeBudget budget, string context, int depth)
    {
        // Conservative O(rows) charge (§2.6, Q6): the value lanes the factory allocates (up to rowCount slots)
        // plus a per-cell validity byte, summed over the requested subtree (bounded by MaxNestedReadDepth).
        // No per-VALUE payload term — every cell is null, so nothing is materialized beyond the lanes.
        budget.ChargeStructural(rowCount, AbsentCellWidth(type, depth), context);
        return BuildAllNullSubtree(type, rowCount, depth, context);
    }

    // Materializes an all-null vector for the requested subtree WITHOUT charging (the parent SynthesizeAbsent
    // child charges the whole subtree once). A STRUCT is built via its immutable constructor with recursively
    // all-null children: a struct's own AppendNull requires EVERY field child already populated (each struct
    // row carries one cell per child, even a null-struct row), so it cannot be committed standalone — this is
    // the one shape whose null-fill must recurse into its children's LANES (still no file leaf read: the
    // recursion is over the DECLARED subtree shape only). No interior physical leaf is read at any level, so
    // the FILE-READ cost stays O(rows); the transient ALLOCATION is O(rows × subtree-width), charged in full
    // by AbsentCellWidth. The requested subtree depth is bounded by MaxNestedReadDepth (fail-closed past it),
    // matching the sibling DecodeNode/ValidateNode guards, so a programmatically-constructed deep type cannot
    // recurse unbounded into a StackOverflow.
    private static ColumnVector BuildAllNullSubtree(DataType type, int rowCount, int depth, string context)
    {
        if (depth > MaxNestedReadDepth)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Nested column exceeds the maximum supported nesting depth of {MaxNestedReadDepth} levels "
                + "while null-filling an absent nested child.");
        }

        if (type is StructType structType)
        {
            var children = new ColumnVector[structType.Count];
            for (int i = 0; i < structType.Count; i++)
            {
                StructField structField = structType[i];
                string childContext =
                    $"struct column '{context}' field '{DiagnosticText.Sanitize(structField.Name)}'";
                children[i] = BuildAllNullSubtree(structField.DataType, rowCount, depth + 1, childContext);
            }

            var nulls = new bool[rowCount];
            Array.Fill(nulls, true);
            return new StructColumnVector(structType, children, nulls);
        }

        // #863: an absent nullable child whose (scalar) leaf type has no managed column vector (e.g.
        // void/NullType) must fail closed with the reader's TYPED contract, not a raw UnsupportedTypeException
        // escaping the storage boundary. Through the public read door ParquetTypeMapping.EnsureReadSupported
        // already rejects such a leaf up front (UnsupportedFeature); CreateLeafVector keeps THIS path
        // independently fail-closed for a direct/defense-in-depth call (the present-decode path's twin site
        // below).
        MutableColumnVector vector = CreateLeafVector(type, Math.Max(rowCount, 1), context);
        for (int r = 0; r < rowCount; r++)
        {
            vector.AppendNull();
        }

        return vector;
    }

    // Allocates a nested leaf's value lane, normalizing ColumnVectors.Create's UnsupportedTypeException — a
    // leaf type with no managed column vector (e.g. void/NullType) — into the reader's fail-closed TYPED
    // contract (#863): a bounded, sanitized DeltaStorageException.UnsupportedFeature, never a raw exception
    // across the storage boundary. Through the public read door ParquetTypeMapping.EnsureReadSupported already
    // rejects such a leaf up front (UnsupportedFeature); this keeps NestedParquetColumnReader INDEPENDENTLY
    // fail-closed, consistent with its sibling defense-in-depth guards. The catch is scoped to the allocation
    // call and to UnsupportedTypeException ONLY, so an unrelated fault still propagates (no over-catch). The
    // raw cause is retained as the inner for server-side diagnostics (N3). `context` is a pre-sanitized
    // diagnostic label that names the offending POSITION (a "nested leaf '…'" at the present-decode site, or a
    // struct-field/column path at the absent-child site); DescribeType renders the bounded TYPE KIND — which
    // may be a CONTAINER (array/map) at the absent-child site, not only a scalar leaf — never a recursive
    // foreign SimpleString.
    private static MutableColumnVector CreateLeafVector(DataType type, int capacity, string context)
    {
        try
        {
            return ColumnVectors.Create(type, capacity);
        }
        catch (UnsupportedTypeException ex)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for {context}: the type '{DiagnosticText.DescribeType(type)}' has no "
                + "supported column vector.", ex);
        }
    }

    // The conservative per-owner-cell byte width of an all-null subtree (§2.6): a scalar contributes its value
    // lane's element width (for a variable-width string/binary, the speculative data lane AND the offset lane
    // the backing store allocates up-front — see ScalarLaneWidth) plus one validity byte; a container adds one
    // validity byte + one offset int for its OWN level plus its children's widths. Summed over the subtree
    // (depth-bounded by MaxNestedReadDepth, fail-closed past it), it is an UPPER BOUND on the transient the
    // synthesized vector allocates — so a wide/deep absent projection cannot escape the row-group decode
    // ceiling by being under-charged.
    private static long AbsentCellWidth(DataType type, int depth)
    {
        if (depth > MaxNestedReadDepth)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Nested column exceeds the maximum supported nesting depth of {MaxNestedReadDepth} levels "
                + "while sizing an absent nested child.");
        }

        switch (type)
        {
            case StructType structType:
                long structWidth = 1; // this struct's own validity byte
                foreach (StructField child in structType)
                {
                    structWidth += AbsentCellWidth(child.DataType, depth + 1);
                }

                return structWidth;
            case ArrayType arrayType:
                return 1 + sizeof(int) + AbsentCellWidth(arrayType.ElementType, depth + 1);
            case MapType mapType:
                return 1 + sizeof(int)
                    + AbsentCellWidth(mapType.KeyType, depth + 1) + AbsentCellWidth(mapType.ValueType, depth + 1);
            default:
                return ScalarLaneWidth(type) + 1;
        }
    }

    // The value-lane element width a scalar MutableColumnVector allocates per cell (mirrors ColumnVectors.
    // Create's backing storage). For a variable-width string/binary vector the managed backing store
    // speculatively allocates BOTH a data lane (8 bytes/cell) AND an offset lane (sizeof(int)/cell) up-front,
    // even though an all-null vector writes no payload — so both are counted here to keep AbsentCellWidth a
    // true upper bound on the transient (Columnar F1).
    private static long ScalarLaneWidth(DataType type) => type switch
    {
        BooleanType or ByteType => 1,
        ShortType => sizeof(short),
        IntegerType or DateType or FloatType => sizeof(int),
        LongType or TimestampType or TimestampNtzType or DoubleType => sizeof(long),
        DecimalType { IsCompact: true } => sizeof(long),
        DecimalType => 16, // Int128 lane
        StringType or BinaryType => 8 + sizeof(int), // speculative data lane (8/cell) + offset lane (no payload — all null)
        _ => sizeof(long), // conservative default
    };

    // The DRIVING leaf of a (possibly nested) file node: the first scalar DataField reachable along the
    // document-order first-child path (list -> Item, map -> Key, struct -> Fields[0], DataField -> itself).
    // Its raw (def, rep) streams fully describe every repeated level between this node and the leaf, so an
    // intermediate container reads them to reconstruct its OWN structure without materializing the child.
    private static DataField FirstDataField(Field node)
    {
        while (true)
        {
            switch (node)
            {
                case DataField dataField:
                    return dataField;
                case PqListField list:
                    node = list.Item;
                    break;
                case PqMapField map:
                    node = map.Key;
                    break;
                case PqStructField structField when structField.Fields.Count > 0:
                    node = structField.Fields[0];
                    break;
                default:
                    throw DeltaStorageException.CorruptData(
                        "A nested file column has no reachable scalar leaf to drive its structure "
                        + "(a zero-field struct or an empty container in the footer).");
            }
        }
    }


    // Resolves a struct child in ID mode (#676 §2.5): binds by the child's delta.columnMapping.id within the
    // resolved container — NEVER by name. The child's id is looked up in the path-keyed footer field-id map
    // (#829); the resolved leaf MUST be one of the container's OWN direct leaf children (containment) so a
    // forged footer that stamps the id on a top-level / sibling-container leaf fails closed rather than
    // mis-attributing a column. The id-selected leaf — and only it — is then type/level-validated via
    // ExpectScalarLeaf. A child that declares no id, whose id is absent from the footer, or whose id resolves
    // outside the container fails closed with NO name fallback.
    private static DataField ResolveStructFieldById(
        PqStructField fileStruct, StructField requested, IReadOnlyDictionary<int, DataField> byFieldId, string columnName)
    {
        if (!ColumnMapping.TryGetId(requested, out long id))
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Struct column '{columnName}' field '{DiagnosticText.Sanitize(requested.Name)}' has no column-mapping id "
                + "under id mode; the schema is inconsistent and cannot be read safely.");
        }

        if (id is < 1 or > int.MaxValue || !byFieldId.TryGetValue((int)id, out DataField? leaf))
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Struct column '{columnName}' field '{DiagnosticText.Sanitize(requested.Name)}' declares a column-mapping id "
                + "that is absent from the file footer field ids; the id-mode read fails closed (no name fallback).");
        }

        if (!IsDirectLeafChild(fileStruct, leaf))
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Struct column '{columnName}' field '{DiagnosticText.Sanitize(requested.Name)}': its column-mapping id resolves "
                + "to a footer leaf outside the resolved container's own children; the id-mode read fails closed to avoid "
                + "cross-column mis-attribution.");
        }

        return ExpectScalarLeaf(
            leaf, requested.DataType, fileStruct.MaxRepetitionLevel, fileStruct.MaxDefinitionLevel,
            $"struct column '{columnName}' field '{DiagnosticText.Sanitize(requested.Name)}'", promoteLeaf: false);
    }

    // True when <paramref name="leaf"/> is one of <paramref name="container"/>'s OWN direct leaf children,
    // compared by full physical path (the #829 footer↔decoder bijection makes path a unique physical-location
    // identity). This is the containment check that scopes an id-mode struct-child lookup to its declared
    // container's subtree.
    private static bool IsDirectLeafChild(PqStructField container, DataField leaf)
    {
        var leafKey = ParquetFileReader.PhysicalPathKey.From(leaf.Path);
        foreach (Field child in container.Fields)
        {
            if (child is DataField childLeaf && ParquetFileReader.PhysicalPathKey.From(childLeaf.Path).Equals(leafKey))
            {
                return true;
            }
        }

        return false;
    }

    // Validates an id-mode struct<scalars> container against its requested (physical) struct type WITHOUT
    // reading any data page (#676 §2.5): each requested child is resolved by id within the container and
    // type/level-validated. The struct arm MUST NOT name-match in id mode — the id-selected leaf is the sole
    // per-leaf validator.
    public static void ValidateStructShapeById(
        PqStructField container, StructType requested, IReadOnlyDictionary<int, DataField> byFieldId, string columnName)
    {
        columnName = DiagnosticText.Sanitize(columnName);
        if (requested.Count == 0)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for struct column '{columnName}': a zero-field struct is not supported.");
        }

        foreach (StructField child in requested)
        {
            _ = ResolveStructFieldById(container, child, byFieldId, columnName);
        }
    }

    // The interior element/key/value field_ids parsed from a container field's delta.columnMapping.nested.ids
    // (#839, design §2.5). Carried from ResolveFileFields (validation) through to the decode (ReadListAsync/
    // ReadMapAsync) so the interior leaf is bound by id — never positionally.
    internal sealed class NestedInteriorIds
    {
        private NestedInteriorIds(long? elementId, long? keyId, long? valueId)
        {
            ElementId = elementId;
            KeyId = keyId;
            ValueId = valueId;
        }

        internal long? ElementId { get; }

        internal long? KeyId { get; }

        internal long? ValueId { get; }

        internal static NestedInteriorIds ForArray(long elementId) => new(elementId, null, null);

        internal static NestedInteriorIds ForMap(long keyId, long valueId) => new(null, keyId, valueId);
    }

    // Collects the interior LEAF DataField(s) that are the container group's OWN direct children — the array
    // element (a list's Item) or the map key/value. A container whose interior is itself nested (Item/Key/Value
    // is not a DataField) contributes no interior leaf, so an id lookup fails closed by containment (that shape
    // is nested-within-nested #585 and rejected upstream).
    private static List<DataField> ListInteriorLeaves(PqListField fileList)
    {
        var leaves = new List<DataField>(1);
        if (fileList.Item is DataField item)
        {
            leaves.Add(item);
        }

        return leaves;
    }

    private static List<DataField> MapInteriorLeaves(PqMapField fileMap)
    {
        var leaves = new List<DataField>(2);
        if (fileMap.Key is DataField key)
        {
            leaves.Add(key);
        }

        if (fileMap.Value is DataField value)
        {
            leaves.Add(value);
        }

        return leaves;
    }

    // Resolves an id-mode array/map interior leaf by its nested.ids field_id within the container's OWN
    // interior leaves (#839, design §2.5 step 2). The id is looked up in the path-keyed footer field-id map
    // (#829); the resolved leaf MUST be one of <paramref name="containerInteriorLeaves"/> (full-path
    // direct-child membership — the #676 containment check one level down) so a forged footer that stamps the
    // id on a top-level / sibling-container leaf fails closed rather than mis-attributing a column. Never binds
    // positionally.
    private static DataField ResolveInteriorLeafById(
        long interiorId, List<DataField> containerInteriorLeaves,
        IReadOnlyDictionary<int, DataField> byFieldId, string context)
    {
        if (interiorId is < 1 or > int.MaxValue || !byFieldId.TryGetValue((int)interiorId, out DataField? leaf))
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Parquet nested read for {context}: its '{ColumnMapping.NestedIdsKey}' interior id is absent from the "
                + "file footer field ids; the id-mode read fails closed (no positional fallback).");
        }

        var leafKey = ParquetFileReader.PhysicalPathKey.From(leaf.Path);
        foreach (DataField interior in containerInteriorLeaves)
        {
            if (ParquetFileReader.PhysicalPathKey.From(interior.Path).Equals(leafKey))
            {
                return leaf;
            }
        }

        throw DeltaStorageException.SchemaMismatch(
            $"Parquet nested read for {context}: its '{ColumnMapping.NestedIdsKey}' interior id resolves to a footer leaf "
            + "outside the resolved container's own interior; the id-mode read fails closed to avoid cross-column mis-attribution.");
    }

    // Validates an id-mode array<scalar> container against its requested type WITHOUT reading a data page
    // (#839): the element leaf is bound by its nested.ids field_id within the container's own interior, then
    // type/level-validated (ExpectScalarLeaf). Never positional.
    public static void ValidateArrayShapeById(
        Field container, ArrayType requested, long elementId,
        IReadOnlyDictionary<int, DataField> byFieldId, string columnName)
    {
        columnName = DiagnosticText.Sanitize(columnName);
        PqListField fileList = container as PqListField
            ?? throw DeltaStorageException.SchemaMismatch(
                $"Column '{columnName}': its container physical name resolves to a non-list file column; the id-mode "
                + "nested read fails closed.");
        string context = $"array column '{columnName}' element";
        DataField elementLeaf = ResolveInteriorLeafById(elementId, ListInteriorLeaves(fileList), byFieldId, context);
        _ = ExpectScalarLeaf(
            elementLeaf, requested.ElementType, fileList.MaxRepetitionLevel, fileList.MaxDefinitionLevel, context,
            promoteLeaf: false);
    }

    // Validates an id-mode map<scalar,scalar> container against its requested type WITHOUT reading a data page
    // (#839): the key/value leaves are bound by their distinct nested.ids field_ids within the container's own
    // interior, then type/level-validated. The canonical key/value name guard is kept as defense-in-depth
    // (§2.4). Never positional.
    public static void ValidateMapShapeById(
        Field container, MapType requested, long keyId, long valueId,
        IReadOnlyDictionary<int, DataField> byFieldId, string columnName)
    {
        columnName = DiagnosticText.Sanitize(columnName);
        PqMapField fileMap = container as PqMapField
            ?? throw DeltaStorageException.SchemaMismatch(
                $"Column '{columnName}': its container physical name resolves to a non-map file column; the id-mode "
                + "nested read fails closed.");
        EnsureCanonicalMapChildNames(fileMap, columnName);
        EnsureRequiredMapKey(fileMap, columnName);
        List<DataField> interiors = MapInteriorLeaves(fileMap);
        DataField keyLeaf = ResolveInteriorLeafById(keyId, interiors, byFieldId, $"map column '{columnName}' key");
        DataField valueLeaf = ResolveInteriorLeafById(valueId, interiors, byFieldId, $"map column '{columnName}' value");
        _ = ExpectScalarLeaf(
            keyLeaf, requested.KeyType, fileMap.MaxRepetitionLevel, fileMap.MaxDefinitionLevel,
            $"map column '{columnName}' key", promoteLeaf: false);
        _ = ExpectScalarLeaf(
            valueLeaf, requested.ValueType, fileMap.MaxRepetitionLevel, fileMap.MaxDefinitionLevel,
            $"map column '{columnName}' value", promoteLeaf: false);
    }

    private static DataField ExpectScalarLeaf(
        Field fileField, DataType requestedScalar, int expectedMaxRepetition, int containerMaxDef, string context,
        bool promoteLeaf)
    {
        if (requestedScalar is ArrayType or MapType or StructType)
        {
            // DEFENSE IN DEPTH, unreachable from the read path today: ParquetTypeMapping.EnsureScalarReadable
            // rejects the same shape earlier (pinned by NestedParquetReadTests.ArrayOfStruct_FailsClosed_*),
            // so no test can drive this arm — which is precisely why it must not be the site that drifts. The
            // predicate makes `requestedScalar` statically nested, so SimpleString necessarily recurses and
            // carries nested field names verbatim; Sanitize bounded it (this was never a flood or an
            // injection) but rendered a 128-char truncation of `struct<...>`. Uses the same bounded renderer
            // as its reachable twin so the two cannot say different things about the same condition.
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for {context}: a nested type within a nested type "
                + $"('{DiagnosticText.DescribeType(requestedScalar)}') is not supported.");
        }

        if (fileField is not DataField leaf)
        {
            throw DeltaStorageException.UnsupportedFeature(
                $"Parquet nested read for {context}: the file column is itself nested, which is not supported.");
        }

        ValidateLeafPhysicalType(leaf, requestedScalar, context, promoteLeaf);
        ValidateLeafStructuralLevels(leaf, expectedMaxRepetition, containerMaxDef, context);
        return leaf;
    }

    // Validates that a scalar leaf's declared STRUCTURAL Dremel levels (max repetition, max definition) match
    // what its position in the requested single-level shape structurally requires (R8). The reader navigates
    // the FILE schema to each leaf and reconstructs positionally from the leaf's def/rep streams, but the
    // reconstruction's row/entry/null thresholds are keyed off the CONTAINER's own levels — so a crafted file
    // whose leaf declares max levels inconsistent with its navigated position can masquerade as a different
    // shape and silently mis-decode. Two invariants, both derived from Dremel level propagation:
    //   1. Max repetition: a scalar leaf directly under a single-level container carries EXACTLY the
    //      container's repetition level (a struct field 0; a list element / map key / map value 1 — the one
    //      repeated ancestor). A leaf declaring a HIGHER level is repeated where the shape is not (a repeated
    //      primitive masquerading as a struct field — its rep stream, which ReadStructAsync does not honor,
    //      would let N element occurrences pose as N rows) or nests further than supported (maxRep >= 2 =
    //      nested-within-nested); a LOWER level is non-repeated where the shape requires repetition. Either
    //      way the reconstruction would mis-count rows/entries -> silent wrong data.
    //   2. Max definition: a scalar leaf carries the container's definition level plus AT MOST ONE (its own
    //      optional level; a required leaf adds nothing). A leaf whose max definition sits BELOW the
    //      container's (fewer optional/repeated ancestors than its own parent — impossible in a well-formed
    //      tree) or ABOVE containerMaxDef + 1 (a phantom optional/repeated ancestor) shifts the def thresholds
    //      the reconstruction uses to tell a present value from a null cell from a null container -> a present
    //      value silently decodes as null (or vice-versa). Nullability itself stays ADVISORY per #570: BOTH a
    //      required leaf (maxDef == containerMaxDef) and an optional leaf (maxDef == containerMaxDef + 1) are
    //      accepted.
    // Fail closed CorruptData on either mismatch, at shape resolution (BEFORE any reconstruction). Internal so
    // the guard can be pinned by direct unit tests for the list/map positions, whose wrong-level leaves no
    // Parquet.Net-based write door can author — including DeltaSharp's own nested write path (#841), which
    // builds its schema through the same ListField/MapField constructors, and those force element/key/value
    // maxRep = 1; the struct-field maxRep masquerade IS authorable end-to-end (a 1-level repeated primitive
    // under a struct). The levels DeltaSharp's shredder emits are separately bounded against these same
    // schema-derived maxima by NestedLevelGuard before every write, so this guard remains a hostile-FILE
    // control rather than a check on our own encoder.
    internal static void ValidateLeafStructuralLevels(
        DataField leaf, int expectedMaxRepetition, int containerMaxDef, string context)
    {
        if (leaf.MaxRepetitionLevel != expectedMaxRepetition)
        {
            throw DeltaStorageException.CorruptData(
                $"Parquet nested read for {context}: the file leaf declares max repetition level "
                + $"{leaf.MaxRepetitionLevel}, but this position requires {expectedMaxRepetition} (a leaf whose "
                + "repetition contradicts its shape would mis-count rows/entries).");
        }

        if (leaf.MaxDefinitionLevel < containerMaxDef || leaf.MaxDefinitionLevel > containerMaxDef + 1)
        {
            throw DeltaStorageException.CorruptData(
                $"Parquet nested read for {context}: the file leaf declares max definition level "
                + $"{leaf.MaxDefinitionLevel}, but this position requires {containerMaxDef} or {containerMaxDef + 1} "
                + "(a leaf whose definition level contradicts its container would mis-classify present vs null cells).");
        }
    }

    // An EXACT physical-type match, OR — when the promotion gate is open (#546, <paramref name="promoteLeaf"/>
    // already composes the container depth ≤ 1 rule) — a NARROWER physical leaf that is a Delta-sanctioned
    // widening of the requested scalar (the read path promotes each value into the wide lane; the
    // integral→decimal fit guard is baked into TypeWidening.IsSanctionedWidening). Mirrors the flat reader's
    // ValidateFileField promotion branch. When the gate is closed an exact match is still required
    // (fail-closed). Skips nullability enforcement (nested value/element/field nullability is advisory per #570).
    private static void ValidateLeafPhysicalType(
        DataField leaf, DataType requested, string context, bool promoteLeaf)
    {
        if (promoteLeaf
            && ParquetTypeMapping.TryToDataType(leaf, out DataType? physicalType)
            && !physicalType.Equals(requested)
            && TypeWidening.IsSanctionedWidening(physicalType, requested))
        {
            return;
        }

        bool matches = requested switch
        {
            BooleanType => leaf.ClrType == typeof(bool),
            ByteType => leaf.ClrType == typeof(sbyte),
            ShortType => leaf.ClrType == typeof(short),
            IntegerType => leaf.ClrType == typeof(int) && !ParquetTypeMapping.IsTimeColumn(leaf),
            LongType => leaf.ClrType == typeof(long) && !ParquetTypeMapping.IsTimeColumn(leaf),
            FloatType => leaf.ClrType == typeof(float),
            DoubleType => leaf.ClrType == typeof(double),
            StringType => ParquetTypeMapping.IsStringPhysicalClrType(leaf.ClrType),
            BinaryType => ParquetTypeMapping.IsBinaryPhysicalClrType(leaf.ClrType),
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
            // #705 predicate: this is the genuinely-defective site of the three flagged in this file. `requested`
            // is a leaf position's type, but a nested request (e.g. array<array<int>>) drives a non-atomic
            // ElementType through here, where `_ => false` fires and a raw `requested.SimpleString` would recurse
            // into every nested field name. DescribeType renders the bounded kind instead.
            throw DeltaStorageException.SchemaMismatch(
                $"Parquet nested read for {context}: the file physical type "
                + $"'{ParquetTypeMapping.DescribePhysical(leaf)}' does not match "
                + $"the requested '{DiagnosticText.DescribeType(requested)}'.");
        }
    }
}
