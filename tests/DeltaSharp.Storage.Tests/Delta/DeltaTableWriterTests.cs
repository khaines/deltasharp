using System.Collections.Immutable;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
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
///
/// <para>Every write entry point requires the incoming write schema (schema declaration is mandatory — the
/// no-schema overloads that would bypass enforcement were removed, #190/FIX-4). These tables carry the empty
/// schema, so passing <see cref="TableSchema"/> (or <c>readSnapshot.Schema</c>) expresses "this write
/// conforms to the current table schema" — a no-op enforcement. Schema enforcement/evolution behavior is
/// covered by <see cref="DeltaSchemaEvolutionWriterTests"/>.</para>
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

    // The tables seeded here carry the empty schema (DeltaTestHarness.Metadata → EmptySchema); passing it as
    // the required writeSchema is a trivially-compatible, no-op enforcement.
    private static readonly StructType TableSchema = StructType.Empty;

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
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet") });

        DeltaCommitResult result = await Writer().AppendAsync(TableSchema, new[] { Staged("b.parquet") });

        Assert.Equal(2L, result.Version);
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, ActivePaths(reloaded));
    }

    [Fact]
    public async Task Append_CommitsAsAddOnly_WithNoRemoves()
    {
        // AC1: the append's commit contains only `add` actions (no removes) — prior state is untouched.
        await SeedTableAsync();
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet") });

        DeltaCommitResult result = await Writer().AppendAsync(TableSchema, new[] { Staged("b.parquet"), Staged("c.parquet") });

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

        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet", stats: stats) });

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

        DeltaCommitResult result = await Writer().AppendAsync(readSnapshot, readSnapshot.Schema, new[] { Staged("mine.parquet") });

        Assert.Equal(2L, result.Version);
        Assert.Equal(new[] { "mine.parquet", "winner.parquet" }, ActivePaths(await LoadAsync()));
    }

    // ---------------------------------------------------------------- AC2: full overwrite

    [Fact]
    public async Task FullOverwrite_RemovesAllPriorActiveFiles_InOneAtomicVersion()
    {
        // AC2: a full-table overwrite removes EVERY prior active file in the SAME version as the new adds.
        await SeedTableAsync();
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet"), Staged("b.parquet") });

        DeltaCommitResult result = await Writer().OverwriteAsync(TableSchema, new[] { Staged("c.parquet") });

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
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet") });
        Snapshot readSnapshot = await LoadAsync();
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Add("winner.parquet"));

        await Assert.ThrowsAsync<ConcurrentAppendException>(() =>
            Writer().OverwriteAsync(readSnapshot, readSnapshot.Schema, new[] { Staged("c.parquet") }));
    }

    // ---------------------------------------------------------------- AC3: dynamic partition overwrite

    [Fact]
    public async Task DynamicPartitionOverwrite_ReplacesOnlyTouchedPartition()
    {
        // AC3 (happy path): a dynamic overwrite of region=US removes only US's prior files and adds the new
        // US file; the untouched region=EU partition is left entirely intact.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await Writer().AppendAsync(TableSchema, new[]
        {
            Staged("us1.parquet", Partition(("region", "US"))),
            Staged("eu1.parquet", Partition(("region", "EU"))),
        });

        await Writer().OverwriteAsync(
            TableSchema,
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
        await Writer().AppendAsync(TableSchema, new[]
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
                readSnapshot.Schema,
                new[] { Staged("us2.parquet", Partition(("region", "US"))) },
                PartitionOverwriteMode.Dynamic));
    }

    [Fact]
    public async Task DynamicPartitionOverwrite_RebasesPastConcurrentAppendToUntouchedPartition()
    {
        // AC3 (the "dynamic" guarantee): a dynamic overwrite of US does NOT conflict with a concurrent
        // append to the untouched EU partition — it rebases and commits, so US is replaced and EU grows.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await Writer().AppendAsync(TableSchema, new[]
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
            readSnapshot.Schema,
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
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet") });
        Snapshot pinned = await LoadAsync(); // pinned at v1: {a}

        await Writer().AppendAsync(TableSchema, new[] { Staged("b.parquet") }); // advances the table to v2

        // The pinned snapshot is immutable: its version and active-file set are unchanged.
        Assert.Equal(1L, pinned.Version);
        Assert.Equal(new[] { "a.parquet" }, ActivePaths(pinned));
        // A fresh read sees the new state — proving the pinned view was genuinely isolated.
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, ActivePaths(await LoadAsync()));
    }

    // ------------------------------------- #486 R1: data-loss hardening + coverage -------------------------------------

    [Fact]
    public async Task DynamicOverwrite_DoesNotRemove_PriorFileMissingPartitionColumn_InUntouchedPartition()
    {
        // #486 R1 CRITICAL (red-team / Security F1) regression: a prior active file that is MISSING a
        // partition-column key (a malformed/foreign-written add, committed here via the raw log to bypass the
        // writer's own coverage guard) lives in a DIFFERENT partition than the one the overwrite touches. The
        // old code selected removals via the read-oriented pruner, which never excludes a file missing the
        // filter column → it over-selected and tombstoned this file from an untouched partition (silent data
        // loss). Exact partition-key matching must leave it intact.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await CommitRawAsync(1, DeltaTestHarness.Add("mal.parquet")); // partitionValues {} — no "region" key
        await CommitRawAsync(2, DeltaTestHarness.Add("us1.parquet", partitionValues: new[] { ("region", "US") }));

        await Writer().OverwriteAsync(
            TableSchema,
            new[] { Staged("us2.parquet", Partition(("region", "US"))) },
            PartitionOverwriteMode.Dynamic);

        // us1 (region=US, touched) is replaced; mal.parquet (region absent ⇒ NOT the US partition) survives.
        Assert.Equal(new[] { "mal.parquet", "us2.parquet" }, ActivePaths(await LoadAsync()));
    }

    [Fact]
    public async Task Append_OnPartitionedTable_RejectsStagedFileMissingPartitionColumn()
    {
        // #486 R1 (Balanced L2 / Security write-path): fail-closed — a partitioned write must specify every
        // partition column, else the add would land in the wrong (null) partition and later mis-select.
        await SeedTableAsync(partitionColumns: new[] { "region" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Writer().AppendAsync(TableSchema, new[] { Staged("nopart.parquet") })); // NoPartition ⇒ missing "region"
    }

    [Fact]
    public async Task Overwrite_OnPartitionedTable_RejectsStagedFileMissingPartitionColumn()
    {
        // #486 R1: the same fail-closed coverage guard applies to overwrites (both modes go through it).
        await SeedTableAsync(partitionColumns: new[] { "region" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Writer().OverwriteAsync(
                TableSchema, new[] { Staged("nopart.parquet") }, PartitionOverwriteMode.Dynamic));
    }

    [Fact]
    public async Task Append_OnUnpartitionedTable_RejectsStagedFileCarryingPartitionValues()
    {
        // HIGH (red-team, #487 round-2): the coverage guard must ALSO fail-closed on an UNPARTITIONED table.
        // Before the fix ValidatePartitionCoverage returned early for an unpartitioned table, so a staged
        // file carrying stray partition values could land in the log as an `add` the table's (empty)
        // partition layout does not declare. It must be rejected fail-closed, not silently committed.
        await SeedTableAsync(); // unpartitioned

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(() =>
            Writer().AppendAsync(TableSchema, new[] { Staged("stray.parquet", Partition(("region", "US"))) }));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task DynamicOverwrite_OnUnpartitionedTable_ReplacesEntireTable()
    {
        // #486 R1 (Quality High): an unpartitioned table is a single partition, so a dynamic overwrite is a
        // full-table overwrite — every prior active file is removed and the new files added.
        await SeedTableAsync();
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet"), Staged("b.parquet") });

        await Writer().OverwriteAsync(TableSchema, new[] { Staged("c.parquet") }, PartitionOverwriteMode.Dynamic);

        Assert.Equal(new[] { "c.parquet" }, ActivePaths(await LoadAsync()));
    }

    [Fact]
    public async Task DynamicOverwrite_OnUnpartitionedTable_AbortsOnConcurrentAppend()
    {
        // #486 R1 (Architect L1 / Delta LOW-1 / Security F2): an unpartitioned dynamic overwrite must be
        // routed to the full-overwrite (WholeTable) path so it has the SAME strong isolation as a static
        // overwrite. If it were left on the ReadFiles(all-priors) scope, this concurrent NEW-file append
        // (which touches no prior) would rebase and survive instead of aborting.
        await SeedTableAsync();
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet") });
        Snapshot readSnapshot = await LoadAsync();
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Add("winner.parquet"));

        await Assert.ThrowsAsync<ConcurrentAppendException>(() =>
            Writer().OverwriteAsync(
                readSnapshot, readSnapshot.Schema, new[] { Staged("c.parquet") }, PartitionOverwriteMode.Dynamic));
    }

    [Fact]
    public async Task DynamicOverwrite_DistinguishesNaivelyCollidingPartitions()
    {
        // #486 R1 (Quality High): PartitionKey must be injective. Two multi-column partitions that would
        // collide under naive concatenation — (a="x", b="yz") vs (a="xy", b="z") — must map to distinct
        // touched sets. Overwriting the first must leave a prior file in the second untouched. A constant/
        // non-injective key would remove both.
        await SeedTableAsync(partitionColumns: new[] { "a", "b" });
        await Writer().AppendAsync(TableSchema, new[]
        {
            Staged("p1.parquet", Partition(("a", "x"), ("b", "yz"))),
            Staged("p2.parquet", Partition(("a", "xy"), ("b", "z"))),
        });

        await Writer().OverwriteAsync(
            TableSchema,
            new[] { Staged("p1b.parquet", Partition(("a", "x"), ("b", "yz"))) },
            PartitionOverwriteMode.Dynamic);

        // Only (x,yz) is replaced; the naively-colliding (xy,z) partition is untouched.
        Assert.Equal(new[] { "p1b.parquet", "p2.parquet" }, ActivePaths(await LoadAsync()));
    }

    [Fact]
    public async Task FullOverwrite_StampsTombstone_WithInjectedClock()
    {
        // #486 R1 (Balanced M1 / Chaos): the writer's tombstone DeletionTimestamp comes from the injected
        // TimeProvider (determinism, no wall-clock). Exercise the internal ctor and assert the exact instant.
        await SeedTableAsync();
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet") });
        var instant = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var writer = new DeltaTableWriter(new DeltaLog(_backend), new DeltaCommitter(_backend), new FixedTimeProvider(instant));

        DeltaCommitResult result = await writer.OverwriteAsync(TableSchema, new[] { Staged("b.parquet") });

        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(result.Version, CancellationToken.None);
        RemoveFileAction removed = Assert.Single(committed.OfType<RemoveFileAction>());
        Assert.Equal(instant.ToUnixTimeMilliseconds(), removed.DeletionTimestamp);
    }

    [Fact]
    public async Task FullOverwrite_AbortsOnConcurrentRemove()
    {
        // #486 R1 (Chaos): AC2 depends on the whole active set, so a concurrent REMOVE (not just an append)
        // of a prior file must abort the overwrite — the WholeTable delete-conflict branch.
        await SeedTableAsync();
        await Writer().AppendAsync(TableSchema, new[] { Staged("a.parquet"), Staged("b.parquet") });
        Snapshot readSnapshot = await LoadAsync();
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Remove("a.parquet"));

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(() =>
            Writer().OverwriteAsync(readSnapshot, readSnapshot.Schema, new[] { Staged("c.parquet") }));
    }

    [Fact]
    public async Task DynamicOverwrite_DoesNotAbortOnConcurrentNewFileAppendToTouchedPartition_Tracked488()
    {
        // #486 R1 (Chaos nit): CHARACTERIZATION of the tracked #488 gap — the ReadFiles(prior-paths) scope
        // does NOT catch a concurrent NEW-file append into a touched partition (its path is not in the read
        // set), so that file rebases and survives the overwrite. This pins the current (deferred) behavior so
        // an accidental change surfaces before #488's partition-predicate scope lands.
        await SeedTableAsync(partitionColumns: new[] { "region" });
        await Writer().AppendAsync(TableSchema, new[] { Staged("us1.parquet", Partition(("region", "US"))) });
        Snapshot readSnapshot = await LoadAsync();
        // Concurrent winner appends a NEW file into the SAME touched (US) partition.
        await CommitRawAsync(
            readSnapshot.Version + 1,
            DeltaTestHarness.Add("us_concurrent.parquet", partitionValues: new[] { ("region", "US") }));

        DeltaCommitResult result = await Writer().OverwriteAsync(
            readSnapshot,
            readSnapshot.Schema,
            new[] { Staged("us2.parquet", Partition(("region", "US"))) },
            PartitionOverwriteMode.Dynamic);

        // #488 gap: us_concurrent survives (real Delta would reject via a partition read-predicate).
        Assert.Equal(readSnapshot.Version + 2, result.Version);
        Assert.Equal(new[] { "us2.parquet", "us_concurrent.parquet" }, ActivePaths(await LoadAsync()));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
