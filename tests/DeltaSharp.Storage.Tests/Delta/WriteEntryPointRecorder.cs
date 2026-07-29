using System.Collections.Concurrent;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Records which <c>DeltaWriteTarget</c> entry points the footer/log parity suite ACTUALLY invoked
/// during a run, as opposed to which ones its code could reach.
/// </summary>
/// <remarks>
/// <para>
/// Two previous versions of the coverage guard were static IL reachability, and both failed OPEN --
/// reporting an operation as covered when nothing executed it. v1 was satisfied by a dead private
/// helper; v2 by a deleted <c>[InlineData]</c> row and by <c>[Theory(Skip = "...")]</c>. Neither is
/// an adversarial edit: deleting a theory row and skipping a flaky test are what maintenance looks
/// like.
/// </para>
/// <para>
/// The root cause was a category error rather than a bug. Static analysis answers "could this be
/// called"; the property the guard's name claims is "was this called". No amount of care about
/// call-graph walking can express xUnit execution semantics -- <c>Skip</c>, a removed data row, a
/// commented-out <c>[Fact]</c>, a trait filter -- because those decide EXECUTION, not reachability.
/// So the mechanism changes kind: the suite records what it drove, and the guard compares that
/// record against the set derived from the product.
/// </para>
/// </remarks>
internal static class WriteEntryPointRecorder
{
    private static readonly ConcurrentDictionary<string, byte> Driven = new(StringComparer.Ordinal);

    /// <summary>Notes that <paramref name="entryPoint"/> was invoked.</summary>
    /// <remarks>
    /// Calls sit immediately beside the invocation they describe, and
    /// <c>RecordedEntryPoints_AreBackedByRealCalls</c> checks each recorded name against the IL of
    /// the method that recorded it -- so a label that drifts away from the call it claims to
    /// describe fails rather than quietly widening the reported coverage.
    /// </remarks>
    internal static void Record(string entryPoint) => Driven[entryPoint] = 0;

    internal static IReadOnlySet<string> Snapshot() => Driven.Keys.ToHashSet(StringComparer.Ordinal);

    internal static void Reset() => Driven.Clear();
}
