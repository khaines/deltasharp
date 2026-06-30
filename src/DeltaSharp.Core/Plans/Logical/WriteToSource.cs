using DeltaSharp.Plans.Expressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// A <b>write intent</b>: the logical plan an action (<c>DataFrameWriter.Save</c>, FEAT-04.6)
/// executes to send its child's rows to a sink. It holds only a logical
/// <see cref="SinkDescriptor"/> — no open writer, stream, file handle, task, or backend object
/// (AC3). It performs no write at construction (the lazy/eager invariant).
/// </summary>
internal sealed class WriteToSource : LogicalPlan
{
    /// <summary>Creates a write intent.</summary>
    public WriteToSource(LogicalPlan child, SinkDescriptor sink)
    {
        Child = child ?? throw new ArgumentNullException(nameof(child));
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _children = PlanCollections.AsReadOnly(Child);
    }

    private readonly IReadOnlyList<LogicalPlan> _children;

    /// <summary>The plan producing the rows to write.</summary>
    public LogicalPlan Child { get; }

    /// <summary>The logical sink descriptor.</summary>
    public SinkDescriptor Sink { get; }

    /// <inheritdoc/>
    public override IReadOnlyList<LogicalPlan> Children => _children;

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => Array.Empty<Expression>();

    /// <inheritdoc/>
    public override string NodeName => "WriteToSource";

    /// <inheritdoc/>
    public override string SimpleString => $"{UnresolvedPrefix}WriteToSource {Sink.SimpleString}";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren) =>
        new WriteToSource(PlanNodes.SingleChild(newChildren, NodeName), Sink);

    /// <inheritdoc/>
    protected override bool NodeEquals(LogicalPlan other) =>
        other is WriteToSource write && Sink.Equals(write.Sink);

    /// <inheritdoc/>
    protected override int NodeHashCode() => PlanHash.Combine(PlanHash.Seed, Sink.GetHashCode());
}
