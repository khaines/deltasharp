using System.Collections.Immutable;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end ACID append/overwrite tests for <see cref="DeltaTableWriter"/> over a real
/// <see cref="LocalFileSystemBackend"/> (STORY-05.3.3 / #188, design §2.11). Proves the four acceptance
/// criteria: an append adds files while prior actives stay active (AC1); a full overwrite removes every
/// prior active file in the same atomic version as the new adds (AC2); a dynamic partition overwrite
/// rejects a concurrent change to a touched partition while rebasing past an append to an untouched one
/// (AC3); and a reader pinned to an old snapshot keeps its old active-file set (AC4). Mirrors
/// <see cref="DeltaCommitterTests"/> + <see cref="DeltaTestHarness"/>.
/// </summary>
public sealed class DeltaTableWriterTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaTableWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltawriter-tests-" + Guid.NewGuid().ToString("N"));
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

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static ImmutableSortedDictionary<string, string?> Partition(params (string Key, string? Value)[] values)
    {
        ImmutableSortedDictionary<string, string?>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in values)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    private static StagedDataFile Staged(
        string path, ImmutableSortedDictionary<string, string?>? partition = null, FileStatistics? stats = null) =>
        new(path, partition ?? NoPartition, Size: 1L, ModificationTime: 1L, stats);

    private DeltaTableWriter Writer() => new(_backend);

    private DeltaLog Log() => new(_backend);

    private Task<Snapshot> LoadAsync(long? version = null) => Log().LoadSnapshotAsync(version);

    private async Task SeedTableAsync(string[]? partitionColumns = null)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.Metadata(partitionColumns: partitionColumns));
    }

    private async Task CommitRawAsync(long version, params string[] lines) =>
        await DeltaTestHarness.WriteCommitAsync(_backend, version, lines);

    private static string[] ActivePaths(Snapshot snapshot) =>
        snapshot.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    // ---------------------------------------------------------------- AC1: append

    [Fact]
    public async Task Append_AddsNewFiles_AndKeepsPriorActiveFiles()
    {
        // AC1: a committed append adds new `add` actions; every prior active file remains active.
        await SeedTableAsync();
        await Writer().AppendAsync(new[] { Staged("a.parquet") });

        DeltaCommitResult result = await Writer().AppendAsync(new[] { Staged("b.parquet") });

        Assert.Equal(2L, result.Version);
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, ActivePaths(reloaded));
    }

    [Fact]
    public async Task Append_CommitsAsAddOnly_WithNoRemoves()
    {
        // AC1: the append's commit contains only `add` actions (no removes) — prior state is untouched.
        await SeedTableAsync();
        await Writer().AppendAsync(new[] { Staged("a.parquet") });

        DeltaCommitResult result = await Writer().AppendAsync(new[] { Staged("b.parquet"), Staged("c.parquet") });

        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Equal(2, committed.OfType<AddFileAction>().Count());
        Assert.Empty(committed.OfType<RemoveFileAction>());
    }

    [Fact]
    public async Task Append_CarriesProvidedStatistics_OnTheAddAction()
    {
        // #197 boundary: #188 commits the adds with the statistics the caller already has (rich min/max
        // GENERATION is #197). A file's row count round-trips onto its `add.stats`.
        await SeedTableAsync();
        FileStatistics stats = FileStatistics.Empty with { NumRecords = 42 };

        await Writer().AppendAsync(new[] { Staged("a.parquet", stats: stats) });

        Snapshot reloaded = await LoadAsync();
        AddFileAction add = Assert.Single(reloaded.ActiveFiles);
        Assert.Equal(42, add.Stats?.NumRecords);
    }

    [Fact]
    public async Task Append_RebasesPastConcurrentAppend_WithoutLoss()
    {
        // AC1 (blind-append scope): an append reads nothing, so a concurrent append that landed since the
        // read snapshot does not conflict — it rebases and both files land exactly once.
        await SeedTableAsync();
        Snapshot readSnapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("winner.parquet")); // concurrent winner at v1

        DeltaCommitResult result = await Writer().AppendAsync(readSnapshot, new[] { Staged("mine.parquet") });

        Assert.Equal(2L, result.Version);
        Assert.Equal(new[] { "mine.parquet", "winner.parquet" }, ActivePaths(await LoadAsync()));
    }

    // ---------------------------------------------------------------- AC2: full overwrite

    [Fact]
    public async Task FullOverwrite_RemovesAllPriorActiveFiles_InOneAtomicVersion()
    {
        // AC2: a full-table overwrite removes EVERY prior active file in the SAME version as the new adds.
        await SeedTableAsync();
        await Writer().AppendAsync(new[] { Staged("a.parquet"), Staged("b.parquet") });

        DeltaCommitResult result = await Writer().OverwriteAsync(new[] { Staged("c.parquet") });

        // Exactly one version advanced, and that single commit carried both removes AND the new add.
        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        Assert.Equal(
            new[] { "a.parquet", "b.parquet" },
            committed.OfType<RemoveFileAction>().Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal("c.parquet", Assert.Single(committed.OfType<AddFileAction>()).Path);

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(new[] { "c.parquet" }, ActivePaths(reloaded));
    }

    [Fact]
    public async Task FullOverwrite_AbortsOnConcurrentAppend()
    {
        // AC2 isolation: a full overwrite depends on the whole active set, so a concurrent append aborts it.
        await SeedTableAsync();
        await Writer().AppendAsync(new[] { Staged("a.parquet") });
        Snapshot readSnapshot = await LoadAsync();
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Add("winner.parquet"));

        await Assert.ThrowsAsync<ConcurrentAppendException>(() =>
            Writer().OverwriteAsync(readSnapshot, new[] { Staged("c.parquet") }));
    }

    // ---------------------------------------------------------------- AC3: dynamic partition overwrite

    [Fact]
    public async Task DynamicPartitionOverwrite_ReplacesOnlyTouchedPartition()
    {
        // AC3 (happy path): a dynamic overwrite of region=US removes only US's prior files and adds the new
        // US file; the untouched region=EU partition is left entirely intact.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await Writer().AppendAsync(new[]
        {
            Staged("us1.parquet", Partition(("region", "US"))),
            Staged("eu1.parquet", Partition(("region", "EU"))),
        });

        await Writer().OverwriteAsync(
            new[] { Staged("us2.parquet", Partition(("region", "US"))) },
            PartitionOverwriteMode.Dynamic);

        Assert.Equal(new[] { "eu1.parquet", "us2.parquet" }, ActivePaths(await LoadAsync()));
    }

    [Fact]
    public async Task DynamicPartitionOverwrite_AbortsOnConcurrentChangeToTouchedPartition()
    {
        // AC3: when a concurrent commit changes a partition this overwrite touches (here it removes a prior
        // US file the overwrite read), conflict detection rejects the unsafe overwrite.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await Writer().AppendAsync(new[]
        {
            Staged("us1.parquet", Partition(("region", "US"))),
            Staged("eu1.parquet", Partition(("region", "EU"))),
        });
        Snapshot readSnapshot = await LoadAsync();
        // Concurrent winner removes us1.parquet — a change to the US partition the overwrite depends on.
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Remove("us1.parquet"));

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(() =>
            Writer().OverwriteAsync(
                readSnapshot,
                new[] { Staged("us2.parquet", Partition(("region", "US"))) },
                PartitionOverwriteMode.Dynamic));
    }

    [Fact]
    public async Task DynamicPartitionOverwrite_RebasesPastConcurrentAppendToUntouchedPartition()
    {
        // AC3 (the "dynamic" guarantee): a dynamic overwrite of US does NOT conflict with a concurrent
        // append to the untouched EU partition — it rebases and commits, so US is replaced and EU grows.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await Writer().AppendAsync(new[]
        {
            Staged("us1.parquet", Partition(("region", "US"))),
            Staged("eu1.parquet", Partition(("region", "EU"))),
        });
        Snapshot readSnapshot = await LoadAsync();
        // Concurrent winner appends a NEW file to the untouched EU partition.
        await CommitRawAsync(
            readSnapshot.Version + 1,
            DeltaTestHarness.Add("eu2.parquet", partitionValues: new[] { ("region", "EU") }));

        DeltaCommitResult result = await Writer().OverwriteAsync(
            readSnapshot,
            new[] { Staged("us2.parquet", Partition(("region", "US"))) },
            PartitionOverwriteMode.Dynamic);

        Assert.Equal(readSnapshot.Version + 2, result.Version); // rebased past the winner, then committed
        Assert.Equal(new[] { "eu1.parquet", "eu2.parquet", "us2.parquet" }, ActivePaths(await LoadAsync()));
    }

    // ---------------------------------------------------------------- AC4: snapshot isolation

    [Fact]
    public async Task PinnedSnapshot_KeepsItsActiveFileSet_AfterALaterCommit()
    {
        // AC4: a reader pinned to an old snapshot keeps seeing its old active-file set until it refreshes,
        // even though a later append changed the table.
        await SeedTableAsync();
        await Writer().AppendAsync(new[] { Staged("a.parquet") });
        Snapshot pinned = await LoadAsync(); // pinned at v1: {a}

        await Writer().AppendAsync(new[] { Staged("b.parquet") }); // advances the table to v2

        // The pinned snapshot is immutable: its version and active-file set are unchanged.
        Assert.Equal(1L, pinned.Version);
        Assert.Equal(new[] { "a.parquet" }, ActivePaths(pinned));
        // A fresh read sees the new state — proving the pinned view was genuinely isolated.
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, ActivePaths(await LoadAsync()));
    }
}
