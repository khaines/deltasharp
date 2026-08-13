using System.Diagnostics;
using System.Text;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Fuzz coverage for the untrusted-input parsers (design §5.4 C-DECODE; "fail deterministically, name the
/// defect, publish no partial state, fail closed"). Random and mutated inputs to the JSON action reader,
/// the checkpoint Parquet reader, the <c>_last_checkpoint</c> hint, and the log-file classifier must only
/// ever succeed or fail closed with a typed exception — the parse-shape faults as
/// <see cref="DeltaProtocolException"/>, a wall-clock decode-budget trip as a
/// <see cref="DeltaStorageException"/> with <see cref="StorageErrorKind.DecodeBudgetExceeded"/> — never an
/// unexpected exception, and never hang.
/// </summary>
/// <remarks>
/// <para><b>Termination is now an ORACLE, not just a comment (#647/#699/#716).</b> The checkpoint oracle
/// (<see cref="AssertCheckpointReadIsClosedAsync"/>) runs each read under a LOW decode budget
/// (<see cref="TestDecodeBudget"/>) AND races it against a wall-clock watchdog
/// (<see cref="OracleWatchdog"/>), so a non-terminating decode of a crafted checkpoint becomes a
/// deterministic test FAILURE instead of a stuck CI job — closing the "fail closed but no time budget" gap
/// the old single-bit-flip oracle had.</para>
/// </remarks>
public sealed class DeltaFuzzTests
{
    // A LOW internal decode budget so a non-terminating decode of a crafted checkpoint is converted to a
    // deterministic typed failure in a few hundred ms — the real default is BoundedDecode.DefaultBudget (30s),
    // far too slow to run a fuzz corpus under. Exercised via the DeltaCheckpointReader.ReadAsync override seam.
    private static readonly TimeSpan TestDecodeBudget = TimeSpan.FromMilliseconds(300);

    // A generous wall-clock oracle watchdog: with the low budget above, a fail-closed read returns in ms, so
    // this only ever trips if the bounded-decode policy itself failed to release the caller (a genuine
    // regression of #647/#699/#716). It converts such a hang into a deterministic TEST FAILURE, not a stuck CI.
    private static readonly TimeSpan OracleWatchdog = TimeSpan.FromSeconds(20);

    // A PER-TEST isolated checkpoint decoder for the hanging-decode fuzz cases (Round-8 test isolation). These
    // tests deliberately drive a NON-TERMINATING decode that DETACHES and strands its door FOREVER. With the
    // honest per-part strand charge (Round-8 #3, GiB-scale), a stranded decode on the PROCESS-GLOBAL static
    // BoundedDecode.CheckpointDecoder would permanently consume its residual and saturate it for every other
    // test in the process (pre-Round-8 the charge was ~KB, so pollution was harmless). Routing each hanging
    // case through its OWN decoder confines the permanent strand to a garbage-collected per-test instance; the
    // shared static door only ever sees healthy (never-stranding) decodes. A DedicatedThread execution mirrors
    // the real checkpoint door (a synchronous decode over pre-buffered bytes).
    //
    // PRODUCTION-SIZED (Round-10 test sizing): sized from the REAL checkpoint door footprint
    // (CheckpointMaxFootprintBytes) with a matching residual, NOT the 1-byte default — otherwise every strand
    // CHARGE clamps to 1 byte and the charge oracles are vacuous. DeriveDoorSizing on a large pod gives the real
    // residual so a stranded checkpoint books its honest LIVE charge (Round-10 #1).
    private static BoundedDecoder IsolatedCheckpointDecoder() =>
        BoundedDecoder.FromSizing(
            BoundedDecode.DeriveDoorSizing(
                256L * 1024 * 1024 * 1024, BoundedDecode.CheckpointMaxFootprintBytes, processorCount: 8),
            DecodeExecution.DedicatedThread);

    [Fact]
    public void JsonActionReader_OnlyFailsClosed_OnRandomBytes()
    {
        var random = new Random(1);
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            AssertJsonParseIsClosed(bytes);
        }
    }

    [Fact]
    public void JsonActionReader_OnlyFailsClosed_OnMutatedValidCommits()
    {
        byte[] valid = Encoding.UTF8.GetBytes(string.Join('\n',
            DeltaTestHarness.Protocol(),
            DeltaTestHarness.Metadata(id: "t", partitionColumns: ["year"]),
            DeltaTestHarness.Add("a.parquet", stats: """{"numRecords":3,"minValues":{"id":1},"maxValues":{"id":9}}""",
                partitionValues: [("year", "2026")]),
            DeltaTestHarness.Remove("b.parquet"),
            DeltaTestHarness.Txn("app", 4)) + "\n");

        var random = new Random(2);
        for (int i = 0; i < 5000; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            int mutations = random.Next(1, 4);
            for (int m = 0; m < mutations; m++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(0, 256);
            }

            AssertJsonParseIsClosed(mutated);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnRandomBytes()
    {
        var random = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            byte[] bytes = new byte[random.Next(0, 256)];
            random.NextBytes(bytes);
            await AssertCheckpointReadIsClosedAsync(bytes);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnTruncatedValidCheckpoint()
    {
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""", partitionColumns: ["year"])
            .Add("a.parquet", size: 5, partitionValues: [("year", "2026")], tags: [("k", "v")])
            .Txn("app", 7)
            .ToParquetAsync();

        var random = new Random(4);
        for (int i = 0; i < 400; i++)
        {
            int length = random.Next(0, valid.Length);
            byte[] truncated = valid[..length];
            await AssertCheckpointReadIsClosedAsync(truncated);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnByteFlippedCheckpoint()
    {
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""")
            .Add("a.parquet", size: 1)
            .ToParquetAsync();

        var random = new Random(5);
        for (int i = 0; i < 400; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));
            await AssertCheckpointReadIsClosedAsync(mutated);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnByteSplattedCheckpoint()
    {
        // Multi-bit / byte-SPLAT corpus (seeded, replayable) alongside the single-bit-flip corpus above. It
        // replaces 1-3 bytes with an ARBITRARY value (Random(4242)), reaching mutations the XOR-single-bit
        // generator provably cannot — e.g. 0x00 -> 0xB4, exactly the #716 class the bit-flip fuzz missed. Each
        // read is bounded (TestDecodeBudget) and watchdogged, so a hang is a failure, not a stuck job.
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""")
            .Add("a.parquet", size: 1)
            .ToParquetAsync();

        var random = new Random(4242);
        for (int i = 0; i < 400; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            int splats = random.Next(1, 4);
            for (int s = 0; s < splats; s++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            await AssertCheckpointReadIsClosedAsync(mutated);
        }
    }

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
            "adc4ae4b08b536affbf63283aaf9870467e4524bb028342f3f498ddfe3f13042",
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
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"expected a fast fail-closed, took {elapsed}.");
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
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"expected a fast fail-closed, took {elapsed}.");
    }

    [Fact]
    public void LastCheckpointHint_NeverThrows_OnRandomBytes()
    {
        var random = new Random(6);
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 48)];
            random.NextBytes(bytes);
            _ = LastCheckpointHint.TryParse(bytes); // must never throw; null or a hint
        }
    }

    [Fact]
    public void DeltaLogFiles_Classify_NeverThrows_OnRandomNames()
    {
        var random = new Random(7);
        const string alphabet = "0123456789abcdef.-checkpoint_json_parquet";
        for (int i = 0; i < 20000; i++)
        {
            int length = random.Next(0, 48);
            var sb = new StringBuilder(length);
            for (int c = 0; c < length; c++)
            {
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            }

            _ = DeltaLogFiles.Classify(sb.ToString()); // total function; must never throw
        }
    }

    private static void AssertJsonParseIsClosed(byte[] bytes)
    {
        try
        {
            _ = DeltaLogActionReader.ParseCommit(bytes, version: 0);
        }
        catch (DeltaProtocolException)
        {
            // acceptable: fail closed
        }
    }

    private static async Task AssertCheckpointReadIsClosedAsync(byte[] bytes)
    {
        // Bound each read with BOTH a low internal decode budget AND a wall-clock watchdog. The read runs on
        // the thread pool so a CPU-bound non-terminating decode — which the underlying decoder does not
        // cancel — cannot block the assertion thread; on a watchdog trip we fail the test rather than stall CI
        // (§5.4 C-DECODE — the bounded wall-clock decode ceiling). A fail-closed outcome is EITHER a
        // DeltaProtocolException (parse-shape fault) OR a DeltaStorageException with DecodeBudgetExceeded (a
        // wall-clock stall that the budget converted to a typed release).
        Task read = Task.Run(async () =>
        {
            try
            {
                _ = await DeltaCheckpointReader.ReadAsync(
                    new MemoryStream(bytes), default, decodeBudget: TestDecodeBudget,
                    decoder: IsolatedCheckpointDecoder());
            }
            catch (DeltaStorageException ex) when (ex.Kind == StorageErrorKind.DecodeBudgetExceeded)
            {
                // acceptable: fail closed on a wall-clock decode-budget trip
            }
            catch (DeltaProtocolException)
            {
                // acceptable: fail closed
            }
        });

        if (await Task.WhenAny(read, Task.Delay(OracleWatchdog)) != read)
        {
            Assert.Fail(
                $"DeltaCheckpointReader.ReadAsync did NOT terminate within {OracleWatchdog.TotalSeconds:0}s under a "
                + $"{TestDecodeBudget.TotalMilliseconds:0}ms decode budget — the bounded-decode policy failed to "
                + "release the caller (regression of #647/#699/#716). §5.4 C-DECODE requires a decode of untrusted "
                + "bytes to fail closed and NEVER hang.");
        }

        // Surface any NON-fail-closed exception (an unexpected type) as a test failure.
        await read;
    }

    // Runs a single untrusted-checkpoint read on the thread pool under the oracle watchdog, returning the
    // (fail-closed) exception it threw and the wall-clock elapsed. A watchdog trip is a deterministic test
    // failure (non-termination), never a stuck job. Used by the pinned #716/#699 regressions to assert both
    // "a typed exception is thrown" and "it returns well under the real default budget".
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
