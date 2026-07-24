using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Change Data Feed <b>read door</b> (increment 3, design §2.6): the public
/// <see cref="DeltaReadSource.LoadChangeFeedAsync"/> (resolve + validate the version range ONCE) and
/// <see cref="DeltaReadSource.ReadChangeBatchesAsync"/> (stream change rows in ascending commit order) pair.
/// Every test is oracle-backed over the committed <c>_delta_log</c> AND the produced <see cref="ColumnBatch"/>es,
/// asserting exact rows, <c>_change_type</c>, <c>_commit_version</c>, <c>_commit_timestamp</c> and partition
/// values — so a "double-counted", "dropped", "wrong-branch" or "spanning-versions" mutant fails on VALUES.
///
/// <para>Highest-risk properties (a subsequent red-team scrutinizes exactly these): <b>precedence</b> — a
/// cdc-bearing version reads EXACTLY its cdc rows and its add/remove are NOT re-derived (no double count),
/// while a non-cdc version derives implicitly (no miss); <b>DV-aware implicit derivation</b> — an overwrite's
/// derived <c>delete</c> is the removed file's LIVE rows (physical \ prior DV), so a row already masked by a
/// prior DV never re-surfaces; <b>range validation fail-closed</b> — start&gt;end, start&lt;0, end&gt;latest,
/// CDF-disabled-in-range and aged-out/VACUUMed ranges all throw; and <b>INV C8</b> — each produced batch
/// carries exactly one <c>_commit_version</c>.</para>
///
/// <para>Isolated in the shared non-parallel filesystem collection (drives a real temp-directory backend).</para>
/// </summary>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class ChangeFeedReadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cdf-read-" + Guid.NewGuid().ToString("N"));

    // id (non-null key) + name (nullable string): the same flat shape the DV/CDF harness uses.
    private static readonly StructType FlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // `region` is the partition column (its values live only on the add/cdc/remove action, never in the file
    // body), `id`/`val` live in the Parquet file — drives partition-column reconstruction on both read paths.
    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("val", DataTypes.LongType, nullable: true),
    });

    // FlatSchema + a nullable `extra` column — the reconciled END schema after an ALTER ADD COLUMN mid-range
    // (§2.8 cross-range reconciliation). Rows written before `extra` existed must null-fill it at read time.
    private static readonly StructType WideFlatSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.LongType, nullable: true),
    });

    // An int `amount` column and its widened long form — drives the int→long type-widening reconciliation
    // (§2.8): narrow int values written before the widening promote to long at the reconciled END schema.
    private static readonly StructType WideningIntSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("amount", DataTypes.IntegerType, nullable: true),
    });

    private static readonly StructType WideningLongSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("amount", DataTypes.LongType, nullable: true),
    });

    // A single nullable `name` column: a physically-narrow file (dropping the required `id`) used to prove an
    // absent REQUIRED column fails closed as schema evolution (never a silent null-fill of a required column).
    private static readonly StructType NarrowNameSchema = new(new[]
    {
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // A cdc-file leaf schema whose `id` data column is STRING while the FlatSchema metadata declares long — a
    // leaf-type mismatch EE-08 must reject. `_change_type` is present with a valid domain token (excluded from
    // the EE-08 data-column check) so ONLY the data-column validation can fire.
    private static readonly StructType CdcTypeMismatchSchema = new(new[]
    {
        new StructField("id", DataTypes.StringType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField(ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType, nullable: false),
    });

    // A cdc-file leaf schema matching the reconciled END schema B (id, name, extra) PLUS `_change_type` — used
    // to craft a v2 cdc file that is consistent with the END schema but NOT v2's OWN schema A (which lacks
    // `extra`). `id` stays long (matching both A and B) so ONLY the presence of `extra` distinguishes A from B:
    // the clean discriminator between per-version EE-08 validation (rejects — `extra` is absent from v2's A)
    // and a would-be end-version validation (accepts — matches B).
    private static readonly StructType CdcMatchesEndSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("extra", DataTypes.LongType, nullable: true),
        new StructField(ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType, nullable: false),
    });

    // The physical body layout the ChangeDataWriter emits for a FlatSchema cdc file: the data columns FIRST,
    // then the engine-appended `_change_type` (design §2.4 / ChangeDataWriter.AppendChangeTypeColumn). Used to
    // author a MULTI-ROW-GROUP cdc file (#644 streaming test) that overwrites a real DELETE-produced cdc file —
    // the explicit read reads EXACTLY the cdc file's rows (precedence), so the authored content is what surfaces.
    private static readonly StructType CdcFlatBodySchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField(ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType, nullable: false),
    });

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---------------------------------------------------------------- CDF-HP-01: append ⇒ inserts, no cdc file

    [Fact]
    public async Task Append_ThenReadSingleVersion_YieldsInserts_AndWritesNoChangeDataFile()
    {
        // CDF-HP-01: an append on a CDF-enabled table writes NO _change_data/ file — its change data is DERIVED
        // at read time from the committed add(dataChange=true) actions as `insert` rows, full data payload.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0 create, v1 enable CDF
        long v = await AppendFlatAsync(Batch((10, "x"), (20, null)));
        Assert.Equal(2L, v);                                         // v2 append (first data change after enable)

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(2, 2));

        Assert.Equal(2L, info.StartVersion);
        Assert.Equal(2L, info.EndVersion);
        AssertCdfOutputSchema(info.Schema, FlatSchema);

        // NO cdc file was written for the append (the implicit path derives from add actions).
        Assert.Empty(CdcFilePaths());

        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);
        Assert.Equal(
            new[]
            {
                (10L, (string?)"x", ChangeDataWriter.InsertChange, 2L),
                (20L, (string?)null, ChangeDataWriter.InsertChange, 2L),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Name, r.ChangeType, r.Version)).ToArray());
    }

    // ---------------------------------------------------------------- CDF-HP-02: DV delete ⇒ exactly deletes

    [Fact]
    public async Task DvDelete_ThenRead_YieldsExactlyDeletedRows_AndReAddedResidualNotSurfacedAsInsert()
    {
        // CDF-HP-02 + PRECEDENCE (§2.2): a merge-on-read DELETE writes cdc, so version v is EXPLICIT — the read
        // is EXACTLY the cdc rows (the newly-deleted rows as `delete`). The commit's residual DV-carrying add
        // (rows 1/3/5 still live) is NOT re-derived as `insert` — precedence suppresses ALL implicit derivation
        // for a cdc-bearing version (no double count, no spurious insert).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "hp02").DeleteAsync(WhereId(id => id is 2 or 4));
        Assert.Equal(2L, del.CommittedVersion);
        Assert.Equal(1, del.FilesWithDeletionVector);   // partial (DV-carrying) delete ⇒ residual add exists

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(2, 2));

        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);
        // EXACTLY the two newly-deleted rows as `delete`; NO insert row for the residual add's live rows.
        Assert.Equal(
            new[]
            {
                (2L, (string?)"b", ChangeDataWriter.DeleteChange, 2L),
                (4L, (string?)"d", ChangeDataWriter.DeleteChange, 2L),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Name, r.ChangeType, r.Version)).ToArray());
        Assert.DoesNotContain(rows, r => r.ChangeType == ChangeDataWriter.InsertChange);
    }

    // ---------------------------------------------------------------- CDF-HP-03: overwrite ⇒ delete + insert

    [Fact]
    public async Task Overwrite_ThenRead_YieldsDerivedDeleteOfOldRows_AndInsertOfNewRows_DeletesFirst()
    {
        // CDF-HP-03: an overwrite commits remove(old, dataChange=true) + add(new, dataChange=true) — no cdc — so
        // the reader DERIVES `delete` (old rows) + `insert` (new rows). Within the version the reader emits
        // derived deletes BEFORE derived inserts (a fixed, deterministic intra-version order).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));    // v0, v1
        long v = await OverwriteFlatAsync(Batch((30, "z"), (40, "w")));
        Assert.Equal(2L, v);

        Assert.Empty(CdcFilePaths());                                // overwrite writes NO cdc file

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2));
        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);

        // Old rows deleted, new rows inserted, all at v2.
        Assert.Equal(
            new[]
            {
                (1L, (string?)"a", ChangeDataWriter.DeleteChange, 2L),
                (2L, (string?)"b", ChangeDataWriter.DeleteChange, 2L),
                (30L, (string?)"z", ChangeDataWriter.InsertChange, 2L),
                (40L, (string?)"w", ChangeDataWriter.InsertChange, 2L),
            },
            rows.OrderBy(r => r.ChangeType, StringComparer.Ordinal).ThenBy(r => r.Id)
                .Select(r => (r.Id, r.Name, r.ChangeType, r.Version)).ToArray());

        // Intra-version order: every `delete` precedes every `insert`.
        int lastDelete = rows.FindLastIndex(r => r.ChangeType == ChangeDataWriter.DeleteChange);
        int firstInsert = rows.FindIndex(r => r.ChangeType == ChangeDataWriter.InsertChange);
        Assert.True(lastDelete < firstInsert, "derived deletes must precede derived inserts within a version");
    }

    // ---------------------------------------------------------------- CDF-HP-05: _commit_timestamp source

    [Fact]
    public async Task CommitTimestamp_EqualsCommitJsonMtime_AndTimeTravelResolvedValue_NotCommitInfoTimestamp()
    {
        // CDF-HP-05 (§2.8): _commit_timestamp is the version's <N>.json mtime (the SAME monotonic value
        // timestampAsOf time-travel resolves), NOT commitInfo.timestamp. Set deterministic, strictly-increasing
        // mtimes in the fixture so eff(v)=mtime(v) (no monotonic adjustment), then assert three ways.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await AppendFlatAsync(Batch((3, "c")));           // v3

        var baseTime = new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromHours(1);
        SetCommitMtimes(baseTime, step, latestVersion: 3);   // v0=12:00 … v3=15:00, strictly increasing

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);

        foreach (long version in new[] { 2L, 3L })
        {
            long expectedMillis = (baseTime + step * version).ToUnixTimeMilliseconds();
            FlatChange row = rows.First(r => r.Version == version);
            long actualMillis = row.TsMicros / 1000L;

            // (1) == the <N>.json mtime we stamped (micros lane = millis × 1000).
            Assert.Equal(expectedMillis * 1000L, row.TsMicros);
            Assert.Equal(expectedMillis, actualMillis);

            // (2) == the value timestampAsOf time travel resolves: feeding _commit_timestamp back resolves to v.
            using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
            DeltaSnapshotInfo tt = await source.LoadSnapshotAsync(
                null, DateTimeOffset.FromUnixTimeMilliseconds(actualMillis), CancellationToken.None);
            Assert.Equal(version, tt.Version);

            // (3) NOT commitInfo.timestamp (the wall-clock write time, a different value from our fixture mtime).
            long commitInfoTs = await ReadCommitInfoTimestampAsync(version);
            Assert.NotEqual(commitInfoTs, actualMillis);
        }
    }

    [Fact]
    public async Task CommitTimestamp_StableAcrossFloorAdvancingLogCleanup()
    {
        // query-exec L2 (item 4) pinning PROOF (§2.8): `_commit_timestamp` is pinned at RESOLVE time, so an
        // intervening log-cleanup that ADVANCES the earliest-reconstructable floor between LoadChangeFeedAsync
        // and ReadChangeBatchesAsync cannot shift a near-floor version's stamped timestamp. All four <N>.json
        // share ONE mtime T, so the monotonic timeline eff(N)=max(mtime(N), eff(N-1)+1ms) CARRIES: with floor 0
        // ⇒ eff = {0:T, 1:T+1, 2:T+2, 3:T+3}. We resolve [2,3] (pinning eff(2)=T+2, eff(3)=T+3), THEN delete
        // v0/v1 and write a complete checkpoint@2 so the floor advances to 2. A read-time RE-derivation would
        // now restart the chain at the new floor ⇒ eff(2)=T, eff(3)=T+1 — DIFFERENT. The read must stamp the
        // PINNED values (T+2, T+3), proving it reads the pinned map, not a fresh listing. (A mutant ignoring
        // Resolution.CommitMillisByVersion re-derives T / T+1 ⇒ this test goes RED.)
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0 create, v1 enable CDF
        await AppendFlatAsync(Batch((10, "x")));           // v2 (near-floor range start)
        await AppendFlatAsync(Batch((20, "y")));           // v3 (range end)

        var baseTime = new DateTimeOffset(2021, 3, 1, 9, 0, 0, TimeSpan.Zero);
        SetCommitMtimes(baseTime, TimeSpan.Zero, latestVersion: 3);   // ALL <N>.json share mtime T ⇒ monotonic carry
        long t = baseTime.ToUnixTimeMilliseconds();

        // Resolve+PIN the range against the pre-cleanup log (floor 0 ⇒ eff(2)=T+2, eff(3)=T+3).
        DeltaChangeFeedInfo info;
        using (DeltaReadSource resolveSource = DeltaReadSource.ForLocalPath(_root))
        {
            info = await resolveSource.LoadChangeFeedAsync(
                DeltaChangeFeedRange.FromVersion(2, 3), CancellationToken.None);
        }

        // Simulate log-cleanup that ADVANCES the floor to 2: a complete checkpoint@2 (protocol + metadata copied
        // from the real snapshot@2, so CDF stays active and the schema is exact) seeds state, then the earliest
        // commit JSONs are removed. [2,3] stays reconstructable (checkpoint@2 + surviving 2.json/3.json), but the
        // effective timeline a FRESH listing computes now differs for the near-floor start (its predecessor gone).
        var backend = new LocalFileSystemBackend(_root);
        await WriteCheckpointFromRealStateAsync(backend, 2);
        File.Delete(CommitPath(0));
        File.Delete(CommitPath(1));

        // Read against the PINNED info with a FRESH source (guarantees a re-listing of the cleaned log).
        var batches = new List<ColumnBatch>();
        using (DeltaReadSource readSource = DeltaReadSource.ForLocalPath(_root))
        {
            await foreach (ColumnBatch batch in readSource.ReadChangeBatchesAsync(info, CancellationToken.None))
            {
                batches.Add(batch);
            }
        }

        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);

        // The near-floor start v2 is the discriminator: stamped == PINNED eff(2)=(T+2)×1000 micros, NOT the
        // (T)×1000 a read-time re-derivation over the advanced floor would compute.
        FlatChange v2Row = rows.Single(r => r.Version == 2);
        Assert.Equal((10L, (string?)"x"), (v2Row.Id, v2Row.Name));
        Assert.Equal((t + 2) * 1000L, v2Row.TsMicros);       // pinned at resolve (floor-0 monotonic carry)
        Assert.NotEqual(t * 1000L, v2Row.TsMicros);          // NOT the read-time re-derivation (floor-2) value

        FlatChange v3Row = rows.Single(r => r.Version == 3);
        Assert.Equal((20L, (string?)"y"), (v3Row.Id, v3Row.Name));
        Assert.Equal((t + 3) * 1000L, v3Row.TsMicros);       // pinned
        Assert.NotEqual((t + 1) * 1000L, v3Row.TsMicros);    // NOT the re-derived (floor-2) value
    }

    // ---------------------------------------------------------------- CDF-HP-06: timestamp-endpoint resolution

    [Fact]
    public async Task TimestampEndpoints_ExactCommitTimestamps_ResolveToThoseVersions()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await AppendFlatAsync(Batch((3, "c")));           // v3
        await AppendFlatAsync(Batch((4, "d")));           // v4

        var baseTime = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromHours(1);
        SetCommitMtimes(baseTime, step, latestVersion: 4);

        DeltaChangeFeedInfo info = await ResolveCdfAsync(
            DeltaChangeFeedRange.FromTimestamp(baseTime + step * 2, baseTime + step * 4));
        Assert.Equal(2L, info.StartVersion);
        Assert.Equal(4L, info.EndVersion);
    }

    [Fact]
    public async Task TimestampEndpoints_BetweenCommits_RoundStartUp_AndEndDown()
    {
        // Spark parity (asymmetric): a start timestamp rounds UP to the first commit at/after it; an end
        // timestamp rounds DOWN to the last commit at/before it.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await AppendFlatAsync(Batch((3, "c")));           // v3
        await AppendFlatAsync(Batch((4, "d")));           // v4

        var baseTime = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromHours(1);
        SetCommitMtimes(baseTime, step, latestVersion: 4);

        var half = TimeSpan.FromMinutes(30);
        // start between v2(02:00) and v3(03:00) ⇒ round up to v3; end between v3(03:00) and v4(04:00) ⇒ down to v3.
        DeltaChangeFeedInfo info = await ResolveCdfAsync(new DeltaChangeFeedRange
        {
            StartingTimestamp = baseTime + step * 2 + half,
            EndingTimestamp = baseTime + step * 3 + half,
        });
        Assert.Equal(3L, info.StartVersion);
        Assert.Equal(3L, info.EndVersion);
    }

    // ---------------------------------------------------------------- CDF-HP-10: mixed endpoints accepted

    [Fact]
    public async Task MixedEndpoints_StartVersion_EndTimestamp_IsAccepted()
    {
        // §2.9: mixing version/timestamp ACROSS endpoints is allowed (Spark parity), only WITHIN one is not.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await AppendFlatAsync(Batch((3, "c")));           // v3

        var baseTime = new DateTimeOffset(2022, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var step = TimeSpan.FromHours(1);
        SetCommitMtimes(baseTime, step, latestVersion: 3);

        DeltaChangeFeedInfo info = await ResolveCdfAsync(new DeltaChangeFeedRange
        {
            StartingVersion = 2,
            EndingTimestamp = baseTime + step * 3,
        });
        Assert.Equal(2L, info.StartVersion);
        Assert.Equal(3L, info.EndVersion);
    }

    [Fact]
    public async Task EndOmitted_DefaultsToLatestCommittedVersion()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await AppendFlatAsync(Batch((3, "c")));           // v3

        DeltaChangeFeedInfo info = await ResolveCdfAsync(DeltaChangeFeedRange.FromVersion(2));
        Assert.Equal(2L, info.StartVersion);
        Assert.Equal(3L, info.EndVersion);               // defaulted to latest committed
    }

    // ---------------------------------------------------------------- CDF-EE-04 + argument contract

    [Fact]
    public async Task StartEndpoint_BothVersionAndTimestamp_ThrowsArgumentException()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));
        await Assert.ThrowsAsync<ArgumentException>(() => ResolveCdfAsync(new DeltaChangeFeedRange
        {
            StartingVersion = 1,
            StartingTimestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndingVersion = 1,
        }));
    }

    [Fact]
    public async Task EndEndpoint_BothVersionAndTimestamp_ThrowsArgumentException()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));
        await Assert.ThrowsAsync<ArgumentException>(() => ResolveCdfAsync(new DeltaChangeFeedRange
        {
            StartingVersion = 1,
            EndingVersion = 1,
            EndingTimestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }));
    }

    [Fact]
    public async Task NoStartBound_ThrowsArgumentException()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));
        await Assert.ThrowsAsync<ArgumentException>(() => ResolveCdfAsync(new DeltaChangeFeedRange
        {
            EndingVersion = 1,   // start omitted entirely
        }));
    }

    // ---------------------------------------------------------------- range validation: fail closed

    [Fact]
    public async Task StartGreaterThanEnd_FailsClosed()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await Assert.ThrowsAsync<DeltaReadException>(() => ResolveCdfAsync(DeltaChangeFeedRange.FromVersion(2, 1)));
    }

    [Fact]
    public async Task NegativeStart_FailsClosed()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await Assert.ThrowsAsync<DeltaReadException>(() => ResolveCdfAsync(DeltaChangeFeedRange.FromVersion(-1, 1)));
    }

    [Fact]
    public async Task EndBeyondLatestCommitted_FailsClosed()
    {
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1 (latest = 1)
        await Assert.ThrowsAsync<DeltaReadException>(() => ResolveCdfAsync(DeltaChangeFeedRange.FromVersion(1, 999)));
    }

    [Fact]
    public async Task CdfDisabledForSomeVersionInRange_FailsClosed()
    {
        // §2.7 conservative rule: the WHOLE requested range must have CDF active. v0 (the create) predates the
        // enable at v1, so a range that includes v0 fails closed even though v1+ have CDF active.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0 create (CDF NOT active), v1 enable
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await Assert.ThrowsAsync<DeltaReadException>(() => ResolveCdfAsync(DeltaChangeFeedRange.FromVersion(0, 2)));
    }

    [Fact]
    public async Task StartAgedOutByLogCleanup_FailsClosed()
    {
        // The start commit JSON aged past log retention (simulated: the earliest commits are gone, no
        // checkpoint) ⇒ the range is no longer available ⇒ fail closed.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((2, "b")));           // v2
        await AppendFlatAsync(Batch((3, "c")));           // v3
        File.Delete(CommitPath(0));
        File.Delete(CommitPath(1));
        File.Delete(CommitPath(2));                       // earliest reconstructable is now v3

        await Assert.ThrowsAsync<DeltaReadException>(() => ResolveCdfAsync(DeltaChangeFeedRange.FromVersion(2, 3)));
    }

    [Fact]
    public async Task VacuumedDataFile_FailsClosedAtRead()
    {
        // The removed/needed data file was VACUUMed away: resolution (log-only) succeeds, but the streaming
        // read opens the missing file and fails closed with the typed read exception.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1
        await AppendFlatAsync(Batch((10, "x")));          // v2 add(dataChange) — implicit insert source
        var backend = new LocalFileSystemBackend(_root);
        AddFileAction add = Assert.Single((await ReadCommitActionsAsync(backend, 2)).OfType<AddFileAction>());
        File.Delete(Path.Combine(_root, add.Path));       // vacuum the data file the derivation must read

        await Assert.ThrowsAsync<DeltaReadException>(
            async () => await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2)));
    }

    // ------------------------------------------------------ forged-info trust boundary: fail closed (red-team)

    [Fact]
    public async Task ForgedChangeFeedInfo_BypassingLoad_FailsClosed()
    {
        // Red-team CRITICAL: DeltaChangeFeedInfo has a PUBLIC ctor, so a consumer could forge one and pass it
        // straight to ReadChangeBatchesAsync, skipping LoadChangeFeedAsync's resolve+validate entirely (range
        // bounds, aged-out/VACUUMed, and CRUCIALLY the §2.7 CDF-enablement gate). The read door now REQUIRES a
        // non-forgeable resolution proof that ONLY LoadChangeFeedAsync can mint: an info built via the public
        // ctor — or `default` — carries none and is rejected fail-closed BEFORE any batch is produced.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // a real CDF table (v0, v1) — the guard is state-independent
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);

        await AssertForgedInfoFailsClosedAsync(source, new DeltaChangeFeedInfo(0, 1, FlatSchema));   // public ctor
        await AssertForgedInfoFailsClosedAsync(source, default);                                      // default(struct)
    }

    [Fact]
    public async Task ForgedInfoForCdfDisabledVersion_CannotBypassEnablementGate_FailsClosed()
    {
        // The red-team's EXACT repro: v0 is the CREATE (CDF NOT active), v1 ENABLES CDF, v2 appends. §2.7
        // requires CDF active for EVERY version in the range, so v0's change rows must be UNreadable. Prove
        // there is NO path — forged OR legit — to them.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0 create (CDF OFF), v1 enable CDF
        await AppendFlatAsync(Batch((2, "b")));           // v2 append

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);

        // (1) The LEGIT resolve of [0,2] fails closed AT RESOLVE (CDF-disabled-in-range, §2.7) — v0 predates the enable.
        await Assert.ThrowsAsync<DeltaReadException>(
            () => source.LoadChangeFeedAsync(DeltaChangeFeedRange.FromVersion(0, 2), CancellationToken.None));

        // A plausible schema for the forgery: the reconciled output schema of a VALID sub-range [1,2] (CDF
        // active for v1,v2). The forged info is thus indistinguishable from a real one EXCEPT it lacks the proof.
        DeltaChangeFeedInfo legit = await source.LoadChangeFeedAsync(
            DeltaChangeFeedRange.FromVersion(1, 2), CancellationToken.None);
        var forged = new DeltaChangeFeedInfo(0, 2, legit.Schema);

        // (2) Forging (0,2,schema) and reading it must ALSO fail closed — the guard rejects the proofless info
        // BEFORE any I/O, so v0's CDF-off change row (the create's add of id 1) NEVER surfaces.
        await AssertForgedInfoFailsClosedAsync(source, forged);
    }

    [Fact]
    public async Task ChangeFeedInfo_FromDifferentSource_FailsClosed()
    {
        // Security F-1 (defense-in-depth): the resolution proof binds to the SOURCE (table root), not just the
        // range. An info resolved on source A must NOT replay on a DIFFERENT source B — else B's versions
        // bypass B's OWN §2.7 enablement gate and get stamped with A's timestamps (a cross-table footgun).

        // Source A (this._root): CDF ENABLED; resolve [2,2] → a proof bound to A's table root.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0, v1 (CDF on)
        await AppendFlatAsync(Batch((10, "x")));          // v2 implicit insert
        DeltaChangeFeedInfo infoA;
        using (DeltaReadSource sourceA = DeltaReadSource.ForLocalPath(_root))
        {
            infoA = await sourceA.LoadChangeFeedAsync(DeltaChangeFeedRange.FromVersion(2, 2), CancellationToken.None);
        }

        // Source B (a SECOND real table root): also has v0/v1/v2 with data (so a read of [2,2] would proceed),
        // but CDF was NEVER enabled — so B's OWN LoadChangeFeedAsync would itself fail closed. Reading A's
        // proof on B must NOT bypass that.
        string rootB = Path.Combine(Path.GetTempPath(), "cdf-read-other-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(rootB))
            {
                await target.CreateDeletionVectorTableAsync(FlatSchema, Array.Empty<string>(), new[] { Batch((1, "a")) });
            }

            using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(rootB))
            {
                await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { Batch((2, "b")) });   // v1
            }

            using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(rootB))
            {
                await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { Batch((3, "c")) });   // v2
            }

            // Handing A's proof to B fails closed: the read door re-checks the bound source identity and
            // rejects the cross-source info BEFORE any I/O, so NO B row surfaces under A's proof.
            using DeltaReadSource sourceB = DeltaReadSource.ForLocalPath(rootB);
            await AssertForgedInfoFailsClosedAsync(sourceB, infoA, "different source");
        }
        finally
        {
            if (Directory.Exists(rootB))
            {
                Directory.Delete(rootB, recursive: true);
            }
        }
    }

    // ---------------------------------------------------------------- mixed explicit + implicit, ascending

    [Fact]
    public async Task MixedExplicitAndImplicitVersions_ReadInAscendingCommitOrder()
    {
        // A range that spans an IMPLICIT version (append ⇒ derived insert) and an EXPLICIT version
        // (DV delete ⇒ cdc) must read both, each by its own path, in ascending commit order.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));   // v0, v1
        long appended = await AppendFlatAsync(Batch((10, "x")));             // v2 implicit insert
        Assert.Equal(2L, appended);
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "mixed").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(3L, del.CommittedVersion);                              // v3 explicit delete (cdc)

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(2, 3));
        Assert.Equal(2L, info.StartVersion);
        Assert.Equal(3L, info.EndVersion);

        (List<FlatChange> rows, List<long> batchVersions) = DecodeFlatChanges(batches);

        // v2 = derived insert of the appended row; v3 = cdc delete of id 2. Exactly these, tagged correctly.
        Assert.Equal(
            new[]
            {
                (10L, ChangeDataWriter.InsertChange, 2L),
                (2L, ChangeDataWriter.DeleteChange, 3L),
            },
            rows.Select(r => (r.Id, r.ChangeType, r.Version)).ToArray());

        // Ascending commit order across batches (v2 batches strictly before v3 batches).
        Assert.Equal(batchVersions.OrderBy(x => x).ToArray(), batchVersions.ToArray());
    }

    // ---------------------------------------------------------------- INV C8: one _commit_version per batch

    [Fact]
    public async Task EachProducedBatch_CarriesExactlyOneCommitVersion()
    {
        // INV C8: a produced ColumnBatch never spans versions — AND, when a batch is MULTI-ROW, every row in it
        // shares the one _commit_version. Write MULTIPLE rows to the SAME partition file per commit so at least
        // one produced batch is genuinely multi-row (a 1-row-per-batch fixture could not exercise the invariant).
        await CreateCdfPartitionedTableAsync(PartitionedBootstrap());          // v0, v1 (partitioned ⇒ multi-file)
        // east = 2 rows ⇒ ONE 2-row file (a multi-row batch); west = 1 row ⇒ a second, single-row file.
        await AppendPartAsync(PartBatch((10, "east", 100), (11, "east", 110), (12, "west", 120))); // v2
        await AppendPartAsync(PartBatch((20, "east", 200), (21, "east", 210), (22, "west", 220))); // v3

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        (List<PartChange> rows, List<long> batchVersions) = DecodePartChanges(batches);   // asserts one version/batch

        // At least one produced batch is genuinely multi-row: the multi-row single-version invariant is exercised.
        Assert.Contains(batches, b => b.LogicalRowCount > 1);
        // INV C8 restated at the ROW level: within EVERY batch, all rows carry the SAME _commit_version.
        foreach (ColumnBatch batch in batches)
        {
            (_, ColumnVector version, _) = MetadataColumns(batch);
            long[] distinct = Enumerable.Range(0, batch.LogicalRowCount)
                .Select(r => version.GetValue<long>(r)).Distinct().ToArray();
            Assert.Single(distinct);
        }

        Assert.True(batches.Count >= 4, "expected at least one batch per partition file per version");
        Assert.All(batches, b => Assert.True(b.LogicalRowCount > 0, "no empty batch should be produced"));
        Assert.Equal(batchVersions.OrderBy(x => x).ToArray(), batchVersions.ToArray());   // ascending
        Assert.Contains(2L, batchVersions);
        Assert.Contains(3L, batchVersions);

        // Value-level: every appended row surfaces exactly once as an insert, stamped with its commit version.
        Assert.Equal(
            new[]
            {
                (10L, "east", (long?)100L, 2L), (11L, "east", (long?)110L, 2L), (12L, "west", (long?)120L, 2L),
                (20L, "east", (long?)200L, 3L), (21L, "east", (long?)210L, 3L), (22L, "west", (long?)220L, 3L),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Region, r.Val, r.Version)).ToArray());
        Assert.All(rows, r => Assert.Equal(ChangeDataWriter.InsertChange, r.ChangeType));
    }

    // ---------------------------------------------------------------- partition reconstruction: both paths

    [Fact]
    public async Task PartitionColumns_Reconstructed_OnImplicitPath()
    {
        await CreateCdfPartitionedTableAsync(PartitionedBootstrap());          // v0, v1
        long v = await AppendPartAsync(PartBatch((3, "east", 30), (4, "west", 40)));
        Assert.Equal(2L, v);

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2));
        (List<PartChange> rows, _) = DecodePartChanges(batches);
        Assert.Equal(
            new[]
            {
                (3L, "east", (long?)30L, ChangeDataWriter.InsertChange),
                (4L, "west", (long?)40L, ChangeDataWriter.InsertChange),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Region, r.Val, r.ChangeType)).ToArray());
    }

    [Fact]
    public async Task PartitionColumns_Reconstructed_OnExplicitPath()
    {
        await CreateCdfPartitionedTableAsync(PartBatch((1, "east", 10), (2, "east", 20), (3, "west", 30)));
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "part-exp").DeleteAsync(WhereId(id => id == 1));
        Assert.Equal(2L, del.CommittedVersion);

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2));
        (List<PartChange> rows, _) = DecodePartChanges(batches);
        PartChange only = Assert.Single(rows);
        Assert.Equal((1L, "east", (long?)10L, ChangeDataWriter.DeleteChange), (only.Id, only.Region, only.Val, only.ChangeType));
    }

    // ---------------------------------------------------------------- column mapping (name + id): explicit path

    [Fact]
    public async Task ColumnMapping_NameMode_ExplicitPath_MapsPhysicalToLogical()
    {
        // A name-mapped table stores its Parquet columns under PHYSICAL names; the explicit cdc read must
        // relabel them to the LOGICAL schema (inverse of the write door) and hydrate the partition column.
        await CreateNameMappedCdfPartitionedTableAsync(PartBatch((1, "east", 10), (2, "east", 20), (3, "west", 30)));
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "name-mode").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(2L, del.CommittedVersion);

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(2, 2));
        AssertCdfOutputSchema(info.Schema, PartitionedSchema);   // LOGICAL names surfaced, not physical
        (List<PartChange> rows, _) = DecodePartChanges(batches);
        PartChange only = Assert.Single(rows);
        Assert.Equal((2L, "east", (long?)20L, ChangeDataWriter.DeleteChange), (only.Id, only.Region, only.Val, only.ChangeType));
    }

    [Fact]
    public async Task ColumnMapping_IdMode_ExplicitPath_ResolvesByFieldId()
    {
        // An id-mapped table resolves data columns by Parquet field_id; the engine-synthesized _change_type
        // (no field_id) is read by NAME. The explicit read must surface the correct LOGICAL values.
        await CreateIdMappedCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "id-mode").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(2L, del.CommittedVersion);

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(2, 2));
        AssertCdfOutputSchema(info.Schema, FlatSchema);
        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);
        FlatChange only = Assert.Single(rows);
        Assert.Equal((2L, (string?)"b", ChangeDataWriter.DeleteChange, 2L), (only.Id, only.Name, only.ChangeType, only.Version));
    }

    [Fact]
    public async Task ColumnMapping_IdMode_ImplicitPath_ForeignTable_ResolvesByFieldId()
    {
        // RED-TEAM DISCRIMINATOR (#660): the sibling id-mode CDF tests above/below author their tables with
        // DeltaSharp's OWN writer, which always names a physical Parquet column == its metaData physicalName.
        // For such tables resolving a data column by FIELD-ID and resolving it by PHYSICAL NAME are
        // INDISTINGUISHABLE — so disabling field-id resolution (byFieldId = null in ParquetFileReader) leaves
        // them GREEN despite their names. This test makes field-id resolution LOAD-BEARING by hand-authoring a
        // FOREIGN id-mode table whose physical Parquet column names (z0/z1) DIVERGE from the metaData
        // physicalNames (col-A/col-B) AND are stored in the OPPOSITE order, but carry the correct footer
        // field_ids. The implicit CDF read derives `insert` rows from the foreign add(dataChange=true) by
        // reading the data file with the projection [col-A(id, field_id 1), col-B(name, field_id 2)] under
        // resolveByFieldId: true. Only field-id resolution can locate z0/z1 from that projection; a by-name
        // read looks for "col-A"/"col-B" (absent from the file) and — because logical `id` is NON-nullable —
        // fails closed with "Requested column 'col-A' is not present in the Parquet file schema" (surfaced as
        // DeltaReadSchemaEvolutionException). So this test is GREEN on real code and goes RED if field-id
        // resolution is disabled. The reversed physical order additionally defeats any positional read.
        await SeedForeignIdModeCdfFlatTableAsync();   // v0 metaData (id mode, CDF on), v1 foreign add(dataChange=true)

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(1, 1));
        AssertCdfOutputSchema(info.Schema, FlatSchema);   // LOGICAL id/name surfaced, never the physical z0/z1
        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);

        // The LOGICAL values, resolved by field_id from the divergently-named / reordered physical file.
        Assert.Equal(
            new[]
            {
                (1L, (string?)"a", ChangeDataWriter.InsertChange, 1L),
                (2L, "b", ChangeDataWriter.InsertChange, 1L),
                (3L, "c", ChangeDataWriter.InsertChange, 1L),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Name, r.ChangeType, r.Version)).ToArray());
    }

    // EXPLICIT (cdc) PATH — DEFERRED, and NOT faked (#660). The task also asked for a foreign-table field-id
    // discriminator on the EXPLICIT cdc read path. It is structurally INFEASIBLE to make such a test both GREEN
    // on real code AND RED under the byFieldId = null mutation, so NO explicit-path test is added here:
    //   * Before the explicit path resolves any data column by field_id, ChangeFeedReader validates the cdc
    //     file's decoded leaf schema against the version's log-resident metadata BY PHYSICAL NAME + leaf type
    //     (CDF-EE-08, ValidateCdcLeafSchema). A FOREIGN cdc file whose physical names DIVERGE from the metaData
    //     physicalNames (the only shape that makes field-id resolution load-bearing) is REJECTED at EE-08
    //     ("missing the version's data column 'col-A'") on BOTH real and mutated code — so it cannot
    //     discriminate the mutation (it fails identically either way, for a different reason).
    //   * If instead the cdc file's data columns are named to MATCH the metaData physicalNames (so EE-08
    //     passes), then — exactly as for a DeltaSharp-authored table — field-id and name resolution are
    //     indistinguishable, and the read stays GREEN under the mutation.
    // Hence EE-08's name-equality gate structurally precedes and subsumes the explicit path's resolveByFieldId,
    // making it non-load-bearing for a divergent-name foreign table. This is a genuine coverage gap in the CDF
    // explicit read (its field-id resolution is dead code for foreign tables), reported to the orchestrator —
    // covering it honestly needs an EE-08-relaxation or a reader change, not a hand-authored cdc file.

    // ---------------------------------------------------------------- DV-aware implicit derivation

    [Fact]
    public async Task DvAware_OverwriteAfterDvDelete_DerivesLiveRowsOnly_MaskedRowNotResurfaced()
    {
        // §2.6 implicit DV awareness: an overwrite REMOVEs a file that already carries a DV (from a prior DELETE).
        // The derived `delete` must be the LIVE rows at removal (physical \ DV) — a row already masked by the
        // prior DV (its delete already emitted at the DELETE version) must NOT re-surface as a delete here.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")));   // v0, v1
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "dv-aware").DeleteAsync(WhereId(id => id == 2));
        Assert.Equal(2L, del.CommittedVersion);                       // v2 explicit: cdc delete of id 2
        long v3 = await OverwriteFlatAsync(Batch((99, "z")));         // v3 implicit: remove(F', DV{2}) + add(G)
        Assert.Equal(3L, v3);

        // The overwrite tombstone carries the removed file's prior DV (the writer fix that makes this correct).
        RemoveFileAction remove = Assert.Single((await ReadCommitActionsAsync(backend, 3)).OfType<RemoveFileAction>());
        Assert.NotNull(remove.DeletionVector);

        // v2 (explicit): id 2 deleted exactly once, here.
        (_, List<ColumnBatch> v2Batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2));
        (List<FlatChange> v2Rows, _) = DecodeFlatChanges(v2Batches);
        FlatChange v2Only = Assert.Single(v2Rows);
        Assert.Equal((2L, ChangeDataWriter.DeleteChange, 2L), (v2Only.Id, v2Only.ChangeType, v2Only.Version));

        // v3 (implicit): derived delete = LIVE rows of the removed file = {1,3,4,5} (NOT 2), insert = {99}.
        (_, List<ColumnBatch> v3Batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(3, 3));
        (List<FlatChange> v3Rows, _) = DecodeFlatChanges(v3Batches);
        Assert.Equal(
            new[]
            {
                (1L, ChangeDataWriter.DeleteChange, 3L),
                (3L, ChangeDataWriter.DeleteChange, 3L),
                (4L, ChangeDataWriter.DeleteChange, 3L),
                (5L, ChangeDataWriter.DeleteChange, 3L),
                (99L, ChangeDataWriter.InsertChange, 3L),
            },
            v3Rows.OrderBy(r => r.ChangeType, StringComparer.Ordinal).ThenBy(r => r.Id)
                .Select(r => (r.Id, r.ChangeType, r.Version)).ToArray());
        Assert.DoesNotContain(v3Rows, r => r.Id == 2);   // the prior-DV-masked row does not re-surface

        // Snapshot-level regression (decorrelated): the overwrite correctly TOMBSTONED the DV-carrying file
        // (previously the DV-less remove key mismatched its (path, dvId) active key, leaving it spuriously
        // active). The live table is EXACTLY the overwrite's new rows.
        Assert.Equal(
            new[] { (99L, (string?)"z") },
            (await ReadLatestFlatAsync()).OrderBy(r => r.Id).Select(r => (r.Id, r.Name)).ToArray());
    }

    // ---------------------------------------------------------------- metadata-only version ⇒ empty feed

    [Fact]
    public async Task MetadataOnlyEnableCommit_YieldsEmptyChangeFeed()
    {
        // v1 (the enable commit) changes only metadata — no add/remove/cdc with dataChange — so the change feed
        // for [1,1] is empty (no rows, no batches). CDF is active at v1, so the range validates.
        await CreateCdfFlatTableAsync(Batch((1, "a")));   // v0 create, v1 enable

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(
            DeltaChangeFeedRange.FromVersion(1, 1));
        Assert.Equal(1L, info.StartVersion);
        Assert.Equal(1L, info.EndVersion);
        Assert.Empty(batches);
    }

    // ---------------------------------------------------------------- completeness + range boundedness

    [Fact]
    public async Task Explicit_MultipleCdcFilesInOneVersion_AllChangeRowsSurface()
    {
        // A DELETE that touches TWO partition files publishes TWO cdc files in one commit; the explicit read
        // must surface EVERY cdc file's rows (a mutant that read only the first would drop a partition's deletes).
        await CreateCdfPartitionedTableAsync(PartBatch((1, "east", 10), (2, "east", 20), (3, "west", 30)));
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult del = await NewCdfDelete(backend, "multi-cdc").DeleteAsync(WhereId(id => id is 1 or 3));
        Assert.Equal(2L, del.CommittedVersion);
        Assert.Equal(2, CdcFilePaths().Count);   // one cdc file per affected partition file

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2));
        (List<PartChange> rows, _) = DecodePartChanges(batches);
        Assert.Equal(
            new[]
            {
                (1L, "east", (long?)10L, ChangeDataWriter.DeleteChange),
                (3L, "west", (long?)30L, ChangeDataWriter.DeleteChange),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Region, r.Val, r.ChangeType)).ToArray());
    }

    [Fact]
    public async Task Explicit_MultiRowGroupCdcFile_StreamsBatchPerRowGroup_PreservingPerRowChangeTypeAndOneCommitVersion()
    {
        // #644: the explicit (cdc) read streams a cdc file ROW-GROUP BY ROW-GROUP (no per-file List<ColumnBatch>
        // buffer), symmetric with the implicit path. A cdc file spanning MULTIPLE row groups must yield MULTIPLE
        // batches — each one row group of one version, carrying its OWN per-row `_change_type` and exactly ONE
        // `_commit_version` (INV C8) — and the concatenation must equal the change rows IN ORDER. We author the
        // cdc body directly (3 row groups × 2 rows, all four `_change_type` tokens) so the row-group boundaries
        // are pinned, overwriting the DELETE-produced cdc file v2 already committed (the explicit read reads
        // EXACTLY the cdc file's rows — precedence — so the authored content is what surfaces).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0 create, v1 enable CDF
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "mrg").DeleteAsync(WhereId(id => id == 1));   // v2 cdc delete (explicit path)
        string cdc = Assert.Single(CdcFilePaths());

        byte[] multiRowGroup = await ParquetTestHelpers.WriteToBytesAsync(
            CdcFlatBodySchema,
            new[]
            {
                CdcFlatBodyBatch(
                    (10, "r0a", ChangeDataWriter.InsertChange),
                    (11, "r0b", ChangeDataWriter.DeleteChange),
                    (12, "r1a", ChangeDataWriter.UpdatePreimageChange),
                    (13, "r1b", ChangeDataWriter.UpdatePostimageChange),
                    (14, "r2a", ChangeDataWriter.InsertChange),
                    (15, null, ChangeDataWriter.DeleteChange)),
            },
            rowGroupRowLimit: 2);   // 6 rows ⇒ 3 row groups of 2
        await File.WriteAllBytesAsync(Path.Combine(_root, cdc), multiRowGroup);

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2));

        // Streamed, NOT buffered-into-one: 3 row groups ⇒ 3 batches (a mutant that buffered the file yields 1).
        Assert.True(batches.Count > 1, "a multi-row-group cdc file must stream more than one batch");
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Equal(2, b.LogicalRowCount));   // one row group (2 rows) per batch

        // DecodeFlatChanges asserts, per batch: exactly ONE `_commit_version` (INV C8) + a valid `_change_type`.
        (List<FlatChange> rows, List<long> batchVersions) = DecodeFlatChanges(batches);
        Assert.Equal(new[] { 2L, 2L, 2L }, batchVersions.ToArray());   // every batch stamped with v2 (one version)

        // Per-row `_change_type` alignment + exact ORDER across batches: the concatenation equals the change rows.
        Assert.Equal(
            new (long, string?, string, long)[]
            {
                (10L, "r0a", ChangeDataWriter.InsertChange, 2L),
                (11L, "r0b", ChangeDataWriter.DeleteChange, 2L),
                (12L, "r1a", ChangeDataWriter.UpdatePreimageChange, 2L),
                (13L, "r1b", ChangeDataWriter.UpdatePostimageChange, 2L),
                (14L, "r2a", ChangeDataWriter.InsertChange, 2L),
                (15L, null, ChangeDataWriter.DeleteChange, 2L),
            },
            rows.Select(r => (r.Id, r.Name, r.ChangeType, r.Version)).ToArray());

        // Positive row-count consistency: the streamed data rows exactly equal the `_change_type` count (6).
        Assert.Equal(6, rows.Count);
    }

    [Fact]
    public async Task Explicit_MultiRowGroupCdcFile_CorruptLaterRowGroup_StreamsEarlierBatchThenFailsClosed()
    {
        // #644 streaming fail-closed: because the explicit read now streams row-group by row-group, error
        // classification must sit AROUND the yield (never across a `yield return`). A cdc file whose LATER row
        // group is corrupt must still stream its earlier well-formed batch and THEN fail closed as a classified
        // CorruptData DeltaReadException — never a partial/torn batch, and never a buffer-then-fail that would
        // hide the earlier rows. This is the reachable "data pass disagrees with `_change_type`" inconsistency:
        // the `offset != changeTypes.Length` totals check is defence in depth (for a DECODABLE single Parquet
        // file both passes read identical per-row-group row counts, so they cannot disagree on a clean file),
        // while a corrupt data-column chunk in a later row group makes the data pass fail after `_change_type`
        // (pass 1, a different column) has already been fully read.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0, v1
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "mrg-corrupt").DeleteAsync(WhereId(id => id == 1));   // v2 cdc delete
        string cdc = Assert.Single(CdcFilePaths());

        byte[] file = await ParquetTestHelpers.WriteToBytesAsync(
            CdcFlatBodySchema,
            new[]
            {
                CdcFlatBodyBatch(
                    (10, "r0a", ChangeDataWriter.InsertChange),
                    (11, "r0b", ChangeDataWriter.DeleteChange),
                    (12, "r1a", ChangeDataWriter.InsertChange),
                    (13, "r1b", ChangeDataWriter.DeleteChange)),
            },
            rowGroupRowLimit: 2);   // 2 row groups of 2
        // Poison row group 1's `id` (data column 0). Pass 1 reads `_change_type` (column 2) intact; the STREAMED
        // data pass decodes row group 0 (yields a batch) then faults on row group 1 → CorruptData, classified.
        byte[] poisoned = await ParquetTestHelpers.PoisonColumnChunkAsync(file, rowGroup: 1, columnIndex: 0);
        await File.WriteAllBytesAsync(Path.Combine(_root, cdc), poisoned);

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(
            DeltaChangeFeedRange.FromVersion(2, 2), CancellationToken.None);
        IAsyncEnumerator<ColumnBatch> enumerator =
            source.ReadChangeBatchesAsync(info, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            // Row group 0 streams COMPLETE first (proves streaming, not buffer-then-fail): a full, correct batch.
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(2, enumerator.Current.LogicalRowCount);
            ColumnVector id0 = enumerator.Current.SelectedColumn(0);
            Assert.Equal(new[] { 10L, 11L }, new[] { id0.GetValue<long>(0), id0.GetValue<long>(1) });

            // Advancing into the corrupt row group fails closed: classified DeltaReadException wrapping the
            // CorruptData storage fault (distinct from the vacuumed NotFound case) — never a partial batch.
            DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
                async () => await enumerator.MoveNextAsync());
            DeltaStorageException inner = Assert.IsType<DeltaStorageException>(ex.InnerException);
            Assert.Equal(StorageErrorKind.CorruptData, inner.Kind);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Explicit_CountInconsistentCdcFile_FailsClosedBeforeFirstYield_ForEarlyBreakConsumer()
    {
        // #644 red-team (High): the streaming refactor must NOT weaken the pre-#644 BUFFERED form's fail-closed
        // guarantee. The buffered form read the WHOLE cdc file (the `_change_type` pass AND the full data pass)
        // before returning, so the `_change_type`-vs-data COUNT-consistency check ran BEFORE any batch surfaced
        // — even an early-break consumer of the public `ReadChangeBatchesAsync` (an IAsyncEnumerable) got the
        // fail-closed error on a count-inconsistent cdc file. Streaming defers the post-loop too-few check to
        // FULL enumeration, so WITHOUT the eager footer-count check an external early-break consumer would
        // receive a batch and bypass the check (a fail-closed regression). A single well-formed Parquet file
        // ALWAYS has matching footer + `_change_type` counts (both derive from the same per-row-group row
        // counts), so we reproduce the red-team's TOCTOU: a decorating backend serves file A (3 `_change_type`
        // values) for the EE-08 schema + `_change_type` opens, then file B (2 rows) for the eager footer probe —
        // the cdc file "atomically replaced" between the passes. Because the CDF read path is wired to sealed
        // concrete backends, this decorator (via ChangeFeedReader's internal DI ctor) is the deterministic way
        // to drive the mismatch. The eager check (footer 2 != change-types 3) must throw on the FIRST
        // MoveNextAsync, BEFORE any batch is yielded.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0 create, v1 enable CDF
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "toctou").DeleteAsync(WhereId(id => id == 1));   // v2 cdc delete (explicit path)
        Assert.Single(CdcFilePaths());

        // File A: 3 rows / 3 valid `_change_type` values — the `_change_type` pass reads N = 3.
        byte[] fileA = await ParquetTestHelpers.WriteToBytesAsync(
            CdcFlatBodySchema,
            new[]
            {
                CdcFlatBodyBatch(
                    (10, "a", ChangeDataWriter.DeleteChange),
                    (11, "b", ChangeDataWriter.DeleteChange),
                    (12, "c", ChangeDataWriter.DeleteChange)),
            });
        // File B: 2 rows — the eager FOOTER probe reads M = 2 (!= 3). Same leaf schema so EE-08 is agnostic to it.
        byte[] fileB = await ParquetTestHelpers.WriteToBytesAsync(
            CdcFlatBodySchema,
            new[]
            {
                CdcFlatBodyBatch(
                    (10, "a", ChangeDataWriter.DeleteChange),
                    (11, "b", ChangeDataWriter.DeleteChange)),
            });

        // Serve A for the first 2 cdc opens (EE-08 schema validation, then the `_change_type` pass), B for the
        // rest (the eager footer probe, and — if the check were missing — the data pass). Drive the SAME
        // ChangeFeedReader for resolve + read via the internal DI ctor so the decorating backend serves the cdc
        // file bytes; the log (DeltaLog) still reads the real backend, so resolve is unaffected.
        var toctou = new CountMismatchCdcBackend(backend, fileA, fileB, serveAForFirstOpens: 2);
        var reader = new ChangeFeedReader(toctou, backend.TableRootId, new DeltaLog(backend), new ParquetFileReader());

        DeltaChangeFeedInfo info = await reader.ResolveAsync(
            DeltaChangeFeedRange.FromVersion(2, 2), CancellationToken.None);
        Assert.Empty(toctou.ServedCdcOpens);   // resolve reads ONLY the log — no cdc-file open yet

        IAsyncEnumerator<ColumnBatch> enumerator = reader.ReadAsync(info, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            // Fail closed on the FIRST MoveNextAsync — BEFORE any batch is yielded. An early-break consumer (one
            // that would `break` after the first batch) therefore can NEVER receive a batch on a count-
            // inconsistent cdc file: it gets the classified fail-closed error instead of a (bypassing) batch.
            DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
                async () => await enumerator.MoveNextAsync());
            Assert.Contains("produced 2 data row(s) but has 3", ex.Message, StringComparison.Ordinal);
            Assert.Contains("inconsistent", ex.Message, StringComparison.Ordinal);
            // The eager COUNT check throws a bare DeltaReadException (no wrapped I/O fault) — distinct from the
            // torn/corrupt-body CorruptData case whose InnerException is a DeltaStorageException.
            Assert.Null(ex.InnerException);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // The eager check fired at the FOOTER probe (open 3 = "B") BEFORE the data pass (which would be open 4):
        // the cdc-file opens are EXACTLY A (EE-08), A (`_change_type`), B (footer). A missing eager check would
        // have advanced to the data pass (a 4th "B" open) and yielded a batch to the early-break consumer.
        Assert.Equal(new[] { "A", "A", "B" }, toctou.ServedCdcOpens.ToArray());
    }

    [Fact]
    public async Task RangeIsInclusiveAndBounded_ReadsOnlyRequestedVersions()
    {
        // Two cdc-bearing versions; reading only [3,3] must surface ONLY v3's change (id 3), never v2's (id 1) —
        // the reader replays exactly the requested inclusive range (a boundedness/off-by-one mutant fails here).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b"), (3, "c")));   // v0, v1
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "range-v2").DeleteAsync(WhereId(id => id == 1));   // v2 cdc delete 1
        DeleteResult v3 = await NewCdfDelete(backend, "range-v3").DeleteAsync(WhereId(id => id == 3)); // v3 cdc delete 3
        Assert.Equal(3L, v3.CommittedVersion);

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(3, 3));
        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);
        FlatChange only = Assert.Single(rows);
        Assert.Equal((3L, ChangeDataWriter.DeleteChange, 3L), (only.Id, only.ChangeType, only.Version));
    }

    // ---------------------------------------------------------------- §2.8 cross-range schema reconciliation

    [Fact]
    public async Task AddNullableColumnAcrossRange_EarlierRowsNullFillAtEndSchema()
    {
        // HP-07 / §2.8: enable CDF → append narrow (v2) → ALTER ADD nullable COLUMN via mergeSchema (v3).
        // Reading [v2..end] reconciles to the END schema; the v2 rows (written before `extra` existed) MUST
        // null-fill `extra` (schema-on-read), while the v3 row carries its value — a mutant that reads each
        // version against its OWN schema (dropping `extra` for v2) or fails to null-fill would break here.
        await CreateCdfFlatTableAsync(Batch((1, "a")));                          // v0, v1 (CDF on)
        long va = await AppendFlatAsync(Batch((10, "x"), (11, "y")));            // v2: narrow (id, name)
        Assert.Equal(2L, va);
        long vb = await AppendMergeAsync(WideFlatSchema, Array.Empty<string>(),
            WideBatch((20, "z", 200L)));                                        // v3: adds nullable `extra`
        Assert.Equal(3L, vb);

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) =
            await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        AssertCdfOutputSchema(info.Schema, WideFlatSchema);                     // reconciled END schema (id,name,extra)

        Assert.Equal(
            new[]
            {
                (10L, (string?)"x", (long?)null, ChangeDataWriter.InsertChange, 2L),  // v2 rows null-fill `extra`
                (11L, "y", (long?)null, ChangeDataWriter.InsertChange, 2L),
                (20L, "z", (long?)200L, ChangeDataWriter.InsertChange, 3L),           // v3 row carries `extra`
            },
            DecodeWideChanges(batches).OrderBy(r => r.Id).ToArray());
    }

    [Fact]
    public async Task IntToLongWideningAcrossRange_EarlierNarrowValuesPromote()
    {
        // §2.8 type-widening reconciliation: an int→long widening between v_a and v_b. Reading [v_a..v_b] against
        // the reconciled END (long) schema promotes the earlier narrow int values to long. A value that only
        // fits in long (> int.MaxValue) proves the column is genuinely widened (not truncated to 32 bits).
        await CreateWideningCdfTableAsync(IntAmountBatch((1, 1)));               // v0 seed, v1 CDF, v2 typeWidening
        long va = await AppendIntAsync(IntAmountBatch((10, 100), (11, 110)));    // v3: narrow int append (CDF on)
        Assert.Equal(3L, va);
        long vb = await AppendMergeAsync(WideningLongSchema, Array.Empty<string>(),
            LongAmountBatch((20, 3_000_000_000L)));                             // v4: widen amount int→long
        Assert.Equal(4L, vb);

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) =
            await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(3, 4));
        AssertCdfOutputSchema(info.Schema, WideningLongSchema);                 // reconciled END schema: amount = long

        Assert.Equal(
            new[]
            {
                (10L, (long?)100L, ChangeDataWriter.InsertChange, 3L),          // narrow int values promoted to long
                (11L, (long?)110L, ChangeDataWriter.InsertChange, 3L),
                (20L, (long?)3_000_000_000L, ChangeDataWriter.InsertChange, 4L), // > int.MaxValue ⇒ genuinely long
            },
            DecodeAmountChanges(batches).OrderBy(r => r.Id).ToArray());
    }

    [Fact]
    public async Task NameModeRenameAcrossRange_EarlierRowsSurfaceUnderRenamedColumn()
    {
        // §2.8 rename reconciliation (name mode): rename `val`→`amount` (v3) AFTER an append (v2). Name mode keeps
        // a STABLE physical name across the logical rename, so the v2 rows (physical `val`) resolve to the END
        // logical `amount` — surfacing under the renamed column. The rename commit itself is metadata-only and
        // contributes ZERO change rows. (RenameColumnAsync requires NAME mode — the write door rejects a metadata
        // rename in id mode — so this exercises rename-reconciliation in name mode; the id-mode field-id-across-
        // range property is covered by IdModeAcrossRange_ResolvesByFieldId.)
        await CreateNameMappedCdfPartitionedTableAsync(PartBatch((1, "east", 1)));       // v0, v1 (name mode, CDF on)
        long va = await AppendPartAsync(PartBatch((10, "east", 100), (11, "west", 110)));// v2 (physical `val`)
        Assert.Equal(2L, va);
        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).RenameColumnAsync("val", "amount"); // v3

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) =
            await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        Assert.Equal("amount", info.Schema[2].Name);   // END schema's data column 2 is the RENAMED `amount`

        (List<PartChange> rows, List<long> batchVersions) = DecodePartChanges(batches);
        Assert.Equal(
            new[]
            {
                (10L, "east", (long?)100L, ChangeDataWriter.InsertChange, 2L),  // pre-rename rows under `amount`
                (11L, "west", (long?)110L, ChangeDataWriter.InsertChange, 2L),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.Region, r.Val, r.ChangeType, r.Version)).ToArray());
        Assert.DoesNotContain(3L, batchVersions);       // the rename commit contributes NO change rows
    }

    [Fact]
    public async Task IdModeAcrossRange_ResolvesByFieldId_AndNullFillsAddedColumn()
    {
        // §2.8 id-mode reconciliation: in ID mapping, columns resolve by FIELD-ID (not physical/logical name).
        // Append (v2) then ADD a nullable column via mergeSchema (v3, fresh field-id). Reading [v2..v3] resolves
        // the v2 file's id/name by field-id against the END schema and null-fills the added column — a mutant
        // resolving by name (or failing to null-fill the field-id-only-present column) would break here.
        await CreateIdMappedCdfFlatTableAsync(Batch((1, "a")));                 // v0, v1 (id mode, CDF on)
        long va = await AppendFlatAsync(Batch((10, "x"), (11, "y")));           // v2 (id, name)
        Assert.Equal(2L, va);
        long vb = await AppendMergeAsync(WideFlatSchema, Array.Empty<string>(),
            WideBatch((20, "z", 200L)));                                       // v3 (adds nullable `extra`, fresh id)
        Assert.Equal(3L, vb);

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) =
            await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        AssertCdfOutputSchema(info.Schema, WideFlatSchema);

        Assert.Equal(
            new[]
            {
                (10L, (string?)"x", (long?)null, ChangeDataWriter.InsertChange, 2L),  // resolved by field-id, null-fill
                (11L, "y", (long?)null, ChangeDataWriter.InsertChange, 2L),
                (20L, "z", (long?)200L, ChangeDataWriter.InsertChange, 3L),
            },
            DecodeWideChanges(batches).OrderBy(r => r.Id).ToArray());
    }

    // ---------------------------------------------------------------- fail-closed: torn / absent / OPTIMIZE

    [Fact]
    public async Task TornDataFile_InRange_FailsClosedAsCorruptData()
    {
        // A resolved in-range DATA file truncated mid-body (footer lost) fails closed as a classified
        // DeltaReadException carrying CorruptData — NOT a partial/garbage batch — and DISTINCT from the
        // vacuumed NotFound case (VacuumedDataFile_FailsClosedAtRead), which carries StorageErrorKind.NotFound.
        await CreateCdfFlatTableAsync(Batch((1, "a")));    // v0, v1
        await AppendFlatAsync(Batch((10, "x")));           // v2 add(dataChange) — implicit insert source
        var backend = new LocalFileSystemBackend(_root);
        AddFileAction add = Assert.Single((await ReadCommitActionsAsync(backend, 2)).OfType<AddFileAction>());
        TruncateInHalf(Path.Combine(_root, add.Path));     // torn mid-body

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            async () => await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2)));
        DeltaStorageException inner = Assert.IsType<DeltaStorageException>(ex.InnerException);
        Assert.Equal(StorageErrorKind.CorruptData, inner.Kind);   // classified corruption, distinct from NotFound
    }

    [Fact]
    public async Task TornCdcFile_InRange_FailsClosedAsCorruptData()
    {
        // A resolved in-range CDC file truncated mid-body fails closed the same way on the EXPLICIT path (the
        // per-version cdc read opens the torn file and classifies CorruptData) — never a partial batch.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0, v1
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "torn-cdc").DeleteAsync(WhereId(id => id == 1));   // v2 cdc delete
        string cdc = Assert.Single(CdcFilePaths());
        TruncateInHalf(Path.Combine(_root, cdc));

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            async () => await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2)));
        DeltaStorageException inner = Assert.IsType<DeltaStorageException>(ex.InnerException);
        Assert.Equal(StorageErrorKind.CorruptData, inner.Kind);
    }

    [Fact]
    public async Task CdcFileSchemaInconsistentWithVersionMetadata_FailsClosed()
    {
        // EE-08 (§3.2, AC1): the explicit path validates each cdc file's data-column leaf schema against THAT
        // version's log-resident metadata (the trusted authority) BEFORE yielding any row, failing closed on a
        // missing/extra data column or a leaf-type disagreement. Here v2's cdc file is overwritten with one whose
        // `id` column is STRING (the metadata declares long): a leaf-type mismatch ⇒ a classified DeltaRead
        // Exception with NO inner storage fault (distinct from the torn-body CorruptData case). `_change_type`
        // carries a valid domain token so ONLY the data-column check can fire (isolating EE-08 from the separate
        // _change_type domain validation).
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0, v1
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "ee08").DeleteAsync(WhereId(id => id == 1));   // v2 cdc delete (explicit path)
        string cdc = Assert.Single(CdcFilePaths());
        byte[] mismatch = await ParquetTestHelpers.WriteToBytesAsync(
            CdcTypeMismatchSchema, new[] { CdcTypeMismatchBatch(("1", "a", ChangeDataWriter.DeleteChange)) });
        await File.WriteAllBytesAsync(Path.Combine(_root, cdc), mismatch);

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            async () => await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2)));
        Assert.Null(ex.InnerException);   // a pure per-version schema-validation failure, not a wrapped I/O fault
        Assert.Contains("id", ex.Message, StringComparison.Ordinal);         // names the offending data column
        Assert.Contains("CDF-EE-08", ex.Message, StringComparison.Ordinal);  // the per-version cdc schema check
    }

    [Fact]
    public async Task Explicit_CdcConsistentWithEachVersionSchema_PassesAcrossEvolutionBoundary()
    {
        // EE-08 discrimination — POSITIVE (§3.2 AC1 + §2.8 cross-range reconciliation): an EXPLICIT (cdc) range
        // that CROSSES a schema-evolution boundary. v2 is a DELETE under schema A (id, name) ⇒ genuine cdc(A);
        // v3 EVOLVES the schema to B (id, name, extra) via mergeSchema (+ insert). Reading [2,3] (END schema = B)
        // must (1) PASS the per-version EE-08 gate for v2's cdc — it validates against v2's OWN metadata A, NOT
        // the reconciled END schema B — and (2) reconcile v2's narrow cdc(A) row to B, null-filling `extra`.
        // This proves EE-08 does NOT false-positive across an evolution boundary AND that the EXPLICIT path
        // reconciles a per-version-narrow cdc file to the end schema. (A mutant validating the cdc against the
        // END schema would REJECT v2's cdc for a MISSING `extra` ⇒ this test goes RED under that mutant.)
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0, v1 (schema A = id, name; CDF on)
        var backend = new LocalFileSystemBackend(_root);
        DeleteResult d2 = await NewCdfDelete(backend, "ee08pos").DeleteAsync(WhereId(id => id == 1)); // v2 cdc(A)
        Assert.Equal(2L, d2.CommittedVersion);
        long v3 = await AppendMergeAsync(WideFlatSchema, Array.Empty<string>(),
            WideBatch((10, "x", 100L), (11, "y", 110L)));           // v3 evolve A→B + insert
        Assert.Equal(3L, v3);

        (DeltaChangeFeedInfo info, List<ColumnBatch> batches) =
            await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        AssertCdfOutputSchema(info.Schema, WideFlatSchema);         // reconciled END schema (id, name, extra)

        Assert.Equal(
            new[]
            {
                (1L, (string?)"a", (long?)null, ChangeDataWriter.DeleteChange, 2L),  // v2 cdc(A) null-fills `extra` at B
                (10L, "x", (long?)100L, ChangeDataWriter.InsertChange, 3L),
                (11L, "y", (long?)110L, ChangeDataWriter.InsertChange, 3L),
            },
            DecodeWideChanges(batches).OrderBy(r => r.Version).ThenBy(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Explicit_CdcMatchingEndSchemaButNotItsVersion_FailsClosed()
    {
        // EE-08 discrimination — NEGATIVE (§3.2 AC1): the case that DISCRIMINATES per-version from end-version
        // cdc schema validation. v2 is a DELETE under schema A (id, name); v3 EVOLVES to B (id, name, extra).
        // v2's cdc file is then OVERWRITTEN with one whose leaf schema matches the reconciled END schema B (it
        // carries an `extra` column A never had) but NOT v2's OWN metadata A. Reading [2,3] must FAIL CLOSED
        // with the CDF-EE-08 marker: DeltaSharp validates each cdc file against THAT version's metadata (A),
        // where `extra` is a column absent from the version's schema. A mutant validating against the END schema
        // B would ACCEPT this file (it matches B) and surface an attacker-chosen row ⇒ this test goes RED under
        // that mutant — whereas the single-version EE-08 test (its END schema == the version's own schema)
        // CANNOT catch it.
        await CreateCdfFlatTableAsync(Batch((1, "a"), (2, "b")));   // v0, v1 (schema A)
        var backend = new LocalFileSystemBackend(_root);
        await NewCdfDelete(backend, "ee08endneg").DeleteAsync(WhereId(id => id == 1));   // v2 cdc(A) delete
        string cdc = Assert.Single(CdcFilePaths());
        long v3 = await AppendMergeAsync(WideFlatSchema, Array.Empty<string>(),
            WideBatch((10, "x", 100L)));                            // v3 evolve A→B (END schema)
        Assert.Equal(3L, v3);

        // Overwrite v2's cdc with a file whose data columns match END schema B (id, name, extra) — consistent
        // with B but NOT with v2's A (which has no `extra`). `id` stays long so the ONLY inconsistency is the
        // extra `extra` column (a clean discriminator, never a leaf-type mismatch).
        byte[] endSchemaCdc = await ParquetTestHelpers.WriteToBytesAsync(
            CdcMatchesEndSchema, new[] { CdcMatchesEndBatch((1L, "a", 1L, ChangeDataWriter.DeleteChange)) });
        await File.WriteAllBytesAsync(Path.Combine(_root, cdc), endSchemaCdc);

        DeltaReadException ex = await Assert.ThrowsAsync<DeltaReadException>(
            async () => await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 3)));
        Assert.Null(ex.InnerException);   // a pure per-version schema-validation failure, not a wrapped I/O fault
        Assert.Contains("extra", ex.Message, StringComparison.Ordinal);      // names the offending end-only column
        Assert.Contains("CDF-EE-08", ex.Message, StringComparison.Ordinal);  // the per-version cdc schema check
    }

    [Fact]
    public async Task AbsentRequiredColumn_InRangeFile_FailsClosedAsSchemaEvolution()
    {
        // EE-05: a required (non-nullable) data column ABSENT from an in-range file fails closed as
        // DeltaReadSchemaEvolutionException (never a silent null-fill of a required column, never masked as
        // corruption). The narrow file is manufactured by overwriting the appended data file with one that
        // physically drops the required `id` — reached on the IMPLICIT path (no cdc), where the reader's
        // ColumnNotPresentInFile sentinel maps to the typed evolution exception.
        await CreateCdfFlatTableAsync(Batch((1, "a")));    // v0, v1
        await AppendFlatAsync(Batch((10, "x")));           // v2 add(dataChange)
        var backend = new LocalFileSystemBackend(_root);
        AddFileAction add = Assert.Single((await ReadCommitActionsAsync(backend, 2)).OfType<AddFileAction>());
        byte[] narrow = await ParquetTestHelpers.WriteToBytesAsync(NarrowNameSchema, new[] { NameOnlyBatch("x") });
        await File.WriteAllBytesAsync(Path.Combine(_root, add.Path), narrow);   // physically drop required `id`

        await Assert.ThrowsAsync<DeltaReadSchemaEvolutionException>(
            async () => await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 2)));
    }

    [Fact]
    public async Task OptimizeCommit_InRange_ContributesNoChangeRows()
    {
        // INV C4: an OPTIMIZE commit (dataChange=false add + dataChange=false removes) is INVISIBLE to CDF — it
        // rewrites file layout without changing table content, so it derives ZERO change rows. Reading a range
        // that SPANS the OPTIMIZE version surfaces only the (dataChange=true) append rows — a mutant that
        // derived inserts from the compacted add (or deletes from the compaction removes) would double-count.
        await CreateCdfFlatTableAsync(Batch((1, "a")));    // v0, v1
        await AppendFlatAsync(Batch((10, "x")));           // v2 (small file 1)
        await AppendFlatAsync(Batch((11, "y")));           // v3 (small file 2)
        await AppendFlatAsync(Batch((12, "z")));           // v4 (small file 3)
        var backend = new LocalFileSystemBackend(_root);
        OptimizeResult opt = await new DeltaOptimize(backend).OptimizeAsync();   // v5: compaction (dataChange=false)
        Assert.Equal(5L, opt.CommittedVersion);

        // The OPTIMIZE commit's file actions are ALL dataChange=false, and it genuinely compacted (≥1 add).
        IReadOnlyList<DeltaAction> v5 = await ReadCommitActionsAsync(backend, 5);
        Assert.NotEmpty(v5.OfType<AddFileAction>());
        Assert.All(v5.OfType<AddFileAction>(), a => Assert.False(a.DataChange));
        Assert.All(v5.OfType<RemoveFileAction>(), r => Assert.False(r.DataChange));

        (_, List<ColumnBatch> batches) = await ReadCdfBatchesAsync(DeltaChangeFeedRange.FromVersion(2, 5));
        (List<FlatChange> rows, List<long> batchVersions) = DecodeFlatChanges(batches);

        Assert.DoesNotContain(5L, batchVersions);          // OPTIMIZE (v5) produced NO batch at all
        Assert.Equal(
            new[]
            {
                (10L, ChangeDataWriter.InsertChange, 2L),
                (11L, ChangeDataWriter.InsertChange, 3L),
                (12L, ChangeDataWriter.InsertChange, 4L),
            },
            rows.OrderBy(r => r.Id).Select(r => (r.Id, r.ChangeType, r.Version)).ToArray());
    }

    // ---------------------------------------------------------------- resolve-time pinning + cancellation

    [Fact]
    public async Task PinnedRange_IgnoresCommitsAfterResolve()
    {
        // The range is resolved (and its end pinned to the then-latest) ONCE at LoadChangeFeedAsync. A commit
        // that lands AFTER resolve but BEFORE the streaming read must NOT appear: ReadChangeBatchesAsync replays
        // exactly the pinned [start,end] (analysis→execution TOCTOU safety, mirroring snapshot pinning).
        await CreateCdfFlatTableAsync(Batch((1, "a")));    // v0, v1
        await AppendFlatAsync(Batch((10, "x")));           // v2 (the only version at resolve time)

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(
            DeltaChangeFeedRange.FromVersion(2), CancellationToken.None);   // end omitted ⇒ pinned to latest (2)
        Assert.Equal(2L, info.EndVersion);

        await AppendFlatAsync(Batch((20, "y")));           // v3 committed AFTER resolve — must be ignored

        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, CancellationToken.None))
        {
            batches.Add(batch);
        }

        (List<FlatChange> rows, _) = DecodeFlatChanges(batches);
        FlatChange only = Assert.Single(rows);             // ONLY v2's insert; v3 is outside the pinned range
        Assert.Equal((10L, ChangeDataWriter.InsertChange, 2L), (only.Id, only.ChangeType, only.Version));
    }

    [Fact]
    public async Task Cancellation_AfterFirstBatch_StopsTheStream()
    {
        // The streaming read honors the CancellationToken: cancelling after the first yielded batch surfaces an
        // OperationCanceledException on the NEXT version's iteration (proves [EnumeratorCancellation] propagation
        // and the per-version cancellation checkpoint). Two single-batch versions guarantee a next iteration.
        await CreateCdfFlatTableAsync(Batch((1, "a")));    // v0, v1
        await AppendFlatAsync(Batch((10, "x")));           // v2 (batch 1)
        await AppendFlatAsync(Batch((20, "y")));           // v3 (batch 2)

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(
            DeltaChangeFeedRange.FromVersion(2, 3), CancellationToken.None);
        using var cts = new CancellationTokenSource();

        int produced = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, cts.Token))
            {
                produced++;
                cts.Cancel();   // after the first batch; the next version's iteration must observe cancellation
            }
        });
        Assert.Equal(1, produced);   // exactly one batch surfaced before cancellation was observed
    }

    // ================================================================ helpers

    private readonly record struct FlatChange(long Version, long TsMicros, string ChangeType, long Id, string? Name);
    private readonly record struct PartChange(
        long Version, long TsMicros, string ChangeType, long Id, string Region, long? Val);

    private async Task<DeltaChangeFeedInfo> ResolveCdfAsync(DeltaChangeFeedRange range)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        return await source.LoadChangeFeedAsync(range, CancellationToken.None);
    }

    // Resolves the range ONCE, then materializes the streaming IAsyncEnumerable<ColumnBatch> so both the
    // resolution-time and read-time (streaming) failure surfaces are exercised through the public door.
    private async Task<(DeltaChangeFeedInfo Info, List<ColumnBatch> Batches)> ReadCdfBatchesAsync(
        DeltaChangeFeedRange range)
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaChangeFeedInfo info = await source.LoadChangeFeedAsync(range, CancellationToken.None);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(info, CancellationToken.None))
        {
            batches.Add(batch);
        }

        return (info, batches);
    }

    // Asserts a forged/unvalidated/cross-source info is rejected fail-closed by the read door: the classified
    // ArgumentException (param `info`, its message containing <paramref name="expectedMessageSubstring"/>)
    // surfaces WITHOUT any batch being yielded — so such an info can never surface change rows.
    private static async Task AssertForgedInfoFailsClosedAsync(
        DeltaReadSource source, DeltaChangeFeedInfo forged, string expectedMessageSubstring = "LoadChangeFeedAsync")
    {
        int yielded = 0;
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (ColumnBatch batch in source.ReadChangeBatchesAsync(forged, CancellationToken.None))
            {
                yielded++;
            }
        });

        Assert.Equal("info", ex.ParamName);
        Assert.Contains(expectedMessageSubstring, ex.Message);
        Assert.Equal(0, yielded);   // fail-closed BEFORE any batch is produced
    }

    // Decodes flat change rows AND asserts, per batch: exactly ONE _commit_version (INV C8), non-null
    // _change_type/_commit_version/_commit_timestamp, and a valid _change_type token.
    private static (List<FlatChange> Rows, List<long> BatchVersions) DecodeFlatChanges(List<ColumnBatch> batches)
    {
        var rows = new List<FlatChange>();
        var batchVersions = new List<long>();
        foreach (ColumnBatch batch in batches)
        {
            int n = batch.ColumnCount;
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            (ColumnVector changeType, ColumnVector version, ColumnVector ts) = MetadataColumns(batch);
            long? single = null;
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                long v = AssertMetadataRow(changeType, version, ts, r, ref single);
                rows.Add(new FlatChange(v, ts.GetValue<long>(r), Utf8(changeType, r),
                    id.GetValue<long>(r), name.IsNull(r) ? null : Utf8(name, r)));
            }

            RecordBatchVersion(batch, single, batchVersions);
            Assert.Equal(FlatSchema.Count + 3, n);
        }

        return (rows, batchVersions);
    }

    private static (List<PartChange> Rows, List<long> BatchVersions) DecodePartChanges(List<ColumnBatch> batches)
    {
        var rows = new List<PartChange>();
        var batchVersions = new List<long>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector region = batch.SelectedColumn(1);
            ColumnVector val = batch.SelectedColumn(2);
            (ColumnVector changeType, ColumnVector version, ColumnVector ts) = MetadataColumns(batch);
            long? single = null;
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                long v = AssertMetadataRow(changeType, version, ts, r, ref single);
                Assert.False(region.IsNull(r), "partition column must be reconstructed (never null-dropped)");
                rows.Add(new PartChange(v, ts.GetValue<long>(r), Utf8(changeType, r),
                    id.GetValue<long>(r), Utf8(region, r), val.IsNull(r) ? null : val.GetValue<long>(r)));
            }

            RecordBatchVersion(batch, single, batchVersions);
        }

        return (rows, batchVersions);
    }

    // The three engine-synthesized metadata columns are always the LAST three, in order
    // _change_type / _commit_version / _commit_timestamp (design §2.4).
    private static (ColumnVector ChangeType, ColumnVector Version, ColumnVector Timestamp) MetadataColumns(
        ColumnBatch batch)
    {
        int n = batch.ColumnCount;
        return (batch.SelectedColumn(n - 3), batch.SelectedColumn(n - 2), batch.SelectedColumn(n - 1));
    }

    private static long AssertMetadataRow(
        ColumnVector changeType, ColumnVector version, ColumnVector timestamp, int r, ref long? single)
    {
        Assert.False(changeType.IsNull(r), "_change_type must never be null");
        Assert.False(version.IsNull(r), "_commit_version must never be null");
        Assert.False(timestamp.IsNull(r), "_commit_timestamp must never be null");
        Assert.True(
            ChangeDataWriter.ChangeTypeDomain.Contains(Utf8(changeType, r)),
            "_change_type must be a valid change-type token");

        long v = version.GetValue<long>(r);
        if (single is null)
        {
            single = v;
        }
        else
        {
            Assert.Equal(single.Value, v);   // INV C8: exactly one _commit_version per batch
        }

        return v;
    }

    private static void RecordBatchVersion(ColumnBatch batch, long? single, List<long> batchVersions)
    {
        if (batch.LogicalRowCount > 0)
        {
            batchVersions.Add(single!.Value);
        }
    }

    private static void AssertCdfOutputSchema(StructType schema, StructType dataSchema)
    {
        Assert.Equal(dataSchema.Count + 3, schema.Count);
        for (int i = 0; i < dataSchema.Count; i++)
        {
            Assert.Equal(dataSchema[i].Name, schema[i].Name);
            Assert.Equal(dataSchema[i].DataType, schema[i].DataType);
            Assert.Equal(dataSchema[i].Nullable, schema[i].Nullable);
        }

        AssertField(schema[dataSchema.Count], ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType);
        AssertField(schema[dataSchema.Count + 1], ChangeDataWriter.CommitVersionColumn, DataTypes.LongType);
        AssertField(schema[dataSchema.Count + 2], ChangeDataWriter.CommitTimestampColumn, DataTypes.TimestampType);
    }

    private static void AssertField(StructField field, string name, DataType type)
    {
        Assert.Equal(name, field.Name);
        Assert.Equal(type, field.DataType);
        Assert.False(field.Nullable, $"metadata column {name} is non-nullable");
    }

    private static string Utf8(ColumnVector vector, int row) => Encoding.UTF8.GetString(vector.GetBytes(row));

    private DeltaDelete NewCdfDelete(LocalFileSystemBackend backend, string idSeed) =>
        new(backend, new DeltaLog(backend),
            idSource: new SeededDeletionVectorIdSource(idSeed),
            // Prefix the cdc file token with the (per-call unique) idSeed so multiple DELETEs in one test never
            // collide on a cdc file name (each PutIfAbsent target is distinct).
            cdcFileNameFactory: SequentialCdcTokens(idSeed + "-"));

    private static Func<string> SequentialCdcTokens(string prefix = "t")
    {
        int n = 0;
        return () => prefix + (n++).ToString("D4", CultureInfo.InvariantCulture);
    }

    private static DeltaDeletePredicate WhereId(Func<long, bool> match) =>
        DeltaDeletePredicate.FromRowPredicate((batch, row) => match(batch.SelectedColumn(0).GetValue<long>(row)));

    private async Task CreateCdfFlatTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(FlatSchema, Array.Empty<string>(), batches);
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    // A partitioned CDF table variant used where the flat helper's schema is irrelevant (INV C8/partition tests).
    private async Task CreateCdfPartitionedTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(PartitionedSchema, new[] { "region" }, batches);
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    private static ColumnBatch PartitionedBootstrap() => PartBatch((1, "east", 1), (2, "west", 2));

    private async Task CreateNameMappedCdfPartitionedTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateNameMappedDeletionVectorTableAsync(
                PartitionedSchema, new[] { "region" }, batches, new SeededPhysicalNameSource("cdf-read-name"));
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    private async Task CreateIdMappedCdfFlatTableAsync(params ColumnBatch[] batches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateIdMappedDeletionVectorTableAsync(
                FlatSchema, Array.Empty<string>(), batches, new SeededPhysicalNameSource("cdf-read-id"));
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(_root)).EnableChangeDataFeedAsync();
    }

    // Authors a FOREIGN id-mode + CDF-enabled table BY HAND across two commits (#660), the shape a non-DeltaSharp
    // engine produces: the physical Parquet data file's column NAMES (z0/z1) do NOT match the metaData
    // physicalNames (col-A/col-B) and are stored in the OPPOSITE order to the logical schema, but each carries
    // the correct footer field_id. DeltaSharp's own writer always names a physical column == its physicalName,
    // so ONLY a foreign table like this makes id-mode field-id resolution LOAD-BEARING (name/positional
    // resolution cannot pass by coincidence). Logical schema id(field_id 1)/name(field_id 2); physical layout
    // [z0=name(field_id 2, nullable string), z1=id(field_id 1, non-null long)] — so logical `id` (field_id 1)
    // is at physical position 1 and its physical name z1 differs from its metaData physicalName col-A.
    //   v0: protocol (reader v3 / writer v7; columnMapping reader+writer, changeDataFeed writer) + metaData
    //       (id-mode column mapping, delta.enableChangeDataFeed=true) — a metadata-only create commit.
    //   v1: the foreign add(dataChange=true) referencing the hand-written Parquet file.
    // Reading the CDF range [1,1] derives three `insert` rows from that add.
    private async Task SeedForeignIdModeCdfFlatTableAsync()
    {
        var physicalSchema = new StructType(new[]
        {
            PhysFieldWithId("z0", DataTypes.StringType, nullable: true, id: 2),    // logical "name" (field_id 2)
            PhysFieldWithId("z1", DataTypes.LongType, nullable: false, id: 1),     // logical "id"   (field_id 1)
        });

        (long Id, string Name)[] rows = { (1, "a"), (2, "b"), (3, "c") };
        MutableColumnVector z0 = ColumnVectors.Create(DataTypes.StringType, rows.Length);  // name
        MutableColumnVector z1 = ColumnVectors.Create(DataTypes.LongType, rows.Length);    // id
        foreach ((long id, string name) in rows)
        {
            z0.AppendBytes(Encoding.UTF8.GetBytes(name));
            z1.AppendValue(id);
        }

        var batch = new ManagedColumnBatch(physicalSchema, new ColumnVector[] { z0, z1 }, rows.Length);

        byte[] parquetBytes;
        using (var buffer = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(
                buffer, physicalSchema, new[] { batch }, CancellationToken.None);
            parquetBytes = buffer.ToArray();
        }

        const string relativePath = "foreign-idmode-cdf.parquet";
        using var backend = new LocalFileSystemBackend(_root);
        await backend.PutIfAbsentAsync(relativePath, parquetBytes, CancellationToken.None);

        // Logical id/name with ids 1/2 and physicalNames col-A/col-B — names that do NOT match the Parquet
        // z0/z1, so a name-based read cannot resolve them; only field_id works.
        const string schemaJson =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";
        string escapedSchema = System.Text.Json.JsonSerializer.Serialize(schemaJson);

        // reader v3 / writer v7 declaring columnMapping (reader+writer) AND changeDataFeed (writer-only), so the
        // metaData's delta.enableChangeDataFeed is honored (ChangeDataFeedFeature.IsActive requires BOTH).
        const string protocol =
            "{\"protocol\":{\"minReaderVersion\":3,\"minWriterVersion\":7,"
            + "\"readerFeatures\":[\"columnMapping\"],"
            + "\"writerFeatures\":[\"columnMapping\",\"changeDataFeed\"]}}";
        string metadata =
            "{\"metaData\":{\"id\":\"t\",\"format\":{\"provider\":\"parquet\",\"options\":{}},"
            + "\"schemaString\":" + escapedSchema + ",\"partitionColumns\":[],\"configuration\":{"
            + "\"delta.columnMapping.mode\":\"id\",\"delta.columnMapping.maxColumnId\":\"2\","
            + "\"delta.enableChangeDataFeed\":\"true\"}}}";

        byte[] v0 = Encoding.UTF8.GetBytes(protocol + "\n" + metadata + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000000.json", v0, CancellationToken.None);

        // v1: the foreign add(dataChange=true). modificationTime:0 is a literal (determinism — no clock read).
        string addLine =
            $"{{\"add\":{{\"path\":\"{relativePath}\",\"partitionValues\":{{}},"
            + $"\"size\":{parquetBytes.Length},\"modificationTime\":0,\"dataChange\":true}}}}";
        byte[] v1 = Encoding.UTF8.GetBytes(addLine + "\n");
        await backend.PutIfAbsentAsync("_delta_log/00000000000000000001.json", v1, CancellationToken.None);
    }

    // A physical StructField carrying a delta.columnMapping.id (as a long MetadataValue) — the shape the id-mode
    // writer stamps into the Parquet field_id, used to author a foreign id-mode data file by hand (mirrors
    // DeltaDeleteColumnMappingTests.PhysFieldWithId).
    private static StructField PhysFieldWithId(string name, DataType type, bool nullable, long id) =>
        new(name, type, nullable, FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(id)),
        }));

    // A fresh int-column table with CDF AND type widening enabled, so a later long append WIDENS `amount`
    // (int→long) IN-RANGE and the earlier narrow int files reconcile (promote) to long at read time (§2.8).
    private async Task CreateWideningCdfTableAsync(params ColumnBatch[] intBatches)
    {
        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root))
        {
            await target.CreateDeletionVectorTableAsync(WideningIntSchema, Array.Empty<string>(), intBatches);
        }

        var backend = new LocalFileSystemBackend(_root);
        await new DeltaTableWriter(backend).EnableChangeDataFeedAsync();
        await new DeltaTableWriter(backend).EnableTypeWideningAsync();
    }

    private async Task<long> AppendFlatAsync(params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(FlatSchema, Array.Empty<string>(), batches);
        return result.Version;
    }

    // An additive/widening append that EVOLVES the schema (Spark mergeSchema): add a nullable column or apply a
    // sanctioned type widening. `schema` is the (wider) target schema written by this commit.
    private async Task<long> AppendMergeAsync(
        StructType schema, IReadOnlyList<string> partitionColumns, params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(schema, partitionColumns, batches, mergeSchema: true);
        return result.Version;
    }

    // Appends narrow int rows to the widening table (the table schema already matches ⇒ no mergeSchema needed).
    private async Task<long> AppendIntAsync(params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(WideningIntSchema, Array.Empty<string>(), batches);
        return result.Version;
    }

    private async Task<long> AppendPartAsync(params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.AppendAsync(PartitionedSchema, new[] { "region" }, batches);
        return result.Version;
    }

    private async Task<long> OverwriteFlatAsync(params ColumnBatch[] batches)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);
        DeltaWriteResult result = await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), batches, DeltaPartitionOverwriteMode.Static);
        return result.Version;
    }

    private async Task<IReadOnlyList<DeltaAction>> ReadCommitActionsAsync(LocalFileSystemBackend backend, long version) =>
        await new DeltaLog(backend).ReadCommitActionsAsync(version, CancellationToken.None);

    // A normal (non-CDF) latest-snapshot read of the flat table — the differential oracle that pins overwrite
    // tombstoning at the snapshot level, independent of the change-feed read path.
    private async Task<List<(long Id, string? Name)>> ReadLatestFlatAsync()
    {
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(_root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null, CancellationToken.None);
        var rows = new List<(long, string?)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Utf8(name, r)));
            }
        }

        return rows;
    }

    private string CommitPath(long version) =>
        Path.Combine(_root, "_delta_log", version.ToString("D20", CultureInfo.InvariantCulture) + ".json");

    // Every *.parquet under _change_data/, as table-root-relative '/'-separated paths — empty when no cdc file
    // was written (the implicit append/overwrite paths write none; only DELETE materializes cdc).
    private List<string> CdcFilePaths()
    {
        string dir = Path.Combine(_root, ChangeDataWriter.ChangeDataDirectory);
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(dir, "*.parquet", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    // Stamps DETERMINISTIC, strictly-increasing mtimes on every <N>.json (0..latestVersion) — the exact seam
    // DeltaLog reads commit timestamps from (via IStorageBackend.ListAsync ⇒ LastWriteTimeUtc). Strictly
    // increasing by `step` ⇒ the effective monotonic timeline equals the raw mtimes (no adjustment).
    private void SetCommitMtimes(DateTimeOffset baseTime, TimeSpan step, long latestVersion)
    {
        for (long v = 0; v <= latestVersion; v++)
        {
            string path = CommitPath(v);
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, (baseTime + step * v).UtcDateTime);
            }
        }
    }

    // Writes a COMPLETE classic checkpoint@`version` whose protocol + metadata are copied verbatim from the
    // real reconstructed snapshot (so CDF stays active and the schema is exact), plus the _last_checkpoint hint.
    // No `add` rows are needed: a CDF read derives changes from commit ACTIONS and uses the snapshot only for
    // protocol + metadata (schema/enablement), never its active-file list — so an add-less checkpoint faithfully
    // seeds everything the read consumes. Advancing the earliest-reconstructable floor onto this checkpoint (by
    // deleting the earlier <N>.json) keeps [version..] reconstructable while changing what a fresh effective-
    // timeline listing would compute for a near-floor version (its monotonic-carry predecessor is now gone).
    private async Task WriteCheckpointFromRealStateAsync(LocalFileSystemBackend backend, long version)
    {
        Snapshot snap = await new DeltaLog(backend).LoadSnapshotAsync(version, CancellationToken.None);
        var fixture = new CheckpointFixture()
            .Protocol(
                snap.Protocol.MinReaderVersion,
                snap.Protocol.MinWriterVersion,
                snap.Protocol.ReaderFeatures.IsDefaultOrEmpty ? null : snap.Protocol.ReaderFeatures.ToArray(),
                snap.Protocol.WriterFeatures.IsDefaultOrEmpty ? null : snap.Protocol.WriterFeatures.ToArray())
            .Metadata(
                snap.Metadata.Id,
                snap.Metadata.SchemaString,
                partitionColumns: snap.Metadata.PartitionColumns.IsDefaultOrEmpty
                    ? null : snap.Metadata.PartitionColumns.ToArray(),
                configuration: snap.Metadata.Configuration.Select(kv => (kv.Key, kv.Value)).ToArray());
        await DeltaTestHarness.WriteCheckpointAsync(backend, version, fixture);
        await DeltaTestHarness.WriteLastCheckpointAsync(backend, version);
    }

    // Reads the RAW on-disk commitInfo.timestamp (epoch millis) for a version — the value CDF must NOT use for
    // _commit_timestamp (it uses the <N>.json mtime instead, §2.8).
    private async Task<long> ReadCommitInfoTimestampAsync(long version)
    {
        foreach (string line in await File.ReadAllLinesAsync(CommitPath(version)))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("commitInfo", out JsonElement commitInfo)
                && commitInfo.TryGetProperty("timestamp", out JsonElement timestamp))
            {
                return timestamp.GetInt64();
            }
        }

        throw new InvalidOperationException($"No commitInfo.timestamp in version {version}.");
    }

    // Decodes the (id, name, extra) wide-schema change rows for the cross-range reconciliation tests, asserting
    // per batch (via AssertMetadataRow): INV C8 (one _commit_version) + non-null, valid metadata columns.
    private static List<(long Id, string? Name, long? Extra, string ChangeType, long Version)> DecodeWideChanges(
        List<ColumnBatch> batches)
    {
        var rows = new List<(long, string?, long?, string, long)>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector name = batch.SelectedColumn(1);
            ColumnVector extra = batch.SelectedColumn(2);
            (ColumnVector changeType, ColumnVector version, ColumnVector ts) = MetadataColumns(batch);
            long? single = null;
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                long v = AssertMetadataRow(changeType, version, ts, r, ref single);
                rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Utf8(name, r),
                    extra.IsNull(r) ? (long?)null : extra.GetValue<long>(r), Utf8(changeType, r), v));
            }
        }

        return rows;
    }

    // Decodes the (id, amount) widening-schema change rows, asserting per batch: INV C8 + non-null metadata.
    private static List<(long Id, long? Amount, string ChangeType, long Version)> DecodeAmountChanges(
        List<ColumnBatch> batches)
    {
        var rows = new List<(long, long?, string, long)>();
        foreach (ColumnBatch batch in batches)
        {
            ColumnVector id = batch.SelectedColumn(0);
            ColumnVector amount = batch.SelectedColumn(1);
            (ColumnVector changeType, ColumnVector version, ColumnVector ts) = MetadataColumns(batch);
            long? single = null;
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                long v = AssertMetadataRow(changeType, version, ts, r, ref single);
                rows.Add((id.GetValue<long>(r), amount.IsNull(r) ? (long?)null : amount.GetValue<long>(r),
                    Utf8(changeType, r), v));
            }
        }

        return rows;
    }

    // Truncates a file to its first half (footer + magic lost) — a torn/partial upload that must fail closed as
    // CorruptData, distinct from a fully-absent (NotFound/vacuumed) file.
    private static void TruncateInHalf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
    }

    private static ColumnBatch Batch(params (long Id, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n) in rows)
        {
            id.AppendValue(i);
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, name }, rows.Length);
    }

    private static ColumnBatch PartBatch(params (long Id, string Region, long? Val)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector val = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, string reg, long? v) in rows)
        {
            id.AppendValue(i);
            region.AppendBytes(Encoding.UTF8.GetBytes(reg));
            if (v is null)
            {
                val.AppendNull();
            }
            else
            {
                val.AppendValue(v.Value);
            }
        }

        return new ManagedColumnBatch(PartitionedSchema, new ColumnVector[] { id, region, val }, rows.Length);
    }

    private static ColumnBatch WideBatch(params (long Id, string? Name, long? Extra)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector extra = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, string? n, long? e) in rows)
        {
            id.AppendValue(i);
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }

            if (e is null)
            {
                extra.AppendNull();
            }
            else
            {
                extra.AppendValue(e.Value);
            }
        }

        return new ManagedColumnBatch(WideFlatSchema, new ColumnVector[] { id, name, extra }, rows.Length);
    }

    private static ColumnBatch IntAmountBatch(params (long Id, int? Amount)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector amount = ColumnVectors.Create(DataTypes.IntegerType, rows.Length);
        foreach ((long i, int? a) in rows)
        {
            id.AppendValue(i);
            if (a is null)
            {
                amount.AppendNull();
            }
            else
            {
                amount.AppendValue(a.Value);
            }
        }

        return new ManagedColumnBatch(WideningIntSchema, new ColumnVector[] { id, amount }, rows.Length);
    }

    private static ColumnBatch LongAmountBatch(params (long Id, long? Amount)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector amount = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        foreach ((long i, long? a) in rows)
        {
            id.AppendValue(i);
            if (a is null)
            {
                amount.AppendNull();
            }
            else
            {
                amount.AppendValue(a.Value);
            }
        }

        return new ManagedColumnBatch(WideningLongSchema, new ColumnVector[] { id, amount }, rows.Length);
    }

    private static ColumnBatch NameOnlyBatch(params string?[] names)
    {
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, names.Length);
        foreach (string? n in names)
        {
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }
        }

        return new ManagedColumnBatch(NarrowNameSchema, new ColumnVector[] { name }, names.Length);
    }

    private static ColumnBatch CdcTypeMismatchBatch(params (string Id, string Name, string ChangeType)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector changeType = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((string i, string n, string c) in rows)
        {
            id.AppendBytes(Encoding.UTF8.GetBytes(i));
            name.AppendBytes(Encoding.UTF8.GetBytes(n));
            changeType.AppendBytes(Encoding.UTF8.GetBytes(c));
        }

        return new ManagedColumnBatch(
            CdcTypeMismatchSchema, new ColumnVector[] { id, name, changeType }, rows.Length);
    }

    // Builds a cdc file body for the END schema B (id long, name string, extra long) + `_change_type` — used to
    // craft the v2 cdc file that matches the reconciled END schema but NOT v2's own (narrower) schema A, the
    // EE-08 per-version-vs-end-version discriminator.
    private static ColumnBatch CdcMatchesEndBatch(params (long Id, string Name, long Extra, string ChangeType)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector extra = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector changeType = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string n, long e, string c) in rows)
        {
            id.AppendValue(i);
            name.AppendBytes(Encoding.UTF8.GetBytes(n));
            extra.AppendValue(e);
            changeType.AppendBytes(Encoding.UTF8.GetBytes(c));
        }

        return new ManagedColumnBatch(
            CdcMatchesEndSchema, new ColumnVector[] { id, name, extra, changeType }, rows.Length);
    }

    // Builds a FlatSchema cdc file body (id, name, `_change_type`) — the physical layout ChangeDataWriter emits
    // — so a test can author a MULTI-ROW-GROUP cdc file with a chosen per-row `_change_type` (#644 streaming).
    private static ColumnBatch CdcFlatBodyBatch(params (long Id, string? Name, string ChangeType)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector changeType = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n, string c) in rows)
        {
            id.AppendValue(i);
            if (n is null)
            {
                name.AppendNull();
            }
            else
            {
                name.AppendBytes(Encoding.UTF8.GetBytes(n));
            }

            changeType.AppendBytes(Encoding.UTF8.GetBytes(c));
        }

        return new ManagedColumnBatch(
            CdcFlatBodySchema, new ColumnVector[] { id, name, changeType }, rows.Length);
    }

    // A storage backend that reproduces the red-team's TOCTOU count-mismatch (#644): for any `_change_data/`
    // path it serves `_fileA`'s bytes (N `_change_type` values) for the FIRST `_serveAForFirstOpens` opens —
    // the EE-08 schema validation (open 1) and the `_change_type` pass (open 2) — then `_fileB`'s bytes
    // (M != N rows) for every later open — the eager footer probe (open 3) and, if the eager check were
    // missing, the data pass (open 4). Every non-cdc path (the `_delta_log` that resolve/read walks) delegates
    // to the real inner backend. A and B share the cdc leaf schema, so EE-08 passes on A while the eager
    // footer read sees B's DIFFERENT row count — the cdc file "atomically replaced" between the passes. This is
    // the deterministic seam for the count mismatch because the CDF read path is wired to sealed concrete
    // backends and a single crafted file's footer + `_change_type` counts always agree.
    private sealed class CountMismatchCdcBackend : IStorageBackend
    {
        private readonly IStorageBackend _inner;
        private readonly byte[] _fileA;
        private readonly byte[] _fileB;
        private readonly int _serveAForFirstOpens;
        private readonly List<string> _served = new();

        public CountMismatchCdcBackend(IStorageBackend inner, byte[] fileA, byte[] fileB, int serveAForFirstOpens)
        {
            _inner = inner;
            _fileA = fileA;
            _fileB = fileB;
            _serveAForFirstOpens = serveAForFirstOpens;
        }

        // The tag ("A"/"B") of each cdc-file open in order — asserted to be exactly [A, A, B], proving the eager
        // check fired at the footer probe (open 3 = "B") BEFORE the data pass (which would be a 4th open).
        public IReadOnlyList<string> ServedCdcOpens => _served;

        public StorageBackendKind Kind => _inner.Kind;

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            if (path.Contains(ChangeDataWriter.ChangeDataDirectory, StringComparison.Ordinal))
            {
                bool serveA = _served.Count < _serveAForFirstOpens;
                _served.Add(serveA ? "A" : "B");
                return new ValueTask<Stream>(new MemoryStream(serveA ? _fileA : _fileB, writable: false));
            }

            return _inner.OpenReadAsync(path, cancellationToken);
        }

        public ValueTask<Stream> ReadRangeAsync(
            string path, long offset, long length, CancellationToken cancellationToken) =>
            _inner.ReadRangeAsync(path, offset, length, cancellationToken);

        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken cancellationToken) =>
            _inner.OpenWriteAsync(path, cancellationToken);

        public ValueTask<bool> PutIfAbsentAsync(
            string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            _inner.PutIfAbsentAsync(path, content, cancellationToken);

        public IAsyncEnumerable<StorageObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken) =>
            _inner.ListAsync(prefix, cancellationToken);

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, cancellationToken);

        public ValueTask<StorageObjectInfo?> HeadAsync(string path, CancellationToken cancellationToken) =>
            _inner.HeadAsync(path, cancellationToken);
    }
}
