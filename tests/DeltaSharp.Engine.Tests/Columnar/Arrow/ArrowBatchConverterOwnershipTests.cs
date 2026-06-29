using System.Threading;
using Apache.Arrow;
using Apache.Arrow.Types;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// AC4 (STORY-02.2.2, #136): ownership transfer at the interop boundary. Disposing an imported batch
/// releases the source Arrow buffers <b>exactly once</b> under
/// <see cref="ArrowImportOwnership.Transfer"/> (idempotent on repeated dispose) and <b>never</b>
/// under <see cref="ArrowImportOwnership.Borrowed"/> (the caller owns the source). Both the dispose
/// funnel (via a counting <see cref="RecordBatch"/>) and the underlying buffers (via a counting
/// allocator) are observed, and the column accessors close after disposal.
/// </summary>
public class ArrowBatchConverterOwnershipTests
{
    [Fact]
    public void Transfer_DisposesSourceExactlyOnce_EvenWhenDisposedTwice()
    {
        DisposeCountingRecordBatch source = BuildCountingBatch();

        ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(source, ArrowImportOwnership.Transfer);
        Assert.Equal(ArrowImportOwnership.Transfer, batch.Ownership);
        Assert.False(batch.IsDisposed);

        batch.Dispose();
        batch.Dispose(); // idempotent: must not release the source a second time

        Assert.True(batch.IsDisposed);
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public void Transfer_DisposesSourceExactlyOnce_UnderConcurrentDispose()
    {
        // The exactly-once guarantee is interlocked, so racing Dispose from many threads must still
        // release the source exactly once. DisposeCountingRecordBatch carries no idempotency guard, so
        // a non-atomic check/set in ArrowColumnBatch.Dispose would let two racers both enter the funnel
        // (DisposeCalls == 2). A Barrier releases all racers simultaneously to maximize the
        // interleaving, repeated across many rounds; on the interlocked code the invariant holds for
        // every interleaving (never false-fails), and the non-atomic mutant is reliably killed.
        const int rounds = 2000;
        const int racers = 8;
        for (int r = 0; r < rounds; r++)
        {
            DisposeCountingRecordBatch source = BuildCountingBatch();
            ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(source, ArrowImportOwnership.Transfer);

            using var barrier = new Barrier(racers);
            var threads = new Thread[racers];
            for (int t = 0; t < racers; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait(); // all racers cross together
                    batch.Dispose();
                })
                { IsBackground = true };
                threads[t].Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }

            Assert.True(batch.IsDisposed);
            Assert.Equal(1, source.DisposeCalls); // exactly once despite the concurrent dispose
        }
    }

    [Fact]
    public void Borrowed_NeverDisposesSource_CallerOwnsLifetime()
    {
        DisposeCountingRecordBatch source = BuildCountingBatch();

        ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(source); // default: Borrowed
        Assert.Equal(ArrowImportOwnership.Borrowed, batch.Ownership);

        batch.Dispose();
        batch.Dispose();
        Assert.True(batch.IsDisposed);
        Assert.Equal(0, source.DisposeCalls); // borrowed: the source is untouched

        source.Dispose();
        Assert.Equal(1, source.DisposeCalls); // the caller releases it exactly once
    }

    [Fact]
    public void Dispose_ClosesColumnAccess()
    {
        DisposeCountingRecordBatch source = BuildCountingBatch();
        ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(source, ArrowImportOwnership.Transfer);

        batch.Dispose();

        Assert.Throws<ObjectDisposedException>(() => batch.Column(0));
        Assert.Throws<ObjectDisposedException>(() => batch.Slice(0, 1));
        Assert.Throws<ObjectDisposedException>(() => batch.WithSelection(new SelectionVector(new[] { 0 })));
    }

    [Fact]
    public void Transfer_FreesAllBuffersExactlyOnce()
    {
        var allocator = new CountingMemoryAllocator();
        RecordBatch source = BuildAllocatorBatch(allocator);
        Assert.True(allocator.AllocationCount > 0);
        Assert.Equal(allocator.AllocationCount, allocator.Outstanding);

        ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(source, ArrowImportOwnership.Transfer);
        batch.Dispose();
        batch.Dispose();

        // This allocator-based oracle proves buffers are freed once at the IMemoryOwner level, but it
        // can't detect a RecordBatch-level double-free: CountingOwner._freed makes Dispose idempotent,
        // masking a second release of the same owner. DisposeCountingRecordBatch (used by the dispose
        // tests above) is the real double-free detector; the two oracles are complementary.
        Assert.Equal(0, allocator.Outstanding); // every buffer freed
        Assert.Equal(allocator.AllocationCount, allocator.FreeCount); // exactly once each
    }

    [Fact]
    public void Borrowed_LeavesBuffersOutstanding_UntilCallerDisposesSource()
    {
        var allocator = new CountingMemoryAllocator();
        RecordBatch source = BuildAllocatorBatch(allocator);
        int allocated = allocator.AllocationCount;

        using (ArrowBatchConverter.FromArrow(source))
        {
            // Disposing the borrowed batch at the end of this scope must not release the source.
        }

        Assert.Equal(allocated, allocator.Outstanding);
        Assert.Equal(0, allocator.FreeCount);

        source.Dispose();
        Assert.Equal(0, allocator.Outstanding);
        Assert.Equal(allocated, allocator.FreeCount);
    }

    private static DisposeCountingRecordBatch BuildCountingBatch()
    {
        Int32Array column = new Int32Array.Builder().Append(1).AppendNull().Append(3).Build();
        var schema = new Schema(new[] { new Field("c", Int32Type.Default, nullable: true) }, metadata: null);
        return new DisposeCountingRecordBatch(schema, new IArrowArray[] { column }, length: 3);
    }

    private static RecordBatch BuildAllocatorBatch(CountingMemoryAllocator allocator)
    {
        Int32Array column = new Int32Array.Builder().Append(1).AppendNull().Append(3).Build(allocator);
        var schema = new Schema(new[] { new Field("c", Int32Type.Default, nullable: true) }, metadata: null);
        return new RecordBatch(schema, new IArrowArray[] { column }, length: 3);
    }
}
