namespace DeltaSharp.Types;

/// <summary>
/// Internal, non-shipped marker that keeps <c>DeltaSharp.Abstractions</c> a non-empty
/// compilation while it is scaffolded (ADR-0016, STORY-04.T.S1a). The assembly is
/// intentionally free of type-model code in this story; the ADR-0008 logical type model
/// (<c>DataType</c> hierarchy, <c>DataTypes</c> factory, <c>TypeCoercion</c>,
/// <c>AnsiMode</c>, the type/coercion exceptions, and the internal <c>StableHash</c>)
/// MOVES here atomically in S1b+S2. Being <c>internal</c>, it contributes nothing to the
/// governed PublicAPI baseline (which is trivially empty for now).
/// </summary>
internal static class AbstractionsAssemblyMarker
{
}
