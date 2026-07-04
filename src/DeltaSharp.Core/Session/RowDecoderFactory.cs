using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// Builds the <see cref="RowDecoder{T}"/> value encoder for a <see cref="Dataset{T}"/> (STORY-04.7.2 /
/// #178), owning the AOT-safety crux (ADR-0001). The <b>default</b> setter tier is pure reflection
/// (<see cref="PropertyInfo.SetValue(object, object)"/>) so the entire encoder is NativeAOT-safe with no
/// unguarded dynamic code. An <b>optional</b> compiled setter tier (LINQ expression delegates) is used
/// only when <see cref="RuntimeFeature.IsDynamicCodeSupported"/> is <see langword="true"/>, is guarded so
/// the trim/AOT analyzers can prove it is elided from an AOT image, and produces results identical to the
/// reflection tier (the parity oracle). See <c>docs/engineering/design/dataset-encoders.md</c>.
/// </summary>
internal static class RowDecoderFactory
{
    /// <summary>
    /// Whether the optional compiled setter tier may be used. This is the single ADR-0001 feature gate:
    /// on a runtime without dynamic code (NativeAOT) it is <see langword="false"/>, so
    /// <see cref="Create{T}()"/> stays on the reflection tier and the AOT analyzer proves the compiled
    /// path unreachable. On .NET 9+ it carries
    /// <c>[FeatureGuard(typeof(RequiresDynamicCodeAttribute))]</c> so the analyzer
    /// treats a guarded <c>[RequiresDynamicCode]</c> call site as satisfied; that attribute does not
    /// exist on net8.0, where the guarded call site instead suppresses IL3050 locally.
    /// </summary>
#if NET9_0_OR_GREATER
    [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
#endif
    internal static bool UseCompiledSetters => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// Builds the decoder for <typeparamref name="T"/> using the runtime-appropriate setter tier
    /// (compiled when <see cref="UseCompiledSetters"/> is <see langword="true"/>, otherwise reflection).
    /// </summary>
    /// <typeparam name="T">The encoded record/POCO type.</typeparam>
    /// <returns>An immutable decoder ready to turn rows into <typeparamref name="T"/> values.</returns>
    /// <exception cref="UnsupportedTypedSchemaException"><typeparamref name="T"/> is not an encodable bean
    /// (value type, abstract/interface, no public parameterless constructor, a mapped property has no
    /// public setter, or two mapped properties share a name).</exception>
    internal static RowDecoder<T> Create<[DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>() =>
        Create<T>(forceReflectionSetters: false);

    /// <summary>
    /// Builds the decoder for <typeparamref name="T"/>, optionally forcing the AOT-safe reflection tier
    /// regardless of <see cref="UseCompiledSetters"/>. Forcing reflection lets tests exercise the default
    /// tier on a JIT runtime and assert both tiers decode to identical values (the ADR-0001 parity
    /// oracle); production callers use the parameterless overload.
    /// </summary>
    /// <typeparam name="T">The encoded record/POCO type.</typeparam>
    /// <param name="forceReflectionSetters"><see langword="true"/> to always use the reflection tier.</param>
    /// <returns>An immutable decoder ready to turn rows into <typeparamref name="T"/> values.</returns>
    /// <exception cref="UnsupportedTypedSchemaException"><typeparamref name="T"/> is not an encodable bean.</exception>
    internal static RowDecoder<T> Create<[DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(bool forceReflectionSetters)
    {
        Type type = typeof(T);
        ValidateEncodable(type);

        // Fail fast on the structural requirement (a public parameterless constructor) before any schema
        // work, so an unsupported shape such as a positional record yields its guiding diagnostic first.
        ConstructorInfo constructor = GetParameterlessConstructor(type);

        // Reuse the schema deriver: it validates every mapped property TYPE (throwing
        // UnsupportedTypedSchemaException naming the member + unsupported type) and detects an ambiguous
        // (duplicated) column name, and it fixes the field order. The decoder aligns to the SAME ordered
        // property list, so property index i binds to schema field i (AC1 property<->ordinal binding).
        StructType schema = DatasetSchema.Derive<T>();
        PropertyInfo[] properties = DatasetSchema.MappableProperties<T>();

        var bindings = new PropertyDecodeBinding[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            ValidateSettable(type, properties[i]);
        }

        Action<object, object?>[] setters = BuildSetters(properties, forceReflectionSetters, out bool useCompiled);
        for (int i = 0; i < properties.Length; i++)
        {
            bindings[i] = new PropertyDecodeBinding(properties[i], setters[i]);
        }

        Func<object> factory = () => constructor.Invoke(null);
        return new RowDecoder<T>(schema, factory, bindings, useCompiled);
    }

    /// <summary>
    /// Chooses the setter tier once. The compiled tier is taken only through the <b>direct</b>
    /// <see cref="UseCompiledSetters"/> guard (mirroring the ADR-0001 <c>ExecutionBackends</c> exemplar)
    /// so the .NET 9+ feature-guard analyzer recognizes it. On net8.0 (which lacks
    /// <c>FeatureGuardAttribute</c>) the guarded <see cref="RequiresDynamicCodeAttribute"/> call cannot be
    /// proven elided by the analyzer, so IL3050 is suppressed with an
    /// <see cref="UnconditionalSuppressMessageAttribute"/> — honored by BOTH the build analyzer AND the
    /// NativeAOT publish (ILC), unlike a <c>#pragma</c> which the C# compiler applies but ILC ignores. The
    /// same <see cref="RuntimeFeature.IsDynamicCodeSupported"/> runtime guard keeps it safe: under AOT the
    /// gate is false and the compiled tier is never invoked (the reflection tier runs).
    /// </summary>
#if !NET9_0_OR_GREATER
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification =
            "The compiled setter tier is invoked only when RuntimeFeature.IsDynamicCodeSupported is true; "
            + "under NativeAOT the gate is false and the AOT-safe reflection tier runs. net8.0 lacks "
            + "[FeatureGuard] to prove this to the analyzer, and #pragma warning disable IL3050 is not "
            + "honored by the ILC publish step, so the guarantee is asserted here.")]
#endif
    private static Action<object, object?>[] BuildSetters(
        PropertyInfo[] properties,
        bool forceReflectionSetters,
        out bool usesCompiled)
    {
        if (!forceReflectionSetters)
        {
            if (UseCompiledSetters)
            {
                usesCompiled = true;
                return CompileSetters(properties);
            }
        }

        usesCompiled = false;
        var setters = new Action<object, object?>[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            setters[i] = ReflectionSetter(properties[i]);
        }

        return setters;
    }

    /// <summary>Builds the AOT-safe reflection setter: assigns via <see cref="PropertyInfo.SetValue(object, object)"/>.</summary>
    private static Action<object, object?> ReflectionSetter(PropertyInfo property) =>
        (instance, value) => property.SetValue(instance, value);

    /// <summary>
    /// Builds the optional compiled setter tier (ADR-0001 codegen tier): one LINQ expression delegate per
    /// property that calls the property's <c>set</c> accessor. It is reached only through the
    /// <see cref="UseCompiledSetters"/> guard and is annotated <see cref="RequiresDynamicCodeAttribute"/>
    /// so the AOT analyzer proves it is elided from an AOT image. Each delegate assigns the <b>same</b>
    /// prepared value the reflection tier would, so both tiers decode to identical values.
    /// </summary>
    [RequiresDynamicCode(
        "The compiled Dataset<T> setter tier emits IL via Expression.Compile; the AOT-safe default is the "
        + "reflection tier, selected automatically when RuntimeFeature.IsDynamicCodeSupported is false.")]
    private static Action<object, object?>[] CompileSetters(PropertyInfo[] properties)
    {
        var setters = new Action<object, object?>[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            setters[i] = CompileSetter(properties[i]);
        }

        return setters;
    }

    /// <summary>Compiles a single property's <c>set</c> accessor into an <see cref="Action{T1, T2}"/>.</summary>
    [RequiresDynamicCode(
        "The compiled Dataset<T> setter tier emits IL via Expression.Compile; the AOT-safe default is the "
        + "reflection tier, selected automatically when RuntimeFeature.IsDynamicCodeSupported is false.")]
    private static Action<object, object?> CompileSetter(PropertyInfo property)
    {
        MethodInfo setMethod = property.SetMethod!;
        ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
        ParameterExpression value = Expression.Parameter(typeof(object), "value");

        // ((TDeclaring)instance).set_Property((TProperty)value)
        UnaryExpression typedInstance = Expression.Convert(instance, property.DeclaringType!);
        UnaryExpression typedValue = Expression.Convert(value, property.PropertyType);
        MethodCallExpression body = Expression.Call(typedInstance, setMethod, typedValue);

        var lambda = Expression.Lambda<Action<object, object?>>(body, instance, value);
#pragma warning disable RS0030 // Banned API: Expression.Compile — the scoped ADR-0001 codegen tier, guarded by UseCompiledSetters.
        return lambda.Compile();
#pragma warning restore RS0030
    }

    private static void ValidateEncodable(Type type)
    {
        if (type.IsValueType)
        {
            throw new UnsupportedTypedSchemaException(
                $"Type '{type.Name}' cannot be used as a Dataset<T> element type because it is a value type. "
                + "Typed encoding (STORY-04.7.2) supports reference types (classes and records) with a public "
                + "parameterless constructor.");
        }

        if (type.IsAbstract || type.IsInterface)
        {
            throw new UnsupportedTypedSchemaException(
                $"Type '{type.Name}' cannot be used as a Dataset<T> element type because it is "
                + $"{(type.IsInterface ? "an interface" : "abstract")} and cannot be instantiated. Provide a "
                + "concrete class or record with a public parameterless constructor.");
        }
    }

    [return: NotNull]
    private static ConstructorInfo GetParameterlessConstructor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type type)
    {
        ConstructorInfo? constructor = type.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
        {
            throw new UnsupportedTypedSchemaException(
                $"Type '{type.Name}' cannot be used as a Dataset<T> element type because it has no public "
                + "parameterless constructor. Positional records (whose only constructor takes parameters) are "
                + "not supported in M1; use a class or record with an 'init'/'set' property for each column and "
                + "a public parameterless constructor.");
        }

        return constructor;
    }

    private static void ValidateSettable(Type type, PropertyInfo property)
    {
        if (property.SetMethod is null || !property.SetMethod.IsPublic)
        {
            throw new UnsupportedTypedSchemaException(
                $"Property '{type.Name}.{property.Name}' of type "
                + $"'{PropertyDecodeBinding.FriendlyName(property.PropertyType)}' cannot be decoded because it has "
                + "no public setter. Give the property an 'init' or 'set' accessor so the typed collect can assign "
                + "its column value.");
        }
    }
}

/// <summary>
/// Per-<typeparamref name="T"/> cache of the built <see cref="RowDecoder{T}"/>, mirroring
/// <see cref="TypedSchemaCache{T}"/>. The decoder (and any diagnostic thrown while building it) is
/// computed once via <see cref="Lazy{T}"/> and reused, so repeated typed collects share one decoder and
/// an <see cref="UnsupportedTypedSchemaException"/> is re-thrown deterministically (same input &#8594;
/// identical message).
/// </summary>
/// <typeparam name="T">The encoded record/POCO type.</typeparam>
internal static class TypedRowDecoderCache<[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicProperties |
    DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>
{
    private static readonly Lazy<RowDecoder<T>> Cached = new(RowDecoderFactory.Create<T>);

    /// <summary>The cached decoder for <typeparamref name="T"/> (built on first access).</summary>
    internal static RowDecoder<T> Value => Cached.Value;
}
