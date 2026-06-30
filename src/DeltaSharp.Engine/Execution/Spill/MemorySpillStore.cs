using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// The default <see cref="ISpillStore"/>: segments hold their records in managed memory. It is the
/// zero-dependency, deterministic spill target used when no disk-backed store is configured — spill
/// still frees the operator's <i>reserved-budget</i> bookkeeping and exercises the full serialize →
/// release → merge path, while the bytes live on the GC heap rather than off-budget native/disk storage.
/// Tests and local runs use it; the executor wires a <see cref="TempFileSpillStore"/> when real
/// off-heap relief is required.
/// </summary>
/// <remarks>
/// Holding spilled bytes on the managed heap means the in-memory store does not by itself lower the
/// process's total memory; its value is correctness — proving the spill/merge algorithm and the
/// budget-release accounting — and giving tests a fault-free, allocation-deterministic medium. The
/// disk-backed <see cref="TempFileSpillStore"/> is what actually moves bytes out of process memory.
/// </remarks>
internal sealed class MemorySpillStore : ISpillStore
{
    /// <inheritdoc />
    public ISpillSegment CreateSegment(string label) => new Segment();

    private sealed class Segment : ISpillSegment
    {
        private readonly List<byte[]> _records = new();
        private bool _disposed;

        public long BytesWritten { get; private set; }

        public void Write(ReadOnlySpan<byte> record)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _records.Add(record.ToArray());
            BytesWritten += record.Length;
        }

        public ISpillSegmentReader OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Reader(_records);
        }

        public void Dispose()
        {
            _disposed = true;
            _records.Clear();
        }

        private sealed class Reader : ISpillSegmentReader
        {
            private readonly List<byte[]> _records;
            private int _index;

            public Reader(List<byte[]> records) => _records = records;

            public bool TryRead([NotNullWhen(true)] out byte[]? record)
            {
                if (_index < _records.Count)
                {
                    record = _records[_index++];
                    return true;
                }

                record = null;
                return false;
            }

            public void Dispose()
            {
            }
        }
    }
}
