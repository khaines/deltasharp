using System.Text;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit oracle for <see cref="PartitionPathResolver"/> — Inc-A of the #806 two-layer partition-path encoding
/// design (<c>docs/engineering/design/partition-path-encoding-interop.md</c> §2.3/§2.7). Proves the
/// decoded-first, open-then-fallback resolution across the three on-disk layouts, the I/O-equivalence no-op
/// oracle (design F4), confinement-on-the-decoded-key propagation (T-Escape), and the fail-closed pre-decode
/// length cap (O5) — each against a recording backend that logs every <c>OpenRead</c> key in order.
/// </summary>
public sealed class PartitionPathResolverTests
{
    // ---- decode ----------------------------------------------------------------------------------

    [Fact]
    public void DecodePhysicalKey_NoPercentEncoding_IsIdentity()
    {
        // The common case: an ASCII-unreserved key decodes to itself (drives the zero-fallback no-op).
        Assert.Equal("col=US/part-000.parquet", PartitionPathResolver.DecodePhysicalKey("col=US/part-000.parquet"));
    }

    [Fact]
    public void DecodePhysicalKey_L2Encoding_RecoversPhysicalKey()
    {
        // Layer-2 encodes the '=' separator (%3D) and re-encodes a layer-1 '%2F' to '%252F'; a single decode
        // recovers the on-disk physical key (design §2.2).
        Assert.Equal("col=US/part-000.parquet", PartitionPathResolver.DecodePhysicalKey("col%3DUS/part-000.parquet"));
        Assert.Equal("col=a%2Fb/part-000.parquet", PartitionPathResolver.DecodePhysicalKey("col%3Da%252Fb/part-000.parquet"));
    }

    [Fact]
    public void DecodePhysicalKey_OverLength_FailsClosed_CorruptData()
    {
        string tooLong = new string('a', PartitionPathResolver.MaxAddPathBytes + 1);
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() => PartitionPathResolver.DecodePhysicalKey(tooLong));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        // Message hygiene: the (attacker-controllable) path is never echoed — only the bounded limit.
        Assert.DoesNotContain("aaaa", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PartitionPathResolver.MaxAddPathBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    // ---- open resolution -------------------------------------------------------------------------

    [Fact]
    public async Task NoPercentEncoding_OpensLiteralOnce_ZeroFallback()
    {
        // I/O-equivalence no-op oracle (design F4): k_dec == k_lit ⇒ exactly ONE OpenRead, zero fallback —
        // byte-identical to the pre-#806 behavior. A regression that added a probe turns this red.
        var backend = new RecordingBackend(existing: "col=US/part-000.parquet");
        await using Stream s = await PartitionPathResolver.OpenReadAsync(backend, "col=US/part-000.parquet", default);
        Assert.Equal(new[] { "col=US/part-000.parquet" }, backend.Opens);
    }

    [Fact]
    public async Task L2Encoded_DecodedKeyExists_OpensDecoded_NoLiteralFallback()
    {
        // The go-forward L2 (or Spark/delta-rs) format: the decoded key is the real key, opened on the first
        // try with NO literal fallback (design §2.3 — one open for L2).
        var backend = new RecordingBackend(existing: "col=US/part-000.parquet");
        await using Stream s = await PartitionPathResolver.OpenReadAsync(backend, "col%3DUS/part-000.parquet", default);
        Assert.Equal(new[] { "col=US/part-000.parquet" }, backend.Opens);
    }

    [Fact]
    public async Task LegacyLiteralPercent_DecodedMisses_FallsBackToLiteral()
    {
        // The #806 L1 migration trap: an on-disk 'col=a%2Fb' whose add.path is stored LITERALLY. Decoding it
        // yields 'col=a/b' (a nonexistent key) → miss → literal fallback recovers the real file. A naive
        // decode-always read would corrupt this. Records [decoded-miss, literal-hit] in order.
        var backend = new RecordingBackend(existing: "col=a%2Fb/part-000.parquet");
        await using Stream s = await PartitionPathResolver.OpenReadAsync(backend, "col=a%2Fb/part-000.parquet", default);
        Assert.Equal(new[] { "col=a/b/part-000.parquet", "col=a%2Fb/part-000.parquet" }, backend.Opens);
    }

    [Fact]
    public async Task NeitherKeyExists_FailsClosed_NotFound()
    {
        var backend = new RecordingBackend(existing: null);
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => PartitionPathResolver.OpenReadAsync(backend, "col%3DUS/part-000.parquet", default).AsTask());
        Assert.Equal(StorageErrorKind.NotFound, ex.Kind);
        // Tried decoded then literal, both missing.
        Assert.Equal(new[] { "col=US/part-000.parquet", "col%3DUS/part-000.parquet" }, backend.Opens);
    }

    [Fact]
    public async Task DecodedKeyEscapesRoot_PathNotConfined_Propagates_NoLiteralFallback()
    {
        // T-Escape (design §5/§6): a foreign add.path that decodes to a traversal ('..%2f..' → '../..') is
        // rejected by the backend on the DECODED key. Because the fallback fires ONLY on NotFound, the
        // PathNotConfined fault PROPAGATES (fail closed) — the literal is NEVER tried.
        var backend = new RecordingBackend(existing: null, confineReject: "../../etc/passwd");
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => PartitionPathResolver.OpenReadAsync(backend, "..%2F..%2Fetc%2Fpasswd", default).AsTask());
        Assert.Equal(StorageErrorKind.PathNotConfined, ex.Kind);
        Assert.Equal(new[] { "../../etc/passwd" }, backend.Opens); // only the decoded key was attempted
    }

    [Fact]
    public async Task OverLengthPath_FailsClosed_BeforeAnyOpen()
    {
        var backend = new RecordingBackend(existing: null);
        string tooLong = new string('a', PartitionPathResolver.MaxAddPathBytes + 1);
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => PartitionPathResolver.OpenReadAsync(backend, tooLong, default).AsTask());
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.Empty(backend.Opens); // fail-closed before touching the backend
    }

    // A minimal IStorageBackend that records every OpenRead key in order; only OpenReadAsync is meaningful.
    private sealed class RecordingBackend : IStorageBackend
    {
        private readonly string? _existing;
        private readonly string? _confineReject;

        public RecordingBackend(string? existing, string? confineReject = null)
        {
            _existing = existing;
            _confineReject = confineReject;
        }

        public List<string> Opens { get; } = new();

        public StorageBackendKind Kind => StorageBackendKind.Pvc;

        public string TableIdentity => "Pvc:recording";

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            Opens.Add(path);
            if (_confineReject is not null && string.Equals(path, _confineReject, StringComparison.Ordinal))
            {
                throw DeltaStorageException.PathNotConfined($"path escapes the root.");
            }

            if (_existing is not null && string.Equals(path, _existing, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("PAR1"), writable: false));
            }

            throw DeltaStorageException.NotFound($"object does not exist.");
        }

        public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<StorageObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
