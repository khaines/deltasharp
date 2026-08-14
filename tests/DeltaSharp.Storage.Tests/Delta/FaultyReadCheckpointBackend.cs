using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Backends;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// A test-only <see cref="IStorageBackend"/> decorator that OPENS a checkpoint object successfully but
/// returns a stream whose first READ throws <see cref="StorageErrorKind.Transient"/> — i.e. a retryable I/O
/// blip raised WHILE reading the checkpoint part, not at open. It pins the KIND filter on #681's
/// non-authoritative-checkpoint swallow (<c>DeltaLog</c>'s <c>when (ex.Kind == UnsupportedFeature)</c> and
/// <c>DeltaCheckpointReader</c>'s <c>UnsupportedFeature</c>-only reclassification exclusion): a
/// <c>Transient</c> raised during the checkpoint READ must PROPAGATE (retryable), NOT be swallowed into a
/// silent full JSON replay nor remapped to a corrupt-checkpoint <c>Malformed</c>. This complements
/// <see cref="UnsupportedOpenBackend"/>, which pins the SCOPE (fault at OPEN); this pins the KIND (fault at
/// READ). Every other operation delegates unchanged.
/// </summary>
internal sealed class FaultyReadCheckpointBackend : IStorageBackend
{
    private readonly IStorageBackend _inner;
    private readonly string _pathMarker;

    public FaultyReadCheckpointBackend(IStorageBackend inner, string pathMarker)
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
            return new TransientOnReadStream();
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
    /// A readable, non-seekable stream that raises a retryable <see cref="StorageErrorKind.Transient"/>
    /// <see cref="DeltaStorageException"/> on the first read attempt — modelling a backend object-store read
    /// that faults after a successful open (the shape the checkpoint reader buffers through
    /// <c>BufferAsync</c>).
    /// </summary>
    internal sealed class TransientOnReadStream : Stream
    {
        private const string Message = "Transient I/O fault while reading the checkpoint object (test injection).";

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw DeltaStorageException.Transient(Message);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw DeltaStorageException.Transient(Message);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw DeltaStorageException.Transient(Message);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
