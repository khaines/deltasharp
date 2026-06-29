namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Repartitions a single input into <see cref="PartitionCount"/> local partitions by hashing
/// <see cref="PartitionKeys"/> (empty = round-robin). This is the in-process stage boundary that
/// later becomes a network shuffle; output schema equals input schema and no rows are lost or
/// duplicated. Backends record routed bytes in <see cref="OperatorMetrics.ShuffleBytes"/>.
/// </summary>
public sealed class ExchangeLocalOperator : PhysicalOperator
{
    private readonly PhysicalOperator[] _children;
    private readonly PhysicalExpression[] _partitionKeys;

    /// <summary>Creates a local exchange.</summary>
    /// <param name="input">The input operator.</param>
    /// <param name="partitionCount">Target partition count (positive).</param>
    /// <param name="partitionKeys">Hash-partition keys; empty distributes round-robin.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="partitionCount"/> is not positive.</exception>
    public ExchangeLocalOperator(PhysicalOperator input, int partitionCount, IReadOnlyList<PhysicalExpression>? partitionKeys = null)
        : base((input ?? throw new ArgumentNullException(nameof(input))).OutputSchema)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        _children = [input];
        PartitionCount = partitionCount;
        _partitionKeys = partitionKeys is null ? [] : [.. partitionKeys];
    }

    /// <inheritdoc />
    public override OperatorKind Kind => OperatorKind.ExchangeLocal;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalOperator> Children => _children;

    /// <summary>The target number of local partitions.</summary>
    public int PartitionCount { get; }

    /// <summary>Hash-partition keys; empty means round-robin distribution.</summary>
    public IReadOnlyList<PhysicalExpression> PartitionKeys => _partitionKeys;
}
