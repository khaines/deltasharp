using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// The decode half of the <see cref="Dataset{T}"/> value encoder (STORY-04.7.2 / #178): turns
/// collected <see cref="Row"/>s into instances of the encoded type <typeparamref name="T"/> so a typed
/// <see cref="Dataset{T}.Collect()"/> can materialize <typeparamref name="T"/> values without leaving
/// Spark semantics. It is the counterpart to <see cref="DatasetSchema"/> (the schema half): it reuses
/// the exact <see cref="DatasetSchema.MappableProperties{T}"/> ordering, so the decoder's
/// property&#8596;column binding never drifts from the derived <see cref="Schema"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bean model (M1).</b> A decoder constructs <typeparamref name="T"/> once per row through its
/// public parameterless constructor and then assigns each mapped column onto a public settable
/// (<c>set</c>/<c>init</c>) property. Instance creation and per-column value preparation are shared;
/// the only per-tier difference is how the prepared value is assigned (see
/// <see cref="RowDecoderFactory"/>): an AOT-safe reflection <see cref="PropertyInfo.SetValue(object, object)"/>
/// by default, or an optional guarded compiled setter — both produce identical results (ADR-0001 parity
/// oracle). See <c>docs/engineering/design/dataset-encoders.md</c>.
/// </para>
/// <para>
/// <b>No <see cref="Row"/> mutation.</b> Decoding only <i>reads</i> a row (its by-name indexer) and
/// writes into the freshly constructed <typeparamref name="T"/>; the source row is never modified.
/// <see cref="BinaryType"/> (<c>byte[]</c>) cells are <b>copied</b> into <typeparamref name="T"/> so the
/// decoded value never aliases the row's array.
/// </para>
/// <para>
/// A decoder is built once per <typeparamref name="T"/> (cached by
/// <see cref="TypedRowDecoderCache{T}"/>) and is immutable after construction, so
/// <see cref="Decode"/> is safe to call concurrently — every call allocates a fresh
/// <typeparamref name="T"/> and shares no mutable state.
/// </para>
/// </remarks>
/// <typeparam name="T">The encoded record/POCO type materialized from each row.</typeparam>
internal sealed class RowDecoder<[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicProperties |
    DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>
{
    private readonly Func<object> _factory;
    private readonly PropertyDecodeBinding[] _bindings;

    /// <summary>Creates a decoder over a fixed schema, instance factory, and column bindings.</summary>
    /// <param name="schema">The schema derived from <typeparamref name="T"/> (for introspection/tests).</param>
    /// <param name="factory">Creates a fresh, default-initialized <typeparamref name="T"/> (its public
    /// parameterless constructor); shared by both setter tiers so construction is identical.</param>
    /// <param name="bindings">One binding per mapped property, in the derived schema's field order.</param>
    /// <param name="usesCompiledSetters"><see langword="true"/> when the optional compiled setter tier
    /// (ADR-0001) is active; <see langword="false"/> for the AOT-safe reflection tier.</param>
    internal RowDecoder(
        StructType schema,
        Func<object> factory,
        PropertyDecodeBinding[] bindings,
        bool usesCompiledSetters)
    {
        Schema = schema;
        _factory = factory;
        _bindings = bindings;
        UsesCompiledSetters = usesCompiledSetters;
    }

    /// <summary>The schema derived from <typeparamref name="T"/>; identical to
    /// <see cref="Dataset{T}.Schema"/> and stable across repeated derivations (AC1).</summary>
    internal StructType Schema { get; }

    /// <summary>Whether the optional compiled setter tier is in use (true only when
    /// <see cref="System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"/> is true and
    /// the tier was not forced off). Both tiers decode to identical <typeparamref name="T"/> values.</summary>
    internal bool UsesCompiledSetters { get; }

    /// <summary>The mapped column names, in binding order (for AC1 property&#8596;ordinal determinism tests).</summary>
    internal IReadOnlyList<string> BoundColumns
    {
        get
        {
            var names = new string[_bindings.Length];
            for (int i = 0; i < _bindings.Length; i++)
            {
                names[i] = _bindings[i].ColumnName;
            }

            return names;
        }
    }

    /// <summary>
    /// Materializes a single <typeparamref name="T"/> from <paramref name="row"/> following ADR-0008
    /// type/null semantics. The row is only read (never mutated).
    /// </summary>
    /// <param name="row">The collected row to decode.</param>
    /// <returns>A fresh <typeparamref name="T"/> whose mapped properties carry the row's values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
    /// <exception cref="ArgumentException">A mapped column is absent from the row (by-name binding).</exception>
    /// <exception cref="InvalidOperationException">A SQL <c>NULL</c> maps to a non-nullable value-typed property.</exception>
    /// <exception cref="InvalidCastException">A cell's runtime type is not the property's expected type.</exception>
    internal T Decode(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        object instance = _factory();
        foreach (PropertyDecodeBinding binding in _bindings)
        {
            binding.Apply(row, instance);
        }

        return (T)instance;
    }
}

/// <summary>
/// Binds one mapped public property of an encoded type to its <see cref="Row"/> column and applies the
/// decoded value onto a constructed instance. It owns the ADR-0008 value preparation (null-handling,
/// runtime-type check, <c>byte[]</c> copy); the assignment step is a delegate so the reflection and
/// compiled tiers (<see cref="RowDecoderFactory"/>) can supply different setters over the <b>same</b>
/// prepared value — the structural basis of the ADR-0001 parity oracle.
/// </summary>
internal sealed class PropertyDecodeBinding
{
    private readonly Action<object, object?> _setter;

    /// <summary>Creates a binding for <paramref name="property"/> that assigns via <paramref name="setter"/>.</summary>
    /// <param name="property">The mapped, settable public property (validated by the factory).</param>
    /// <param name="setter">The assignment delegate (reflection or compiled) invoked with the prepared value.</param>
    internal PropertyDecodeBinding(PropertyInfo property, Action<object, object?> setter)
    {
        ColumnName = property.Name;
        PropertyType = property.PropertyType;
        DeclaringTypeName = property.DeclaringType?.Name ?? typeof(object).Name;

        // ADR-0008 nullability, matching DatasetSchema.ToField: a Nullable<U> value type or any
        // reference type accepts SQL NULL; a non-nullable value type does not. ExpectedType is the
        // logical CLR value type the cell must carry (the Nullable<> unwrapped underlying type).
        Type? underlying = Nullable.GetUnderlyingType(PropertyType);
        AllowsNull = underlying is not null || !PropertyType.IsValueType;
        ExpectedType = underlying ?? PropertyType;
        _setter = setter;
    }

    /// <summary>The row column this property binds to (the property name; by-name, case-sensitive).</summary>
    internal string ColumnName { get; }

    /// <summary>The declared CLR property type (for diagnostics).</summary>
    internal Type PropertyType { get; }

    /// <summary>The declaring type's simple name (for diagnostics).</summary>
    internal string DeclaringTypeName { get; }

    /// <summary>The logical CLR type the cell must be (the <see cref="Nullable{T}"/>-unwrapped type).</summary>
    internal Type ExpectedType { get; }

    /// <summary>Whether this property may hold SQL <c>NULL</c> (Nullable value type or reference type).</summary>
    internal bool AllowsNull { get; }

    /// <summary>Prepares the row's value for this column and assigns it onto <paramref name="instance"/>.</summary>
    /// <param name="row">The source row (read-only).</param>
    /// <param name="instance">The encoded instance being populated.</param>
    internal void Apply(Row row, object instance) => _setter(instance, PrepareValue(row));

    /// <summary>
    /// Reads and validates the cell for <see cref="ColumnName"/> per ADR-0008 without mutating the row:
    /// a SQL <c>NULL</c> becomes <see langword="null"/> for a nullable target (else a deterministic
    /// throw), a non-null cell must already be the <see cref="ExpectedType"/>, and a <c>byte[]</c> cell
    /// is copied so the decoded value does not alias the row's array.
    /// </summary>
    private object? PrepareValue(Row row)
    {
        // By-name access (Spark case-class encoders bind by column name). Row's indexer is
        // case-sensitive and throws a deterministic ArgumentException naming the missing column.
        object? cell = row[ColumnName];

        if (cell is null)
        {
            if (AllowsNull)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Cannot decode SQL NULL from column '{ColumnName}' into the non-nullable property "
                + $"'{DeclaringTypeName}.{ColumnName}' of type '{FriendlyName(PropertyType)}'. Declare the "
                + $"property as '{FriendlyName(PropertyType)}?' (Nullable) to accept NULL values.");
        }

        if (!ExpectedType.IsInstanceOfType(cell))
        {
            throw new InvalidCastException(
                $"Cannot decode column '{ColumnName}' value of runtime type '{cell.GetType().Name}' into "
                + $"the property '{DeclaringTypeName}.{ColumnName}' of type '{FriendlyName(PropertyType)}'.");
        }

        // byte[] (BinaryType) is copied so the decoded T never aliases the row's array (value
        // semantics); every other supported cell is an immutable value shared by reference.
        if (cell is byte[] bytes)
        {
            return bytes.AsSpan().ToArray();
        }

        return cell;
    }

    /// <summary>A stable, culture-independent display name for a CLR type used in diagnostics
    /// (<c>Int32?</c>, <c>byte[]</c>, <c>String</c>), so decode errors are deterministic and readable.</summary>
    internal static string FriendlyName(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return FriendlyName(underlying) + "?";
        }

        if (type == typeof(byte[]))
        {
            return "byte[]";
        }

        return type.Name;
    }
}
