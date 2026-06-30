using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// A disk-backed <see cref="ISpillStore"/>: each segment is a temp file holding length-prefixed
/// records. It is the spill target that genuinely moves bytes out of process memory under pressure.
/// Temp files are deleted when their segment is disposed and any stragglers are removed when the store
/// is disposed, so a normal completion, a cancellation, or a spill failure all leave no leaked files
/// (STORY-03.6.2: spill temp files must be cleaned up on every path).
/// </summary>
/// <remarks>
/// Records are framed <c>[length:int32-le][payload]</c>; the reader range-checks every length against
/// the remaining file size before reading, so a truncated or corrupt segment surfaces a
/// <see cref="SpillIOException"/> rather than reading past end. All <see cref="IOException"/>s from the
/// underlying file APIs are wrapped as <see cref="SpillIOException"/> so the operator sees one
/// deterministic failure type regardless of medium.
/// </remarks>
internal sealed class TempFileSpillStore : ISpillStore, IDisposable
{
    private static long s_storeCounter;

    private readonly string _root;
    private readonly List<Segment> _segments = new();
    private int _counter;
    private bool _disposed;

    /// <summary>Creates a store rooted in a fresh temp directory under the OS temp path.</summary>
    /// <exception cref="SpillIOException">The temp directory could not be created.</exception>
    public TempFileSpillStore()
    {
        try
        {
            // Deterministic, collision-free directory identity without the banned Guid.NewGuid: the
            // process id plus a monotonic per-process counter uniquely names each store's temp root.
            long id = Interlocked.Increment(ref s_storeCounter);
            _root = Path.Combine(Path.GetTempPath(), $"deltasharp-spill-{Environment.ProcessId}-{id}");
            Directory.CreateDirectory(_root);
        }
        catch (IOException ex)
        {
            throw new SpillIOException("open", "temp spill directory", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SpillIOException("open", "temp spill directory", ex);
        }
    }

    /// <inheritdoc />
    public ISpillSegment CreateSegment(string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string path = Path.Combine(_root, $"{label}-{_counter++}.spill");
        var segment = new Segment(path);
        _segments.Add(segment);
        return segment;
    }

    /// <summary>The temp directory backing this store (test-visible so cleanup can be asserted).</summary>
    internal string Root => _root;

    /// <summary>Disposes every segment (deleting its file) and removes the temp directory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Segment segment in _segments)
        {
            segment.Dispose();
        }

        _segments.Clear();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup: a directory that is already gone (every segment deleted its file) or
            // momentarily locked must not turn teardown into a fault. Per-segment Dispose already removed
            // the files; a stale empty directory is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Segment : ISpillSegment
    {
        private readonly string _path;
        private FileStream? _writer;
        private bool _disposed;

        public Segment(string path)
        {
            _path = path;
            try
            {
                _writer = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1 << 16);
            }
            catch (IOException ex)
            {
                throw new SpillIOException("open", $"spill segment '{Path.GetFileName(path)}'", ex);
            }
        }

        public long BytesWritten { get; private set; }

        public void Write(ReadOnlySpan<byte> record)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writer is null)
            {
                throw new SpillIOException("write", $"spill segment '{Path.GetFileName(_path)}' (already sealed for reading)");
            }

            try
            {
                Span<byte> header = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, record.Length);
                _writer.Write(header);
                _writer.Write(record);
                BytesWritten += record.Length;
            }
            catch (IOException ex)
            {
                throw new SpillIOException("write", $"spill segment '{Path.GetFileName(_path)}'", ex);
            }
        }

        public ISpillSegmentReader OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Seal the writer so all bytes are flushed to disk before the reader opens.
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                return new Reader(_path);
            }
            catch (IOException ex)
            {
                throw new SpillIOException("open", $"spill segment '{Path.GetFileName(_path)}' for reading", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Dispose();
                _writer = null;
            }
            catch (IOException)
            {
                // Fall through to deletion regardless.
            }

            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (IOException)
            {
                // Best-effort: a missing or transiently locked file must not fault teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class Reader : ISpillSegmentReader
        {
            private readonly FileStream _stream;
            private readonly string _path;

            public Reader(string path)
            {
                _path = path;
                try
                {
                    _stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1 << 16);
                }
                catch (IOException ex)
                {
                    throw new SpillIOException("open", $"spill segment '{Path.GetFileName(path)}' for reading", ex);
                }
            }

            public bool TryRead([NotNullWhen(true)] out byte[]? record)
            {
                try
                {
                    Span<byte> header = stackalloc byte[sizeof(int)];
                    int read = _stream.ReadAtLeast(header, sizeof(int), throwOnEndOfStream: false);
                    if (read == 0)
                    {
                        record = null;
                        return false;
                    }

                    if (read < sizeof(int))
                    {
                        throw new SpillIOException("read", $"spill segment '{Path.GetFileName(_path)}' (truncated header)");
                    }

                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < 0 || length > _stream.Length - _stream.Position)
                    {
                        throw new SpillIOException("read", $"spill segment '{Path.GetFileName(_path)}' (record length {length} runs past end)");
                    }

                    byte[] payload = new byte[length];
                    _stream.ReadExactly(payload);
                    record = payload;
                    return true;
                }
                catch (IOException ex)
                {
                    throw new SpillIOException("read", $"spill segment '{Path.GetFileName(_path)}'", ex);
                }
            }

            public void Dispose() => _stream.Dispose();
        }
    }
}
