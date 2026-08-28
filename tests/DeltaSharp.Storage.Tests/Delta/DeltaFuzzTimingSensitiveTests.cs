using System.Diagnostics;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The <see cref="DeltaFuzzTests"/> checkpoint cases that assert a crafted checkpoint fails closed WITHIN a
/// real wall-clock decode budget (#699/#716). Both pin the same time-based invariant — the read is held until
/// at least the budget elapses and then released with a typed <see cref="StorageErrorKind.DecodeBudgetExceeded"/>
/// — and both race that deadline the same way under full-parallel suite load: the crafted decode strands on
/// its own thread while the fail-closed exception propagation resumes on a shared <see cref="System.Threading.ThreadPool"/>
/// starved by the rest of the suite, so the wall-clock oracle can miss its window even though the policy is
/// healthy. They run in the non-parallel <see cref="TimingSensitiveDecodeCollection"/> so no sibling test
/// starves the pool while they run; both keep their REAL wall-clock budget and watchdog (nothing weakened).
/// The pinned <c>OnMinimized716Input</c> sibling is moved here too because it shares the identical
/// <see cref="TimeBoundedReadAsync"/> premise and would otherwise remain a latent flake of the same cluster.
/// </summary>
[Collection(TimingSensitiveDecodeCollection.Name)]
public sealed class DeltaFuzzTimingSensitiveTests
{
    // A LOW internal decode budget so a non-terminating decode of a crafted checkpoint is converted to a
    // deterministic typed failure in a few hundred ms — the real default is BoundedDecode.DefaultBudget (30s),
    // far too slow to run a fuzz corpus under. Exercised via the DeltaCheckpointReader.ReadAsync override seam.
    private static readonly TimeSpan TestDecodeBudget = TimeSpan.FromMilliseconds(300);

    // A generous wall-clock oracle watchdog: with the low budget above, a fail-closed read returns in ms, so
    // this only ever trips if the bounded-decode policy itself failed to release the caller (a genuine
    // regression of #647/#699/#716). It converts such a hang into a deterministic TEST FAILURE, not a stuck CI.
    private static readonly TimeSpan OracleWatchdog = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task CheckpointReader_FailsClosedWithinBudget_OnMinimized716Input()
    {
        // #716 minimized DETERMINISTIC repro. One byte — index 5595, the last footer-BODY byte immediately
        // before the 4-byte footer_length — changed 0x00 -> 0xB4 drives DeltaCheckpointReader.ReadAsync into
        // >4 min 30 s of unbounded, cancellation-ignoring work. The bounded-decode policy must convert that
        // into a deterministic typed fail-closed exception WITHIN the budget rather than hanging.
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""")
            .Add("a.parquet", size: 1)
            .ToParquetAsync();

        // Pin the fixture size AND content so the hard-coded byte index stays the byte the issue minimized to;
        // assert the PRE-mutation value the repro flipped (0x00 at index 5595 → 0xB4) so a fixture drift that
        // moved that byte cannot silently pass a different mutation.
        Assert.Equal(5604, valid.Length);
        Assert.Equal(0x00, valid[5595]);
        valid[5595] = 0xB4;
        Assert.Equal(
            // Re-derived under Parquet.Net 6.1.0 (#832): the library's own footer/page bytes shifted, so the
            // fixture's exact bytes changed while its length and structure invariants above are unchanged.
            "ca34fdac2017b8eef6d59fae3552ec877cff9bff28b68c053841997e1d7966c2",
            Sha256Hex(valid));

        (Exception? thrown, TimeSpan elapsed) = await TimeBoundedReadAsync(
            () => DeltaCheckpointReader.ReadAsync(
                new MemoryStream(valid), default, decodeBudget: TestDecodeBudget, decoder: IsolatedCheckpointDecoder()));

        // (a) the DISTINCT typed DecodeBudgetExceeded (a DeltaStorageException, NOT DeltaProtocolException: a
        // wall-clock stall is a resource fault, not proven corruption — #649/#655/#681 classification), which
        // DeltaLog still routes to JSON replay (under the DecodeTimeout reason); and (b) it returns AT LEAST
        // the budget yet well under the real default (BoundedDecode.DefaultBudget, 30s). This kind is unique to
        // the bounded-decode path, so removing the wrapper turns this red (the input hangs → watchdog fires).
        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, ex.Kind);
        Assert.True(elapsed >= TestDecodeBudget, $"expected the read to run at least the budget, took {elapsed}.");
    }

    [Fact]
    public async Task CheckpointReader_FailsClosedWithinBudget_OnLastFooterByteFlip()
    {
        // #699 on the CHECKPOINT door. A single bit flip in the LAST footer byte (the terminal Thrift STOP of
        // FileMetaData, index len-9 before the 4-byte footer_length + PAR1 magic) drives the open
        // (ParquetReader.CreateAsync + the forced lazy schema) into unbounded, token-ignoring work. The
        // bounded-time open must fail closed within the budget. (This is the same byte class — index 5595 for
        // this fixture — as #716, reached by a bit flip rather than a splat.)
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""")
            .Add("a.parquet", size: 1)
            .ToParquetAsync();
        valid[^9] ^= 1;

        (Exception? thrown, TimeSpan elapsed) = await TimeBoundedReadAsync(
            () => DeltaCheckpointReader.ReadAsync(
                new MemoryStream(valid), default, decodeBudget: TestDecodeBudget, decoder: IsolatedCheckpointDecoder()));

        // A wall-clock stall on the checkpoint door — the DISTINCT DecodeBudgetExceeded kind (a
        // DeltaStorageException), NOT DeltaProtocolException; DeltaLog routes it to JSON replay all the same.
        DeltaStorageException ex = Assert.IsType<DeltaStorageException>(thrown);
        Assert.Equal(StorageErrorKind.DecodeBudgetExceeded, ex.Kind);
        Assert.True(elapsed >= TestDecodeBudget, $"expected the read to run at least the budget, took {elapsed}.");
    }

    // A PER-TEST isolated checkpoint decoder for the hanging-decode fuzz cases (Round-8 test isolation). These
    // tests deliberately drive a NON-TERMINATING decode that DETACHES and strands its door FOREVER; routing
    // each through its OWN decoder confines the permanent strand to a garbage-collected per-test instance so
    // the shared static BoundedDecode.CheckpointDecoder only ever sees healthy decodes. Production-sized so the
    // strand charge is not vacuously clamped to 1 byte (Round-10 sizing).
    private static BoundedDecoder IsolatedCheckpointDecoder() =>
        BoundedDecoder.FromSizing(
            BoundedDecode.DeriveDoorSizing(
                256L * 1024 * 1024 * 1024, BoundedDecode.CheckpointMaxFootprintBytes, processorCount: 8),
            DecodeExecution.DedicatedThread);

    // Runs a single untrusted-checkpoint read on the thread pool under the oracle watchdog, returning the
    // (fail-closed) exception it threw and the wall-clock elapsed. A watchdog trip is a deterministic test
    // failure (non-termination), never a stuck job.
    private static async Task<(Exception? Thrown, TimeSpan Elapsed)> TimeBoundedReadAsync(Func<Task> read)
    {
        var stopwatch = Stopwatch.StartNew();
        Task<Exception?> run = Task.Run(async () =>
        {
            try
            {
                await read();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        if (await Task.WhenAny(run, Task.Delay(OracleWatchdog)) != run)
        {
            Assert.Fail(
                $"The read did not terminate within {OracleWatchdog.TotalSeconds:0}s — the bounded-decode policy "
                + "failed to release the caller (regression of #647/#699/#716).");
        }

        Exception? thrown = await run;
        stopwatch.Stop();
        return (thrown, stopwatch.Elapsed);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
