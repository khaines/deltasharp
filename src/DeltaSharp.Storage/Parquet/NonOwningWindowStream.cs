namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// A read-only, seekable view over <c>[offset, offset + length)</c> of an underlying stream that does
/// <b>not</b> own it: <see cref="Dispose(bool)"/>/<see cref="DisposeAsync"/> are no-ops.
/// </summary>
/// <remarks>
/// Exists for the §2.4b post-write footer reconciliation, which must read the file it just wrote back out of
/// the caller's output stream. <c>ParquetFileReader.OpenAsync</c> constructs its <c>ParquetReader</c> with
/// <c>leaveStreamOpen: false</c>, so handing it the output stream directly would DISPOSE the caller's stream
/// mid-write-path. The window also rebases positions, so a stream the caller had already written other bytes
/// into (a shared buffer) still presents the Parquet file at offset 0 — Parquet.Net locates the footer by
/// seeking from the END, so it must not see any trailing or leading foreign bytes.
/// </remarks>
internal sealed class NonOwningWindowStream : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is
    /// negative, or the window runs past the end of <paramref name="inner"/> (N2). A window that overruns its
    /// backing stream would let the footer reader seek from an END that does not exist, so it fails closed at
    /// construction rather than mid-parse.</exception>
    internal NonOwningWindowStream(Stream inner, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (inner.CanSeek)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, inner.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, inner.Length - offset);
        }

        _inner = inner;
        _offset = offset;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int available = (int)Math.Min(buffer.Length, Math.Max(_length - _position, 0));
        if (available == 0)
        {
            return 0;
        }

        _inner.Position = _offset + _position;
        int read = _inner.Read(buffer[..available]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int available = (int)Math.Min(buffer.Length, Math.Max(_length - _position, 0));
        if (available == 0)
        {
            return 0;
        }

        _inner.Position = _offset + _position;
        int read = await _inner.ReadAsync(buffer[..available], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        // Deliberately does NOT dispose the underlying stream; see the type remarks.
    }
}
