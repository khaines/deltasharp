using Xunit;

namespace DeltaSharp.TestSupport;

/// <summary>
/// Serializes tests that read or mutate PROCESS-WIDE state — environment variables (for example
/// <c>DELTASHARP_TEST_SEED</c>), ambient configuration, current-directory, or other global slots —
/// so they cannot race one another or perturb parallel tests (STORY-00.5.1 AC2).
/// </summary>
/// <remarks>
/// This is the same collection/fixture boundary rule that <c>SparkSessionTestCollection</c> applies
/// to the process-wide active/default <c>SparkSession</c> slots: xUnit runs distinct collections in
/// parallel but serializes a collection marked <c>DisableParallelization</c>. Randomized tests that
/// each own a local <see cref="SeededRandom"/> need no such boundary and parallelize freely; only
/// tests touching shared mutable state join this collection. It is defined in each <c>*.Tests</c>
/// assembly (xUnit collections are per-assembly) and is <see langword="public"/> for xUnit
/// discovery. See <c>docs/engineering/design/test-harness-conventions.md</c>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentSensitiveTestCollection
{
    /// <summary>The xUnit collection name shared by every environment-sensitive test class.</summary>
    public const string Name = "DeltaSharp environment-sensitive";
}
