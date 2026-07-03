using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// A single row of a materialized action result, equivalent to Apache Spark's <c>Row</c> — an
/// ordered, schema-carrying tuple of column values with Spark-parity field access. A
/// <see cref="DataFrame"/> is conceptually an untyped <c>Dataset&lt;Row&gt;</c>, so
/// <see cref="DataFrame.Collect"/> returns <see cref="Row"/> values and <see cref="DataFrame.Show(int, bool)"/>
/// renders them.
/// </summary>
/// <remarks>
/// <para>
/// STORY-04.7.1 (#177) introduces this type as the public materialization contract that action
/// results preserve: field <see cref="Schema"/>, <b>ordinal</b> access (<see cref="this[int]"/>,
/// <see cref="GetAs{T}(int)"/>, <see cref="IsNullAt(int)"/>), and <b>by-name</b> access
/// (<see cref="this[string]"/>, <see cref="GetAs{T}(string)"/>, <see cref="FieldIndex(string)"/>),
/// with null-aware semantics throughout. A row carries its own <see cref="StructType"/> so a single
/// collected result is self-describing without a side channel.
/// </para>
/// <para>
/// A row is <b>immutable</b>: the supplied values are copied at construction and never exposed as a
/// mutable array. Values follow the ADR-0008 logical type model — a field's runtime value is the CLR
/// representation of its <see cref="StructField.DataType"/> (for example <see cref="int"/> for
/// <c>int</c>, <see cref="string"/> for <c>string</c>), or <see langword="null"/> when the field is
/// SQL <c>NULL</c>. See <c>docs/engineering/design/actions-and-row.md</c>.
/// </para>
/// <para>
/// The engine (the <c>DeltaSharp.Executor</c> backend, STORY-04.6.2 / #174) is what produces rows
/// from executed physical plans; user code does not usually construct <see cref="Row"/> instances,
/// but the constructors are public so tests and adapters can build canned rows.
/// </para>
/// <para>
/// A row has <b>structural value equality</b> (see <see cref="Equals(object?)"/>), matching Spark's
/// value-equal <c>Row</c>. <b>Deferred (M1):</b> the Spark-parity backlog — typed getters
/// (<c>getInt</c>/<c>getString</c>/…), <c>toSeq</c>/<c>mkString</c>, complex-type getters, a
/// <see cref="DataFrame.Show(int, bool)"/> truncate-width overload, and CJK display-width handling — is
/// tracked by <see href="https://github.com/khaines/deltasharp/issues/418">#418</see>.
/// </para>
/// </remarks>
public sealed class Row
{
    private readonly object?[] _values;

    /// <summary>
    /// Creates a row with the given <paramref name="schema"/> and column <paramref name="values"/>
    /// (one per schema field, in ordinal order).
    /// </summary>
    /// <param name="schema">The row's schema; its field count must equal <paramref name="values"/>.Length.</param>
    /// <param name="values">The column values in ordinal order (a <see langword="null"/> element is
    /// SQL <c>NULL</c>). The array is copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">The number of values does not match the schema field count.</exception>
    public Row(StructType schema, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != schema.Count)
        {
            throw new ArgumentException(
                $"Row value count ({values.Length}) does not match schema field count ({schema.Count}).",
                nameof(values));
        }

        Schema = schema;
        _values = (object?[])values.Clone();
    }

    /// <summary>
    /// Creates a row with the given <paramref name="schema"/> and column <paramref name="values"/>.
    /// </summary>
    /// <param name="schema">The row's schema; its field count must equal <paramref name="values"/> count.</param>
    /// <param name="values">The column values in ordinal order (copied).</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">The number of values does not match the schema field count.</exception>
    public Row(StructType schema, IReadOnlyList<object?> values)
        : this(schema, ToArray(values))
    {
    }

    /// <summary>The row's schema (never null). Mirrors Spark's <c>Row.schema</c>.</summary>
    public StructType Schema { get; }

    /// <summary>The number of columns in this row. Mirrors Spark's <c>Row.length</c>/<c>size</c>.</summary>
    public int Length => _values.Length;

    /// <summary>The number of columns in this row (alias of <see cref="Length"/>; Spark's <c>Row.size</c>).</summary>
    public int Size => _values.Length;

    /// <summary>
    /// Gets the value at <paramref name="ordinal"/> (Spark's <c>Row.apply(int)</c>/<c>get(int)</c>),
    /// or <see langword="null"/> when the field is SQL <c>NULL</c>.
    /// </summary>
    /// <param name="ordinal">The zero-based column position.</param>
    /// <returns>The boxed column value, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative or ≥ <see cref="Length"/>.</exception>
    public object? this[int ordinal]
    {
        get
        {
            if ((uint)ordinal >= (uint)_values.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordinal), ordinal,
                    $"Ordinal must be in the range [0, {_values.Length}).");
            }

            return _values[ordinal];
        }
    }

    /// <summary>
    /// Gets the value of the field named <paramref name="fieldName"/> (case-sensitive, Spark parity),
    /// or <see langword="null"/> when the field is SQL <c>NULL</c>.
    /// </summary>
    /// <param name="fieldName">The field name to look up.</param>
    /// <returns>The boxed column value, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is null.</exception>
    /// <exception cref="ArgumentException">No field is named <paramref name="fieldName"/>.</exception>
    public object? this[string fieldName] => _values[FieldIndex(fieldName)];

    /// <summary>Returns whether the value at <paramref name="ordinal"/> is SQL <c>NULL</c> (Spark's <c>Row.isNullAt</c>).</summary>
    /// <param name="ordinal">The zero-based column position.</param>
    /// <returns><see langword="true"/> when the field is null.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is out of range.</exception>
    public bool IsNullAt(int ordinal) => this[ordinal] is null;

    /// <summary>Returns whether any column in this row is SQL <c>NULL</c> (Spark's <c>Row.anyNull</c>).</summary>
    public bool AnyNull
    {
        get
        {
            for (int i = 0; i < _values.Length; i++)
            {
                if (_values[i] is null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Returns the ordinal position of the field named <paramref name="name"/> (case-sensitive),
    /// mirroring Spark's <c>Row.fieldIndex</c>.
    /// </summary>
    /// <param name="name">The field name.</param>
    /// <returns>The zero-based ordinal of the field.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">No field is named <paramref name="name"/>.</exception>
    public int FieldIndex(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int index = Schema.IndexOf(name);
        if (index < 0)
        {
            throw new ArgumentException(
                $"Field '{name}' does not exist. Available fields: "
                + $"[{string.Join(", ", Schema.Select(f => f.Name))}].",
                nameof(name));
        }

        return index;
    }

    /// <summary>
    /// Gets the value at <paramref name="ordinal"/> cast to <typeparamref name="T"/> (Spark's
    /// <c>Row.getAs[T](int)</c>).
    /// </summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="ordinal">The zero-based column position.</param>
    /// <returns>The value cast to <typeparamref name="T"/>; <see langword="default"/> when the value is
    /// null and <typeparamref name="T"/> is a reference type or <see cref="System.Nullable{T}"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is out of range.</exception>
    /// <exception cref="InvalidOperationException">The value is null and <typeparamref name="T"/> is a
    /// non-nullable value type. Use <see cref="IsNullAt(int)"/> or a nullable <typeparamref name="T"/>.</exception>
    /// <exception cref="InvalidCastException">The stored value is not assignable to <typeparamref name="T"/>.</exception>
    public T GetAs<T>(int ordinal)
    {
        object? value = this[ordinal];
        if (value is null)
        {
            if (default(T) is null)
            {
                return default!;
            }

            throw new InvalidOperationException(
                $"Field at ordinal {ordinal} ('{Schema[ordinal].Name}') is null and cannot be read as "
                + $"non-nullable type '{typeof(T)}'. Use IsNullAt(ordinal) or read it as a nullable type.");
        }

        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidCastException(
            $"Field at ordinal {ordinal} ('{Schema[ordinal].Name}') holds a value of type "
            + $"'{value.GetType()}' that cannot be cast to '{typeof(T)}'.");
    }

    /// <summary>
    /// Gets the value of the field named <paramref name="fieldName"/> cast to <typeparamref name="T"/>
    /// (Spark's <c>Row.getAs[T](String)</c>).
    /// </summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="fieldName">The field name (case-sensitive).</param>
    /// <returns>The value cast to <typeparamref name="T"/>; <see langword="default"/> when the value is
    /// null and <typeparamref name="T"/> is a reference type or <see cref="System.Nullable{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is null.</exception>
    /// <exception cref="ArgumentException">No field is named <paramref name="fieldName"/>.</exception>
    /// <exception cref="InvalidOperationException">The value is null and <typeparamref name="T"/> is a
    /// non-nullable value type.</exception>
    /// <exception cref="InvalidCastException">The stored value is not assignable to <typeparamref name="T"/>.</exception>
    public T GetAs<T>(string fieldName) => GetAs<T>(FieldIndex(fieldName));

    /// <summary>
    /// Renders the row as a bracketed, comma-separated list of its values (Spark's <c>Row.toString</c>),
    /// with SQL <c>NULL</c> shown as <c>null</c>. Intended for diagnostics, not a stable serialization
    /// format.
    /// </summary>
    /// <returns>For example <c>[Alice,30,null]</c>.</returns>
    public override string ToString()
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < _values.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Render(_values[i]));
        }

        return builder.Append(']').ToString();
    }

    /// <summary>
    /// Renders a single cell value for display (<see cref="ToString"/> and
    /// <see cref="DataFrame.Show(int, bool)"/>): <see langword="null"/> becomes <c>null</c>, booleans
    /// become lowercase, and numeric/temporal values use the invariant culture so output is
    /// deterministic across locales.
    /// </summary>
    internal static string Render(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    /// <summary>
    /// Determines whether <paramref name="obj"/> is a <see cref="Row"/> with the <b>same schema and the
    /// same values</b>, mirroring Spark's value-equal <c>Row</c>. Two rows are equal iff their
    /// <see cref="Schema"/>s are equal (<see cref="StructType"/> value equality) and every value is
    /// equal in ordinal order (null-aware — two SQL <c>NULL</c>s at the same ordinal are equal). This
    /// makes rows usable as expected results (<c>Assert.Equal(expected, row)</c>), in
    /// <see cref="System.Collections.Generic.HashSet{T}"/> de-duplication, and with
    /// <c>Contains</c>/<c>Distinct</c>.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a schema- and value-equal row.</returns>
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is not Row other || _values.Length != other._values.Length || !Schema.Equals(other.Schema))
        {
            return false;
        }

        for (int i = 0; i < _values.Length; i++)
        {
            if (!Equals(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns a hash code consistent with <see cref="Equals(object?)"/>: it combines the
    /// <see cref="Schema"/> hash with each value in ordinal order (null-aware), so schema- and
    /// value-equal rows hash equal and de-duplicate correctly in a hash set.
    /// </summary>
    /// <returns>A hash code over the schema and ordered values.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Schema);
        for (int i = 0; i < _values.Length; i++)
        {
            hash.Add(_values[i]);
        }

        return hash.ToHashCode();
    }

    private static object?[] ToArray(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var array = new object?[values.Count];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = values[i];
        }

        return array;
    }
}
