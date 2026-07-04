namespace DeltaSharp;

/// <summary>
/// Thrown by the <see cref="Dataset{T}"/> typed bridge (STORY-04.2.4 / #163) when the reflection
/// <b>schema deriver</b> cannot map a public property of the encoded type <c>T</c> to an ADR-0008
/// <see cref="DeltaSharp.Types.DataType"/>. This is the <b>schema-mapping</b> half of the AC4
/// diagnostic; the <b>lambda-lowering</b> half is <see cref="UnsupportedTypedExpressionException"/>.
/// The two are distinct so a <c>catch</c> for a bad <c>where</c>/<c>select</c> lambda does not also
/// swallow an unmappable POCO property (and vice versa); catch the shared
/// <see cref="UnsupportedTypedException"/> base to handle both.
/// </summary>
/// <remarks>
/// Raised eagerly when a <see cref="Dataset{T}"/> is first created (via
/// <see cref="DataFrame.As{T}"/>), never during execution. The message names the exact offending
/// property and its CLR type. See <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </remarks>
public sealed class UnsupportedTypedSchemaException : UnsupportedTypedException
{
    /// <summary>Initializes a new instance.</summary>
    public UnsupportedTypedSchemaException()
    {
    }

    /// <summary>Initializes a new instance with a precise <paramref name="message"/>.</summary>
    public UnsupportedTypedSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public UnsupportedTypedSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
