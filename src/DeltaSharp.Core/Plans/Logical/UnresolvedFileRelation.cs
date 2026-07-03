using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// An <b>unresolved file-format scan</b> (Spark's path-based <c>UnresolvedRelation</c>): a leaf that
/// records a <see cref="Format"/> (for example <c>parquet</c>), a <see cref="Path"/>, the reader
/// <see cref="Options"/>, and an optional user-specified <see cref="UserSchema"/>, before the
/// storage layer (EPIC-05) can resolve it to a reader. It holds no reader, no file handle, and no
/// schema binding, and performs no I/O — it is a pure descriptor (STORY-04.1.2 / #158, AC2).
/// </summary>
/// <remarks>
/// The M1 reader (STORY-04.1.2) delivers this <b>node</b> only; the file-format reader itself is
/// EPIC-05 (Delta/Parquet storage). The node is therefore always
/// <see cref="LogicalPlan.Resolved"/> = <see langword="false"/>: an action that analyzes it fails
/// with a deterministic <see cref="DeltaSharp.Analysis.AnalysisException"/> naming EPIC-05 ownership
/// (the analysis-time analog of the physical planner's <c>UnsupportedPlanException</c>). When EPIC-05
/// lands it resolves this node — using <see cref="UserSchema"/> or an inferred schema and honoring
/// <see cref="Options"/> — into a resolved file relation.
/// </remarks>
internal sealed class UnresolvedFileRelation : LogicalPlan
{
    /// <summary>Creates an unresolved file-format scan.</summary>
    /// <param name="format">The data-source format (for example <c>parquet</c>).</param>
    /// <param name="path">The source path.</param>
    /// <param name="options">The reader options (recorded for EPIC-05 to honor).</param>
    /// <param name="userSchema">An optional user-specified read schema.</param>
    /// <exception cref="ArgumentException"><paramref name="format"/> or <paramref name="path"/> is null or empty.</exception>
    public UnresolvedFileRelation(
        string format,
        string path,
        IReadOnlyDictionary<string, string>? options = null,
        StructType? userSchema = null)
        : base(PlanCollections.Empty<LogicalPlan>())
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentException.ThrowIfNullOrEmpty(path);
        Format = format;
        Path = path;
        Options = PlanCollections.ToOptions(options);
        UserSchema = userSchema;
    }

    /// <summary>The data-source format (for example <c>parquet</c>).</summary>
    public string Format { get; }

    /// <summary>The source path.</summary>
    public string Path { get; }

    /// <summary>The reader options carried for the EPIC-05 reader.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>The user-specified read schema, or <see langword="null"/> when none was supplied.</summary>
    public StructType? UserSchema { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

    /// <summary>Always unresolved: the file-format reader is EPIC-05.</summary>
    protected override bool IsNodeResolved => false;

    /// <inheritdoc/>
    public override string NodeName => "UnresolvedRelation";

    /// <inheritdoc/>
    public override string SimpleString
    {
        get
        {
            // Render option KEYS ONLY (never values) and a redacted path: options and cloud paths can
            // carry credentials (SAS ?sig=, presigned URLs, userinfo), which must not leak the moment
            // this node is stringified (Explain #179, logging).
            string options = Options.Count == 0
                ? string.Empty
                : ", options=[" + string.Join(", ", Options.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "]";
            string schema = UserSchema is null ? string.Empty : $", userSchema={UserSchema.SimpleString}";
            return $"{UnresolvedPrefix}UnresolvedRelation {Format} [{SecretRedaction.RedactPath(Path)}]{options}{schema}";
        }
    }

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 0)
        {
            throw new ArgumentException(
                "UnresolvedFileRelation is a leaf and takes no children.", nameof(newChildren));
        }

        return this;
    }

    /// <inheritdoc/>
    public override LogicalPlan WithNewExpressions(IReadOnlyList<Expression> newExpressions)
    {
        PlanNodes.RequireNoExpressions(newExpressions, NodeName);
        return this;
    }

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is UnresolvedFileRelation relation
        && string.Equals(Format, relation.Format, StringComparison.Ordinal)
        && string.Equals(Path, relation.Path, StringComparison.Ordinal)
        && PlanCollections.OptionsEqual(Options, relation.Options)
        && Equals(UserSchema, relation.UserSchema);

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(PlanHash.OfString(Format), PlanHash.OfString(Path));
        hash = PlanHash.Combine(hash, PlanHash.OfStringMap(Options));
        return UserSchema is null ? hash : PlanHash.Combine(hash, UserSchema.GetHashCode());
    }
}
