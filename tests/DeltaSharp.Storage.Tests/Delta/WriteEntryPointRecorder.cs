using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Records which production entry points the footer/log parity suite ACTUALLY invoked
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
    private static readonly ConcurrentDictionary<(string Type, string Method, string Signature), byte>
        Driven = new();

    /// <summary>Notes that <paramref name="method"/> on <paramref name="owner"/> was invoked.</summary>
    /// <remarks>
    /// Calls sit immediately beside the invocation they describe, and
    /// <c>RecordedEntryPoints_AreBackedByRealCalls</c> checks each recorded name against the IL of
    /// the suite -- so a label that drifts away from the call it claims to describe fails rather
    /// than quietly widening the reported coverage.
    /// <para>
    /// The owning TYPE is recorded, not just the name, because the required side is derived from
    /// <c>SchemaJson.ToJson</c> call sites across all of <c>DeltaSharp.Storage</c> -- which span
    /// more than one type and more than one return type -- and coverage is computed by walking
    /// production IL forward from what actually ran. A bare name cannot be resolved back to a
    /// method to walk from.
    /// </para>
    /// </remarks>
    /// <param name="parameterTypes">
    /// The driven overload's parameter types. Omit them only while the name is unambiguous: the
    /// guard resolves a label to EXACTLY ONE product method and fails if the name matches several,
    /// because joining on a bare name silently promotes every same-named overload to a driven root.
    /// A name is not a method.
    /// </param>
    internal static void Record(Type owner, string method, params Type[] parameterTypes)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        Driven[(owner.FullName!, method, SignatureOf(parameterTypes))] = 0;
    }

    internal static IReadOnlySet<(string Type, string Method, string Signature)> Snapshot() =>
        Driven.Keys.ToHashSet();

    internal static void Reset() => Driven.Clear();

    /// <summary>The canonical signature key, or the empty string when none was supplied.</summary>
    internal static string SignatureOf(IEnumerable<Type> parameterTypes) =>
        string.Join(",", parameterTypes.Select(t => t.FullName ?? t.Name));
}
