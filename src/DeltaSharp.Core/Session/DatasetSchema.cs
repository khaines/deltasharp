using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// Derives a DeltaSharp <see cref="StructType"/> schema from the public properties of an encoded
/// type <c>T</c> (STORY-04.2.4 / #163, AC3). This is the schema half of the <see cref="Dataset{T}"/>
/// typed bridge: it maps each readable public instance property to a <see cref="StructField"/> whose
/// <see cref="StructField.DataType"/> follows the read-door's CLR&#8594;<see cref="DataType"/> value
/// contract and whose <see cref="StructField.Nullable"/> flag follows ADR-0008 nullability
/// (<see cref="System.Nullable{T}"/>/reference types &#8594; nullable; non-nullable value types
/// &#8594; non-nullable).
/// </summary>
/// <remarks>
/// This type performs <b>no</b> value materialization — it reads only property <i>metadata</i>, never
/// property values — so deriving a schema never instantiates <c>T</c> or reads a row. The full
/// <c>Row</c>&#8596;<c>T</c> value encoders are deferred to STORY-04.7.2 (#178). See
/// <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </remarks>
internal static class DatasetSchema
{
    /// <summary>
    /// Reflects <typeparamref name="T"/>'s public, readable, non-indexer instance properties into an
    /// ordered <see cref="StructType"/>. <b>Inherited</b> public properties are included (matching
    /// Spark's JavaBean/product encoders, which project inherited getters). Properties are ordered
    /// <b>base-class first</b> — by inheritance depth, most-derived last — and, within a single
    /// declaring type, by <see cref="MemberInfo.MetadataToken"/> (declaration order). Ordering by
    /// metadata token alone is only stable <i>within</i> one type's metadata scope; combining it with
    /// inheritance depth keeps the derived schema deterministic even when a base type lives in another
    /// module or assembly, rather than depending on the unspecified order of
    /// <see cref="System.Type.GetProperties()"/>.
    /// </summary>
    /// <typeparam name="T">The encoded record/POCO type whose properties define the schema.</typeparam>
    /// <returns>The derived schema (an empty struct when <typeparamref name="T"/> has no mappable
    /// properties).</returns>
    /// <exception cref="UnsupportedTypedSchemaException">A property's CLR type has no ADR-0008
    /// <see cref="DataType"/> mapping.</exception>
    public static StructType Derive<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>()
    {
        PropertyInfo[] properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var fields = new List<StructField>(properties.Length);
        foreach (PropertyInfo property in properties
                     .Where(static p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .OrderByDescending(p => InheritanceDepth(typeof(T), p.DeclaringType))
                     .ThenBy(static p => p.MetadataToken))
        {
            fields.Add(ToField(property));
        }

        return new StructType(fields);
    }

    // The number of base-type hops from T down to the property's declaring type. A property declared
    // on T itself has depth 0; one inherited from T's base has depth 1, and so on. Ordering by
    // DESCENDING depth therefore lists base-class properties before derived ones (Spark-bean parity).
    private static int InheritanceDepth(Type type, Type? declaringType)
    {
        int depth = 0;
        for (Type? current = type; current is not null && current != declaringType; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static StructField ToField(PropertyInfo property)
    {
        Type propertyType = property.PropertyType;

        // ADR-0008 nullability: a Nullable<U> value type or any reference type may hold null; a
        // non-nullable value type (int, bool, DateOnly, …) may not. C# nullable-reference-type
        // annotations (string vs string?) are intentionally NOT consulted in v1 — Spark's product/bean
        // encoders treat every object field as nullable, and reading the annotations would add a
        // trim/AOT-sensitive NullabilityInfoContext dependency for no parity gain (see the design doc).
        Type? underlying = Nullable.GetUnderlyingType(propertyType);
        bool nullable = underlying is not null || !propertyType.IsValueType;
        Type mappedType = underlying ?? propertyType;

        DataType dataType = MapClrType(mappedType)
            ?? throw new UnsupportedTypedSchemaException(
                $"Property '{property.DeclaringType?.Name}.{property.Name}' of CLR type "
                + $"'{propertyType.Name}' has no supported DeltaSharp schema mapping. Supported "
                + "property types are bool, sbyte, short, int, long, float, double, decimal, string, "
                + "byte[], System.DateOnly, and System.DateTime (and their Nullable<> forms).");

        return new StructField(property.Name, dataType, nullable);
    }

    // The CLR->DataType mapping mirrors the read-door value contract enforced by the executor's
    // LocalRelationBatches.Append (src/DeltaSharp.Executor/Physical/LocalRelationBatches.cs) and
    // documented in docs/engineering/design/read-door.md, so a schema derived here binds to values a
    // Row can carry once STORY-04.7.2 (#178) lands the value encoders. Widening is intentionally NOT
    // performed (an int property maps to IntegerType, never LongType).
    internal static DataType? MapClrType(Type type)
    {
        if (type == typeof(bool))
        {
            return BooleanType.Instance;
        }

        if (type == typeof(sbyte))
        {
            return ByteType.Instance;
        }

        if (type == typeof(short))
        {
            return ShortType.Instance;
        }

        if (type == typeof(int))
        {
            return IntegerType.Instance;
        }

        if (type == typeof(long))
        {
            return LongType.Instance;
        }

        if (type == typeof(float))
        {
            return FloatType.Instance;
        }

        if (type == typeof(double))
        {
            return DoubleType.Instance;
        }

        if (type == typeof(decimal))
        {
            // Spark maps an unparameterised decimal to DecimalType.SYSTEM_DEFAULT = decimal(38,18);
            // DeltaSharp mirrors that so a decimal property has a well-defined precision/scale.
            return new DecimalType(DecimalType.MaxPrecision, DefaultDecimalScale);
        }

        if (type == typeof(string))
        {
            return StringType.Instance;
        }

        if (type == typeof(byte[]))
        {
            return BinaryType.Instance;
        }

        if (type == typeof(DateOnly))
        {
            return DateType.Instance;
        }

        if (type == typeof(DateTime))
        {
            return TimestampType.Instance;
        }

        return null;
    }

    // Spark's DecimalType.SYSTEM_DEFAULT scale.
    private const int DefaultDecimalScale = 18;
}
