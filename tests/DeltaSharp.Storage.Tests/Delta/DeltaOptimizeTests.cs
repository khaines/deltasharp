using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Diagnostics;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end OPTIMIZE / small-file compaction tests for <see cref="DeltaOptimize"/> over a real
/// <see cref="LocalFileSystemBackend"/> with real Parquet data files (design §2.9.2/§2.11.2,
/// STORY-05.6.1 / #195). Each test maps to an acceptance criterion:
/// <list type="bullet">
/// <item>AC1 — many small files compact into <b>one</b> Delta commit whose <c>remove</c>s (each input) and
/// <c>add</c>s (each compacted output) all carry <c>dataChange=false</c>, and the row content is preserved
/// byte-for-byte (a content oracle re-reads the rewritten rows).</item>
/// <item>AC2 — a concurrent writer that <c>remove</c>s a compaction input aborts OPTIMIZE (the
/// <see cref="DeltaReadScope.ReadFiles(System.Collections.Generic.IEnumerable{string})"/> scope), while a
/// concurrent <b>blind append</b> of a new file does not (OPTIMIZE rebases past it — the <c>dataChange=false</c>
/// remove/add never overlaps a brand-new file).</item>
/// <item>AC3 — a partition filter scopes OPTIMIZE: only the selected partition's small files are compacted
/// and every unselected active file is left untouched.</item>
/// <item>AC4 — a failure before the commit leaves the table unchanged (inputs still active) and the
/// written-but-uncommitted compacted file is a pure orphan that <see cref="OrphanCleanup"/> would reclaim.</item>
/// </list>
/// Mirrors <see cref="DeltaTableWriterTests"/> / <see cref="DeltaVacuumTests"/> / <see cref="WriteTimeStatisticsTests"/>.
/// </summary>
public sealed class DeltaOptimizeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The DATA schema is exactly what the Parquet data files contain: partition columns are never stored in
    // the data files (their values ride on the add action), so an unpartitioned table's data schema is the
    // whole schema and the partitioned table's data schema is the whole schema minus "region".
    private static readonly StructType DataSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
    });

    // The DATA schema after an additive schema evolution (#190) adds a NULLABLE "extra" column: files
    // written under DataSchema ([id,value]) are physically narrower than this, so OPTIMIZE reads them with
    // read-side null-fill (#497/#530) and rewrites a widened compacted file carrying [id,value,extra] with
    // "extra" NULL for the older rows.
    private static readonly StructType EvolvedSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.StringType, nullable: true),
    });

    // A DATA schema after TWO additive schema evolutions (#190) add TWO nullable columns "extra" and
    // "extra2": files written under DataSchema ([id,value]) are physically narrower than this by two lanes,
    // so OPTIMIZE reads them with read-side null-fill (#497/#530) and rewrites a widened compacted file
    // carrying [id,value,extra,extra2] with BOTH later columns NULL for the older rows.
    private static readonly StructType TwiceEvolvedSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.StringType, nullable: true),
        new StructField("extra2", DataTypes.StringType, nullable: true),
    });

    // The partitioned DATA schema (partition column "region" is never stored in the data files) after an
    // additive evolution adds a NULLABLE "extra": partitioned files written under [id,value] are null-filled
    // to [id,value,extra] on compaction, per partition (#530).
    private static readonly StructType PartitionedEvolvedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.StringType, nullable: true),
    });

    // A (malformed / non-additive) evolution that adds a REQUIRED (non-nullable) "req" column absent from the
    // older files: a required lane can never be null-filled, so OPTIMIZE must still FAIL CLOSED on it (#530).
    private static readonly StructType RequiredColumnEvolvedSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
        new StructField("req", DataTypes.StringType, nullable: false),
    });

    // A (non-widening) TYPE change of an existing column: "value" string -> long. Older files physically
    // carry "value" as a string, so reading them under this schema is a present-but-mistyped SchemaMismatch
    // (NOT an absent-column ColumnNotPresentInFile) -- used by the #513 specificity oracle to prove OPTIMIZE
    // does not mask a genuine type mismatch as additive schema evolution.
    private static readonly StructType TypeMismatchEvolvedSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.LongType, nullable: true),
    });

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    // A deterministic per-test sequence used to name each test's temp root. xUnit constructs a FRESH test
    // instance per test method and runs a class's tests SEQUENTIALLY (no intra-class parallelism), so a
    // monotonically increasing counter yields a stable, reproducible directory name with NO nondeterministic
    // identity source (the banned Guid.NewGuid / Random / DateTime). Each root is also pre-cleaned in the
    // constructor so a directory left behind by a crashed prior run can never leak into a later test.
    private static int _sequence;

    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaOptimizeTests()
    {
        int seq = System.Threading.Interlocked.Increment(ref _sequence);
        _root = Path.Combine(
            Path.GetTempPath(),
            "optimize-tests-" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

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

    // ---------------------------------------------------------------- AC1: compact → one commit

    [Fact]
    public async Task Optimize_CompactsSmallFiles_IntoOneCommit_PreservingRows()
    {
        // AC1: four small unpartitioned files (8 rows total) compact into a single output published in ONE
        // Delta commit that removes the four inputs and adds the one output.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a"), (2, "b")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((3, "c"), (4, null)));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((5, "e"), (6, "f")));
        StagedDataFile d = await WriteDataFileAsync("d.parquet", Batch((7, "g"), (8, "h")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c, d);

        var inputs = new[] { "a.parquet", "b.parquet", "c.parquet", "d.parquet" };
        List<(long, string?)> before = Sorted(await ReadRowsAsync(inputs));
        Assert.Equal(8, before.Count);

        OptimizeResult result = await Optimize().OptimizeAsync();

        // ONE commit at read + 1, four removed, one added, all eight rows preserved (AC1).
        Assert.Equal(result.ReadVersion + 1, result.CommittedVersion);
        Assert.Equal(4, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);
        Assert.Equal(8, result.RowCount);

        Snapshot after = await Log().LoadSnapshotAsync();
        AddFileAction output = Assert.Single(after.ActiveFiles);
        Assert.False(output.DataChange);
        Assert.NotNull(output.Stats); // write-time statistics emitted on the compacted add.

        // Content oracle: the compacted output holds exactly the inputs' rows (as a multiset).
        List<(long, string?)> compacted = Sorted(await ReadRowsAsync(new[] { output.Path }));
        Assert.Equal(before, compacted);

        // The single commit's adds AND removes all carry dataChange=false — the compaction-vs-append
        // correctness core (§2.11.2).
        IReadOnlyList<DeltaAction> committed =
            await Log().ReadCommitActionsAsync(result.CommittedVersion!.Value, CancellationToken.None);
        List<AddFileAction> adds = committed.OfType<AddFileAction>().ToList();
        List<RemoveFileAction> removes = committed.OfType<RemoveFileAction>().ToList();
        Assert.Single(adds);
        Assert.Equal(4, removes.Count);
        Assert.All(adds, add => Assert.False(add.DataChange));
        Assert.All(removes, remove => Assert.False(remove.DataChange));
        Assert.Equal(
            inputs.OrderBy(p => p, StringComparer.Ordinal),
            removes.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Optimize_BinPacksSmallFiles_ByTargetSize_IntoMultipleGroups_InOneCommit()
    {
        // Four size-100 files with target 250: next-fit packs {a,b} and {c,d} into two groups → two
        // compacted outputs, still published in ONE commit (AC1 "single Delta commit" holds for the whole
        // OPTIMIZE, not per group). Declared sizes drive the plan; the real bytes are tiny.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")), declaredSize: 100);
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")), declaredSize: 100);
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")), declaredSize: 100);
        StagedDataFile d = await WriteDataFileAsync("d.parquet", Batch((4, "d")), declaredSize: 100);
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c, d);

        List<(long, string?)> before = Sorted(await ReadRowsAsync(new[] { "a.parquet", "b.parquet", "c.parquet", "d.parquet" }));

        OptimizeResult result = await Optimize().OptimizeAsync(targetFileSize: 250);

        Assert.Equal(4, result.FilesRemoved);
        Assert.Equal(2, result.FilesAdded); // two bins → two compacted files
        Assert.Equal(result.ReadVersion + 1, result.CommittedVersion); // ... but still exactly one commit.

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(2, after.ActiveFiles.Length);
        List<(long, string?)> compacted = Sorted(await ReadRowsAsync(ActivePaths(after)));
        Assert.Equal(before, compacted); // all rows preserved across the two outputs.
    }

    [Fact]
    public async Task Optimize_LeavesFilesAtOrAboveTarget_Untouched()
    {
        // Two files whose declared size is at/above the target are not small-file candidates: no-op.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")), declaredSize: 1000);
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")), declaredSize: 1000);
        await SeedAsync(DataSchema, partitionColumns: null, a, b);
        Snapshot before = await Log().LoadSnapshotAsync();

        OptimizeResult result = await Optimize().OptimizeAsync(targetFileSize: 500);

        Assert.Null(result.CommittedVersion);
        Assert.Equal(0, result.FilesRemoved);
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, ActivePaths(after));
    }

    [Fact]
    public async Task Optimize_SkipsSingleFileGroup_AsNoOp()
    {
        // A partition with a single small file gains nothing from a rewrite (one file → one file), so it is
        // skipped and OPTIMIZE is a no-op with no commit (documented rule).
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        await SeedAsync(DataSchema, partitionColumns: null, a);
        Snapshot before = await Log().LoadSnapshotAsync();

        OptimizeResult result = await Optimize().OptimizeAsync();

        Assert.Null(result.CommittedVersion);
        Assert.Equal(0, result.FilesRemoved);
        Assert.Equal(0, result.FilesAdded);
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(new[] { "a.parquet" }, ActivePaths(after));
    }

    // ---------------------------------------------------------------- AC2: conflict detection

    [Fact]
    public async Task Optimize_AbortsWhenConcurrentWriterRemovesAnInput()
    {
        // AC2: a concurrent commit removes a compaction input since the read snapshot. The ReadFiles scope
        // over the input paths detects it and aborts OPTIMIZE — its inputs changed underneath it.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c);

        Snapshot readSnapshot = await Log().LoadSnapshotAsync();
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Remove("a.parquet"));

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(
            () => Optimize().OptimizeAsync(readSnapshot));

        // Only the concurrent winner advanced the table; OPTIMIZE committed nothing and left the survivors
        // active.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(readSnapshot.Version + 1, after.Version);
        Assert.Equal(new[] { "b.parquet", "c.parquet" }, ActivePaths(after));
    }

    [Fact]
    public async Task Optimize_RebasesPastConcurrentBlindAppend()
    {
        // AC2b: a concurrent BLIND APPEND of a brand-new file (dataChange=true, not touching any input) must
        // NOT abort OPTIMIZE. Because the compaction remove/add are dataChange=false and the ReadFiles scope
        // covers only the inputs, the new file is outside the scope and the committer rebases past it — the
        // two coexist (§2.11.2 conflict matrix, Compaction row).
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c);

        Snapshot readSnapshot = await Log().LoadSnapshotAsync();

        // A real, brand-new appended file committed concurrently at the next version.
        await WriteDataFileAsync("d.parquet", Batch((9, "z")));
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Add("d.parquet"));

        List<(long, string?)> expected =
            Sorted(await ReadRowsAsync(new[] { "a.parquet", "b.parquet", "c.parquet", "d.parquet" }));

        OptimizeResult result = await Optimize().OptimizeAsync(readSnapshot);

        // OPTIMIZE rebased on top of the append: it committed at read + 2 (the winner took read + 1).
        Assert.Equal(readSnapshot.Version + 2, result.CommittedVersion);

        Snapshot after = await Log().LoadSnapshotAsync();
        string[] active = ActivePaths(after);
        Assert.Contains("d.parquet", active);                 // the concurrent append survives...
        Assert.DoesNotContain("a.parquet", active);           // ... and the inputs were compacted away.
        Assert.DoesNotContain("b.parquet", active);
        Assert.DoesNotContain("c.parquet", active);
        Assert.Equal(2, after.ActiveFiles.Length);            // { compacted output, d.parquet }

        // Every row (the three compacted inputs + the appended file) is still present exactly once.
        Assert.Equal(expected, Sorted(await ReadRowsAsync(active)));
    }

    // ---------------------------------------------------------------- AC3: partition-scoped

    [Fact]
    public async Task Optimize_ScopedToPartition_LeavesOtherPartitionsUntouched()
    {
        // AC3: a filter restricts OPTIMIZE to region=US. Its three small files compact into one; the EU
        // partition's files are never candidates and remain exactly as they were.
        StagedDataFile us1 = await WriteDataFileAsync("region=US/us1.parquet", Batch((1, "a")), Partition(("region", "US")));
        StagedDataFile us2 = await WriteDataFileAsync("region=US/us2.parquet", Batch((2, "b")), Partition(("region", "US")));
        StagedDataFile us3 = await WriteDataFileAsync("region=US/us3.parquet", Batch((3, "c")), Partition(("region", "US")));
        StagedDataFile eu1 = await WriteDataFileAsync("region=EU/eu1.parquet", Batch((4, "d")), Partition(("region", "EU")));
        StagedDataFile eu2 = await WriteDataFileAsync("region=EU/eu2.parquet", Batch((5, "e")), Partition(("region", "EU")));
        await SeedAsync(PartitionedSchema, partitionColumns: new[] { "region" }, us1, us2, us3, eu1, eu2);

        List<(long, string?)> usBefore =
            Sorted(await ReadRowsAsync(new[] { "region=US/us1.parquet", "region=US/us2.parquet", "region=US/us3.parquet" }));

        OptimizeResult result = await Optimize().OptimizeAsync(
            partitionFilter: pv => pv.TryGetValue("region", out string? region) && region == "US");

        Assert.Equal(3, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);
        OptimizePartitionSummary summary = Assert.Single(result.Partitions);
        Assert.Equal("US", summary.PartitionValues["region"]);

        Snapshot after = await Log().LoadSnapshotAsync();
        string[] active = ActivePaths(after);
        Assert.Contains("region=EU/eu1.parquet", active); // EU untouched.
        Assert.Contains("region=EU/eu2.parquet", active);
        Assert.DoesNotContain("region=US/us1.parquet", active); // US inputs compacted away.
        Assert.DoesNotContain("region=US/us2.parquet", active);
        Assert.DoesNotContain("region=US/us3.parquet", active);

        AddFileAction output = Assert.Single(
            after.ActiveFiles.Where(f => f.PartitionValues.TryGetValue("region", out string? r) && r == "US"));
        Assert.Equal("US", output.PartitionValues["region"]);
        Assert.StartsWith("region=US/", output.Path, StringComparison.Ordinal);
        Assert.Equal(usBefore, Sorted(await ReadRowsAsync(new[] { output.Path }))); // US rows preserved.
    }

    // ---------------------------------------------------------------- AC4: pre-commit failure → orphans

    [Fact]
    public async Task Optimize_PreCommitFailure_LeavesInputsActive_AndOrphansOutputs()
    {
        // AC4: a failure injected after the compacted file is written but before the commit leaves the table
        // unchanged; the written file is a pure orphan (unreferenced by the log) that OrphanCleanup reclaims.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c);
        Snapshot before = await Log().LoadSnapshotAsync();

        DeltaOptimize optimize = Optimize(nameFactory: () => "ORPHAN");
        optimize.BeforeCommitProbe = _ => throw new InvalidOperationException("injected pre-commit failure");

        await Assert.ThrowsAsync<InvalidOperationException>(() => optimize.OptimizeAsync());

        // The table state is unchanged: inputs still active, version not advanced.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(new[] { "a.parquet", "b.parquet", "c.parquet" }, ActivePaths(after));

        // The compacted output was durably written but is referenced by nothing — a pure orphan.
        const string orphan = "part-ORPHAN.parquet";
        Assert.NotNull(await _backend.HeadAsync(orphan, CancellationToken.None));
        Assert.DoesNotContain(orphan, ActivePaths(after));

        // OrphanCleanup would reclaim it: not active, not a tombstone, staged before any future cutoff.
        IReadOnlyList<string> deletable = OrphanCleanup.SelectDeletable(
            after,
            new[] { new OrphanCandidate(orphan, ModificationTimeMillis: 0) },
            retentionCutoffMillis: long.MaxValue);
        Assert.Contains(orphan, deletable);
    }

    // ---------------------------------------------------------------- dry-run

    [Fact]
    public async Task Optimize_DryRun_ReportsPlan_WithoutWritingOrCommitting()
    {
        // A dry run reports what WOULD be compacted (two inputs → one output, two rows) without writing a
        // file or advancing the log.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);
        Snapshot before = await Log().LoadSnapshotAsync();

        OptimizeResult result = await Optimize().OptimizeAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Null(result.CommittedVersion);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);
        // FIX 5 (dual fidelity): a dry run measures nothing, so the MEASURED RowCount is 0; the advisory
        // EstimatedRowCount carries the best-effort plan estimate from the inputs' add.stats record counts.
        Assert.Equal(0, result.RowCount);
        Assert.Equal(2, result.EstimatedRowCount);
        OptimizePartitionSummary dryPartition = Assert.Single(result.Partitions);
        Assert.Equal(0, dryPartition.RowCount);
        Assert.Equal(2, dryPartition.EstimatedRowCount);

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version); // nothing committed.
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, ActivePaths(after));
    }

    // ------------------------------------------------------------ FIX 1: additive schema evolution (#530)

    [Fact]
    public async Task Optimize_OnAdditivelyEvolvedTable_Compacts_NullFillingLaterColumns()
    {
        // A narrow table [id,value] with two small files, then an additive schema evolution (#190) adds a
        // NULLABLE "extra" column so the pre-evolution files are physically narrower than the current data
        // schema. OPTIMIZE now reads them with read-side null-fill (#497/#530) and compacts them into one
        // widened output physically carrying [id,value,extra] — the older rows' "extra" is NULL — with
        // recomputed statistics reflecting that null_count.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b); // v0 metadata, v1 append

        await EvolveSchemaAsync(version: 2, EvolvedSchema); // metadata-only add-column evolution at v2
        Snapshot before = await Log().LoadSnapshotAsync();
        Assert.Equal(2, before.Version);

        OptimizeResult result = await Optimize().OptimizeAsync();

        // ONE commit at read + 1, two narrow inputs removed, one widened output added, both rows preserved.
        Assert.Equal(result.ReadVersion + 1, result.CommittedVersion);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);
        Assert.Equal(2, result.RowCount);

        Snapshot after = await Log().LoadSnapshotAsync();
        AddFileAction output = Assert.Single(after.ActiveFiles);
        Assert.False(output.DataChange);
        Assert.NotNull(output.Stats);

        // Statistics are recomputed over the WIDENED, null-filled batches: numRecords=2 and the later-added
        // "extra" column's null_count is 2 (both older rows are null-filled). id/value carry no nulls.
        Assert.Equal(2L, output.Stats!.NumRecords);
        Assert.Equal(2L, output.Stats.NullCount["extra"]);
        Assert.Equal(0L, output.Stats.NullCount["id"]);
        Assert.Equal(0L, output.Stats.NullCount["value"]);

        // The compacted output PHYSICALLY carries [id,value,extra]: read it back under the evolved schema
        // WITHOUT null-fill (every column must be present in the file) — the older rows' "extra" is NULL.
        List<(long, string?, string?)> compacted = await ReadEvolvedRowsAsync(new[] { output.Path }, nullFill: false);
        Assert.Equal(
            new (long, string?, string?)[] { (1L, "a", null), (2L, "b", null) },
            compacted.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Optimize_WhenCurrentSchemaAddsRequiredColumn_FailsClosed_WithClearError()
    {
        // A required (non-nullable) lane can never be null-filled, so a pre-evolution file missing a column
        // the current schema declares NON-nullable is genuinely unreadable under the current schema: OPTIMIZE
        // must still FAIL CLOSED with a clear, actionable error (never the misleading raw "column not present"
        // corruption text), leaving the table unchanged.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);

        await EvolveSchemaAsync(version: 2, RequiredColumnEvolvedSchema);
        Snapshot before = await Log().LoadSnapshotAsync();
        Assert.Equal(2, before.Version);

        OptimizeSchemaEvolutionException ex = await Assert.ThrowsAsync<OptimizeSchemaEvolutionException>(
            () => Optimize().OptimizeAsync());

        // The clear error names the file and the required-column cause, and does NOT surface the raw defect.
        Assert.Contains("a.parquet", ex.Message, StringComparison.Ordinal);
        Assert.Contains("REQUIRED", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not present in the Parquet file schema", ex.Message, StringComparison.Ordinal);

        // Fail-closed / orphan-safe: the table is unchanged — same version and same active files.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(ActivePaths(before), ActivePaths(after));
    }

    [Fact]
    public async Task Optimize_GenuineCorruption_IsNotMaskedAsSchemaEvolution_Issue513()
    {
        // #513 specificity oracle (OPTIMIZE guard): IsUnfillableSchemaEvolutionInput matches ONLY the
        // dedicated StorageErrorKind.ColumnNotPresentInFile sentinel, so a genuinely corrupt input (real
        // byte corruption) must NOT be misclassified as additive schema evolution. OPTIMIZE must fail closed
        // with the raw CorruptData storage fault — never a benign OptimizeSchemaEvolutionException that would
        // present a corrupt file as compactable — leaving the table unchanged.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);
        Snapshot before = await Log().LoadSnapshotAsync();

        // Corrupt one input on disk AFTER seeding (non-Parquet garbage).
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "a.parquet"),
            Encoding.ASCII.GetBytes("not a parquet file -- genuine corruption"));

        // DeltaStorageException is a sibling (not a supertype) of OptimizeSchemaEvolutionException, so an
        // exact-type ThrowsAsync<DeltaStorageException> passing proves the corruption is NOT masked.
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => Optimize().OptimizeAsync());
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);

        // Fail-closed / orphan-safe: the table is unchanged.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(ActivePaths(before), ActivePaths(after));
    }

    [Fact]
    public async Task Optimize_PresentButMistypedColumn_IsNotMaskedAsSchemaEvolution_Issue513()
    {
        // #513 specificity oracle (OPTIMIZE guard): a present-but-mistyped column (SchemaMismatch, NOT an
        // absent-column ColumnNotPresentInFile) must NOT be masked as additive schema evolution. Evolve
        // "value" string -> long (a non-widening type change); OPTIMIZE reading the older string files under
        // the long schema must fail closed with the raw SchemaMismatch fault, never
        // OptimizeSchemaEvolutionException, leaving the table unchanged.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);

        await EvolveSchemaAsync(version: 2, TypeMismatchEvolvedSchema);
        Snapshot before = await Log().LoadSnapshotAsync();
        Assert.Equal(2, before.Version);

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => Optimize().OptimizeAsync());
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);

        // Fail-closed / orphan-safe: the table is unchanged.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(ActivePaths(before), ActivePaths(after));
    }

    [Fact]
    public async Task Optimize_SkipsFilesCarryingDeletionVectors_NoResurrection()
    {
        // OPTIMIZE reads its inputs' RAW Parquet (it does not route through the merge-on-read DV filter), so
        // compacting a DV'd file by reading it raw would RESURRECT the logically-deleted rows. OPTIMIZE
        // therefore SKIPS any file that still carries a deletion vector (#192/#530). Two small DV'd files
        // that would otherwise bin-pack together are left untouched, so no deleted row can resurrect and the
        // survivors stay exactly what the DVs allow.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((10, "j"), (20, "k"), (30, "l")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((40, "m"), (50, "n"), (60, "o")));
        await SeedDvCapableAsync(DataSchema, a, b);

        // Apply an inline DV to each file excluding physical position 1 (ids 20 and 50).
        Snapshot seeded = await Log().LoadSnapshotAsync();
        Snapshot dvApplied = await ApplyInlineDvAsync(seeded, deletedPositionPerFile: 1, physicalRecords: 3);
        AddFileAction[] dvAdds = dvApplied.ActiveFiles.Where(f => f.DeletionVector is not null).ToArray();
        Assert.Equal(2, dvAdds.Length);

        OptimizeResult result = await Optimize().OptimizeAsync(dvApplied);

        // No-op: both candidates were skipped (they carry DVs), nothing removed/added, no commit.
        Assert.Equal(0, result.FilesRemoved);
        Assert.Equal(0, result.FilesAdded);
        Assert.Null(result.CommittedVersion);

        // The DV'd files are still active with their DVs intact (untouched → survivors trivially exact).
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(dvApplied.Version, after.Version);
        Assert.Equal(2, after.ActiveFiles.Count(f => f.DeletionVector is not null));
        Assert.All(after.ActiveFiles, f => Assert.Equal(1L, f.DeletionVector!.Cardinality));

        // Survivors, by value, through the merge-on-read read path: the DV-excluded rows stay excluded.
        List<(long, string?)> survivors = Sorted(await ReadSurvivorsAsync(after));
        Assert.Equal(
            new (long, string?)[] { (10L, "j"), (30L, "l"), (40L, "m"), (60L, "o") },
            survivors);
    }

    [Fact]
    public async Task Optimize_MixedDvAndNonDvBin_CompactsOnlyNonDvFiles_SkipsDvFile_SurvivorsExact()
    {
        // COVERAGE (Quality gap a, highest value; durable form of the red-team throwaway probe
        // RedTeam_Probe1_MixedDv_And_PurgedDv): a SINGLE partition holds three small files that would all
        // bin-pack together, but only ONE (c) still carries a deletion vector. OPTIMIZE must compact ONLY the
        // two DV-free files (a,b merge → one output) and SKIP the DV'd file (c stays active, unchanged, DV
        // intact) — reading a DV'd input raw would resurrect its logically-deleted row (#192/#530). Every
        // survivor, through the merge-on-read read path, must be exact.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((10, "j"), (20, "k"), (30, "l")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((40, "m"), (50, "n"), (60, "o")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((70, "p"), (80, "q"), (90, "r")));
        await SeedDvCapableAsync(DataSchema, a, b, c);

        // Apply an inline DV to ONLY c, excluding physical position 1 (id 80); a and b stay DV-free.
        Snapshot seeded = await Log().LoadSnapshotAsync();
        Snapshot dvApplied = await ApplyInlineDvToAsync(
            seeded, new HashSet<string>(new[] { "c.parquet" }, StringComparer.Ordinal), deletedPosition: 1, physicalRecords: 3);
        Assert.Equal(1, dvApplied.ActiveFiles.Count(f => f.DeletionVector is not null));

        OptimizeResult result = await Optimize().OptimizeAsync(dvApplied);

        // Only the two DV-free files were compacted; the DV'd file was skipped.
        Assert.Equal(result.ReadVersion + 1, result.CommittedVersion);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);

        Snapshot after = await Log().LoadSnapshotAsync();
        string[] active = ActivePaths(after);
        Assert.DoesNotContain("a.parquet", active);   // a,b merged away...
        Assert.DoesNotContain("b.parquet", active);
        Assert.Contains("c.parquet", active);          // ... c skipped and still active.

        // The skipped DV'd file is UNCHANGED: same path, DV still present with cardinality 1.
        AddFileAction skipped = Assert.Single(after.ActiveFiles.Where(f => f.Path == "c.parquet"));
        Assert.NotNull(skipped.DeletionVector);
        Assert.Equal(1L, skipped.DeletionVector!.Cardinality);
        Assert.Single(after.ActiveFiles, f => f.DeletionVector is null); // exactly the one compacted output.

        // The compacted output physically holds exactly a's and b's rows (the DV'd file's rows are NOT in it).
        AddFileAction output = Assert.Single(after.ActiveFiles.Where(f => f.DeletionVector is null));
        Assert.Equal(
            new (long, string?)[] { (10L, "j"), (20L, "k"), (30L, "l"), (40L, "m"), (50L, "n"), (60L, "o") },
            Sorted(await ReadRowsAsync(new[] { output.Path })));

        // Survivors through merge-on-read: a+b in full, c minus its DV-excluded row (id 80). No resurrection.
        List<(long, string?)> survivors = Sorted(await ReadSurvivorsAsync(after));
        Assert.Equal(
            new (long, string?)[]
            {
                (10L, "j"), (20L, "k"), (30L, "l"), (40L, "m"), (50L, "n"), (60L, "o"), (70L, "p"), (90L, "r"),
            },
            survivors);
    }

    [Fact]
    public async Task Optimize_PurgedDeletionVector_MakesFileACandidateAgain()
    {
        // COVERAGE (Quality gap a): a file whose DV was PHYSICALLY PURGED (rewritten to survivors-only with
        // DeletionVector == null, the shape a real REORG/purge produces) is a normal small-file compaction
        // candidate again (#530). Before the purge OPTIMIZE is a no-op — a is skipped for its DV and b is a
        // lone single-file group; after the purge, the purged file and b compact together.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((10, "j"), (20, "k"), (30, "l")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((40, "m"), (50, "n"), (60, "o")));
        await SeedDvCapableAsync(DataSchema, a, b);

        // DV on ONLY a (delete id 20). Now a carries a DV; b is DV-free but a lone candidate.
        Snapshot seeded = await Log().LoadSnapshotAsync();
        Snapshot dvApplied = await ApplyInlineDvToAsync(
            seeded, new HashSet<string>(new[] { "a.parquet" }, StringComparer.Ordinal), deletedPosition: 1, physicalRecords: 3);

        OptimizeResult noop = await Optimize().OptimizeAsync(dvApplied);
        Assert.Null(noop.CommittedVersion);          // a skipped (DV), b lone → nothing to do.
        Assert.Equal(0, noop.FilesRemoved);

        // PURGE a's DV: rewrite it to a survivors-only file (ids 10,30) carrying NO DV.
        StagedDataFile purged = await WriteDataFileAsync("a-purged.parquet", Batch((10, "j"), (30, "l")));
        Snapshot afterPurge = await PurgeDeletionVectorAsync(dvApplied, "a.parquet", purged);
        Assert.All(afterPurge.ActiveFiles, f => Assert.Null(f.DeletionVector)); // no DV remains anywhere.

        // The purged file is a candidate again: it and b now compact into one output.
        OptimizeResult result = await Optimize().OptimizeAsync(afterPurge);
        Assert.Equal(result.ReadVersion + 1, result.CommittedVersion);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);

        Snapshot after = await Log().LoadSnapshotAsync();
        AddFileAction output = Assert.Single(after.ActiveFiles);
        Assert.Equal(
            new (long, string?)[] { (10L, "j"), (30L, "l"), (40L, "m"), (50L, "n"), (60L, "o") },
            Sorted(await ReadRowsAsync(new[] { output.Path })));
    }

    [Fact]
    public async Task Optimize_OnTwiceAdditivelyEvolvedTable_NullFillsBothLaterColumns()
    {
        // COVERAGE (Quality gap a): a narrow [id,value] table is additively evolved TWICE, adding TWO nullable
        // columns ("extra", then "extra2"). OPTIMIZE reads the pre-evolution files with read-side null-fill
        // (#497/#530) and compacts them into ONE output physically carrying [id,value,extra,extra2] — BOTH
        // later columns NULL for the older rows — with recomputed statistics reflecting both null_counts.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b); // v0 metadata, v1 append

        await EvolveSchemaAsync(version: 2, EvolvedSchema);       // add nullable "extra"
        await EvolveSchemaAsync(version: 3, TwiceEvolvedSchema);  // add nullable "extra2"
        Snapshot before = await Log().LoadSnapshotAsync();
        Assert.Equal(3, before.Version);

        OptimizeResult result = await Optimize().OptimizeAsync();

        Assert.Equal(result.ReadVersion + 1, result.CommittedVersion);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);
        Assert.Equal(2, result.RowCount);

        Snapshot after = await Log().LoadSnapshotAsync();
        AddFileAction output = Assert.Single(after.ActiveFiles);
        Assert.NotNull(output.Stats);
        // Both later-added columns are all-null across the null-filled rows.
        Assert.Equal(2L, output.Stats!.NumRecords);
        Assert.Equal(2L, output.Stats.NullCount["extra"]);
        Assert.Equal(2L, output.Stats.NullCount["extra2"]);
        Assert.Equal(0L, output.Stats.NullCount["id"]);
        Assert.Equal(0L, output.Stats.NullCount["value"]);

        // The compacted output PHYSICALLY carries [id,value,extra,extra2]: read it back WITHOUT null-fill
        // (every column must be present in the file) — both older-row later columns are NULL.
        List<(long, string?, string?, string?)> compacted =
            await ReadTwiceEvolvedRowsAsync(new[] { output.Path }, nullFill: false);
        Assert.Equal(
            new (long, string?, string?, string?)[] { (1L, "a", null, null), (2L, "b", null, null) },
            compacted.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Optimize_PartitionedAndEvolved_CompactsPerPartition_WithNullFill()
    {
        // COVERAGE (Quality gap a): a PARTITIONED table (partition column "region") is additively evolved to
        // add a nullable "extra". OPTIMIZE compacts each partition INDEPENDENTLY with read-side null-fill,
        // producing one widened [id,value,extra] output per partition (extra NULL for the older rows), and
        // never mixes rows across partitions (#530).
        StagedDataFile us1 = await WriteDataFileAsync("region=US/us1.parquet", Batch((1, "a")), Partition(("region", "US")));
        StagedDataFile us2 = await WriteDataFileAsync("region=US/us2.parquet", Batch((2, "b")), Partition(("region", "US")));
        StagedDataFile eu1 = await WriteDataFileAsync("region=EU/eu1.parquet", Batch((3, "c")), Partition(("region", "EU")));
        StagedDataFile eu2 = await WriteDataFileAsync("region=EU/eu2.parquet", Batch((4, "d")), Partition(("region", "EU")));
        await SeedAsync(PartitionedSchema, partitionColumns: new[] { "region" }, us1, us2, eu1, eu2);

        // Additive evolution that PRESERVES the partition columns (adds nullable "extra").
        await EvolveSchemaAsync(version: 2, PartitionedEvolvedSchema, partitionColumns: new[] { "region" });
        Snapshot before = await Log().LoadSnapshotAsync();
        Assert.Equal(2, before.Version);

        OptimizeResult result = await Optimize().OptimizeAsync();

        Assert.Equal(4, result.FilesRemoved);
        Assert.Equal(2, result.FilesAdded);          // one per partition
        Assert.Equal(2, result.Partitions.Length);

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(2, after.ActiveFiles.Length);
        foreach (AddFileAction output in after.ActiveFiles)
        {
            string region = output.PartitionValues["region"]!;
            Assert.StartsWith($"region={region}/", output.Path, StringComparison.Ordinal);

            // Each output physically carries [id,value,extra] with extra NULL, and holds only its partition's
            // rows (no cross-partition mixing).
            List<(long, string?, string?)> rows = await ReadEvolvedRowsAsync(new[] { output.Path }, nullFill: false);
            Assert.All(rows, r => Assert.Null(r.Item3));       // extra null-filled
            long[] expectedIds = region == "US" ? new long[] { 1, 2 } : new long[] { 3, 4 };
            Assert.All(rows, r => Assert.Contains(r.Item1, expectedIds));

            // Statistics record the null-filled later column.
            Assert.NotNull(output.Stats);
            Assert.Equal(2L, output.Stats!.NullCount["extra"]);
        }
    }

    [Fact]
    public async Task Optimize_EvolvedAllNullColumn_HasNullCountButNoMinMax()
    {
        // COVERAGE (Quality gap a): the recomputed statistics for a later-added, all-null (null-filled)
        // column must record its null_count but carry NO min/max — an all-null column has no bound, so it is
        // OMITTED from minValues/maxValues (a min/max on an all-null column would be meaningless / unsound).
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);

        await EvolveSchemaAsync(version: 2, EvolvedSchema); // add nullable "extra"

        OptimizeResult result = await Optimize().OptimizeAsync();
        Assert.Equal(1, result.FilesAdded);

        Snapshot after = await Log().LoadSnapshotAsync();
        AddFileAction output = Assert.Single(after.ActiveFiles);
        Assert.NotNull(output.Stats);

        // The all-null later column appears in null_count...
        Assert.True(output.Stats!.NullCount.ContainsKey("extra"));
        Assert.Equal(2L, output.Stats.NullCount["extra"]);

        // ... but NOT in min/max (all-null → no bound).
        Assert.False(output.Stats.MinValues.ContainsKey("extra"));
        Assert.False(output.Stats.MaxValues.ContainsKey("extra"));

        // The non-null columns DO carry min/max, proving the omission is specific to the all-null column.
        Assert.True(output.Stats.MinValues.ContainsKey("id"));
        Assert.True(output.Stats.MaxValues.ContainsKey("id"));
        Assert.True(output.Stats.MinValues.ContainsKey("value"));
        Assert.True(output.Stats.MaxValues.ContainsKey("value"));
    }

    // ------------------------------------------------------------------ orphan-safety / regressions

    // ---------------------------------------------------------------- FIX 3: AC3 byte-identity of untouched files

    [Fact]
    public async Task Optimize_DoesNotRewriteUnselectedFiles_ByteIdentityPreserved()
    {
        // a,b are small candidates; c is already >= target so it is never selected. After OPTIMIZE, c must
        // still be active AND its bytes must be byte-for-byte identical (proving OPTIMIZE only rewrites the
        // selected files and never touches the rest).
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")), declaredSize: 100);
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")), declaredSize: 100);
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")), declaredSize: 10_000);
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c);

        string cHashBefore = await Sha256Async("c.parquet");

        OptimizeResult result = await Optimize().OptimizeAsync(targetFileSize: 500);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);

        Snapshot after = await Log().LoadSnapshotAsync();
        string[] active = ActivePaths(after);
        Assert.Contains("c.parquet", active);       // the unselected file is still active by path...
        Assert.DoesNotContain("a.parquet", active);
        Assert.DoesNotContain("b.parquet", active);

        string cHashAfter = await Sha256Async("c.parquet");
        Assert.Equal(cHashBefore, cHashAfter);       // ... and its bytes are unchanged (never rewritten).
    }

    // ---------------------------------------------------------------- FIX 2: operation-level telemetry

    [Fact]
    public async Task Optimize_CompletedRun_EmitsSpan_Metrics_AndLogs_NoDataLeakage()
    {
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);

        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaOptimize>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);
        using var activities = new ActivityCapture(telemetry.DeltaActivitySource);

        OptimizeResult result = await Optimize(logger: logger, telemetry: telemetry).OptimizeAsync();
        Assert.NotNull(result.CommittedVersion);

        // Metrics: one terminal count tagged outcome=completed, plus files-removed / files-added counters.
        MeterCapture.Measurement count = Assert.Single(meters.ForInstrument("deltasharp.delta.optimize.count"));
        Assert.Equal(1d, count.Value);
        Assert.Equal("completed", count.Tags["deltasharp.outcome"]);
        Assert.Equal(2d, Assert.Single(meters.ForInstrument("deltasharp.delta.optimize.files_removed")).Value);
        Assert.Equal(1d, Assert.Single(meters.ForInstrument("deltasharp.delta.optimize.files_added")).Value);
        Assert.Single(meters.ForInstrument("deltasharp.delta.optimize.duration"));

        // Logs: started + completed lifecycle events.
        Assert.True(logger.Has("DeltaOptimizeStarted"));
        Assert.True(logger.Has("DeltaOptimizeCompleted"));

        // Span: the OPTIMIZE span carries the bounded outcome tag.
        System.Diagnostics.Activity activity = Assert.Single(activities.Stopped);
        Assert.Equal(DeltaStorageTelemetry.OptimizeActivityName, activity.OperationName);
        Assert.Equal("completed", activity.GetTagItem("deltasharp.outcome"));

        // No data leakage: no row value or column value ever appears in a log message.
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("part-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Optimize_Abort_EmitsAbortedOutcome()
    {
        // A concurrent writer removes an input → OPTIMIZE aborts fail-closed. The terminal metric records
        // outcome=aborted (not a generic failure) and the aborted log line fires.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c);

        Snapshot readSnapshot = await Log().LoadSnapshotAsync();
        await CommitRawAsync(readSnapshot.Version + 1, DeltaTestHarness.Remove("a.parquet"));

        using var telemetry = new DeltaStorageTelemetry();
        var logger = new RecordingLogger<DeltaOptimize>();
        using var meters = new MeterCapture(telemetry.DeltaMeter, telemetry.StorageMeter);

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(
            () => Optimize(logger: logger, telemetry: telemetry).OptimizeAsync(readSnapshot));

        MeterCapture.Measurement count = Assert.Single(meters.ForInstrument("deltasharp.delta.optimize.count"));
        Assert.Equal("aborted", count.Tags["deltasharp.outcome"]);
        Assert.True(logger.Has("DeltaOptimizeAborted"));
    }

    // ---------------------------------------------------------------- FIX 7: coverage gaps

    [Fact]
    public async Task Optimize_TwoConcurrentRunsOverOverlappingGroup_LoserAborts_AllRowsSurviveOnce()
    {
        // Two OPTIMIZE runs planned against the SAME read snapshot over the SAME (overlapping) group: the
        // winner commits; the loser's inputs were removed underneath it, so its ReadFiles scope aborts it
        // with ConcurrentDeleteReadException. Every row must survive exactly once.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")));
        StagedDataFile d = await WriteDataFileAsync("d.parquet", Batch((4, "d")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c, d);

        Snapshot readSnapshot = await Log().LoadSnapshotAsync();
        List<(long, string?)> before =
            Sorted(await ReadRowsAsync(new[] { "a.parquet", "b.parquet", "c.parquet", "d.parquet" }));
        Assert.Equal(4, before.Count);

        OptimizeResult winner = await Optimize(nameFactory: () => "WIN").OptimizeAsync(readSnapshot);
        Assert.Equal(readSnapshot.Version + 1, winner.CommittedVersion);

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(
            () => Optimize(nameFactory: () => "LOSE").OptimizeAsync(readSnapshot));

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(readSnapshot.Version + 1, after.Version); // only the winner advanced the table.
        Assert.Equal(before, Sorted(await ReadRowsAsync(ActivePaths(after))));
    }

    [Fact]
    public async Task Optimize_MultiPartition_Unfiltered_CompactsEachPartitionIndependently()
    {
        // No filter: both partitions are compacted, independently (no cross-partition mixing). The Partitions
        // breakdown has >1 entry and each partition compacts its two files into one.
        StagedDataFile us1 = await WriteDataFileAsync("region=US/us1.parquet", Batch((1, "a")), Partition(("region", "US")));
        StagedDataFile us2 = await WriteDataFileAsync("region=US/us2.parquet", Batch((2, "b")), Partition(("region", "US")));
        StagedDataFile eu1 = await WriteDataFileAsync("region=EU/eu1.parquet", Batch((3, "c")), Partition(("region", "EU")));
        StagedDataFile eu2 = await WriteDataFileAsync("region=EU/eu2.parquet", Batch((4, "d")), Partition(("region", "EU")));
        await SeedAsync(PartitionedSchema, partitionColumns: new[] { "region" }, us1, us2, eu1, eu2);

        List<(long, string?)> before = Sorted(await ReadRowsAsync(new[]
        {
            "region=US/us1.parquet", "region=US/us2.parquet", "region=EU/eu1.parquet", "region=EU/eu2.parquet",
        }));

        OptimizeResult result = await Optimize().OptimizeAsync();

        Assert.Equal(4, result.FilesRemoved);
        Assert.Equal(2, result.FilesAdded);
        Assert.True(result.Partitions.Length > 1);
        Assert.Equal(2, result.Partitions.Length);
        Assert.All(result.Partitions, p =>
        {
            Assert.Equal(2, p.FilesRemoved);
            Assert.Equal(1, p.FilesAdded);
        });

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(2, after.ActiveFiles.Length);
        foreach (AddFileAction output in after.ActiveFiles)
        {
            string region = output.PartitionValues["region"]!;
            Assert.StartsWith($"region={region}/", output.Path, StringComparison.Ordinal);
            long[] expectedIds = region == "US" ? new long[] { 1, 2 } : new long[] { 3, 4 };
            List<(long Id, string? Value)> rows = await ReadRowsAsync(new[] { output.Path });
            Assert.All(rows, r => Assert.Contains(r.Id, expectedIds)); // no cross-partition mixing.
        }

        Assert.Equal(before, Sorted(await ReadRowsAsync(ActivePaths(after))));
    }

    [Fact]
    public async Task Optimize_NonPositiveTargetFileSize_Throws_AndCommitsNothing()
    {
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Optimize().OptimizeAsync(targetFileSize: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Optimize().OptimizeAsync(targetFileSize: -1));

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(1, after.Version); // the guard is fail-closed: nothing committed.
    }

    [Fact]
    public async Task Optimize_CompactsAcrossRowGroupBoundaries_PreservingRows()
    {
        // Each input is written with a small row-group limit so it spans multiple Parquet row groups; the
        // full-read + rewrite path must reassemble every row across those boundaries.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", BatchRange(0, 150), rowGroupRowLimit: 40);
        StagedDataFile b = await WriteDataFileAsync("b.parquet", BatchRange(150, 150), rowGroupRowLimit: 40);
        await SeedAsync(DataSchema, partitionColumns: null, a, b);

        List<(long, string?)> before = Sorted(await ReadRowsAsync(new[] { "a.parquet", "b.parquet" }));
        Assert.Equal(300, before.Count);

        OptimizeResult result = await Optimize().OptimizeAsync();
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(1, result.FilesAdded);
        Assert.Equal(300, result.RowCount);

        Snapshot after = await Log().LoadSnapshotAsync();
        AddFileAction output = Assert.Single(after.ActiveFiles);
        Assert.Equal(before, Sorted(await ReadRowsAsync(new[] { output.Path })));
    }

    [Fact]
    public async Task Optimize_SecondRunOnCompactedTable_IsNoOp()
    {
        // Idempotency: after compaction each partition holds a single file, so a second OPTIMIZE finds only
        // single-file groups (skipped) — a no-op with no commit and an unchanged version.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")));
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")));
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")));
        StagedDataFile d = await WriteDataFileAsync("d.parquet", Batch((4, "d")));
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c, d);

        OptimizeResult first = await Optimize().OptimizeAsync();
        Assert.NotNull(first.CommittedVersion);
        Snapshot afterFirst = await Log().LoadSnapshotAsync();

        OptimizeResult second = await Optimize().OptimizeAsync();
        Assert.Null(second.CommittedVersion);
        Assert.Equal(0, second.FilesRemoved);
        Assert.Equal(0, second.FilesAdded);

        Snapshot afterSecond = await Log().LoadSnapshotAsync();
        Assert.Equal(afterFirst.Version, afterSecond.Version); // no commit on the second run.
        Assert.Equal(ActivePaths(afterFirst), ActivePaths(afterSecond));
    }

    [Fact]
    public async Task Optimize_GroupExactlyAtTargetBoundary_PacksAndSkipsLoneFile()
    {
        // Sizes 250,250,250 with target 500: next-fit packs {a,b} at EXACTLY the boundary (250+250 == 500 is
        // not > target), then c would overflow (500+250 > 500) so it closes into a lone bin that is skipped.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")), declaredSize: 250);
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")), declaredSize: 250);
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")), declaredSize: 250);
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c);

        OptimizeResult result = await Optimize().OptimizeAsync(targetFileSize: 500);
        Assert.Equal(2, result.FilesRemoved); // {a,b} packed at the boundary.
        Assert.Equal(1, result.FilesAdded);

        Snapshot after = await Log().LoadSnapshotAsync();
        string[] active = ActivePaths(after);
        Assert.Contains("c.parquet", active);        // the lone bin is skipped, so c is untouched.
        Assert.DoesNotContain("a.parquet", active);
        Assert.DoesNotContain("b.parquet", active);
    }

    [Fact]
    public async Task Optimize_EmptyTable_IsNoOp()
    {
        await SeedEmptyAsync(DataSchema);
        Snapshot before = await Log().LoadSnapshotAsync();

        OptimizeResult result = await Optimize().OptimizeAsync();

        Assert.Null(result.CommittedVersion);
        Assert.Equal(0, result.FilesRemoved);
        Assert.Equal(0, result.FilesAdded);
        Assert.Empty(result.Partitions);

        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Empty(after.ActiveFiles);
    }

    [Fact]
    public async Task Optimize_MultiGroup_PreCommitFailure_LeavesAllInputsActive_AndOrphansAllOutputs()
    {
        // AC4 across >1 group: target 250 packs four size-100 files into two groups {a,b} and {c,d}. A
        // pre-commit failure fired AFTER both outputs are written must leave ALL four inputs active and BOTH
        // outputs orphaned — never a partial commit.
        StagedDataFile a = await WriteDataFileAsync("a.parquet", Batch((1, "a")), declaredSize: 100);
        StagedDataFile b = await WriteDataFileAsync("b.parquet", Batch((2, "b")), declaredSize: 100);
        StagedDataFile c = await WriteDataFileAsync("c.parquet", Batch((3, "c")), declaredSize: 100);
        StagedDataFile d = await WriteDataFileAsync("d.parquet", Batch((4, "d")), declaredSize: 100);
        await SeedAsync(DataSchema, partitionColumns: null, a, b, c, d);
        Snapshot before = await Log().LoadSnapshotAsync();

        int n = 0;
        DeltaOptimize optimize = Optimize(
            nameFactory: () => "ORPH" + (n++).ToString(System.Globalization.CultureInfo.InvariantCulture));
        optimize.BeforeCommitProbe = _ => throw new InvalidOperationException("injected pre-commit failure");

        await Assert.ThrowsAsync<InvalidOperationException>(() => optimize.OptimizeAsync(targetFileSize: 250));

        // Unchanged: all four inputs still active, version not advanced.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(new[] { "a.parquet", "b.parquet", "c.parquet", "d.parquet" }, ActivePaths(after));

        // Both compacted outputs were durably written but are referenced by nothing — pure orphans.
        foreach (string orphan in new[] { "part-ORPH0.parquet", "part-ORPH1.parquet" })
        {
            Assert.NotNull(await _backend.HeadAsync(orphan, CancellationToken.None));
            Assert.DoesNotContain(orphan, ActivePaths(after));
        }
    }

    // ---------------------------------------------------------------- #553: column-mapping guard

    [Fact]
    public async Task Optimize_OnNameModeTable_IsRejectedFailClosed_Issue553()
    {
        // #553: OPTIMIZE does not support column mapping. A name-mode table's data files store PHYSICAL
        // (col-<uuid>) names, but OPTIMIZE resolves the data schema under ColumnMappingMode.None (LOGICAL
        // names), so it must fail closed up front rather than request absent columns. Two small files would
        // otherwise be compaction candidates.
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedTableAsync(
                DataSchema, Array.Empty<string>(), new[] { Batch((1, "a")) }, RandomPhysicalNameSource.Instance);
            await target.AppendAsync(DataSchema, Array.Empty<string>(), new[] { Batch((2, "b")) });
        }

        Snapshot before = await Log().LoadSnapshotAsync();
        Assert.Equal(2, before.ActiveFiles.Length); // two compaction-candidate small files

        OptimizeColumnMappingUnsupportedException ex =
            await Assert.ThrowsAsync<OptimizeColumnMappingUnsupportedException>(() => Optimize().OptimizeAsync());
        Assert.Equal(ColumnMappingMode.Name, ex.Mode);

        // Fail-closed: the table is unchanged (same version, same active files) — no compacted commit.
        Snapshot after = await Log().LoadSnapshotAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ActiveFiles.Length, after.ActiveFiles.Length);
    }

    [Fact]
    public async Task Optimize_OnNameModeTable_DryRun_IsAlsoRejectedFailClosed_Issue553()
    {
        // The guard is independent of dry-run: the compaction plan is meaningless for physical-named files, so
        // even a dry run is rejected (never reports a plan it could not actually execute).
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedTableAsync(
                DataSchema, Array.Empty<string>(), new[] { Batch((1, "a")) }, RandomPhysicalNameSource.Instance);
            await target.AppendAsync(DataSchema, Array.Empty<string>(), new[] { Batch((2, "b")) });
        }

        await Assert.ThrowsAsync<OptimizeColumnMappingUnsupportedException>(
            () => Optimize().OptimizeAsync(dryRun: true));
    }

    [Fact]
    public async Task Optimize_OnAllNullableNameModeTable_DoesNotNullFillAndCommit_Issue553()
    {
        // #553 DATA-LOSS regression: for an ALL-NULLABLE name-mode schema every logical column is
        // "absent + nullable + null-fill enabled" in the physical files, so WITHOUT the guard OPTIMIZE would
        // null-fill all columns and commit an all-null compacted output — silently dropping the real rows.
        // The guard rejects fail-closed, so the real (non-null) data remains intact and readable. (Reverting
        // the guard makes the read-back below return all-null, so this is a matched-pair oracle.)
        var allNullable = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: true),
            new StructField("value", DataTypes.StringType, nullable: true),
        });
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedTableAsync(
                allNullable, Array.Empty<string>(),
                new[] { NullableBatch(allNullable, (1, "a")) }, RandomPhysicalNameSource.Instance);
            await target.AppendAsync(allNullable, Array.Empty<string>(), new[] { NullableBatch(allNullable, (2, "b")) });
        }

        Snapshot before = await Log().LoadSnapshotAsync();

        OptimizeColumnMappingUnsupportedException ex =
            await Assert.ThrowsAsync<OptimizeColumnMappingUnsupportedException>(() => Optimize().OptimizeAsync());
        Assert.Equal(ColumnMappingMode.Name, ex.Mode);

        // The table is unchanged and the real rows still read back through the name-mode read door — NOT
        // dropped to all-null (which is what an unguarded null-fill compaction would have committed).
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        Assert.Equal(before.Version, info.Version);

        var rows = new List<(long?, string?)>();
        foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
        {
            for (int r = 0; r < b.LogicalRowCount; r++)
            {
                ColumnVector idc = b.SelectedColumn(0);
                ColumnVector valc = b.SelectedColumn(1);
                rows.Add((
                    idc.IsNull(r) ? (long?)null : idc.GetValue<long>(r),
                    valc.IsNull(r) ? null : Encoding.UTF8.GetString(valc.GetBytes(r))));
            }
        }

        Assert.Equal(new (long?, string?)[] { (1L, "a"), (2L, "b") }, rows.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Optimize_OnPartitionedNameModeTable_IsRejectedFailClosed_Issue553()
    {
        // The guard is partition-agnostic — it fires ABOVE PlanCompaction / partition resolution. A
        // partitioned name-mode table with two small files in one partition is still rejected fail-closed.
        // (Locks the guard's top-of-method placement against a future refactor that moved it below planning.)
        var schema = new StructType(new[]
        {
            new StructField("region", DataTypes.StringType, nullable: true), // partition
            new StructField("id", DataTypes.LongType, nullable: false),
        });
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedTableAsync(
                schema, new[] { "region" }, new[] { RegionBatch(schema, "us", 1L) }, RandomPhysicalNameSource.Instance);
            await target.AppendAsync(schema, new[] { "region" }, new[] { RegionBatch(schema, "us", 2L) });
        }

        Snapshot before = await Log().LoadSnapshotAsync();
        OptimizeColumnMappingUnsupportedException ex =
            await Assert.ThrowsAsync<OptimizeColumnMappingUnsupportedException>(() => Optimize().OptimizeAsync());
        Assert.Equal(ColumnMappingMode.Name, ex.Mode);
        Assert.Equal(before.Version, (await Log().LoadSnapshotAsync()).Version); // unchanged
    }

    [Fact]
    public async Task Optimize_OnSingleFileNameModeTable_IsRejectedFailClosed_Issue553()
    {
        // Even a single-file name-mode table (not a compaction candidate) is rejected by the top-of-method
        // guard — OPTIMIZE never quietly returns an empty no-op plan for a column-mapped table.
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedTableAsync(
                DataSchema, Array.Empty<string>(), new[] { Batch((1, "a")) }, RandomPhysicalNameSource.Instance);
        }

        OptimizeColumnMappingUnsupportedException ex =
            await Assert.ThrowsAsync<OptimizeColumnMappingUnsupportedException>(() => Optimize().OptimizeAsync());
        Assert.Equal(ColumnMappingMode.Name, ex.Mode);
    }

    [Fact]
    public void OptimizeColumnMappingUnsupportedException_IdMode_CarriesModeAndLabel_Issue553()
    {
        // `id` mode is rejected at snapshot load (deferred to #523), so the guard's id branch is
        // defense-in-depth reachable only via the internal explicit-snapshot seam. Unit-cover the exception's
        // id path directly: it carries ColumnMappingMode.Id and labels the message 'id'.
        var ex = new OptimizeColumnMappingUnsupportedException(ColumnMappingMode.Id);
        Assert.Equal(ColumnMappingMode.Id, ex.Mode);
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private DeltaLog Log() => new(_backend);

    private DeltaOptimize Optimize(
        TimeProvider? timeProvider = null,
        Func<string>? nameFactory = null,
        ParquetFileWriter? writer = null,
        Microsoft.Extensions.Logging.ILogger<DeltaOptimize>? logger = null,
        DeltaStorageTelemetry? telemetry = null) =>
        new(
            _backend,
            new DeltaLog(_backend),
            new DeltaCommitter(_backend),
            timeProvider: timeProvider ?? new FixedTimeProvider(Now),
            writer: writer,
            compactedFileNameFactory: nameFactory,
            logger: logger,
            telemetry: telemetry);

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

    private static ColumnBatch Batch(params (long Id, string? Value)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector value = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long rowId, string? rowValue) in rows)
        {
            id.AppendValue(rowId);
            if (rowValue is null)
            {
                value.AppendNull();
            }
            else
            {
                value.AppendBytes(Encoding.UTF8.GetBytes(rowValue));
            }
        }

        return new ManagedColumnBatch(DataSchema, new ColumnVector[] { id, value }, rows.Length);
    }

    // Builds a batch under an explicit (e.g. all-nullable) [id: long, value: string] schema — used by the
    // #553 all-nullable name-mode guard regression, where the batch schema's nullability differs from the
    // fixed DataSchema.
    private static ColumnBatch NullableBatch(StructType schema, params (long Id, string? Value)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector value = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long rowId, string? rowValue) in rows)
        {
            id.AppendValue(rowId);
            if (rowValue is null)
            {
                value.AppendNull();
            }
            else
            {
                value.AppendBytes(Encoding.UTF8.GetBytes(rowValue));
            }
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { id, value }, rows.Length);
    }

    // Builds a single-row [region: string (partition), id: long] batch — used by the #553 partitioned
    // name-mode guard test.
    private static ColumnBatch RegionBatch(StructType schema, string region, long id)
    {
        MutableColumnVector regionCol = ColumnVectors.Create(DataTypes.StringType, 1);
        MutableColumnVector idCol = ColumnVectors.Create(DataTypes.LongType, 1);
        regionCol.AppendBytes(Encoding.UTF8.GetBytes(region));
        idCol.AppendValue(id);
        return new ManagedColumnBatch(schema, new ColumnVector[] { regionCol, idCol }, 1);
    }

    // Builds a batch of <paramref name="count"/> sequential rows (id = start..start+count-1, value = "v<id>"),
    // used to exercise multi-row-group / batch-straddling compaction with more than the 1–2 rows other tests
    // use.
    private static ColumnBatch BatchRange(long start, int count)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, count);
        MutableColumnVector value = ColumnVectors.Create(DataTypes.StringType, count);
        for (int i = 0; i < count; i++)
        {
            long rowId = start + i;
            id.AppendValue(rowId);
            value.AppendBytes(Encoding.UTF8.GetBytes("v" + rowId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new ManagedColumnBatch(DataSchema, new ColumnVector[] { id, value }, count);
    }

    // Writes a real Parquet data file (the DATA schema — never the partition column) to the backend at
    // <paramref name="path"/> and returns the corresponding staged add. An explicit <paramref name="declaredSize"/>
    // lets a test drive the bin-packing plan independently of the (tiny) real byte size; by default the add
    // records the real measured size. An explicit <paramref name="rowGroupRowLimit"/> forces the writer to
    // split the batch across multiple Parquet row groups (so the read/rewrite path is exercised across
    // row-group boundaries).
    private async Task<StagedDataFile> WriteDataFileAsync(
        string path,
        ColumnBatch batch,
        ImmutableSortedDictionary<string, string?>? partition = null,
        long? declaredSize = null,
        int? rowGroupRowLimit = null)
    {
        using var buffer = new MemoryStream();
        var writer = rowGroupRowLimit is int limit ? new ParquetFileWriter(limit) : new ParquetFileWriter();
        ParquetFileWriter.WriteResult result = await writer.WriteWithStatisticsAsync(
            buffer, DataSchema, new[] { batch }, StatisticsPolicy.Default, CancellationToken.None);
        await _backend.PutIfAbsentAsync(path, buffer.ToArray(), CancellationToken.None);
        return new StagedDataFile(
            path, partition ?? NoPartition, declaredSize ?? result.ByteSize, 1L, result.Statistics);
    }

    // Seeds v0 (protocol + metadata with the real schema) then commits all files as one append at v1.
    private async Task SeedAsync(StructType tableSchema, string[]? partitionColumns, params StagedDataFile[] files)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(tableSchema, partitionColumns: partitionColumns));
        Snapshot snapshot = await Log().LoadSnapshotAsync();
        await new DeltaTableWriter(_backend).AppendAsync(snapshot, snapshot.Schema, files);
    }

    // Seeds an empty table: v0 protocol + metadata only, no data files (no active files).
    private async Task SeedEmptyAsync(StructType tableSchema) =>
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(tableSchema));

    // Commits a metadata-only evolution at the given version, replacing the table schema in place.
    private async Task EvolveSchemaAsync(long version, StructType evolvedSchema) =>
        await DeltaTestHarness.WriteCommitAsync(
            _backend, version, DeltaTestHarness.MetadataWithSchema(evolvedSchema));

    // Commits a metadata-only evolution that PRESERVES the partition columns (the unpartitioned overload
    // drops them). Used to additively evolve a partitioned table so OPTIMIZE still derives the correct
    // per-partition DATA schema (whole schema minus the partition columns) and null-fills the added lane.
    private async Task EvolveSchemaAsync(long version, StructType evolvedSchema, string[] partitionColumns) =>
        await DeltaTestHarness.WriteCommitAsync(
            _backend, version,
            DeltaTestHarness.MetadataWithSchema(evolvedSchema, partitionColumns: partitionColumns));

    // Seeds a DELETION-VECTOR-capable table (reader v3 / writer v7 declaring the `deletionVectors` feature)
    // so an add can legally carry a DV descriptor and the read path applies it, then commits all files as one
    // append at v1.
    private async Task SeedDvCapableAsync(StructType tableSchema, params StagedDataFile[] files)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.ProtocolWithReaderFeature("deletionVectors", minReader: 3, minWriter: 7),
            DeltaTestHarness.MetadataWithSchema(tableSchema));
        Snapshot snapshot = await Log().LoadSnapshotAsync();
        await new DeltaTableWriter(_backend).AppendAsync(snapshot, snapshot.Schema, files);
    }

    // Applies an INLINE deletion vector to EVERY active file, each excluding the single physical position
    // <paramref name="deletedPositionPerFile"/> (cardinality 1). Commits one remove(prior add) +
    // add(same path, inline DV, physical numRecords) per file in a single commit, and returns the resulting
    // snapshot. Mirrors the inline-DV pattern in DeletionVectorReadWriteTests.
    private async Task<Snapshot> ApplyInlineDvAsync(Snapshot seeded, int deletedPositionPerFile, long physicalRecords)
    {
        byte[] rawBitmap = RoaringBitmapArray.Serialize(new long[] { deletedPositionPerFile });
        DeletionVectorDescriptor inline = DeletionVectorDescriptor.ForInline(rawBitmap, cardinality: 1);

        var actions = new List<DeltaAction>();
        foreach (AddFileAction add in seeded.ActiveFiles)
        {
            actions.Add(new RemoveFileAction(
                add.Path, DeletionTimestamp: 1, DataChange: true, ExtendedFileMetadata: true,
                add.PartitionValues, add.Size, DeletionVector: null));
            actions.Add(new AddFileAction(
                add.Path, add.PartitionValues, add.Size, ModificationTime: 1, DataChange: true,
                add.Stats! with { NumRecords = physicalRecords }, add.Tags, inline));
        }

        await new DeltaCommitter(_backend).CommitAsync(
            seeded, actions, DeltaReadScope.ReadFiles(seeded.ActiveFiles.Select(a => a.Path)));
        return await Log().LoadSnapshotAsync();
    }

    // Applies an INLINE deletion vector to ONLY the named files (each excluding the single physical position
    // <paramref name="deletedPosition"/>, cardinality 1), leaving every other active file DV-free. Commits
    // one remove(prior add) + add(same path, inline DV, physical numRecords) per selected file in a single
    // commit, and returns the resulting snapshot. Used to build a MIXED bin where only SOME files carry a DV.
    private async Task<Snapshot> ApplyInlineDvToAsync(
        Snapshot seeded, IReadOnlySet<string> paths, int deletedPosition, long physicalRecords)
    {
        byte[] rawBitmap = RoaringBitmapArray.Serialize(new long[] { deletedPosition });
        DeletionVectorDescriptor inline = DeletionVectorDescriptor.ForInline(rawBitmap, cardinality: 1);

        var actions = new List<DeltaAction>();
        foreach (AddFileAction add in seeded.ActiveFiles.Where(a => paths.Contains(a.Path)))
        {
            actions.Add(new RemoveFileAction(
                add.Path, DeletionTimestamp: 1, DataChange: true, ExtendedFileMetadata: true,
                add.PartitionValues, add.Size, DeletionVector: null));
            actions.Add(new AddFileAction(
                add.Path, add.PartitionValues, add.Size, ModificationTime: 1, DataChange: true,
                add.Stats! with { NumRecords = physicalRecords }, add.Tags, inline));
        }

        await new DeltaCommitter(_backend).CommitAsync(
            seeded, actions, DeltaReadScope.ReadFiles(seeded.ActiveFiles.Select(a => a.Path)));
        return await Log().LoadSnapshotAsync();
    }

    // Physically PURGES a DV'd file the way a real REORG/purge does: removes the DV'd add and adds a FRESH
    // file (new path) that physically contains only the surviving rows and carries NO deletion vector
    // (DeletionVector == null). The purged file is therefore a normal small-file compaction candidate again
    // (#530). <paramref name="purged"/> is the already-staged survivors-only replacement.
    private async Task<Snapshot> PurgeDeletionVectorAsync(Snapshot current, string dvPath, StagedDataFile purged)
    {
        AddFileAction dvAdd = current.ActiveFiles.Single(f => f.Path == dvPath && f.DeletionVector is not null);
        var actions = new List<DeltaAction>
        {
            new RemoveFileAction(
                dvAdd.Path, DeletionTimestamp: 1, DataChange: true, ExtendedFileMetadata: true,
                dvAdd.PartitionValues, dvAdd.Size, dvAdd.DeletionVector),
            new AddFileAction(
                purged.Path, purged.PartitionValues, purged.Size, ModificationTime: 1, DataChange: true,
                purged.Stats, ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
                DeletionVector: null),
        };

        await new DeltaCommitter(_backend).CommitAsync(
            current, actions, DeltaReadScope.ReadFiles(new[] { dvPath }));
        return await Log().LoadSnapshotAsync();
    }
    private async Task<List<(long Id, string? Value)>> ReadSurvivorsAsync(Snapshot snapshot)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        var rows = new List<(long, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(snapshot.Version))
        {
            ColumnVector idColumn = batch.SelectedColumn(0);
            ColumnVector valueColumn = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                long id = idColumn.GetValue<long>(r);
                string? value = valueColumn.IsNull(r) ? null : Encoding.UTF8.GetString(valueColumn.GetBytes(r));
                rows.Add((id, value));
            }
        }

        return rows;
    }

    // A content oracle for a WIDENED (schema-evolved) file: reads every row under EvolvedSchema into
    // (id, value, extra) tuples. With <paramref name="nullFill"/> false every requested column MUST be
    // physically present (proves the compacted output carries the current schema); with true, an absent
    // NULLABLE column reads back as all-null (#497).
    private async Task<List<(long Id, string? Value, string? Extra)>> ReadEvolvedRowsAsync(
        IEnumerable<string> paths, bool nullFill)
    {
        var reader = new ParquetFileReader();
        var rows = new List<(long, string?, string?)>();
        foreach (string path in paths)
        {
            Stream stream = await _backend.OpenReadAsync(path, CancellationToken.None);
            await using (stream)
            {
                await foreach (ColumnBatch batch in reader.ReadAsync(
                    stream, EvolvedSchema, null, nullFillMissingColumns: nullFill, allowTypeWideningPromotion: false, CancellationToken.None))
                {
                    ColumnVector idColumn = batch.SelectedColumn(0);
                    ColumnVector valueColumn = batch.SelectedColumn(1);
                    ColumnVector extraColumn = batch.SelectedColumn(2);
                    for (int r = 0; r < batch.LogicalRowCount; r++)
                    {
                        long id = idColumn.GetValue<long>(r);
                        string? value = valueColumn.IsNull(r) ? null : Encoding.UTF8.GetString(valueColumn.GetBytes(r));
                        string? extra = extraColumn.IsNull(r) ? null : Encoding.UTF8.GetString(extraColumn.GetBytes(r));
                        rows.Add((id, value, extra));
                    }
                }
            }
        }

        return rows;
    }

    // A content oracle for a TWICE-WIDENED (schema-evolved) file: reads every row under TwiceEvolvedSchema
    // into (id, value, extra, extra2) tuples. With <paramref name="nullFill"/> false every requested column
    // MUST be physically present (proves the compacted output carries BOTH later-added columns); with true,
    // an absent NULLABLE column reads back as all-null (#497).
    private async Task<List<(long Id, string? Value, string? Extra, string? Extra2)>> ReadTwiceEvolvedRowsAsync(
        IEnumerable<string> paths, bool nullFill)
    {
        var reader = new ParquetFileReader();
        var rows = new List<(long, string?, string?, string?)>();
        foreach (string path in paths)
        {
            Stream stream = await _backend.OpenReadAsync(path, CancellationToken.None);
            await using (stream)
            {
                await foreach (ColumnBatch batch in reader.ReadAsync(
                    stream, TwiceEvolvedSchema, null, nullFillMissingColumns: nullFill, allowTypeWideningPromotion: false, CancellationToken.None))
                {
                    ColumnVector idColumn = batch.SelectedColumn(0);
                    ColumnVector valueColumn = batch.SelectedColumn(1);
                    ColumnVector extraColumn = batch.SelectedColumn(2);
                    ColumnVector extra2Column = batch.SelectedColumn(3);
                    for (int r = 0; r < batch.LogicalRowCount; r++)
                    {
                        long id = idColumn.GetValue<long>(r);
                        string? value = valueColumn.IsNull(r) ? null : Encoding.UTF8.GetString(valueColumn.GetBytes(r));
                        string? extra = extraColumn.IsNull(r) ? null : Encoding.UTF8.GetString(extraColumn.GetBytes(r));
                        string? extra2 = extra2Column.IsNull(r) ? null : Encoding.UTF8.GetString(extra2Column.GetBytes(r));
                        rows.Add((id, value, extra, extra2));
                    }
                }
            }
        }

        return rows;
    }

    // Reads a data file's raw bytes and returns their SHA-256 (proves byte-identity of files OPTIMIZE never
    // rewrote, AC3).
    private async Task<string> Sha256Async(string path)
    {
        Stream stream = await _backend.OpenReadAsync(path, CancellationToken.None);
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, CancellationToken.None);
            return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }
    }

    private async Task CommitRawAsync(long version, params string[] lines) =>
        await DeltaTestHarness.WriteCommitAsync(_backend, version, lines);

    private static string[] ActivePaths(Snapshot snapshot) =>
        snapshot.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    // A content oracle: reads every row of the given data files back into (id, value) tuples so a test can
    // assert the rewritten rows equal the inputs' rows as a multiset (AC1 row-count / content equivalence).
    private async Task<List<(long Id, string? Value)>> ReadRowsAsync(IEnumerable<string> paths)
    {
        var reader = new ParquetFileReader();
        var rows = new List<(long, string?)>();
        foreach (string path in paths)
        {
            Stream stream = await _backend.OpenReadAsync(path, CancellationToken.None);
            await using (stream)
            {
                await foreach (ColumnBatch batch in reader.ReadAsync(stream, DataSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false, CancellationToken.None))
                {
                    ColumnVector idColumn = batch.SelectedColumn(0);
                    ColumnVector valueColumn = batch.SelectedColumn(1);
                    for (int r = 0; r < batch.LogicalRowCount; r++)
                    {
                        long id = idColumn.GetValue<long>(r);
                        string? value = valueColumn.IsNull(r) ? null : Encoding.UTF8.GetString(valueColumn.GetBytes(r));
                        rows.Add((id, value));
                    }
                }
            }
        }

        return rows;
    }

    private static List<(long Id, string? Value)> Sorted(IEnumerable<(long Id, string? Value)> rows) =>
        rows.OrderBy(r => r.Id).ThenBy(r => r.Value, StringComparer.Ordinal).ToList();

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
