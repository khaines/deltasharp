using System.Runtime.CompilerServices;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Types;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// An <b>in-memory relation</b> (Spark's <c>LocalRelation</c>): a leaf that carries an explicit
/// <see cref="StructType"/> schema and the local <see cref="Row"/> sequence a
/// <see cref="SparkSession.CreateDataFrame(System.Collections.Generic.IEnumerable{Row}, StructType)"/>
/// wraps. It is the scan logical plan for local data (STORY-04.1.2 / #158).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-phase, mirroring <see cref="UnresolvedRelation"/> → <see cref="ResolvedRelation"/>.</b> As
/// built by <c>CreateDataFrame</c> the node is <b>unresolved</b> (<see cref="Output"/> is
/// <see langword="null"/>, <see cref="LogicalPlan.Resolved"/> is <see langword="false"/>): DeltaSharp
/// mints <see cref="ExprId"/>s fresh per analyze pass (see
/// <see cref="DeltaSharp.Analysis.ExprIdGenerator"/>), so a self-assigned id would collide with the
/// analyzer's counter. The analyzer's ResolveRelations rule mints the <see cref="Output"/> attributes
/// from the shared per-pass generator (identically to a catalog table) via
/// <see cref="WithResolvedOutput"/>, carrying the <see cref="Data"/> reference through unchanged.
/// </para>
/// <para>
/// <b>Laziness + stable snapshot.</b> The caller's sequence is wrapped in a
/// <see cref="MemoizedRowSequence"/> and exposed as <see cref="Data"/>. Construction enumerates
/// <b>nothing</b> (AC1 — opens no file, materializes no row, per ADR-0001); the <b>first</b> action's
/// execution enumerates the source once and caches an immutable snapshot, and every later enumeration
/// (multi-action, multi-scan, self-join) replays that same snapshot. This gives Spark's
/// <c>createDataFrame(List, schema)</c> stable semantics without breaking laziness: mutating the source
/// after the first action, or passing a single-use iterator, can no longer make
/// <see cref="DataFrame.Count()"/> and <see cref="DataFrame.Collect()"/> disagree.
/// </para>
/// <para>
/// <b>Node identity.</b> <see cref="NodeEquals"/>/<see cref="NodeHashCode"/> compare <see cref="Data"/>
/// by <b>reference identity</b> (schema and output by value): value-comparing the rows would force
/// enumeration and mishandle one-shot iterators. The single memoizing wrapper is shared across the
/// unresolved/resolved forms (see <see cref="WithResolvedOutput"/>), so reference identity holds.
/// </para>
/// </remarks>
internal sealed class LocalRelation : LogicalPlan
{
    /// <summary>Creates an <b>unresolved</b> in-memory relation over <paramref name="data"/> with the
    /// explicit <paramref name="schema"/>.</summary>
    /// <param name="schema">The authoritative relation schema.</param>
    /// <param name="data">The local rows (read positionally by an action; never enumerated here).</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public LocalRelation(StructType schema, IEnumerable<Row> data)
        : this(schema, data, output: null)
    {
    }

    private LocalRelation(StructType schema, IEnumerable<Row> data, IReadOnlyList<AttributeReference>? output)
        : base(PlanCollections.Empty<LogicalPlan>())
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        ArgumentNullException.ThrowIfNull(data);

        // Wrap the caller's sequence in a memoizing snapshot so every action replays identical rows
        // (Wrap is idempotent, so the resolved form shares the unresolved form's snapshot and keeps
        // reference identity). Wrapping enumerates nothing, preserving AC1 laziness.
        Data = MemoizedRowSequence.Wrap(data);
        Output = output;
    }

    /// <summary>The authoritative ADR-0008 schema of every row.</summary>
    public StructType Schema { get; }

    /// <summary>The local rows as a memoizing snapshot: never enumerated by Core (only at execution), and
    /// replayable so every action sees the same stable rows.</summary>
    public IEnumerable<Row> Data { get; }

    /// <summary>The resolved output attributes (one per schema field), or <see langword="null"/> until
    /// the analyzer resolves this relation.</summary>
    public IReadOnlyList<AttributeReference>? Output { get; }

    /// <summary>Returns the resolved form of this relation carrying <paramref name="output"/> (minted by
    /// the analyzer from its per-pass id generator), sharing this node's <see cref="Data"/> reference.</summary>
    /// <param name="output">The output attributes, one per schema field, in field order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/>'s count does not match the schema.</exception>
    public LocalRelation WithResolvedOutput(IReadOnlyList<AttributeReference> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Count != Schema.Count)
        {
            throw new ArgumentException(
                $"Output attribute count ({output.Count}) does not match schema field count ({Schema.Count}).",
                nameof(output));
        }

        return new LocalRelation(Schema, Data, PlanCollections.ToImmutable(output, nameof(output)));
    }

    /// <inheritdoc/>
    public override IReadOnlyList<Expression> Expressions => PlanCollections.Empty<Expression>();

    /// <summary>An in-memory relation is resolved only once the analyzer has bound its
    /// <see cref="Output"/> attributes (the generic child/expression check cannot see this state).</summary>
    protected override bool IsNodeResolved => Output is not null;

    /// <inheritdoc/>
    public override string NodeName => "LocalRelation";

    /// <inheritdoc/>
    public override string SimpleString =>
        Output is null
            ? $"{UnresolvedPrefix}LocalRelation ["
                + string.Join(", ", Schema.Select(f => $"{f.Name}: {f.DataType.SimpleString}")) + "]"
            : $"LocalRelation [{string.Join(", ", Output.Select(a => a.SimpleString))}]";

    /// <inheritdoc/>
    public override LogicalPlan WithNewChildren(IReadOnlyList<LogicalPlan> newChildren)
    {
        ArgumentNullException.ThrowIfNull(newChildren);
        if (newChildren.Count != 0)
        {
            throw new ArgumentException(
                "LocalRelation is a leaf and takes no children.", nameof(newChildren));
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
        other is LocalRelation relation
        && ReferenceEquals(Data, relation.Data)
        && Schema.Equals(relation.Schema)
        && OutputsEqual(Output, relation.Output);

    /// <inheritdoc/>
    protected override int NodeHashCode()
    {
        int hash = PlanHash.Combine(Schema.GetHashCode(), RuntimeHelpers.GetHashCode(Data));
        if (Output is not null)
        {
            hash = PlanNodes.HashExpressions(hash, Output);
        }

        return hash;
    }

    private static bool OutputsEqual(
        IReadOnlyList<AttributeReference>? left, IReadOnlyList<AttributeReference>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return PlanNodes.ExpressionsEqual(left, right);
    }
}
