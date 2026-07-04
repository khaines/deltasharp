namespace DeltaSharp;

/// <summary>
/// Thrown by the <see cref="Dataset{T}"/> typed transformation bridge (STORY-04.2.4 / #163) when a
/// node in a typed <c>where</c>/<c>select</c> <b>lambda</b> cannot be lowered onto the shared logical
/// plan model — an unsupported operator (for example a bitwise <c>&amp;</c>/<c>|</c>), a method call,
/// a nested member access, or a parameter-independent subexpression the bridge cannot fold to a
/// constant.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>lambda-lowering</b> half of the story's AC4 diagnostic; the <b>schema-mapping</b>
/// half (an unmappable property of the encoded type <c>T</c>) is the sibling
/// <see cref="UnsupportedTypedSchemaException"/>. Both derive from
/// <see cref="UnsupportedTypedException"/>, so a <c>catch</c> can target one precisely or the base to
/// handle both. It is raised eagerly at <b>plan-construction</b> time — when a typed transformation
/// is built — <b>not</b> during execution, so a caller never reaches the engine with a shape the
/// bridge cannot represent. The message names the exact unsupported <c>System.Linq.Expressions</c>
/// node kind or member, so failures are reproducible and machine-greppable rather than surfacing as a
/// raw expression-tree error.
/// </para>
/// <para>
/// The bridge deliberately owns only the typed-transformation and schema-derivation seam; the full
/// <c>Row</c>&#8596;<c>T</c> value encoders are a separate deferred story (STORY-04.7.2 / #178). See
/// <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </para>
/// </remarks>
public sealed class UnsupportedTypedExpressionException : UnsupportedTypedException
{
    /// <summary>Initializes a new instance.</summary>
    public UnsupportedTypedExpressionException()
    {
    }

    /// <summary>Initializes a new instance with a precise <paramref name="message"/>.</summary>
    public UnsupportedTypedExpressionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public UnsupportedTypedExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
