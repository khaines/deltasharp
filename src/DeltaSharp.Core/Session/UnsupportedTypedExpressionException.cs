namespace DeltaSharp;

/// <summary>
/// Thrown by the <see cref="Dataset{T}"/> typed transformation bridge (STORY-04.2.4 / #163) when a
/// typed input cannot be lowered onto the shared logical plan model — either a property of the
/// encoded type <c>T</c> that the reflection schema deriver cannot map to an ADR-0008
/// <see cref="DeltaSharp.Types.DataType"/>, or a node in a typed <c>where</c>/<c>select</c> lambda
/// that the expression lowering does not understand.
/// </summary>
/// <remarks>
/// <para>
/// This is the single, deterministic <b>unsupported-expression diagnostic</b> the story's AC4 calls
/// for. It is raised eagerly at <b>plan-construction</b> time — when a typed transformation is built
/// or when a <see cref="Dataset{T}"/> is first created — <b>not</b> during execution, so a caller
/// never reaches the engine with a shape the bridge cannot represent. The message names the exact
/// offending property (and its CLR type) or the exact unsupported <c>System.Linq.Expressions</c> node
/// kind, so failures are reproducible and machine-greppable rather than surfacing as a raw
/// reflection or expression-tree error.
/// </para>
/// <para>
/// The bridge deliberately owns only the typed-transformation and schema-derivation seam; the full
/// <c>Row</c>&#8596;<c>T</c> value encoders are a separate deferred story (STORY-04.7.2 / #178). See
/// <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </para>
/// </remarks>
public sealed class UnsupportedTypedExpressionException : Exception
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
