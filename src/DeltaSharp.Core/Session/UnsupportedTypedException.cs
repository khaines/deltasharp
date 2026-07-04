namespace DeltaSharp;

/// <summary>
/// The shared base for the two deterministic diagnostics the <see cref="Dataset{T}"/> typed
/// transformation bridge (STORY-04.2.4 / #163, AC4) raises when a typed input cannot be lowered onto
/// the shared logical plan model. It is <b>abstract</b>: a caller catches the concrete
/// <see cref="UnsupportedTypedExpressionException"/> (a <c>where</c>/<c>select</c> lambda node the
/// lowering does not understand) or <see cref="UnsupportedTypedSchemaException"/> (a property of the
/// encoded type <c>T</c> that the reflection schema deriver cannot map to a
/// <see cref="DeltaSharp.Types.DataType"/>) for catch-site precision, or catches this base to handle
/// both.
/// </summary>
/// <remarks>
/// Both diagnostics are raised eagerly at <b>plan-construction</b> time — when a typed transformation
/// is built or a <see cref="Dataset{T}"/> is first created — <b>not</b> during execution, so a caller
/// never reaches the engine with a shape the bridge cannot represent. Messages name the exact
/// offender (the unsupported node kind, or the property and its CLR type) so failures are reproducible
/// and machine-greppable. See <c>docs/engineering/design/dataset-typed-bridge.md</c>.
/// </remarks>
public abstract class UnsupportedTypedException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    protected UnsupportedTypedException()
    {
    }

    /// <summary>Initializes a new instance with a precise <paramref name="message"/>.</summary>
    protected UnsupportedTypedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    protected UnsupportedTypedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
