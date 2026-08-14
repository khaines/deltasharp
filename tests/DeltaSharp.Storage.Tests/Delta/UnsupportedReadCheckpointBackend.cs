using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that OPENS a checkpoint object successfully but
/// returns a stream whose first READ throws a <see cref="StorageErrorKind.UnsupportedFeature"/>
/// <see cref="DeltaStorageException"/> whose message is NOT one of the Parquet Modular Encryption classifier's
/// own verdict constants — i.e. a backend/storage-layer unsupported-feature signal raised WHILE reading the
/// checkpoint part, distinct from the checkpoint reader's own "this Parquet is encrypted" verdict.
/// <para>It pins the #771 narrowing: the non-authoritative-checkpoint swallow
/// (<c>DeltaLog.TrySeedFromCheckpointAsync</c>) is gated on the classifier's OWN verdict
/// (<c>ParquetEncryption.IsEncryptionClassifierVerdict</c>), NOT on <see cref="StorageErrorKind.UnsupportedFeature"/>
/// alone. A non-classifier <c>UnsupportedFeature</c> raised during the checkpoint read must therefore SURFACE
/// (fail the table read), NOT be masked as an encrypted-checkpoint fallback into a silent full JSON replay.
/// This is the streamed-part-fault shape #698's <c>Kind</c>-keyed filters could not distinguish; buffering
/// makes it reachable through <c>BufferAsync</c> today. Every other operation delegates unchanged.</para>
/// </summary>
internal sealed class UnsupportedReadCheckpointBackend : IStorageBackend
{
    private readonly IStorageBackend _inner;
    private readonly string _pathMarker;

    public UnsupportedReadCheckpointBackend(IStorageBackend inner, string pathMarker)
    {
        _inner = inner;
        _pathMarker = pathMarker;
    }

    public StorageBackendKind Kind => _inner.Kind;

    public string TableIdentity => _inner.TableIdentity;

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        if (path.Contains(_pathMarker, StringComparison.Ordinal))
        {
            return new UnsupportedFeatureOnReadStream();
        }

        return await _inner.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> ReadRangeAsync(string path, long offset, long length, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(path, offset, length, cancellationToken);

    public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
        _inner.OpenWriteAsync(path, cancellationToken);

    public ValueTask<bool> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        _inner.PutIfAbsentAsync(path, content, cancellationToken);

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(path, cancellationToken);

    public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
        _inner.HeadAsync(path, cancellationToken);

    public async IAsyncEnumerable<StorageObjectInfo> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (StorageObjectInfo info in _inner.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            yield return info;
        }
    }

    /// <summary>
    /// A readable, non-seekable stream that raises a <see cref="StorageErrorKind.UnsupportedFeature"/>
    /// <see cref="DeltaStorageException"/> on the first read attempt, with a message that is deliberately NOT
    /// one of the Parquet Modular Encryption classifier's verdict constants — modelling a backend that
    /// surfaces an unsupported-feature storage signal mid-read rather than the checkpoint reader's own
    /// "encrypted Parquet" verdict.
    /// </summary>
    internal sealed class UnsupportedFeatureOnReadStream : Stream
    {
        internal const string Message =
            "Backend-surfaced unsupported feature while reading the checkpoint object (test injection); "
            + "NOT the Parquet Modular Encryption classifier verdict.";

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw DeltaStorageException.UnsupportedFeature(Message);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw DeltaStorageException.UnsupportedFeature(Message);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw DeltaStorageException.UnsupportedFeature(Message);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
