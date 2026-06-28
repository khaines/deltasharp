namespace DeltaSharp.Engine;

/// <summary>
/// Inert placeholder marking the DeltaSharp engine assembly seam.
/// </summary>
/// <remarks>
/// The engine targets <c>net10.0</c> only (ADR-0014) and contains no execution logic
/// in the M1 skeleton. It exists so future planning, scheduling, and executor code has
/// a stable assembly home that is separated from the public API surface.
/// </remarks>
public static class EngineBuildInfo
{
    /// <summary>
    /// Gets the target framework moniker this engine assembly is built for.
    /// </summary>
    public static string TargetFramework => "net10.0";
}
