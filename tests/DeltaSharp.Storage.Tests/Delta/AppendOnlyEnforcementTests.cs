using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Append-only enforcement teeth (#549). Verified against Delta PROTOCOL.md "Append-only Tables" +
/// "Active Features" and the Spark golden
/// (<c>OptimisticTransaction.prepareCommit</c>: <c>if (removes.exists(_.dataChange))
/// DeltaLog.assertRemovable(snapshot)</c> → <c>assertRemovable</c> throws iff
/// <c>IS_APPEND_ONLY.fromMetaData</c>): when <c>delta.appendOnly=true</c>, a commit that DELETES or CHANGES
/// committed data (a <c>remove</c> with <c>dataChange=true</c> — DELETE / OVERWRITE) is refused fail-closed,
/// while compaction removes (<c>dataChange=false</c> — OPTIMIZE) and pure appends are allowed. Enforcement
/// lives at the single <see cref="DeltaCommitter"/> seam every remove-bearing path funnels through
/// (DeltaDelete / overwrite emit <c>dataChange=true</c>; DeltaOptimize emits <c>dataChange=false</c>).
/// </summary>
public sealed class AppendOnlyEnforcementTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public AppendOnlyEnforcementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "appendonly-tests-" + Guid.NewGuid().ToString("N"));
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

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path, bool dataChange = true) =>
        new(path, NoPartition, 1L, 1L, dataChange, Stats: null, Tags: NoTags);

    private static RemoveFileAction Remove(string path, bool dataChange) =>
        new(path, DeletionTimestamp: 1L, dataChange, ExtendedFileMetadata: false, NoPartition, Size: null);

    private Task<Snapshot> LoadAsync(long? version = null) => new DeltaLog(_backend).LoadSnapshotAsync(version);

    // Seeds a legacy writer-2 table (appendOnly implicit at the writer version) carrying delta.appendOnly.
    private Task SeedLegacyAppendOnlyAsync(string appendOnly = "true") =>
        DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithConfig(("delta.appendOnly", appendOnly)),
            DeltaTestHarness.Add("seed.parquet"));

    // Seeds a table-features (writer 7) table that explicitly enumerates the appendOnly writer feature.
    private Task SeedTableFeaturesAppendOnlyAsync() =>
        DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            """{"protocol":{"minReaderVersion":3,"minWriterVersion":7,"readerFeatures":[],"writerFeatures":["appendOnly"]}}""",
            DeltaTestHarness.MetadataWithConfig(("delta.appendOnly", "true")),
            DeltaTestHarness.Add("seed.parquet"));

    // ---- AppendOnlyFeature unit level ----

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    public void IsEnabled_ParsesPropertyCaseInsensitively(string value, bool expected)
    {
        Assert.Equal(expected, AppendOnlyFeature.IsEnabled(
            new Dictionary<string, string> { ["delta.appendOnly"] = value }));
    }

    [Fact]
    public void IsEnabled_AbsentProperty_IsFalse()
    {
        Assert.False(AppendOnlyFeature.IsEnabled(ImmutableDictionary<string, string>.Empty));
    }

    [Fact]
    public void WithWriterFeature_AddsWhenAbsent_AndIsIdempotent()
    {
        ImmutableArray<string> added = AppendOnlyFeature.WithWriterFeature(ImmutableArray.Create("typeWidening"));
        Assert.Equal(new[] { "typeWidening", "appendOnly" }, added.ToArray());

        // Idempotent: already present → unchanged (no duplicate).
        Assert.Equal(added, AppendOnlyFeature.WithWriterFeature(added));

        // A default (uninitialized) array is treated as empty.
        Assert.Equal(new[] { "appendOnly" }, AppendOnlyFeature.WithWriterFeature(default).ToArray());
    }

    [Fact]
    public void EnsureCommitAllowed_NotAppendOnly_AllowsDataChangeRemove()
    {
        // No exception: the property is absent, so a data-changing remove is permitted.
        AppendOnlyFeature.EnsureCommitAllowed(
            ImmutableDictionary<string, string>.Empty,
            new DeltaAction[] { Remove("a.parquet", dataChange: true) });
    }

    [Fact]
    public void EnsureCommitAllowed_AppendOnly_RejectsDataChangeRemove()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            AppendOnlyFeature.EnsureCommitAllowed(
                new Dictionary<string, string> { ["delta.appendOnly"] = "true" },
                new DeltaAction[] { Add("new.parquet"), Remove("old.parquet", dataChange: true) }));

        Assert.Equal(DeltaProtocolErrorKind.AppendOnlyViolation, ex.Kind);
        Assert.Contains("delta.appendOnly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCommitAllowed_AppendOnly_AllowsCompactionRemove()
    {
        // A dataChange=false remove (OPTIMIZE compaction) is permitted even on an append-only table —
        // matching Spark's `if (removes.exists(_.dataChange)) assertRemovable`.
        AppendOnlyFeature.EnsureCommitAllowed(
            new Dictionary<string, string> { ["delta.appendOnly"] = "true" },
            new DeltaAction[] { Add("compacted.parquet", dataChange: false), Remove("old.parquet", dataChange: false) });
    }

    [Fact]
    public void EnsureCommitAllowed_AppendOnly_AllowsPureAppend()
    {
        AppendOnlyFeature.EnsureCommitAllowed(
            new Dictionary<string, string> { ["delta.appendOnly"] = "true" },
            new DeltaAction[] { Add("new.parquet") });
    }

    [Fact]
    public void EnsureCommitAllowed_AppendOnly_MixedRemoves_RejectsWhenAnyDataChangeTrue()
    {
        // One compaction remove and one data-changing remove: the data-changing one is forbidden.
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            AppendOnlyFeature.EnsureCommitAllowed(
                new Dictionary<string, string> { ["delta.appendOnly"] = "true" },
                new DeltaAction[]
                {
                    Remove("compacted-away.parquet", dataChange: false),
                    Remove("deleted.parquet", dataChange: true),
                }));

        Assert.Equal(DeltaProtocolErrorKind.AppendOnlyViolation, ex.Kind);
    }

    // ---- DeltaCommitter seam (real loaded snapshot) ----

    [Fact]
    public async Task CommitAsync_LegacyAppendOnlyTable_RejectsDataChangeRemove()
    {
        // A legacy writer-2 table with delta.appendOnly=true: a delete/overwrite (dataChange=true remove) is
        // refused fail-closed BEFORE any log write (this closes a latent gap — a writer-2 appendOnly table
        // previously had no delete enforcement).
        await SeedLegacyAppendOnlyAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Remove("seed.parquet", dataChange: true) },
                DeltaReadScope.WholeTable));

        Assert.Equal(DeltaProtocolErrorKind.AppendOnlyViolation, ex.Kind);

        // No version was published — the table is unchanged at v0.
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task CommitAsync_TableFeaturesAppendOnlyTable_RejectsDataChangeRemove()
    {
        // A writer-7 table that enumerates the appendOnly feature enforces identically.
        await SeedTableFeaturesAppendOnlyAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Add("over.parquet"), Remove("seed.parquet", dataChange: true) },
                DeltaReadScope.WholeTable));

        Assert.Equal(DeltaProtocolErrorKind.AppendOnlyViolation, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version);
    }

    [Fact]
    public async Task CommitAsync_AppendOnlyTable_AllowsCompactionRemove()
    {
        // OPTIMIZE-style commit (all removes dataChange=false) succeeds on an append-only table.
        await SeedLegacyAppendOnlyAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot,
            new DeltaAction[] { Add("compacted.parquet", dataChange: false), Remove("seed.parquet", dataChange: false) },
            DeltaReadScope.WholeTable);

        Assert.Equal(1L, result.Version);
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(new[] { "compacted.parquet" }, reloaded.ActiveFiles.Select(a => a.Path).ToArray());
    }

    [Fact]
    public async Task CommitAsync_AppendOnlyTable_AllowsPureAppend()
    {
        await SeedLegacyAppendOnlyAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Add("part-1.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(
            new[] { "part-1.parquet", "seed.parquet" },
            (await LoadAsync()).ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CommitAsync_AppendOnlyFalse_AllowsDataChangeRemove()
    {
        // delta.appendOnly=false (explicitly disabled) does not gate deletes — a data-changing remove commits.
        await SeedLegacyAppendOnlyAsync(appendOnly: "false");
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Remove("seed.parquet", dataChange: true) }, DeltaReadScope.WholeTable);

        Assert.Equal(1L, result.Version);
        Assert.Empty((await LoadAsync()).ActiveFiles);
    }

    [Fact]
    public async Task CommitAsync_NonAppendOnlyTable_AllowsDataChangeRemove()
    {
        // Regression: a normal (no appendOnly property) table's delete/overwrite path is unaffected.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.Metadata(),
            DeltaTestHarness.Add("seed.parquet"));
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Remove("seed.parquet", dataChange: true) }, DeltaReadScope.WholeTable);

        Assert.Equal(1L, result.Version);
        Assert.Empty((await LoadAsync()).ActiveFiles);
    }
}
