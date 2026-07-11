using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Executor;

/// <summary>
/// The single place that resolves a <see cref="SinkDescriptor"/>'s write-target path, so the in-memory
/// registry (<see cref="InMemorySinkRegistry"/>) and the Delta sink (<see cref="DeltaLocalSink"/>) never
/// drift on how a path is discovered. The <c>DataFrameWriter</c> normally reconciles a <c>path</c> option
/// into <see cref="SinkDescriptor.Path"/>, but a descriptor built without that reconciliation still routes
/// correctly because a case-insensitive <c>path</c> option is honored here too (matching the writer's
/// OrdinalIgnoreCase option contract).
/// </summary>
internal static class SinkDescriptorPaths
{
    /// <summary>The explicit path: <see cref="SinkDescriptor.Path"/> when set, else a case-insensitive
    /// <c>path</c> option, else <see langword="null"/> (the caller decides the fallback/error).</summary>
    public static string? ResolvePath(SinkDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.IsNullOrEmpty(descriptor.Path))
        {
            return descriptor.Path;
        }

        foreach (KeyValuePair<string, string> option in descriptor.Options)
        {
            if (string.Equals(option.Key, "path", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(option.Value))
            {
                return option.Value;
            }
        }

        return null;
    }
}
