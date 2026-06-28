namespace DeltaSharp.Engine.Types;

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
/// <see cref="DataType.TryGetPhysicalLayout"/> to branch without exceptions on the hot path.
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
