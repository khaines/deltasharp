namespace DeltaSharp.Types;

/// <summary>
/// Thrown when a schema or type definition is invalid — for example a struct with duplicate
/// field names, a map with an unsupported key type, or a decimal whose precision/scale is
/// out of range (STORY-02.5.1 AC2). The message names the offending element precisely.
/// </summary>
public sealed class SchemaValidationException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public SchemaValidationException()
    {
    }

    /// <summary>Initializes a new instance with a precise validation <paramref name="message"/>.</summary>
    public SchemaValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public SchemaValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a logical type has no supported physical representation for a builder that
/// requires one (STORY-02.5.1 AC4) — for example <see cref="NullType"/>. Prefer
/// <c>PhysicalLayoutResolver.TryResolve</c> to branch without exceptions on the hot path.
/// </summary>
public sealed class UnsupportedTypeException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public UnsupportedTypeException()
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/>.</summary>
    public UnsupportedTypeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public UnsupportedTypeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an implicit type coercion is not supported between two types (STORY-02.5.2
/// AC4). The message identifies the source type, the target type, and the expression path
/// (for example <c>items.element.price</c>) so a nested mismatch points at the exact field.
/// </summary>
public sealed class TypeCoercionException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public TypeCoercionException()
    {
    }

    /// <summary>Initializes a new instance with a precise coercion <paramref name="message"/>.</summary>
    public TypeCoercionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public TypeCoercionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The source type that could not be coerced; non-null for path-aware throws.</summary>
    public string? SourceType { get; private init; }

    /// <summary>The target type the source could not be coerced to; non-null for path-aware throws.</summary>
    public string? TargetType { get; private init; }

    /// <summary>The dotted expression path to the offending element; non-null for path-aware throws.</summary>
    public string? Path { get; private init; }

    /// <summary>
    /// Builds an exception naming the source type, target type, and expression path. The path
    /// uses dotted/<c>element</c>/<c>key</c>/<c>value</c> segments so nested mismatches are precise.
    /// </summary>
    public static TypeCoercionException ForPath(DataType source, DataType target, string path) =>
        new($"Cannot coerce '{source.SimpleString}' to '{target.SimpleString}' at '{path}'.")
        {
            SourceType = source.SimpleString,
            TargetType = target.SimpleString,
            Path = path,
        };
}

/// <summary>
/// Thrown when an arithmetic or cast result exceeds the target type's precision/scale (decimal)
/// or value range (integral/temporal) under <see cref="AnsiMode.Ansi"/> (STORY-02.5.2 AC2).
/// Under <see cref="AnsiMode.Legacy"/> the same condition yields SQL <c>NULL</c> instead.
/// </summary>
public sealed class ArithmeticOverflowException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public ArithmeticOverflowException()
    {
    }

    /// <summary>Initializes a new instance with a precise overflow <paramref name="message"/>.</summary>
    public ArithmeticOverflowException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public ArithmeticOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
