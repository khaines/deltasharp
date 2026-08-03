using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

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

    /// <summary>
    /// Invokes <paramref name="call"/> on <paramref name="target"/> and records the entry point
    /// IT ACTUALLY CALLED, after it has returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ONLY way to record, and <c>Record</c> is private so that no other code can emit
    /// a label. That is deliberate and it is the whole fix for v7. Previously a label was AUTHORED
    /// next to a call rather than produced BY one, so the two could be satisfied from different
    /// places: move the real call into a never-taken branch (which still satisfies a
    /// branch-insensitive IL walk) and emit its label from a different helper that does run, and an
    /// entry point nothing invoked is reported driven with the suite green. Ordering was not the
    /// defect and moving the label after the await would not have fixed it -- the label was simply
    /// not bound to its own call site.
    /// </para>
    /// <para>
    /// Binding is structural here rather than checked: the identity comes from the
    /// <see cref="MethodInfo"/> in the expression the compiler resolved, so the label cannot name a
    /// method other than the one invoked, and it is emitted only after the invocation completes, so
    /// a call that does not happen cannot be labelled. Resolution answers WHICH METHOD A LABEL
    /// NAMES; only this answers WHETHER THE METHOD THAT EMITTED IT CALLED IT.
    /// </para>
    /// </remarks>
    /// <typeparam name="TTarget">The receiver type.</typeparam>
    /// <typeparam name="TResult">The awaited result type.</typeparam>
    /// <param name="target">The receiver to invoke against.</param>
    /// <param name="call">A single method call on <paramref name="target"/>.</param>
    /// <returns>Whatever the driven entry point returned.</returns>
    internal static async Task<TResult> DriveAsync<TTarget, TResult>(
        TTarget target, Expression<Func<TTarget, Task<TResult>>> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.Body is not MethodCallExpression invocation)
        {
            throw new ArgumentException(
                "A driven entry point must be a single method call, so the recorded label is the "
                + "method the compiler resolved rather than one an author chose.",
                nameof(call));
        }

        TResult result = await call.Compile()(target).ConfigureAwait(false);

        MethodInfo driven = invocation.Method;
        Record(
            driven.DeclaringType ?? typeof(TTarget),
            driven.Name,
            driven.GetParameters().Select(q => q.ParameterType).ToArray());

        return result;
    }

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
    private static void Record(Type owner, string method, params Type[] parameterTypes)
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
