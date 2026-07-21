using System.Collections.Immutable;
using System.Security.Cryptography;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// An advisory, non-protocol per-file content checksum (STORY-05.6.1 / #504 follow-up to #195). Writes
/// and OPTIMIZE compaction stamp each <c>add</c> action with a durable fingerprint of the file it just
/// wrote, recorded under the DeltaSharp-namespaced <see cref="TagKey"/> in <c>add.tags</c> so a content
/// equivalence / duplication audit can compare files across rewrites <b>without re-reading</b> them.
///
/// <para>The value is a SHA-256 over the <b>exact on-disk Parquet bytes</b> (self-described as
/// <c>sha256:&lt;lowercase-hex&gt;</c>): two byte-identical files share a checksum and any content or
/// encoding difference changes it. Because the write path embeds no wall-clock/random input (the Parquet
/// footer's custom metadata is a constant identity + the schema JSON), identical logical content yields
/// identical bytes and therefore an identical, reproducible checksum — satisfying the determinism ban.</para>
///
/// <para>It is stored as a <c>tag</c>, which a strict Delta reader ignores, so it is purely additive
/// engine metadata: it never changes read correctness and requires no protocol/table feature.</para>
///
/// <para><b>Version scoping.</b> The fingerprint is over bytes produced by a SPECIFIC Parquet.Net version
/// and writer configuration (row-group size, compression, encoding). A Parquet.Net upgrade or codec change
/// alters the physical layout, so identical logical content written across that boundary will NOT share a
/// checksum. The audit therefore compares files within a fixed writer generation, not across Parquet.Net
/// upgrades.</para>
/// </summary>
internal static class ContentChecksum
{
    /// <summary>
    /// The <c>add.tags</c> key carrying the content checksum. DeltaSharp-namespaced (<c>deltaSharp.</c>)
    /// so it never collides with a Delta-defined tag and is obviously advisory to other engines.
    /// </summary>
    internal const string TagKey = "deltaSharp.contentChecksum";

    private const string Prefix = "sha256:";

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Computes the self-describing <c>sha256:&lt;lowercase-hex&gt;</c> checksum of the on-disk file bytes.
    /// </summary>
    internal static string Compute(ReadOnlySpan<byte> content)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, digest);
        return Prefix + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// The single, null-safe stamping seam both write paths use to build an <c>add</c>'s tags: an
    /// ordinal-keyed map carrying only the content-checksum tag, or the empty map when
    /// <paramref name="checksum"/> is <see langword="null"/> (a caller that did not compute one). Keeps the
    /// regular-write and OPTIMIZE seams on one idiom rather than each inlining <c>SetItem</c>.
    /// </summary>
    internal static ImmutableSortedDictionary<string, string> TagsFor(string? checksum) =>
        checksum is null ? NoTags : NoTags.SetItem(TagKey, checksum);

    /// <summary>
    /// Reads the content checksum previously stamped on an <c>add</c> (its <see cref="AddFileAction.Tags"/>),
    /// or <see langword="null"/> when the file predates the checksum or was written by another engine. This
    /// is the read-back entry point for the content-equivalence / duplication audit use case.
    /// </summary>
    internal static string? TryRead(ImmutableSortedDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return tags.TryGetValue(TagKey, out string? checksum) ? checksum : null;
    }
}
