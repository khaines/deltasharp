using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// A strongly typed view over a <see cref="DataFrame"/>'s logical plan, equivalent to Apache Spark's
/// <c>Dataset&lt;T&gt;</c> (a <see cref="DataFrame"/> is Spark's untyped <c>Dataset&lt;Row&gt;</c>).
/// It pairs the same immutable, unresolved logical <see cref="Plan"/> a <see cref="DataFrame"/> wraps
/// with a <see cref="Schema"/> derived from the properties of the encoded type <typeparamref name="T"/>
/// (STORY-04.2.4 / #163).
/// </summary>
/// <remarks>
/// <para>
/// The typed surface <b>shares</b> the DataFrame engine pipeline rather than forking it: a typed
/// <see cref="Where(Expression{Func{T, bool}})"/>/<see cref="Select(Expression{Func{T, object}}[])"/>
/// lambda is lowered by <see cref="TypedExpressionLowering"/> into the very same
/// <see cref="Plans.Logical.Filter"/>/<see cref="Plans.Logical.Project"/> nodes as the equivalent
/// untyped call (AC1), and <see cref="ToDF"/> hands back a <see cref="DataFrame"/> over the
/// <b>identical</b> <see cref="Plan"/> reference with no materialization (AC2). Every typed
/// transformation is <b>lazy</b> — it only extends the plan; only an action executes (ADR-0001).
/// </para>
/// <para>
/// <b>Scope boundary.</b> This bridge owns the typed-transformation lowering and the
/// reflection-derived <see cref="Schema"/> (AC3); it does <b>not</b> materialize values. The full
/// <c>Row</c>&#8596;<typeparamref name="T"/> value encoders (turning collected rows into
/// <typeparamref name="T"/> instances and back) are the separate deferred STORY-04.7.2 (#178), which
/// chains after this. Until then a <see cref="Dataset{T}"/> is a typed plan builder; run it by
/// converting to a <see cref="DataFrame"/> with <see cref="ToDF"/>. See
/// <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </para>
/// </remarks>
/// <typeparam name="T">The encoded record/POCO type whose public properties define the
/// <see cref="Schema"/>.</typeparam>
public sealed class Dataset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
{
    /// <summary>Wraps a logical plan as a typed dataset, deriving <typeparamref name="T"/>'s schema.
    /// Non-public: a <see cref="Dataset{T}"/> is produced by <see cref="DataFrame.As{T}"/>, not by
    /// user code.</summary>
    /// <exception cref="UnsupportedTypedSchemaException">A property of <typeparamref name="T"/> has
    /// no supported schema mapping (AC4).</exception>
    internal Dataset(SparkSession? session, LogicalPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Session = session;
        Schema = TypedSchemaCache<T>.Value;
    }

    /// <summary>The immutable, unresolved logical plan backing this dataset — the same plan model a
    /// <see cref="DataFrame"/> wraps, so typed and untyped pipelines never diverge.</summary>
    internal LogicalPlan Plan { get; }

    /// <summary>The owning session, or <see langword="null"/> for a session-free dataset; propagated
    /// unchanged through every typed transformation so a whole chain shares one session.</summary>
    internal SparkSession? Session { get; }

    /// <summary>
    /// The schema derived from <typeparamref name="T"/>'s public properties, with ADR-0008 nullability
    /// (AC3). This is metadata read from <typeparamref name="T"/> at construction — it consults no
    /// data and materializes nothing.
    /// </summary>
    public StructType Schema { get; }

    /// <summary>
    /// Converts this typed dataset back to an untyped <see cref="DataFrame"/>, mirroring Spark's
    /// <c>Dataset.toDF()</c>. The returned frame wraps the <b>identical</b> <see cref="Plan"/>
    /// reference bound to the same session — plan identity is preserved and <b>nothing is
    /// materialized</b> (AC2). It is a lazy view, not a copy.
    /// </summary>
    /// <returns>A <see cref="DataFrame"/> over this dataset's plan.</returns>
    public DataFrame ToDF() => new(Session, Plan);

    /// <summary>
    /// Filters rows with a typed predicate lambda, returning a new <see cref="Dataset{T}"/> whose plan
    /// is a <see cref="Plans.Logical.Filter"/> over this dataset's plan, mirroring Spark's
    /// <c>Dataset.filter(FilterFunction&lt;T&gt;)</c>. The lambda is <b>lowered</b> to the shared
    /// expression IR (never compiled or executed), so it builds the same <c>Filter</c> node as the
    /// untyped <see cref="DataFrame.Filter(Column)"/> (AC1). This is a lazy transformation; this
    /// instance is unchanged and the two datasets share this plan subtree by reference (#167).
    /// </summary>
    /// <param name="predicate">A boolean predicate over a row of <typeparamref name="T"/> (for example
    /// <c>row =&gt; row.Age &gt;= 21</c>).</param>
    /// <returns>A new filtered <see cref="Dataset{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is null.</exception>
    /// <exception cref="UnsupportedTypedExpressionException">The predicate contains a node the bridge
    /// cannot lower (AC4).</exception>
    public Dataset<T> Filter(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Column condition = TypedExpressionLowering.Lower(predicate);
        return new Dataset<T>(Session, new Filter(condition.Expr, Plan));
    }

    /// <summary>
    /// An alias for <see cref="Filter(Expression{Func{T, bool}})"/>, mirroring Spark's
    /// <c>Dataset.where</c>. Exactly equivalent — same lowered <c>Filter</c> plan node.
    /// </summary>
    /// <param name="predicate">A boolean predicate over a row of <typeparamref name="T"/>.</param>
    /// <returns>A new filtered <see cref="Dataset{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is null.</exception>
    /// <exception cref="UnsupportedTypedExpressionException">The predicate contains a node the bridge
    /// cannot lower (AC4).</exception>
    public Dataset<T> Where(Expression<Func<T, bool>> predicate) => Filter(predicate);

    /// <summary>
    /// Filters rows with an already-built <see cref="Column"/> predicate, returning a new
    /// <see cref="Dataset{T}"/>, mirroring Spark's <c>Dataset.filter(Column)</c>. Type-preserving and
    /// lazy: it appends the same <see cref="Plans.Logical.Filter"/> node
    /// <see cref="DataFrame.Filter(Column)"/> builds. Use it to reuse a <see cref="Functions"/>-built
    /// predicate while staying in the typed surface.
    /// </summary>
    /// <param name="condition">The boolean predicate to retain rows by.</param>
    /// <returns>A new filtered <see cref="Dataset{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    public Dataset<T> Filter(Column condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new Dataset<T>(Session, new Filter(condition.Expr, Plan));
    }

    /// <summary>An alias for <see cref="Filter(Column)"/>, mirroring Spark's <c>Dataset.where(Column)</c>.</summary>
    /// <param name="condition">The boolean predicate to retain rows by.</param>
    /// <returns>A new filtered <see cref="Dataset{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    public Dataset<T> Where(Column condition) => Filter(condition);

    /// <summary>
    /// Projects a set of typed member/expression selectors, returning an untyped
    /// <see cref="DataFrame"/> whose plan is a <see cref="Plans.Logical.Project"/> over this dataset's
    /// plan, mirroring Spark's <c>Dataset.select</c>. Each selector lambda is lowered to the shared
    /// expression IR, so it builds the same <c>Project</c> node as the untyped
    /// <see cref="DataFrame.Select(Column[])"/> (AC1). The result is a <see cref="DataFrame"/> (an
    /// untyped <c>Dataset&lt;Row&gt;</c>): a projection changes the row shape, and reconstructing a
    /// typed <c>Dataset&lt;U&gt;</c> needs the output-type value encoders deferred to #178.
    /// </summary>
    /// <param name="selectors">The typed projection selectors, in output order (for example
    /// <c>row =&gt; row.Name</c>, <c>row =&gt; row.Age</c>). Calling with none builds an empty
    /// projection, matching <see cref="DataFrame.Select(Column[])"/>.</param>
    /// <returns>A new <see cref="DataFrame"/> projecting the selectors.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selectors"/> or any element is null.</exception>
    /// <exception cref="UnsupportedTypedExpressionException">A selector contains a node the bridge
    /// cannot lower (AC4).</exception>
    public DataFrame Select(params Expression<Func<T, object?>>[] selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        var projectList = new Plans.Expressions.Expression[selectors.Length];
        for (int i = 0; i < selectors.Length; i++)
        {
            Expression<Func<T, object?>> selector = selectors[i]
                ?? throw new ArgumentNullException(nameof(selectors), "Selector cannot be null.");
            projectList[i] = TypedExpressionLowering.Lower(selector).Expr;
        }

        return new DataFrame(Session, new Project(projectList, Plan));
    }
}
