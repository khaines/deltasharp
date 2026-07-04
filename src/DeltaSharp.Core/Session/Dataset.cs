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
/// reflection-derived <see cref="Schema"/> (AC3). STORY-04.7.2 (#178) adds the
/// <c>Row</c>&#8594;<typeparamref name="T"/> value <b>decoder</b> behind <see cref="Collect()"/>: it
/// runs the plan through the same executor seam <see cref="DataFrame.Collect()"/> uses and materializes
/// each result row as a <typeparamref name="T"/> without mutating the row. The reverse
/// <typeparamref name="T"/>&#8594;<c>Row</c> encode and non-bean shapes (positional records, structs,
/// nested/collection types) remain deferred. Every typed transformation stays <b>lazy</b>; only
/// <see cref="Collect()"/> (an action) executes. See
/// <c>docs/engineering/design/dataset-encoders.md</c> and
/// <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </para>
/// </remarks>
/// <typeparam name="T">The encoded record/POCO type whose public properties define the
/// <see cref="Schema"/>.</typeparam>
public sealed class Dataset<[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicProperties |
    DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>
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
    /// Runs this dataset's plan and returns every row decoded as a <typeparamref name="T"/> value,
    /// mirroring Spark's <c>Dataset.collect()</c>. This is an <b>action</b> (ADR-0001): it executes the
    /// plan through the <b>same</b> engine seam <see cref="DataFrame.Collect()"/> uses, then decodes each
    /// resulting <see cref="Row"/> into a <typeparamref name="T"/> with the STORY-04.7.2 value encoder —
    /// reading the row only, never mutating it. As with any <c>collect</c>, the full result is buffered
    /// into driver memory. See <c>docs/engineering/design/dataset-encoders.md</c>.
    /// </summary>
    /// <returns>The decoded <typeparamref name="T"/> values, in the plan's result order.</returns>
    /// <exception cref="InvalidOperationException">This dataset is not bound to a
    /// <see cref="SparkSession"/>, or a SQL <c>NULL</c> maps to a non-nullable value-typed property.</exception>
    /// <exception cref="UnsupportedTypedSchemaException"><typeparamref name="T"/> is not an encodable
    /// bean — a value type, abstract/interface, missing a public parameterless constructor, having a
    /// mapped property with no public setter, or an ambiguous (duplicated) property name. Validated once,
    /// before execution, and re-thrown deterministically.</exception>
    /// <exception cref="InvalidCastException">A row cell's runtime type does not match its property.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// failed while running the plan (propagated from <see cref="DataFrame.Collect()"/>).</exception>
    public IReadOnlyList<T> Collect() => Collect(CancellationToken.None);

    /// <summary>The cancellable overload of <see cref="Collect()"/>; cancellation is observed by the
    /// underlying <see cref="DataFrame.Collect(CancellationToken)"/> execution before any decoding.</summary>
    /// <param name="cancellationToken">A token that cooperatively cancels the action.</param>
    /// <returns>The decoded <typeparamref name="T"/> values, in the plan's result order.</returns>
    /// <exception cref="InvalidOperationException">This dataset is not bound to a
    /// <see cref="SparkSession"/>, or a SQL <c>NULL</c> maps to a non-nullable value-typed property.</exception>
    /// <exception cref="UnsupportedTypedSchemaException"><typeparamref name="T"/> is not an encodable bean.</exception>
    /// <exception cref="InvalidCastException">A row cell's runtime type does not match its property.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="TimeoutException">A configured execution timeout elapsed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// failed while running the plan (propagated from <see cref="DataFrame.Collect(CancellationToken)"/>).</exception>
    public IReadOnlyList<T> Collect(CancellationToken cancellationToken)
    {
        // Build/validate the decoder FIRST so an unsupported T fails fast and deterministically, before
        // any execution work (AC3). The decoder (and any diagnostic) is cached per T, so a repeated
        // typed collect re-throws the identical message.
        RowDecoder<T> decoder = TypedRowDecoderCache<T>.Value;

        IReadOnlyList<Row> rows = ToDF().Collect(cancellationToken);
        var results = new T[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            results[i] = decoder.Decode(rows[i]);
        }

        return results;
    }

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
    /// <remarks>The predicate is <b>translated</b> to Spark Column IR, so where C# and Spark SQL
    /// semantics differ (e.g. integer <c>/</c> is fractional division; <c>checked</c>/<c>unchecked</c>
    /// is not honored on column operands), <b>Spark SQL</b> semantics apply — see
    /// <c>docs/engineering/design/dataset-typed-bridge.md</c> §"Expression semantics: Spark SQL, not C#".</remarks>
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
    /// typed <c>Dataset&lt;U&gt;</c> needs the output-type value encoders deferred to #447.
    /// </summary>
    /// <param name="selectors">The typed projection selectors, in output order (for example
    /// <c>row =&gt; row.Name</c>, <c>row =&gt; row.Age</c>). Calling with none builds an empty
    /// projection, matching <see cref="DataFrame.Select(Column[])"/>.</param>
    /// <returns>A new <see cref="DataFrame"/> projecting the selectors.</returns>
    /// <remarks>Each selector is <b>translated</b> to Spark Column IR, so where C# and Spark SQL
    /// semantics differ (e.g. integer <c>/</c> is fractional division returning <c>DOUBLE</c>;
    /// <c>checked</c>/<c>unchecked</c> is not honored on column operands), <b>Spark SQL</b> semantics
    /// apply — see <c>docs/engineering/design/dataset-typed-bridge.md</c> §"Expression semantics: Spark
    /// SQL, not C#".</remarks>
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
