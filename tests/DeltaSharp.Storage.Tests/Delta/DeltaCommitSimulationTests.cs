using System.Collections.Immutable;
using System.Linq;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Seeded concurrent-writer simulation for <see cref="DeltaCommitter"/> (design §3.4 correctness-under-
/// concurrency). Many writers race to append to the same table with seeded per-writer jitter that varies
/// the interleaving per seed; a mechanical <b>history-checker oracle</b> then asserts the exactly-once
/// invariant over the reconstructed <c>_delta_log</c> — contiguous versions <c>1..K</c>, every writer's file
/// present exactly once, none lost or duplicated (I5/I6). This exercises deep multi-winner rebase chains that
/// the single deterministic <c>Barrier(2)</c> race cannot. A fully deterministic single-threaded scheduler /
/// Jepsen-style history checker is tracked separately (issue linked in the PR).
/// </summary>
public sealed class DeltaCommitSimulationTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitSimulationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-sim-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static AddFileAction Add(string path) =>
        new(
            path,
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            1L,
            1L,
            DataChange: true,
            Stats: null,
            Tags: ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

    // A small deterministic hash → 0..3 ms of jitter, so each (seed, writer, attempt) yields a stable delay.
    private static int SeededDelayMs(int seed, int writer, int attempt) =>
        (int)(((uint)((seed * 31 + writer) * 31 + attempt) * 2654435761u) >> 30);

    [Theory]
    [InlineData(0x0DE17A5D)]
    [InlineData(0x1234ABCD)]
    [InlineData(0x5EEDF00D)]
    [InlineData(0x00C0FFEE)]
    public async Task SeededConcurrentBlindAppends_PreserveExactlyOnceInvariant(int seed)
    {
        const int writers = 6;
        await DeltaTestHarness.WriteCommitAsync(_backend, 0, DeltaTestHarness.Protocol(), DeltaTestHarness.Metadata());
        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync(); // all writers read v0

        var tasks = new List<Task<DeltaCommitResult>>(writers);
        for (int w = 0; w < writers; w++)
        {
            int writerId = w;
            var committer = new DeltaCommitter(_backend)
            {
                BeforePutProbe = async (attempt, _, ct) =>
                {
                    int ms = SeededDelayMs(seed, writerId, attempt);
                    if (ms > 0)
                    {
                        await Task.Delay(ms, ct);
                    }
                },
            };
            tasks.Add(Task.Run(() => committer.CommitAsync(
                snapshot, new DeltaAction[] { Add($"w{writerId}.parquet") }, DeltaReadScope.BlindAppend)));
        }

        DeltaCommitResult[] results = await Task.WhenAll(tasks);

        // Oracle 1: every writer succeeded at a distinct, contiguous version 1..K (exactly one per version).
        Assert.Equal(
            Enumerable.Range(1, writers).Select(i => (long)i).ToArray(),
            results.Select(r => r.Version).OrderBy(v => v).ToArray());

        // Oracle 2: the reconstructed snapshot has every writer's file exactly once — no loss, no duplication.
        Snapshot reloaded = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal(writers, (int)reloaded.Version);
        Assert.Equal(
            Enumerable.Range(0, writers).Select(i => $"w{i}.parquet").OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }
}
