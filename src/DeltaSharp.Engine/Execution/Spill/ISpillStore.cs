using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Engine.Execution.Spill;

/// <summary>
/// The spill <b>target</b> a stateful operator writes partial state to when a memory reservation is
/// refused (STORY-03.6.2). It is the runtime-supplied half of the EPIC-02 spill seam: the unified
/// memory model (ADR-0013) decides <i>when</i> to spill (a refused reservation), and this abstraction
/// decides <i>where</i> the bytes go — an in-memory buffer (<see cref="MemorySpillStore"/>), a local
/// temp file (<see cref="TempFileSpillStore"/>), or, in tests, a fault-injecting store that makes the
/// I/O-failure contract (AC5) deterministically reproducible.
/// </summary>
/// <remarks>
/// <para>
/// A store hands out append-only <see cref="ISpillSegment"/>s. An operator opens one segment per spill
/// partition/run, writes opaque byte records (already serialized by <see cref="RowSpillCodec"/> or an
/// aggregator state writer), then reads them back in write order for the merge/recover phase. The store
/// owns no schema knowledge — it moves bytes only — so it stays AOT-clean and shares one implementation
/// across aggregate, sort, join, and exchange.
/// </para>
/// <para>
/// <b>Threading.</b> Each operator owns its store and its segments; the data path is single-threaded per
/// operator, so implementations need no internal locking on the hot path.
/// </para>
/// </remarks>
internal interface ISpillStore
{
    /// <summary>
    /// Creates a fresh append-only segment. <paramref name="label"/> is a human-readable tag used in
    /// temp-file names and diagnostics (e.g. <c>"agg-p3"</c>); it need not be unique.
    /// </summary>
    /// <exception cref="SpillIOException">The backing medium could not allocate a segment.</exception>
    ISpillSegment CreateSegment(string label);
}

/// <summary>
/// One append-only sequence of byte records produced by an operator's spill phase. A segment is written
/// fully, then read back in write order (one or more times). Disposal releases the backing medium and,
/// for a temp-file segment, deletes the file — on the normal, cancellation, and failure paths alike.
/// </summary>
internal interface ISpillSegment : IDisposable
{
    /// <summary>The total number of payload bytes written so far (the spill-bytes accounting unit).</summary>
    long BytesWritten { get; }

    /// <summary>Appends one opaque record to the segment.</summary>
    /// <param name="record">The record bytes (already framed/serialized by the caller).</param>
    /// <exception cref="SpillIOException">The write failed; the operator must release memory and fail deterministically.</exception>
    void Write(ReadOnlySpan<byte> record);

    /// <summary>
    /// Opens a reader positioned at the first record. Multiple readers may be opened in sequence; the
    /// caller disposes each. Implementations assume all writes precede the first read.
    /// </summary>
    /// <exception cref="SpillIOException">The segment could not be opened for reading.</exception>
    ISpillSegmentReader OpenRead();
}

/// <summary>A forward-only reader over an <see cref="ISpillSegment"/>'s records, in write order.</summary>
internal interface ISpillSegmentReader : IDisposable
{
    /// <summary>
    /// Reads the next record, or returns <see langword="false"/> at end of segment.
    /// </summary>
    /// <param name="record">The next record's bytes when this returns <see langword="true"/>.</param>
    /// <exception cref="SpillIOException">The read failed or the segment is corrupt/truncated.</exception>
    bool TryRead([NotNullWhen(true)] out byte[]? record);
}
