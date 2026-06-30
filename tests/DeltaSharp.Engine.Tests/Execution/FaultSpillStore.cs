using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Execution.Spill;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// A fault-injecting <see cref="ISpillStore"/> for STORY-03.6.2 AC5: it wraps a real store and throws a
/// <see cref="SpillIOException"/> deterministically on the Nth write (<see cref="FailOnWriteAfter"/>) or
/// on the first read (<see cref="FailOnRead"/>). This makes the spill I/O-failure contract — release-all,
/// deterministic typed error, no partial output — reproducible without depending on a real disk fault.
/// </summary>
internal sealed class FaultSpillStore : ISpillStore
{
    private readonly ISpillStore _inner;
    private int _writes;

    public FaultSpillStore()
        : this(new MemorySpillStore())
    {
    }

    public FaultSpillStore(ISpillStore inner) => _inner = inner;

    /// <summary>Throw on the write whose 1-based ordinal exceeds this count (0 disables write faults).</summary>
    public int FailOnWriteAfter { get; init; }

    /// <summary>Throw on the first read from any segment.</summary>
    public bool FailOnRead { get; init; }

    public ISpillSegment CreateSegment(string label) => new Segment(this, _inner.CreateSegment(label));

    private bool ShouldFailWrite() =>
        FailOnWriteAfter > 0 && Interlocked.Increment(ref _writes) > FailOnWriteAfter;

    private sealed class Segment : ISpillSegment
    {
        private readonly FaultSpillStore _store;
        private readonly ISpillSegment _inner;

        public Segment(FaultSpillStore store, ISpillSegment inner)
        {
            _store = store;
            _inner = inner;
        }

        public long BytesWritten => _inner.BytesWritten;

        public void Write(ReadOnlySpan<byte> record)
        {
            if (_store.ShouldFailWrite())
            {
                throw new SpillIOException("write", "injected spill write fault");
            }

            _inner.Write(record);
        }

        public ISpillSegmentReader OpenRead() =>
            _store.FailOnRead ? new FaultReader() : _inner.OpenRead();

        public void Dispose() => _inner.Dispose();
    }

    private sealed class FaultReader : ISpillSegmentReader
    {
        public bool TryRead([NotNullWhen(true)] out byte[]? record) =>
            throw new SpillIOException("read", "injected spill read fault");

        public void Dispose()
        {
        }
    }
}
