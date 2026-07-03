using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Serializes the Executor tests that create a <see cref="SparkSession"/> through the public API. A
/// session touches process-wide active/default session state (and disposing one stops it), so two
/// session-using test classes must not run in parallel or one could stop the session another holds.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionExecutionTestCollection
{
    /// <summary>The xUnit collection name shared by every session-using Executor test class.</summary>
    public const string Name = "Executor session serial";
}
