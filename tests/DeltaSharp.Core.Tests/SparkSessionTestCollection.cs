using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Serializes the <see cref="SparkSession"/> tests. Active- and default-session tracking is
/// process-wide static state (a thread-local active slot and a global default), so the session
/// tests must not run in parallel with one another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SparkSessionTestCollection
{
    /// <summary>The xUnit collection name shared by every session test class.</summary>
    public const string Name = "SparkSession serial";
}
