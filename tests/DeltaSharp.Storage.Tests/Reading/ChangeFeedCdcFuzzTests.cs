using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using DeltaSharp.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Fail-closed fuzz for the Change Data Feed <b>read door</b> on a corrupted <c>_change_data/</c> cdc file
/// (increment 4 of #193; <c>storage-delta-architecture.md</c> §5.4 (C-DECODE) "fail deterministically … fail
/// closed", applied to §2.6).
/// </summary>
/// <remarks>
/// <para><b>Inherited coverage (NOT duplicated here).</b> The cdc bytes-decode engine is the SHARED
/// <c>ParquetFileReader</c> — the SAME reader the snapshot/checkpoint paths use — which is already fuzzed for
/// fail-closed <i>decode</i> at the Parquet tier (e.g. <c>ParquetReaderTests</c> and
/// <c>ParquetCorruptionTests</c>: garbage / malformed-footer / truncated / byte-flipped Parquet all throw a
/// typed <c>DeltaStorageException</c>). Raw-byte Parquet decode-exception fuzzing is therefore inherited; this
/// test does NOT re-fuzz Parquet decode exception-mapping.</para>
/// <para><b>Termination is NOT fully inherited — this fuzz found a real gap.</b>
/// <c>storage-delta-architecture.md</c> §5.4 (C-DECODE) also requires the decode to NEVER hang, but a corrupt
/// Parquet <i>data-page header</i> can drive <c>Parquet.Net</c>'s synchronous decode into a non-terminating CPU
/// loop that no exception handler or <c>CancellationToken</c> can interrupt (repro: this fuzz with
/// <c>DELTASHARP_TEST_SEED=42</c>, byte-flip strategy, iteration 148). That is a Parquet-reader-tier gap
/// affecting ALL Parquet reads, not fixable at the DeltaSharp layer today; the production non-termination /
/// multi-tenant-DoS exposure it creates is tracked by #647.</para>
/// <para><b>The <see cref="WatchdogTimeout"/> here is a CI/TEST-TIER bound, NOT a production control.</b> It
/// exists only to convert such a hang into a deterministic, attributable <i>test</i> failure (citing #647)
/// instead of stalling CI — it enforces "never hangs" <i>as a test observation</i>. It does NOT bound a
/// production reader (a real deployment gets no watchdog); the production fix is tracked by #647. The delivered
/// default seed never trips it (all 200 iterations fail closed in ms).</para>
/// <para><b>New coverage added here.</b> The END-TO-END read door layers CDF-specific logic ON TOP of the
/// shared reader — exception classification/wrapping (Parquet <c>DeltaStorageException</c> →
/// <see cref="DeltaReadException"/>), <c>_change_type</c> domain validation, per-version leaf-schema
/// reconciliation, and row-count consistency. This test drives the REAL door
/// (<see cref="DeltaReadSource.LoadChangeFeedAsync"/> + <see cref="DeltaReadSource.ReadChangeBatchesAsync"/>)
/// over a real, on-disk cdc file mutated with random-overwrite / truncate / byte-flip / trailing-garbage
/// strategies, and asserts the door only ever COMPLETES (a benign mutation) OR throws the typed
/// <see cref="DeltaReadException"/> / <see cref="DeltaReadSchemaEvolutionException"/> — never an unexpected
/// exception type (a non-fail-closed bug). It drives the door DIRECTLY (not the harness decode) so a mutation
/// that yields well-formed-but-different bytes is a legal "completed" outcome, not an oracle violation.</para>
/// <para>The seed honors <see cref="TestSeed.EnvironmentVariable"/> (<c>DELTASHARP_TEST_SEED</c>); the house
/// <c>[deltasharp-seed]</c> line is emitted so any failure is deterministically replayable.</para>
/// </remarks>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class ChangeFeedCdcFuzzTests : IDisposable
{
    private const string Scope = nameof(ChangeFeedCdcFuzzTests);

    private readonly ITestOutputHelper _output;
    private readonly List<string> _roots = [];

    public ChangeFeedCdcFuzzTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task CdcReadDoor_OnlyFailsClosed_OnMutatedCdcFile()
    {
        int baseSeed = TestSeed.Resolve();
        var random = new Random(TestSeed.Combine(baseSeed, Scope));
        _output.WriteLine($"[deltasharp-seed] {Scope} baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");

        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                                                          // v0
        await table.EnableCdfAsync();                                                            // v1
        await table.AppendAsync([new(1, "east", 10), new(2, "east", 20), new(3, "east", 30)]);   // v2 (one east file)
        DeleteResult delete = await table.DeleteAsync([1, 2]);                                    // v3 partial delete ⇒ cdc file
        Assert.Equal(3L, delete.CommittedVersion);
        Assert.Equal(1, delete.FilesWithDeletionVector); // partial delete: the east file keeps id 3 + a new DV

        IReadOnlyList<string> cdcFiles = table.CdcFilePaths();
        Assert.NotEmpty(cdcFiles); // a real cdc file exists to mutate (the explicit CDF path materialized it)
        string cdcPath = table.AbsolutePath(cdcFiles[0]);
        byte[] original = await File.ReadAllBytesAsync(cdcPath);
        Assert.NotEmpty(original);

        // Baseline: the UNmutated door read of the delete version yields exactly the two deleted rows — proves
        // the range/read is correct before we start corrupting (so a later throw is due to the mutation).
        var deleteRange = DeltaChangeFeedRange.FromVersion(3, 3);
        Assert.Equal(2, (await table.ReadRangeAsync(deleteRange)).Changes.Count);

        const int iterations = 200;
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                byte[] mutated = Mutate(original, random);
                await File.WriteAllBytesAsync(cdcPath, mutated);

                // Alternate the read range: the cdc-only version [3,3], and the full range [1,3] (which yields
                // v2's implicit insert batch FIRST, then hits the corrupt cdc at v3 — exercising the mid-stream
                // fail-closed contract). Both must fail closed identically.
                DeltaChangeFeedRange range = (i % 2 == 0)
                    ? deleteRange
                    : DeltaChangeFeedRange.FromVersion(1, 3);

                await AssertDoorFailsClosedAsync(table.Root, range, baseSeed, i, mutated.Length);
            }
        }
        finally
        {
            await File.WriteAllBytesAsync(cdcPath, original); // leave the table consistent for disposal
        }
    }

    // CI/TEST-TIER watchdog — NOT a production control. A benign or fail-closed door read completes in
    // MILLISECONDS; this generous ceiling only ever trips on a genuinely non-terminating decode.
    // storage-delta-architecture.md §5.4 (C-DECODE) requires the decode to fail closed and NEVER hang, but a
    // corrupt Parquet data-page header can drive Parquet.Net's synchronous decode into a pathological CPU loop
    // that observes no CancellationToken (a Parquet-reader-tier gap; see the class remarks). This watchdog only
    // converts such a hang into a deterministic, attributable TEST failure so this fuzz enforces "never hangs"
    // as a test observation instead of stalling CI. It does NOT bound a production reader — the production
    // non-termination / multi-tenant-DoS fix is tracked by #647. The delivered default seed never trips it.
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);

    private async Task AssertDoorFailsClosedAsync(
        string root, DeltaChangeFeedRange range, int baseSeed, int iteration, int mutatedLength)
    {
        // Bound EVERY read with the watchdog. The read runs on the thread pool so a CPU-bound non-terminating
        // decode (which observes no cancellation) cannot block the assertion; on a trip we abandon that read and
        // fail closed — the test process stays bounded.
        Task read = Task.Run(() => ReadDoorAsync(root, range, CancellationToken.None));
        if (await Task.WhenAny(read, Task.Delay(WatchdogTimeout)) != read)
        {
            EmitReproduction(baseSeed, iteration);
            Assert.Fail(
                $"CDF read door did NOT terminate within {WatchdogTimeout.TotalSeconds:0}s on a mutated cdc file "
                + $"(range=[{range.StartingVersion},{range.EndingVersion}], mutatedBytes={mutatedLength}, "
                + $"iteration={iteration}). storage-delta-architecture.md §5.4 (C-DECODE) requires the decode to "
                + "fail closed and NEVER hang. This is the known Parquet-reader-tier decode-termination gap "
                + "tracked by #647 (a corrupt data-page header drives Parquet.Net's synchronous decode into a "
                + "non-terminating CPU loop that observes no CancellationToken) — NOT a CDF-layer defect.");
        }

        try
        {
            await read;

            // Completed without throwing: acceptable — the mutation produced a still-valid Parquet whose rows
            // pass the door's _change_type / schema / row-count checks (fail-closed permits a benign mutation).
        }
        catch (DeltaReadException)
        {
            // Fail-closed: the door classified corruption / inconsistency into its typed read exception.
        }
        catch (DeltaReadSchemaEvolutionException)
        {
            // Fail-closed: a mutated leaf schema dropped a REQUIRED column the output schema demands.
        }
        catch (Exception ex)
        {
            EmitReproduction(baseSeed, iteration);
            Assert.Fail(
                $"CDF read door threw an UNEXPECTED {ex.GetType().FullName} — not "
                + $"{nameof(DeltaReadException)}/{nameof(DeltaReadSchemaEvolutionException)} — on a mutated cdc "
                + $"file (range=[{range.StartingVersion},{range.EndingVersion}], mutatedBytes={mutatedLength}, "
                + $"iteration={iteration}). The fail-closed decode contract "
                + $"(storage-delta-architecture.md §5.4 C-DECODE) is violated.\n{ex}");
        }
    }

    private void EmitReproduction(int baseSeed, int iteration) =>
        _output.WriteLine(
            $"[deltasharp-seed] scope={Scope} baseSeed={baseSeed} iteration={iteration} | reproduce: "
            + $"{TestSeed.EnvironmentVariable}={baseSeed} dotnet test tests/DeltaSharp.Storage.Tests "
            + $"--filter \"FullyQualifiedName~{Scope}\"");

    /// <summary>Drives the REAL read door end-to-end (resolve + full batch enumeration), materializing every
    /// batch. No harness oracle asserts — only the door's own fail-closed behavior is under test.</summary>
    private static async Task ReadDoorAsync(string root, DeltaChangeFeedRange range, CancellationToken ct)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range, ct);
        await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, ct))
        {
            _ = batch.LogicalRowCount; // force full materialization of each yielded batch
        }
    }

    private static byte[] Mutate(byte[] original, Random random)
    {
        switch (random.Next(4))
        {
            case 0: // random overwrite (arbitrary length, including empty)
                byte[] noise = new byte[random.Next(0, original.Length + 8)];
                random.NextBytes(noise);
                return noise;

            case 1: // truncate to a random shorter length (including 0)
                return original[..random.Next(0, original.Length)];

            case 2: // flip a handful of random bits
                byte[] flipped = (byte[])original.Clone();
                int flips = random.Next(1, 8);
                for (int f = 0; f < flips; f++)
                {
                    flipped[random.Next(flipped.Length)] ^= (byte)(1 << random.Next(8));
                }

                return flipped;

            default: // append trailing garbage (corrupts the Parquet footer-length interpretation)
                byte[] appended = new byte[original.Length + random.Next(1, 32)];
                original.CopyTo(appended, 0);
                for (int k = original.Length; k < appended.Length; k++)
                {
                    appended[k] = (byte)random.Next(256);
                }

                return appended;
        }
    }

    private CdfTable NewTable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-cdf-fuzz-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new CdfTable(root);
    }
}
