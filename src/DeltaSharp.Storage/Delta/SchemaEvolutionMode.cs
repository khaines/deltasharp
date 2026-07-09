namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Selects which <b>schema-evolution</b> operations a Delta write is permitted to apply to the table schema
/// before it commits (STORY-05.4.2 AC2; design §2.12.2). It is the storage-side analog of Spark's
/// <c>mergeSchema</c>/type-widening write options: <see cref="None"/> is strict enforcement (the write must
/// already be compatible with the table schema and may change nothing), and each set flag opts into one
/// class of <b>additive, non-destructive</b> schema change that <see cref="DeltaSchemaEnforcer"/> may fold
/// into a merged schema and commit as a <c>metaData</c> action in the <b>same</b> version as the data adds.
///
/// <para><b>What is never allowed, in any mode</b> (rejected by <see cref="DeltaSchemaEnforcer"/> as a
/// <see cref="DeltaSchemaMismatchException"/>): dropping or omitting a required (non-nullable) column,
/// tightening a nullable column to required, narrowing a type, and any incompatible type change. Evolution
/// only ever <i>relaxes</i> the schema (adds nullable columns, widens types); it never weakens a constraint
/// existing rows already satisfy.</para>
/// </summary>
[Flags]
internal enum SchemaEvolutionMode
{
    /// <summary>Strict enforcement — the write must be compatible with the current table schema and may not
    /// change it. Any new column, type widening, or nullability relaxation is rejected before commit
    /// (STORY-05.4.2 AC1).</summary>
    None = 0,

    /// <summary>Union new <b>nullable</b> columns (including new nested <c>struct</c> fields) from the write
    /// schema into the table schema — Spark's <c>mergeSchema</c>. A new column must be nullable (existing
    /// rows have no value for it); a non-nullable new column is rejected.</summary>
    AddNewColumns = 1 << 0,

    /// <summary>Widen an existing column's type to a strictly wider, lossless type from the permitted set
    /// (<c>byte→short→int→long</c>, <c>float→double</c>, <c>date→timestamp</c>, and precision/scale-safe
    /// <c>decimal</c> widening), recursing through nested <c>struct</c>/<c>array</c>/<c>map</c> element
    /// types. Narrowing and lossy changes are always rejected. Delta gates cross-engine read-back of this on
    /// the <c>typeWidening</c> table feature (§2.14); that protocol wiring is tracked separately (see
    /// <see cref="DeltaSchemaEnforcer"/> remarks).</summary>
    WidenTypes = 1 << 1,

    /// <summary>Full additive evolution: <see cref="AddNewColumns"/> and <see cref="WidenTypes"/> together —
    /// the storage-side meaning of <c>mergeSchema=true</c> on a build that also permits type widening.</summary>
    MergeSchema = AddNewColumns | WidenTypes,
}
