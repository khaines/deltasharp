using System.Text;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The centralized <b>read-side</b> resolver from a Delta <c>add.path</c> (or <c>remove.path</c>) — a
/// URI-encoded relative path per the Delta protocol (RFC 2396) — to the physical object key opened against a
/// backend. This is increment <b>Inc-A</b> of the #806 two-layer partition-path encoding design
/// (<c>docs/engineering/design/partition-path-encoding-interop.md</c>, §2.3/§2.5).
/// </summary>
/// <remarks>
/// <para><b>Decoded-first, open-then-fallback (design §2.3).</b> A protocol-conforming (or, once Inc-B lands,
/// DeltaSharp two-layer) path decodes to the real key, so the resolver <b>opens the decoded key first</b> and,
/// only on a <see cref="StorageErrorKind.NotFound"/>, falls back to opening the <b>literal</b> path. This
/// reads all three on-disk layouts without an existence probe: a path with no percent-encoding
/// (<c>decoded == addPath</c> — the overwhelmingly common case, and every current DeltaSharp table's
/// ASCII-unreserved segments) opens the literal key directly (one open, zero extra I/O); a legacy
/// DeltaSharp table whose literal <c>add.path</c> carries a <c>%</c> (e.g. a value <c>a/b</c> stored as
/// <c>col=a%2Fb/…</c>) decodes to a non-existent key (<c>col=a/b/…</c>) → miss → literal fallback (the #806
/// L1 migration trap: a naive decode-always read would corrupt it).</para>
/// <para><b>Confinement is on the DECODED key.</b> The backend root-jail runs on whatever key is opened, so a
/// foreign <c>add.path</c> that decodes to a traversal (<c>..%2f..%2f…</c>) is rejected
/// (<see cref="StorageErrorKind.PathNotConfined"/>) and, because the fallback fires <b>only</b> on
/// <see cref="StorageErrorKind.NotFound"/>, that security fault propagates (fail closed) — never silently
/// retried on the literal.</para>
/// <para><b>Bounded, fail-closed decode (DoS).</b> <c>add.path</c> is foreign input; the resolver caps its
/// pre-decode length (<see cref="MaxAddPathBytes"/>) so an adversarial multi-megabyte path cannot force an
/// unbounded allocation. <see cref="Uri.UnescapeDataString(string)"/> is a single O(n) pass.</para>
/// <para><b>Partition truth is unaffected.</b> This resolver locates the data <i>file</i> only; partition
/// column values are reconstructed from the authoritative <c>add.partitionValues</c>, never parsed from the
/// path, so a path-encoding change can never alter query results.</para>
/// </remarks>
internal static class PartitionPathResolver
{
    /// <summary>Conservative pre-decode byte cap on a foreign <c>add.path</c> (design §9 O5). A legitimate Delta
    /// data-file relative key is a few hundred bytes at most (partition segments under the portable
    /// per-component budget plus a <c>part-&lt;token&gt;.parquet</c> name); 8&#160;KiB leaves ample head-room for
    /// deep partitioning while bounding an adversarial path. Independent of the write-door
    /// <c>ColumnMapping.MaxPathSegmentNameBytes</c>, which does not bound a foreign read path.</summary>
    internal const int MaxAddPathBytes = 8192;

    /// <summary>Opens the data file referenced by <paramref name="addPath"/> (a URI-encoded relative
    /// <c>add.path</c>/<c>remove.path</c>) against <paramref name="backend"/>, decoded-first with a literal
    /// fallback (see the type <c>&lt;remarks&gt;</c>).</summary>
    /// <exception cref="DeltaStorageException">The path exceeds <see cref="MaxAddPathBytes"/>
    /// (<see cref="StorageErrorKind.CorruptData"/>), the (decoded) key escapes the table root
    /// (<see cref="StorageErrorKind.PathNotConfined"/>), or neither the decoded nor the literal key exists
    /// (<see cref="StorageErrorKind.NotFound"/>).</exception>
    internal static async ValueTask<Stream> OpenReadAsync(
        IStorageBackend backend, string addPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(addPath);

        string decoded = DecodePhysicalKey(addPath);
        if (string.Equals(decoded, addPath, StringComparison.Ordinal))
        {
            // No percent-encoding: exactly one open, byte-identical to the pre-#806 behavior.
            return await backend.OpenReadAsync(addPath, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await backend.OpenReadAsync(decoded, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaStorageException ex) when (ex.Kind == StorageErrorKind.NotFound)
        {
            // The decoded key does not exist → a legacy literal-`%` layout (L0/L1). Open the literal path.
            // A PathNotConfined fault on the decoded key is NOT caught here — it propagates (fail closed).
            return await backend.OpenReadAsync(addPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Decodes a URI-encoded <c>add.path</c> to its physical object key, enforcing the
    /// <see cref="MaxAddPathBytes"/> pre-decode length cap (fail-closed on breach). A path with no
    /// percent-encoding decodes to itself.</summary>
    /// <exception cref="DeltaStorageException">The path exceeds <see cref="MaxAddPathBytes"/> UTF-8 bytes
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    internal static string DecodePhysicalKey(string addPath)
    {
        ArgumentNullException.ThrowIfNull(addPath);
        if (Encoding.UTF8.GetByteCount(addPath) > MaxAddPathBytes)
        {
            // Message hygiene: name only the bounded limit, never the (attacker-controllable) path itself.
            throw DeltaStorageException.CorruptData(
                $"A data-file path exceeds the {MaxAddPathBytes}-byte maximum and cannot be resolved.");
        }

        return Uri.UnescapeDataString(addPath);
    }
}
