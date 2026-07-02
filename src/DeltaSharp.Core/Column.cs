using DeltaSharp.Plans.Expressions;

namespace DeltaSharp;

/// <summary>
/// A named expression over the columns of a <see cref="DataFrame"/>, equivalent to Apache Spark's
/// <c>Column</c>. A column is a <b>lazy</b> description of intent: constructing one — with
/// <see cref="Functions.Col(string)"/>, <see cref="Functions.Lit(object?)"/>, or an alias — performs
/// <b>no</b> schema lookup and <b>no</b> evaluation. It merely wraps an immutable node of the
/// internal logical expression IR that the analyzer (FEAT-04.5) later resolves and the engine later
/// evaluates when an action runs (ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// The wrapped expression is deliberately <b>not</b> part of the public surface: users compose
/// columns through this type and the <see cref="Functions"/> entry points and never see the
/// internal IR. DeltaSharp mirrors Spark's <c>col("x").as("y")</c> ergonomics with .NET-idiomatic
/// PascalCase names (<see cref="As(string)"/>/<see cref="Alias(string)"/>/<see cref="Name(string)"/>).
/// </para>
/// <para>
/// STORY-04.3.1 delivered column references, aliases, and literals. STORY-04.3.2 (#165) adds the
/// operator surface below: arithmetic (<c>+ - * / %</c>), comparison (<c>&lt; &lt;= &gt; &gt;=</c>,
/// <see cref="EqualTo(DeltaSharp.Column)"/>/<see cref="NotEqual(DeltaSharp.Column)"/>), boolean
/// combinators (<see cref="And(DeltaSharp.Column)"/>/<see cref="Or(DeltaSharp.Column)"/>/
/// <see cref="Not"/>), and null predicates (<see cref="IsNull"/>/<see cref="IsNotNull"/>/
/// <see cref="EqualNullSafe(DeltaSharp.Column)"/>). STORY-04.3.3 (#166) adds the <c>CASE</c>-expression
/// builders <see cref="When(Column, object?)"/>/<see cref="Otherwise(object?)"/> (the companion to
/// <see cref="Functions.When(Column, object?)"/>). Every operator and builder is <b>lazy</b>: it only
/// wraps a new immutable expression node — no schema lookup, no evaluation, and (per AC4) <b>no</b>
/// type coercion or validation, so the analyzer (FEAT-04.5) — not the API — reports operator
/// misuse under ADR-0008's three-valued logic. See
/// <c>docs/engineering/design/column-operators.md</c>.
/// </para>
/// </remarks>
public sealed class Column
{
    /// <summary>Wraps an immutable logical expression node.</summary>
    internal Column(Expression expression)
    {
        Expr = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <summary>
    /// The wrapped, immutable logical expression node. Internal so the DataFrame API and the
    /// analyzer can unwrap a column without exposing the IR on the public surface.
    /// </summary>
    internal Expression Expr { get; }

    /// <summary>
    /// Returns a new column that gives this expression the output <paramref name="alias"/>, mirroring
    /// Spark's <c>Column.as(alias)</c>. The alias is recorded (as an <c>Alias</c> node) for the
    /// analyzer to preserve during name resolution; it triggers no work.
    /// </summary>
    /// <param name="alias">The output name to assign.</param>
    /// <returns>A new aliased <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is null or empty.</exception>
    public Column As(string alias) => new(new Alias(Expr, alias));

    /// <summary>
    /// Returns a new column that gives this expression the output <paramref name="alias"/>, mirroring
    /// Spark's <c>Column.alias(alias)</c>. Equivalent to <see cref="As(string)"/>.
    /// </summary>
    /// <param name="alias">The output name to assign.</param>
    /// <returns>A new aliased <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is null or empty.</exception>
    public Column Alias(string alias) => As(alias);

    /// <summary>
    /// Returns a new column that gives this expression the output <paramref name="alias"/>, mirroring
    /// Spark's <c>Column.name(alias)</c>. Equivalent to <see cref="As(string)"/>.
    /// </summary>
    /// <param name="alias">The output name to assign.</param>
    /// <returns>A new aliased <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is null or empty.</exception>
    public Column Name(string alias) => As(alias);

    /// <summary>
    /// Adds a <c>WHEN <paramref name="condition"/> THEN <paramref name="value"/></c> branch to a
    /// <c>CASE</c> expression, mirroring Spark's <c>Column.when(condition, value)</c>. It is valid
    /// <b>only</b> on a column previously produced by <see cref="Functions.When(Column, object?)"/>
    /// (or a prior chained <see cref="When(Column, object?)"/>) and before any
    /// <see cref="Otherwise(object?)"/> — matching Spark, which rejects <c>when</c> applied to any
    /// other column or after <c>otherwise</c>. <b>Lazy:</b> returns a new column wrapping an extended
    /// unresolved <c>CaseWhen</c>; <paramref name="value"/> is wrapped via
    /// <see cref="Functions.Lit(object?)"/>. This instance is unchanged.
    /// </summary>
    /// <param name="condition">The boolean predicate column for the new branch.</param>
    /// <param name="value">The result when <paramref name="condition"/> is true; a literal or a
    /// <see cref="Column"/>.</param>
    /// <returns>A new <c>CaseWhen</c> column with the branch appended.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    /// <exception cref="InvalidOperationException">This column was not produced by
    /// <see cref="Functions.When(Column, object?)"/>, or an <c>otherwise</c> is already set.</exception>
    public Column When(Column condition, object? value)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (Expr is not CaseWhen caseWhen)
        {
            throw new InvalidOperationException(
                "when() can only be applied on a Column previously generated by the When() function.");
        }

        return new Column(caseWhen.AddBranch(condition.Expr, Functions.Lit(value).Expr));
    }

    /// <summary>
    /// Sets the default <c>ELSE <paramref name="value"/></c> of a <c>CASE</c> expression, mirroring
    /// Spark's <c>Column.otherwise(value)</c>. It is valid <b>only</b> on a column produced by
    /// <see cref="Functions.When(Column, object?)"/> (or a chained <see cref="When(Column, object?)"/>)
    /// and only once — matching Spark, which rejects <c>otherwise</c> elsewhere or applied twice.
    /// With no <c>otherwise</c>, an unmatched row is SQL <c>NULL</c>. <b>Lazy:</b> returns a new
    /// column wrapping the closed unresolved <c>CaseWhen</c>; <paramref name="value"/> is wrapped via
    /// <see cref="Functions.Lit(object?)"/>. This instance is unchanged.
    /// </summary>
    /// <param name="value">The default result; a literal or a <see cref="Column"/>.</param>
    /// <returns>A new <c>CaseWhen</c> column with the <c>ELSE</c> set.</returns>
    /// <exception cref="InvalidOperationException">This column was not produced by
    /// <see cref="Functions.When(Column, object?)"/>, or an <c>otherwise</c> is already set.</exception>
    public Column Otherwise(object? value)
    {
        if (Expr is not CaseWhen caseWhen)
        {
            throw new InvalidOperationException(
                "otherwise() can only be applied on a Column previously generated by the When() "
                + "function.");
        }

        return new Column(caseWhen.WithElse(Functions.Lit(value).Expr));
    }

    /// <summary>
    /// Marks this column for <b>ascending</b> ordering, mirroring Spark's <c>Column.asc</c>. Returns a
    /// new <see cref="Column"/> wrapping a <c>SortOrder(this, Ascending, NullsFirst)</c> — Spark's
    /// <c>asc</c> defaults SQL <c>NULL</c>s <b>first</b>. Intended as an argument to
    /// <see cref="DataFrame.OrderBy(Column[])"/>/<see cref="DataFrame.Sort(Column[])"/>. This is a
    /// <b>lazy</b> builder: it only wraps a new immutable ordering node — no schema lookup and no
    /// evaluation (ADR-0001) — and leaves this instance unchanged.
    /// </summary>
    /// <returns>A new <see cref="Column"/> describing an ascending, nulls-first ordering term.</returns>
    public Column Asc() =>
        new(new SortOrder(Expr, SortDirection.Ascending, NullOrdering.NullsFirst));

    /// <summary>
    /// Marks this column for <b>descending</b> ordering, mirroring Spark's <c>Column.desc</c>. Returns
    /// a new <see cref="Column"/> wrapping a <c>SortOrder(this, Descending, NullsLast)</c> — Spark's
    /// <c>desc</c> defaults SQL <c>NULL</c>s <b>last</b>. Intended as an argument to
    /// <see cref="DataFrame.OrderBy(Column[])"/>/<see cref="DataFrame.Sort(Column[])"/>. Like
    /// <see cref="Asc"/> it is a lazy builder that leaves this instance unchanged.
    /// </summary>
    /// <returns>A new <see cref="Column"/> describing a descending, nulls-last ordering term.</returns>
    public Column Desc() =>
        new(new SortOrder(Expr, SortDirection.Descending, NullOrdering.NullsLast));

    /// <summary>
    /// Returns the Catalyst-style inline rendering of the wrapped expression (for example
    /// <c>'name</c> for an unresolved reference or <c>'name AS x</c> for an alias), matching Spark's
    /// <c>Column.toString</c>. Intended for diagnostics; it performs no analysis.
    /// </summary>
    public override string ToString() => Expr.SimpleString;

    // ---------------------------------------------------------------------------------------------
    // Arithmetic operators (Spark Column.plus/minus/multiply/divide/mod → Catalyst Add/Subtract/
    // Multiply/Divide/Remainder). Each has a Column overload and an object?-literal overload; the
    // literal overload coerces the scalar through Functions.Lit (a Column passes through unchanged).
    // The API builds the node only — it applies no numeric type coercion (AC4); the analyzer does.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>this + <paramref name="other"/></c> as an <c>Add</c> expression, mirroring Spark's
    /// <c>Column.plus</c>. Builds the node only — no evaluation and no type coercion.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Plus(Column other) => Arithmetic(other, ArithmeticOperator.Add);

    /// <summary>
    /// Returns <c>this + <paramref name="value"/></c> as an <c>Add</c> expression against the literal
    /// <paramref name="value"/> (coerced via <see cref="Functions.Lit(object?)"/>).
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Plus(object? value) => Arithmetic(value, ArithmeticOperator.Add);

    /// <summary>
    /// Returns <c>this - <paramref name="other"/></c> as a <c>Subtract</c> expression, mirroring
    /// Spark's <c>Column.minus</c>. Builds the node only.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Minus(Column other) => Arithmetic(other, ArithmeticOperator.Subtract);

    /// <summary>
    /// Returns <c>this - <paramref name="value"/></c> as a <c>Subtract</c> expression against the
    /// literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Minus(object? value) => Arithmetic(value, ArithmeticOperator.Subtract);

    /// <summary>
    /// Returns <c>this * <paramref name="other"/></c> as a <c>Multiply</c> expression, mirroring
    /// Spark's <c>Column.multiply</c>. Builds the node only.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Multiply(Column other) => Arithmetic(other, ArithmeticOperator.Multiply);

    /// <summary>
    /// Returns <c>this * <paramref name="value"/></c> as a <c>Multiply</c> expression against the
    /// literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Multiply(object? value) => Arithmetic(value, ArithmeticOperator.Multiply);

    /// <summary>
    /// Returns <c>this / <paramref name="other"/></c> as a <c>Divide</c> expression, mirroring Spark's
    /// <c>Column.divide</c>. Builds the node only.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Divide(Column other) => Arithmetic(other, ArithmeticOperator.Divide);

    /// <summary>
    /// Returns <c>this / <paramref name="value"/></c> as a <c>Divide</c> expression against the literal
    /// <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Divide(object? value) => Arithmetic(value, ArithmeticOperator.Divide);

    /// <summary>
    /// Returns <c>this % <paramref name="other"/></c> as a <c>Remainder</c> expression, mirroring
    /// Spark's <c>Column.mod</c>. Builds the node only.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Mod(Column other) => Arithmetic(other, ArithmeticOperator.Remainder);

    /// <summary>
    /// Returns <c>this % <paramref name="value"/></c> as a <c>Remainder</c> expression against the
    /// literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Mod(object? value) => Arithmetic(value, ArithmeticOperator.Remainder);

    // ---------------------------------------------------------------------------------------------
    // Comparison operators (Spark Column.equalTo/notEqual/lt/leq/gt/geq → Catalyst EqualTo/NotEqualTo/
    // LessThan/LessThanOrEqual/GreaterThan/GreaterThanOrEqual, all BooleanType-typed).
    //
    // Equality is deliberately exposed as EqualTo/NotEqual methods, NOT operator ==/!=. Overloading
    // operator == on a reference type is a known .NET landmine: it must return bool (so it cannot
    // return a Column expression), and it collides with reference/null equality (col == null) and
    // IDictionary/HashSet identity. Methods keep the expression-returning, Spark-portable semantics
    // unambiguous (issue #164 DX-API forward-guidance). Reference/null equality on Column therefore
    // stays the default object identity.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>this = <paramref name="other"/></c> as an <c>EqualTo</c> predicate, mirroring
    /// Spark's <c>Column.equalTo</c> (Scala <c>===</c>). This is <b>not</b> <c>operator ==</c>: value
    /// equality of the expression is a builder method so reference/null equality is unaffected.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column EqualTo(Column other) => Comparison(other, ComparisonOperator.Equal);

    /// <summary>
    /// Returns <c>this = <paramref name="value"/></c> as an <c>EqualTo</c> predicate against the
    /// literal <paramref name="value"/> (coerced via <see cref="Functions.Lit(object?)"/>).
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column EqualTo(object? value) => Comparison(value, ComparisonOperator.Equal);

    /// <summary>
    /// Returns <c>this &lt;&gt; <paramref name="other"/></c> as a <c>NotEqualTo</c> predicate,
    /// mirroring Spark's <c>Column.notEqual</c> (Scala <c>=!=</c>). Provided as a method rather than
    /// <c>operator !=</c> for the same reason as <see cref="EqualTo(DeltaSharp.Column)"/>.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column NotEqual(Column other) => Comparison(other, ComparisonOperator.NotEqual);

    /// <summary>
    /// Returns <c>this &lt;&gt; <paramref name="value"/></c> as a <c>NotEqualTo</c> predicate against
    /// the literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column NotEqual(object? value) => Comparison(value, ComparisonOperator.NotEqual);

    /// <summary>
    /// Returns <c>this &lt; <paramref name="other"/></c> as a <c>LessThan</c> predicate, mirroring
    /// Spark's <c>Column.lt</c>. Equivalent to <c>this &lt; other</c>.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Lt(Column other) => Comparison(other, ComparisonOperator.LessThan);

    /// <summary>
    /// Returns <c>this &lt; <paramref name="value"/></c> as a <c>LessThan</c> predicate against the
    /// literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Lt(object? value) => Comparison(value, ComparisonOperator.LessThan);

    /// <summary>
    /// Returns <c>this &lt;= <paramref name="other"/></c> as a <c>LessThanOrEqual</c> predicate,
    /// mirroring Spark's <c>Column.leq</c>. Equivalent to <c>this &lt;= other</c>.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Leq(Column other) => Comparison(other, ComparisonOperator.LessThanOrEqual);

    /// <summary>
    /// Returns <c>this &lt;= <paramref name="value"/></c> as a <c>LessThanOrEqual</c> predicate against
    /// the literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Leq(object? value) => Comparison(value, ComparisonOperator.LessThanOrEqual);

    /// <summary>
    /// Returns <c>this &gt; <paramref name="other"/></c> as a <c>GreaterThan</c> predicate, mirroring
    /// Spark's <c>Column.gt</c>. Equivalent to <c>this &gt; other</c>.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Gt(Column other) => Comparison(other, ComparisonOperator.GreaterThan);

    /// <summary>
    /// Returns <c>this &gt; <paramref name="value"/></c> as a <c>GreaterThan</c> predicate against the
    /// literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Gt(object? value) => Comparison(value, ComparisonOperator.GreaterThan);

    /// <summary>
    /// Returns <c>this &gt;= <paramref name="other"/></c> as a <c>GreaterThanOrEqual</c> predicate,
    /// mirroring Spark's <c>Column.geq</c>. Equivalent to <c>this &gt;= other</c>.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Geq(Column other) => Comparison(other, ComparisonOperator.GreaterThanOrEqual);

    /// <summary>
    /// Returns <c>this &gt;= <paramref name="value"/></c> as a <c>GreaterThanOrEqual</c> predicate
    /// against the literal <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Geq(object? value) => Comparison(value, ComparisonOperator.GreaterThanOrEqual);

    // ---------------------------------------------------------------------------------------------
    // Boolean combinators (Spark Column.and/or, functions.not → Catalyst And/Or/Not) under SQL
    // three-valued logic (ADR-0008); the API records structure, evaluation is later. C# cannot
    // meaningfully overload &&/|| for lazy user semantics, so And/Or are methods; the & and |
    // operator overloads below delegate to them (PySpark-aligned & / |).
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>this AND <paramref name="other"/></c> as an <c>And</c> predicate under SQL
    /// three-valued logic (ADR-0008), mirroring Spark's <c>Column.and</c>. Builds the node only.
    /// </summary>
    /// <param name="other">The right boolean operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column And(Column other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Column(new And(Expr, other.Expr));
    }

    /// <summary>
    /// Returns <c>this AND <paramref name="value"/></c> as an <c>And</c> predicate against the literal
    /// <paramref name="value"/> (coerced via <see cref="Functions.Lit(object?)"/>).
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column And(object? value) => new(new And(Expr, Functions.Lit(value).Expr));

    /// <summary>
    /// Returns <c>this OR <paramref name="other"/></c> as an <c>Or</c> predicate under SQL three-valued
    /// logic (ADR-0008), mirroring Spark's <c>Column.or</c>. Builds the node only.
    /// </summary>
    /// <param name="other">The right boolean operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column Or(Column other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Column(new Or(Expr, other.Expr));
    }

    /// <summary>
    /// Returns <c>this OR <paramref name="value"/></c> as an <c>Or</c> predicate against the literal
    /// <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <remarks>A bare <c>null</c> binds the <c>Column</c> overload (which throws); pass
    /// <c>(object?)null</c> for a SQL <c>NULL</c> literal.</remarks>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Or(object? value) => new(new Or(Expr, Functions.Lit(value).Expr));

    /// <summary>
    /// Returns <c>NOT this</c> as a <c>Not</c> predicate under SQL three-valued logic
    /// (<c>NOT NULL = NULL</c>, ADR-0008), mirroring Spark's <c>functions.not</c>/Scala <c>!col</c>.
    /// Builds the node only.
    /// </summary>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column Not() => new(new Not(Expr));

    // ---------------------------------------------------------------------------------------------
    // Null predicates (Spark Column.isNull/isNotNull/eqNullSafe → Catalyst IsNull/IsNotNull/
    // EqualNullSafe). These preserve Spark's null behavior as distinct node kinds: IsNull/IsNotNull
    // never yield NULL, and EqualNullSafe (Spark <=>) treats two NULLs as equal — distinct from
    // EqualTo, whose three-valued NULL = NULL is unknown.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>this IS NULL</c> as an <c>IsNull</c> predicate, mirroring Spark's
    /// <c>Column.isNull</c>. Always boolean-valued (never SQL <c>NULL</c>). Builds the node only.
    /// </summary>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column IsNull() => new(new IsNull(Expr));

    /// <summary>
    /// Returns <c>this IS NOT NULL</c> as an <c>IsNotNull</c> predicate, mirroring Spark's
    /// <c>Column.isNotNull</c>. Always boolean-valued. Builds the node only.
    /// </summary>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column IsNotNull() => new(new IsNotNull(Expr));

    /// <summary>
    /// Returns <c>this &lt;=&gt; <paramref name="other"/></c> as an <c>EqualNullSafe</c> predicate,
    /// mirroring Spark's <c>Column.eqNullSafe</c> (Scala <c>&lt;=&gt;</c>). Two <c>NULL</c>s compare
    /// equal, so it never yields SQL <c>NULL</c> — distinct from <see cref="EqualTo(DeltaSharp.Column)"/>.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column EqualNullSafe(Column other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Column(new EqualNullSafe(Expr, other.Expr));
    }

    /// <summary>
    /// Returns <c>this &lt;=&gt; <paramref name="value"/></c> as an <c>EqualNullSafe</c> predicate
    /// against the literal <paramref name="value"/> (coerced via <see cref="Functions.Lit(object?)"/>).
    /// A <c>null</c> value becomes a typed SQL <c>NULL</c> literal, so
    /// <c>col.EqualNullSafe((object?)null)</c> tests "is this column null" with null-safe semantics.
    /// </summary>
    /// <remarks>
    /// A <b>bare</b> <c>null</c> (<c>col.EqualNullSafe(null)</c>) binds to the more-specific
    /// <see cref="EqualNullSafe(DeltaSharp.Column)"/> overload — which null-guards and throws — not to
    /// this overload. To pass a SQL <c>NULL</c> literal write <c>(object?)null</c>, or prefer
    /// <see cref="IsNull"/> for the "is this column null" idiom.
    /// </remarks>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column EqualNullSafe(object? value) =>
        new(new EqualNullSafe(Expr, Functions.Lit(value).Expr));

    /// <summary>
    /// Alias for <see cref="EqualNullSafe(DeltaSharp.Column)"/> matching Spark's
    /// <c>Column.eqNullSafe</c> spelling for prefix-match IntelliSense discoverability.
    /// <see cref="EqualNullSafe(DeltaSharp.Column)"/> remains the canonical member.
    /// </summary>
    /// <param name="other">The right operand.</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public Column EqNullSafe(Column other) => EqualNullSafe(other);

    /// <summary>
    /// Alias for <see cref="EqualNullSafe(object?)"/> matching Spark's <c>Column.eqNullSafe</c>
    /// spelling for prefix-match IntelliSense discoverability.
    /// <see cref="EqualNullSafe(object?)"/> remains the canonical member.
    /// </summary>
    /// <remarks>
    /// A <b>bare</b> <c>null</c> (<c>col.EqNullSafe(null)</c>) binds to the more-specific
    /// <see cref="EqNullSafe(DeltaSharp.Column)"/> overload — which null-guards and throws — not to
    /// this overload. Write <c>(object?)null</c> for a SQL <c>NULL</c> literal, or prefer
    /// <see cref="IsNull"/>.
    /// </remarks>
    /// <param name="value">The right operand as a literal value (or an existing column).</param>
    /// <returns>A new boolean-valued <see cref="Column"/>; this instance is unchanged.</returns>
    public Column EqNullSafe(object? value) => EqualNullSafe(value);

    // ---------------------------------------------------------------------------------------------
    // C# operator overloads. Arithmetic (+ - * / %) and ordering comparison (< <= > >=) are
    // unambiguous, so they are provided in addition to the named methods (which are the CA2225
    // alternate members). Each has (Column, Column), (Column, object?), and (object?, Column) forms
    // for literal coercion on either side. == / != are intentionally NOT overloaded (see EqualTo).
    // & / | delegate to And/Or (PySpark-aligned); C# cannot overload && / || meaningfully here.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds <c>left + right</c>; see <see cref="Plus(DeltaSharp.Column)"/>.</summary>
    public static Column operator +(Column left, Column right) => Require(left).Plus(right);

    /// <summary>Builds <c>left + value</c>; see <see cref="Plus(object?)"/>.</summary>
    public static Column operator +(Column left, object? value) => Require(left).Plus(value);

    /// <summary>Builds <c>value + right</c>; see <see cref="Plus(DeltaSharp.Column)"/>.</summary>
    public static Column operator +(object? value, Column right) => Functions.Lit(value).Plus(right);

    /// <summary>Builds <c>left - right</c>; see <see cref="Minus(DeltaSharp.Column)"/>.</summary>
    public static Column operator -(Column left, Column right) => Require(left).Minus(right);

    /// <summary>Builds <c>left - value</c>; see <see cref="Minus(object?)"/>.</summary>
    public static Column operator -(Column left, object? value) => Require(left).Minus(value);

    /// <summary>Builds <c>value - right</c>; see <see cref="Minus(DeltaSharp.Column)"/>.</summary>
    public static Column operator -(object? value, Column right) => Functions.Lit(value).Minus(right);

    /// <summary>Builds <c>left * right</c>; see <see cref="Multiply(DeltaSharp.Column)"/>.</summary>
    public static Column operator *(Column left, Column right) => Require(left).Multiply(right);

    /// <summary>Builds <c>left * value</c>; see <see cref="Multiply(object?)"/>.</summary>
    public static Column operator *(Column left, object? value) => Require(left).Multiply(value);

    /// <summary>Builds <c>value * right</c>; see <see cref="Multiply(DeltaSharp.Column)"/>.</summary>
    public static Column operator *(object? value, Column right) => Functions.Lit(value).Multiply(right);

    /// <summary>Builds <c>left / right</c>; see <see cref="Divide(DeltaSharp.Column)"/>.</summary>
    public static Column operator /(Column left, Column right) => Require(left).Divide(right);

    /// <summary>Builds <c>left / value</c>; see <see cref="Divide(object?)"/>.</summary>
    public static Column operator /(Column left, object? value) => Require(left).Divide(value);

    /// <summary>Builds <c>value / right</c>; see <see cref="Divide(DeltaSharp.Column)"/>.</summary>
    public static Column operator /(object? value, Column right) => Functions.Lit(value).Divide(right);

    /// <summary>Builds <c>left % right</c>; see <see cref="Mod(DeltaSharp.Column)"/>.</summary>
    public static Column operator %(Column left, Column right) => Require(left).Mod(right);

    /// <summary>Builds <c>left % value</c>; see <see cref="Mod(object?)"/>.</summary>
    public static Column operator %(Column left, object? value) => Require(left).Mod(value);

    /// <summary>Builds <c>value % right</c>; see <see cref="Mod(DeltaSharp.Column)"/>.</summary>
    public static Column operator %(object? value, Column right) => Functions.Lit(value).Mod(right);

    /// <summary>Builds <c>left &lt; right</c>; see <see cref="Lt(DeltaSharp.Column)"/>.</summary>
    public static Column operator <(Column left, Column right) => Require(left).Lt(right);

    /// <summary>Builds <c>left &lt; value</c>; see <see cref="Lt(object?)"/>.</summary>
    public static Column operator <(Column left, object? value) => Require(left).Lt(value);

    /// <summary>Builds <c>value &lt; right</c>; see <see cref="Lt(DeltaSharp.Column)"/>.</summary>
    public static Column operator <(object? value, Column right) => Functions.Lit(value).Lt(right);

    /// <summary>Builds <c>left &gt; right</c>; see <see cref="Gt(DeltaSharp.Column)"/>.</summary>
    public static Column operator >(Column left, Column right) => Require(left).Gt(right);

    /// <summary>Builds <c>left &gt; value</c>; see <see cref="Gt(object?)"/>.</summary>
    public static Column operator >(Column left, object? value) => Require(left).Gt(value);

    /// <summary>Builds <c>value &gt; right</c>; see <see cref="Gt(DeltaSharp.Column)"/>.</summary>
    public static Column operator >(object? value, Column right) => Functions.Lit(value).Gt(right);

    /// <summary>Builds <c>left &lt;= right</c>; see <see cref="Leq(DeltaSharp.Column)"/>.</summary>
    public static Column operator <=(Column left, Column right) => Require(left).Leq(right);

    /// <summary>Builds <c>left &lt;= value</c>; see <see cref="Leq(object?)"/>.</summary>
    public static Column operator <=(Column left, object? value) => Require(left).Leq(value);

    /// <summary>Builds <c>value &lt;= right</c>; see <see cref="Leq(DeltaSharp.Column)"/>.</summary>
    public static Column operator <=(object? value, Column right) => Functions.Lit(value).Leq(right);

    /// <summary>Builds <c>left &gt;= right</c>; see <see cref="Geq(DeltaSharp.Column)"/>.</summary>
    public static Column operator >=(Column left, Column right) => Require(left).Geq(right);

    /// <summary>Builds <c>left &gt;= value</c>; see <see cref="Geq(object?)"/>.</summary>
    public static Column operator >=(Column left, object? value) => Require(left).Geq(value);

    /// <summary>Builds <c>value &gt;= right</c>; see <see cref="Geq(DeltaSharp.Column)"/>.</summary>
    public static Column operator >=(object? value, Column right) => Functions.Lit(value).Geq(right);

    /// <summary>
    /// Builds <c>left AND right</c> (PySpark-aligned <c>&amp;</c>); see
    /// <see cref="And(DeltaSharp.Column)"/>. This is a non-short-circuiting builder — both operand
    /// expressions are always assembled — which is correct for lazy IR construction.
    /// </summary>
    public static Column operator &(Column left, Column right) => Require(left).And(right);

    /// <summary>
    /// Builds <c>left OR right</c> (PySpark-aligned <c>|</c>); see <see cref="Or(DeltaSharp.Column)"/>.
    /// </summary>
    public static Column operator |(Column left, Column right) => Require(left).Or(right);

    /// <summary>
    /// Builds <c>NOT operand</c> (Scala-aligned <c>!col</c>); see <see cref="Not"/>. Equivalent to
    /// <see cref="operator ~(DeltaSharp.Column)"/>.
    /// </summary>
    public static Column operator !(Column operand) => Require(operand).Not();

    /// <summary>
    /// Builds <c>NOT operand</c> (PySpark-aligned <c>~col</c>, Spark's primary negation); see
    /// <see cref="Not"/>. Equivalent to <see cref="operator !(DeltaSharp.Column)"/> — both build a
    /// <c>Not</c> node — and legal C# because the operator returns the reference type
    /// <see cref="Column"/> rather than an integral bitwise complement.
    /// </summary>
    public static Column operator ~(Column operand) => Require(operand).Not();

    private static Column Require(Column column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column;
    }

    private Column Arithmetic(Column other, ArithmeticOperator op)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Column(new BinaryArithmetic(Expr, other.Expr, op));
    }

    private Column Arithmetic(object? value, ArithmeticOperator op) =>
        new(new BinaryArithmetic(Expr, Functions.Lit(value).Expr, op));

    private Column Comparison(Column other, ComparisonOperator op)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Column(new BinaryComparison(Expr, other.Expr, op));
    }

    private Column Comparison(object? value, ComparisonOperator op) =>
        new(new BinaryComparison(Expr, Functions.Lit(value).Expr, op));
}
