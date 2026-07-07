using System.Collections.Immutable;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The <b>read scope</b> a commit was computed against — the input that makes Delta conflict detection
/// asymmetric and read-scope-driven (design §2.11.2). A commit conflicts with a concurrent winner only
/// where its read scope overlaps what the winner changed, so a writer that read nothing (a blind append)
/// can rebase past concurrent appends, while a writer that read files (a delete/overwrite/merge) cannot.
///
/// <para><b>v1 scope.</b> Three shapes are modeled: <see cref="BlindAppend"/> (empty read set),
/// <see cref="WholeTable"/> (depends on every active file — a full overwrite or unpartitioned delete), and
/// <see cref="ReadFiles"/> (depends on a named file set — a targeted delete/merge). Predicate/partition-
/// precise scopes (conflict only when a winner touches an overlapping partition <i>predicate</i>) are a
/// tracked refinement (design §2.11.2 conditional cells); the modeled scopes are deliberately <b>no less
/// conservative</b> than Delta, so they never miss a real conflict.</para>
/// </summary>
internal abstract record DeltaReadScope
{
    private DeltaReadScope()
    {
    }

    /// <summary>An empty read set — a streaming or plain <c>append</c> that read no existing data, so it
    /// conflicts <b>only</b> with a concurrent metadata/protocol change (the load-bearing fast path).</summary>
    public static DeltaReadScope BlindAppend { get; } = new BlindAppendScope();

    /// <summary>A commit that depends on the entire active file set (a full overwrite or an unpartitioned
    /// delete): it conflicts with any concurrent <c>add</c> or <c>remove</c>.</summary>
    public static DeltaReadScope WholeTable { get; } = new WholeTableScope();

    /// <summary>A commit that read a specific set of files (a targeted delete/merge): it conflicts with a
    /// concurrent <c>remove</c> or re-<c>add</c> of any file in <paramref name="paths"/>.</summary>
    public static DeltaReadScope ReadFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ReadFilesScope(paths.ToImmutableHashSet(StringComparer.Ordinal));
    }

    internal sealed record BlindAppendScope : DeltaReadScope;

    internal sealed record WholeTableScope : DeltaReadScope;

    internal sealed record ReadFilesScope(ImmutableHashSet<string> Paths) : DeltaReadScope;
}
