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
/// <para><b>Type widening is intentionally NOT a mode.</b> Widening the <i>logical</i> table schema
/// (<c>int→long</c>, <c>float→double</c>, <c>date→timestamp</c>, growing a <c>decimal</c>) without also
/// registering Delta's <c>typeWidening</c> table feature and its per-field widening metadata makes the
/// existing Parquet files <b>unreadable — even by DeltaSharp itself</b>: the reader
/// (<c>ParquetFileReader.ValidateFileField</c>) does an exact physical-type match, so an <c>Int32</c> file
/// read under a widened <c>long</c> schema throws <c>SchemaMismatch</c>. Type widening is therefore
/// <b>fail-closed</b> (rejected as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>) until the
/// <c>typeWidening</c> feature lands; there is no mode that enables it (tracked in #495). See
/// <see cref="DeltaSchemaEnforcer"/>.</para>
///
/// <para><b>What is never allowed, in any mode</b> (rejected by <see cref="DeltaSchemaEnforcer"/> as a
/// <see cref="DeltaSchemaMismatchException"/>): dropping or omitting a required (non-nullable) column,
/// tightening a nullable column to required, <b>any</b> type change (narrowing, widening, or unrelated),
/// evolving a partition column, and a merged schema with two columns whose names collide case-insensitively.
/// Evolution only ever <i>adds</i> a nullable column; it never weakens a constraint existing rows already
/// satisfy, and never alters an existing column's type.</para>
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
    /// rows have no value for it); a non-nullable new column is rejected. This is the <b>only</b> schema
    /// change any mode permits: no existing column's type is ever altered.</summary>
    AddNewColumns = 1 << 0,

    /// <summary>A Spark-familiar alias for <see cref="AddNewColumns"/> — the storage-side meaning of
    /// <c>mergeSchema=true</c>. It is <b>exactly equal</b> to <see cref="AddNewColumns"/> and deliberately
    /// does <b>not</b> enable any type change: type widening is fail-closed until Delta's <c>typeWidening</c>
    /// table feature is implemented (tracked in #495; see <see cref="DeltaSchemaEnforcer"/>).</summary>
    MergeSchema = AddNewColumns,
}
