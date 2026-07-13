namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Selects which <b>schema-evolution</b> operations a Delta write is permitted to apply to the table schema
/// before it commits (STORY-05.4.2 AC2; design §2.12.2). It is the storage-side analog of Spark's
/// <c>mergeSchema</c> write option: <see cref="None"/> is strict enforcement (the write must already be
/// compatible with the table schema and may change nothing), and <see cref="AddNewColumns"/> opts into the
/// only class of <b>additive, non-destructive</b> schema change <see cref="DeltaSchemaEnforcer"/> may fold
/// into a merged schema and commit as a <c>metaData</c> action in the <b>same</b> version as the data adds:
/// unioning new <b>nullable</b> columns.
///
/// <para><b>Type widening is NOT selected by this mode — it is governed by the table feature.</b> Applying a
/// Delta-sanctioned widening of an existing column's type (<c>int→long</c>, <c>float→double</c>, grow-only
/// <c>decimal</c>) is gated by the Delta <c>typeWidening</c> <b>table feature</b> plus the
/// <c>delta.enableTypeWidening</c> property (#495) — not by any <see cref="SchemaEvolutionMode"/>. When the
/// table enables type widening, <see cref="DeltaSchemaEnforcer"/> applies the widening (recording a
/// <c>delta.typeChanges</c> entry so the reader promotes pre-widening files); when it does not, such a change
/// is rejected fail-closed and distinctly as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>.
/// This enum only ever controls the <i>additive-column</i> dimension; <c>mergeSchema</c> here (=
/// <see cref="AddNewColumns"/>) unions new nullable columns and does <b>not</b> itself widen any column's
/// type. See <see cref="DeltaSchemaEnforcer"/>.</para>
///
/// <para><b>What is never allowed, in any mode</b> (rejected by <see cref="DeltaSchemaEnforcer"/> as a
/// <see cref="DeltaSchemaMismatchException"/>): dropping or omitting a required (non-nullable) column,
/// tightening a nullable column to required, a <b>narrowing</b> or otherwise non-sanctioned type change,
/// evolving a <b>partition</b> column's type, and a merged schema with two columns whose names collide
/// case-insensitively. Adding a nullable column is the only change a <i>mode</i> enables; a sanctioned type
/// <i>widening</i> is enabled separately by the <c>typeWidening</c> table feature (above), never by weakening
/// a constraint existing rows already satisfy.</para>
/// </summary>
[Flags]
internal enum SchemaEvolutionMode
{
    /// <summary>Strict enforcement — the write must be compatible with the current table schema and may not
    /// change it. Any new column, type change, or nullability relaxation is rejected before commit
    /// (STORY-05.4.2 AC1).</summary>
    None = 0,

    /// <summary>Union new <b>nullable</b> columns (including new nested <c>struct</c> fields) from the write
    /// schema into the table schema — Spark's <c>mergeSchema</c>. A new column must be nullable (existing
    /// rows have no value for it); a non-nullable new column is rejected. Adding a nullable column is the
    /// <b>only</b> schema change a <i>mode</i> permits; applying a sanctioned type widening to an existing
    /// column is governed separately by the <c>typeWidening</c> table feature (#495), not by this mode.</summary>
    AddNewColumns = 1 << 0,

    /// <summary>A Spark-familiar alias for <see cref="AddNewColumns"/> — the storage-side meaning of
    /// <c>mergeSchema=true</c>. It is <b>exactly equal</b> to <see cref="AddNewColumns"/> and deliberately
    /// does <b>not</b> itself widen any column's type: applying a sanctioned type widening is governed by the
    /// Delta <c>typeWidening</c> table feature + <c>delta.enableTypeWidening</c> (#495; see
    /// <see cref="DeltaSchemaEnforcer"/>), independently of <c>mergeSchema</c>.</summary>
    MergeSchema = AddNewColumns,
}
