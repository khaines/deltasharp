namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The deterministic reason a Delta write's schema was rejected by <see cref="DeltaSchemaEnforcer"/> before
/// any action was committed (STORY-05.4.2 AC1). Callers and tests branch on
/// <see cref="DeltaSchemaMismatchException.Kind"/> rather than parsing the message (mirroring
/// <see cref="DeltaProtocolErrorKind"/>/<see cref="StorageErrorKind"/>).
/// </summary>
internal enum DeltaSchemaMismatchKind
{
    /// <summary>The table has a required (non-nullable) column that the write schema does not provide;
    /// committing would write <c>null</c> into a column that forbids it. Always rejected.</summary>
    MissingRequiredColumn,

    /// <summary>The write schema introduces a column the table does not have, but
    /// <see cref="SchemaEvolutionMode.AddNewColumns"/> was not enabled (strict enforcement).</summary>
    NewColumnNotAllowed,

    /// <summary>Evolution would add a new column, but the write declares it non-nullable; a column added to a
    /// table that already has rows must be nullable (existing rows have no value for it). Always rejected.</summary>
    NewColumnMustBeNullable,

    /// <summary>The write would carry <c>null</c>-bearing data into a required (non-nullable) column,
    /// array element, or map value — i.e. the write relaxes a nullability constraint the table enforces.
    /// Tightening a nullable column to required is the symmetric, always-rejected case. Always rejected.</summary>
    NullabilityViolation,

    /// <summary>The write changes a column's type to one that is neither equal nor a lossless widening — a
    /// narrowing or an unrelated type (for example <c>long→int</c> or <c>string↔long</c>). Delta never
    /// silently downcasts. Always rejected.</summary>
    IncompatibleType,

    /// <summary>The write changes a column's type to one that <i>would</i> be a lossless widening
    /// (<c>int→long</c>, <c>float→double</c>, <c>date→timestamp</c>, a growing <c>decimal</c>), but type
    /// widening is <b>not supported</b> here: widening the logical schema without Delta's <c>typeWidening</c>
    /// table feature makes the existing Parquet files unreadable even by DeltaSharp (the reader does an exact
    /// physical-type match). Fail-closed until the feature lands (tracked in #495). Distinct from
    /// <see cref="IncompatibleType"/> so the message can name the deferred feature.</summary>
    TypeWideningUnsupported,

    /// <summary>Evolution would produce a merged schema containing two columns whose names are equal under
    /// case-insensitive comparison (e.g. table <c>id</c> + a new write column <c>ID</c>). Delta/Spark treat
    /// column-name uniqueness as case-insensitive at the storage/protocol level, so such a schema is
    /// invalid. Always rejected. (Matching remains case-sensitive/ordinal; only the merged result is
    /// checked for a case-fold collision.)</summary>
    CaseInsensitiveDuplicateColumn,

    /// <summary>The write would change the type of a <b>partition</b> column. A partition column's type is
    /// physically encoded in the directory layout and cannot be evolved in place (it requires a full table
    /// rewrite), so it is always rejected — an explicit, clearer reason than the generic type-change
    /// classification.</summary>
    PartitionColumnEvolutionUnsupported,

    /// <summary>A staged Parquet data file's <b>actual physical data schema</b> does not match the data
    /// columns of the <b>declared</b> write schema (#497). The write-door records each staged file's true
    /// written schema (<c>StagedDataFile.DataSchema</c>); the enforcing write path cross-checks it so schema
    /// enforcement gates the real bytes rather than trusting the caller's declaration. A divergence — a
    /// caller (or a defect) staging bytes whose columns/types/nullability differ from what it declared — is
    /// rejected fail-closed <b>before</b> any action is committed. Always rejected.</summary>
    PhysicalWriteSchemaMismatch,
}

/// <summary>
/// Thrown when an incoming write's schema is rejected before any action is committed — either by
/// <see cref="DeltaSchemaEnforcer"/> (the incoming <i>logical</i> write schema is incompatible with the
/// table schema, or requires a change the active <see cref="SchemaEvolutionMode"/> does not permit;
/// STORY-05.4.2 AC1) or by the write-door's <b>physical write-schema validation</b> (a staged Parquet
/// file's actual data schema diverges from the declared write schema — <see cref="DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch"/>,
/// #497). It is raised <b>before</b> any <c>add</c>/<c>metaData</c> action is built or committed, so a
/// rejected write leaves the table completely unchanged (fail-closed, mirroring <c>DeltaTableWriter</c>'s
/// partition-coverage guard). The message names the offending column <see cref="Path"/> (dotted, with
/// <c>element</c>/<c>key</c>/<c>value</c> segments for nested types; empty for a whole-file schema
/// divergence) and the concrete disagreement so the failure is actionable.
/// </summary>
internal sealed class DeltaSchemaMismatchException : Exception
{
    private DeltaSchemaMismatchException(DeltaSchemaMismatchKind kind, string path, string message)
        : base(message)
    {
        Kind = kind;
        Path = path;
    }

    /// <summary>The classified reason the schema was rejected.</summary>
    public DeltaSchemaMismatchKind Kind { get; }

    /// <summary>The dotted path to the offending column/field (e.g. <c>address.zip</c>,
    /// <c>tags.element</c>, <c>lookup.value</c>).</summary>
    public string Path { get; }

    public static DeltaSchemaMismatchException MissingRequiredColumn(string path) =>
        new(
            DeltaSchemaMismatchKind.MissingRequiredColumn,
            path,
            $"The write is missing required (non-nullable) column '{path}'; a write must provide every " +
            "non-nullable column, or the column must first be made nullable.");

    public static DeltaSchemaMismatchException NewColumnNotAllowed(string path) =>
        new(
            DeltaSchemaMismatchKind.NewColumnNotAllowed,
            path,
            $"The write introduces column '{path}', which is not in the table schema. Enable schema " +
            "evolution (SchemaEvolutionMode.AddNewColumns / MergeSchema) to add new columns.");

    public static DeltaSchemaMismatchException NewColumnMustBeNullable(string path) =>
        new(
            DeltaSchemaMismatchKind.NewColumnMustBeNullable,
            path,
            $"The write adds new column '{path}' as non-nullable; a column added to a table that already " +
            "has data must be nullable because existing rows carry no value for it.");

    public static DeltaSchemaMismatchException NullabilityViolation(string path) =>
        new(
            DeltaSchemaMismatchKind.NullabilityViolation,
            path,
            $"The write declares '{path}' nullable but the table requires it to be non-nullable; " +
            "null-bearing data cannot be written into a required column, and a required column cannot be " +
            "relaxed to nullable by a write.");

    public static DeltaSchemaMismatchException IncompatibleType(string path, string tableType, string writeType) =>
        new(
            DeltaSchemaMismatchKind.IncompatibleType,
            path,
            $"The write type '{writeType}' for column '{path}' is not compatible with the table type " +
            $"'{tableType}'; an existing column's type cannot be changed by a write (never a narrowing or an " +
            "unrelated type).");

    public static DeltaSchemaMismatchException TypeWideningUnsupported(string path, string tableType, string writeType) =>
        new(
            DeltaSchemaMismatchKind.TypeWideningUnsupported,
            path,
            $"The type change '{tableType}'→'{writeType}' for column '{path}' requires the Delta " +
            "typeWidening table feature, which is not yet supported (tracked in #495). Widening the logical " +
            "schema without it would make the table's existing Parquet files unreadable, so it is rejected.");

    public static DeltaSchemaMismatchException CaseInsensitiveDuplicateColumn(string path, string other) =>
        new(
            DeltaSchemaMismatchKind.CaseInsensitiveDuplicateColumn,
            path,
            $"Evolving the schema would add column '{path}', which collides case-insensitively with existing " +
            $"column '{other}'. Delta requires column names to be unique ignoring case, so this write is rejected.");

    public static DeltaSchemaMismatchException PartitionColumnEvolution(string path, string tableType, string writeType) =>
        new(
            DeltaSchemaMismatchKind.PartitionColumnEvolutionUnsupported,
            path,
            $"The write changes the type of partition column '{path}' from '{tableType}' to '{writeType}'; a " +
            "partition column's type is encoded in the table layout and cannot be evolved (it requires a full " +
            "table rewrite). Rejected.");

    public static DeltaSchemaMismatchException PhysicalWriteSchemaMismatch(string filePath, string declared, string actual) =>
        new(
            DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch,
            path: string.Empty,
            $"Staged file '{filePath}' was written with physical data schema '{actual}', which does not " +
            $"match the declared write schema's data columns '{declared}'. Schema enforcement gates the real " +
            "written bytes (#497), so a write whose staged files diverge from its declared schema is rejected.");
}
