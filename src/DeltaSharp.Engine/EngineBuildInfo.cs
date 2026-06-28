using System.Reflection;
using System.Runtime.Versioning;

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
    /// Gets the framework name this engine assembly was compiled against — for example
    /// <c>.NETCoreApp,Version=v10.0</c> — derived from the assembly's
    /// <see cref="TargetFrameworkAttribute"/> so it cannot drift from the real build.
    /// </summary>
    public static string FrameworkName =>
        typeof(EngineBuildInfo).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
        ?? "unknown";
}
