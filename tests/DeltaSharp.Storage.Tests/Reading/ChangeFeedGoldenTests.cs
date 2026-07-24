using System.Globalization;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Tests.Delta.DeletionVectors;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Curated in-repo Change Data Feed goldens (increment 4 of #193; design §3.3 oracle (a) — golden histories +
/// per-version expected change manifests, UNIT tier). Unlike the model oracle's random histories, these are
/// hand-authored fixed histories with hand-written expected manifests, so they pin specific §3 scenarios and
/// catch a class of bug where an independent model and the reader could share a misconception. Each golden
/// asserts a MECHANICAL manifest equality (canonical, timestamp-free rendering ⇒ multiset equality).
/// </summary>
/// <remarks>
/// These deliberately cover §3 cases NOT already pinned by the increment-3 read tests: INV C6 folds-to-snapshot
/// on a curated history; INV C9 partition fidelity across BOTH the implicit (add) and explicit (cdc) paths;
/// INV C4 OPTIMIZE-invisibility over a range that STRADDLES the optimize (optimize in the MIDDLE, not at an
/// endpoint); and the CDF-HP-08 mixed-commit — a single DELETE that partially deletes one file (new DV) AND
/// fully deletes another (bare remove), read through the door (INV C2 both branches).
/// <para><b>These are the UNIT-tier substitute for cross-engine (reference-engine-emitted) CDF interop
/// goldens, which are the integration-tier follow-up tracked by #646.</b></para>
/// </remarks>
[Collection(DeletionVectorFileTestCollection.Name)]
public sealed class ChangeFeedGoldenTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(11);

    private readonly List<string> _roots = [];

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

    /// <summary>
    /// INV C6 (folds-to-snapshot) on a curated append/delete/overwrite history. Asserts (1) the full CDF read
    /// of <c>[1,5]</c> equals the hand-written per-version change manifest, (2) net-applying that read from
    /// empty reconstructs the read <c>snapshot(5)</c>, and (3) every row's <c>_commit_timestamp</c> equals its
    /// version's commit-file mtime lane (CDF-HP-05 / INV C7).
    /// </summary>
    [Fact]
    public async Task Golden_FoldsToSnapshot_CuratedHistory_C6()
    {
        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                                            // v0
        await table.EnableCdfAsync();                                              // v1
        await table.AppendAsync([Row(1, "east", 10), Row(2, "west", 20), Row(3, "east", 30)]); // v2
        await table.DeleteAsync([2]);                                             // v3 (west file fully deleted)
        await table.AppendAsync([Row(4, "south", 40), Row(5, "east", 50)]);        // v4
        await table.OverwriteAsync([Row(6, "west", 60)]);                          // v5 (deletes live 1,3,4,5)
        table.SetCommitMtimes(BaseTime, Step, 5);

        CdfChange[] expected =
        [
            Insert(2, 1, "east", 10), Insert(2, 2, "west", 20), Insert(2, 3, "east", 30),
            Delete(3, 2, "west", 20),
            Insert(4, 4, "south", 40), Insert(4, 5, "east", 50),
            Delete(5, 1, "east", 10), Delete(5, 3, "east", 30), Delete(5, 4, "south", 40),
            Delete(5, 5, "east", 50), Insert(5, 6, "west", 60),
        ];

        CdfReadResult read = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(1, 5));
        Assert.Equal(Manifest(expected), Manifest(read.Changes));

        // INV C7 / CDF-HP-05: each row's _commit_timestamp equals its version's commit-file mtime lane.
        foreach (CdfChange change in read.Changes)
        {
            Assert.Equal(CdfTable.ExpectedCommitMicros(BaseTime, Step, change.Version), change.TsMicros);
        }

        // INV C6: net-apply the read from empty, then compare to the independently-read snapshot(5).
        IReadOnlyList<CdfRow> folded = Fold(read.Changes);
        IReadOnlyList<CdfRow> snapshot = await table.ReadSnapshotAsync(5);
        Assert.Equal("id=6 region=west val=60", RowManifest(folded));
        Assert.Equal(RowManifest(folded), RowManifest(snapshot));
    }

    /// <summary>
    /// INV C9 partition fidelity on BOTH reconstruction paths. The implicit (add) path (append at v2) and the
    /// explicit (cdc) path (DELETE at v3) must each surface the partition column value on every change row.
    /// Asserted as manifest equality that PINS the actual <c>region</c> values (not merely non-null).
    /// </summary>
    [Fact]
    public async Task Golden_PartitionFidelity_BothPaths_C9()
    {
        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                                    // v0
        await table.EnableCdfAsync();                                      // v1
        await table.AppendAsync([Row(1, "east", 10), Row(2, "west", 20)]); // v2 implicit inserts
        await table.DeleteAsync([1]);                                     // v3 explicit cdc delete
        table.SetCommitMtimes(BaseTime, Step, 3);

        // Implicit path (add): partition value surfaces on each insert.
        CdfReadResult implicitRead = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(2, 2));
        Assert.Equal(
            Manifest([Insert(2, 1, "east", 10), Insert(2, 2, "west", 20)]),
            Manifest(implicitRead.Changes));

        // Explicit path (cdc): partition value surfaces on the delete row too.
        CdfReadResult explicitRead = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(3, 3));
        Assert.Equal(Manifest([Delete(3, 1, "east", 10)]), Manifest(explicitRead.Changes));
    }

    /// <summary>
    /// INV C4 OPTIMIZE-invisibility over a range that STRADDLES the optimize. The OPTIMIZE at v4 (compacting
    /// the two small east files from v2/v3) sits in the MIDDLE of the read range <c>[2,5]</c> and MUST
    /// contribute zero change rows; the surrounding appends' inserts still surface with the correct versions.
    /// </summary>
    [Fact]
    public async Task Golden_OptimizeInvisibility_StraddleRange_C4()
    {
        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                        // v0
        await table.EnableCdfAsync();                          // v1
        await table.AppendAsync([Row(1, "east", 10)]);          // v2 (east file #1)
        await table.AppendAsync([Row(2, "east", 20)]);          // v3 (east file #2 — now compactable)
        OptimizeResult optimize = await table.OptimizeAsync(); // v4 (dataChange=false)
        Assert.Equal(4L, optimize.CommittedVersion);           // the optimize really committed (not a no-op)
        Assert.True(optimize.FilesRemoved >= 2, "the straddle optimize must actually compact ≥2 files");
        await table.AppendAsync([Row(3, "east", 30)]);          // v5
        table.SetCommitMtimes(BaseTime, Step, 5);

        CdfReadResult read = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(2, 5));
        Assert.Equal(
            Manifest([Insert(2, 1, "east", 10), Insert(3, 2, "east", 20), Insert(5, 3, "east", 30)]),
            Manifest(read.Changes));

        // Mechanical INV C4: no change row (and no batch) is stamped with the optimize version.
        Assert.DoesNotContain(read.Changes, c => c.Version == 4);
        Assert.DoesNotContain(read.BatchVersions, v => v == 4);
    }

    /// <summary>
    /// CDF-HP-08 mixed-commit read through the door (INV C2 both branches). A single DELETE at v3 partially
    /// deletes the east file (id 1 masked by a new DV, id 2 retained) AND fully deletes the west file (id 3,
    /// bare remove). Both branches must surface as <c>delete</c> change rows with the correct partition values,
    /// and the commit's DeleteResult must record exactly one DV'd file and one fully-deleted file.
    /// </summary>
    [Fact]
    public async Task Golden_MixedDeleteCommit_BothBranches_ReadThroughDoor_C2_HP08()
    {
        using CdfTable table = NewTable();
        await table.CreateEmptyAsync();                                                        // v0
        await table.EnableCdfAsync();                                                          // v1
        await table.AppendAsync([Row(1, "east", 10), Row(2, "east", 20), Row(3, "west", 30)]); // v2: east{1,2}, west{3}
        DeleteResult result = await table.DeleteAsync([1, 3]);                                 // v3: partial east + full west
        table.SetCommitMtimes(BaseTime, Step, 3);

        // The commit genuinely exercised BOTH branches (INV C2): one file got a new DV, one was fully removed.
        Assert.Equal(3L, result.CommittedVersion);
        Assert.Equal(1, result.FilesWithDeletionVector);
        Assert.Equal(1, result.FilesFullyDeleted);
        Assert.Equal(2L, result.RowsDeleted);

        // The DELETE version's feed carries both deletes (partial-branch id 1 east, full-branch id 3 west).
        CdfReadResult deleteRead = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(3, 3));
        Assert.Equal(
            Manifest([Delete(3, 1, "east", 10), Delete(3, 3, "west", 30)]),
            Manifest(deleteRead.Changes));

        // Full range: the appends' inserts then the mixed deletes; snapshot(3) is the single retained row.
        CdfReadResult fullRead = await table.ReadRangeAsync(DeltaChangeFeedRange.FromVersion(2, 3));
        Assert.Equal(
            Manifest(
            [
                Insert(2, 1, "east", 10), Insert(2, 2, "east", 20), Insert(2, 3, "west", 30),
                Delete(3, 1, "east", 10), Delete(3, 3, "west", 30),
            ]),
            Manifest(fullRead.Changes));

        Assert.Equal("id=2 region=east val=20", RowManifest(Fold(fullRead.Changes)));
        Assert.Equal("id=2 region=east val=20", RowManifest(await table.ReadSnapshotAsync(3)));
    }

    // ------------------------------------------------------------------ helpers

    private static CdfChange Insert(long version, long id, string region, long? val) =>
        new(version, ChangeDataWriter.InsertChange, id, region, val);

    private static CdfChange Delete(long version, long id, string region, long? val) =>
        new(version, ChangeDataWriter.DeleteChange, id, region, val);

    private static CdfRow Row(long id, string region, long? val) => new(id, region, val);

    /// <summary>Canonical (sorted, timestamp-free) change manifest — string equality ⇔ multiset equality.</summary>
    private static string Manifest(IReadOnlyList<CdfChange> changes) =>
        string.Join(
            "\n",
            changes
                .Select(c => c.WithoutTimestamp())
                .OrderBy(c => c.Version)
                .ThenBy(c => c.Id)
                .ThenBy(c => c.ChangeType, StringComparer.Ordinal)
                .ThenBy(c => c.Region, StringComparer.Ordinal)
                .ThenBy(c => c.Val ?? long.MinValue)
                .Select(c => string.Create(
                    CultureInfo.InvariantCulture,
                    $"v{c.Version} {c.ChangeType} id={c.Id} region={c.Region} val={Format(c.Val)}")));

    private static string RowManifest(IReadOnlyList<CdfRow> rows) =>
        string.Join(
            "\n",
            rows
                .OrderBy(r => r.Id)
                .ThenBy(r => r.Region, StringComparer.Ordinal)
                .ThenBy(r => r.Val ?? long.MinValue)
                .Select(r => string.Create(
                    CultureInfo.InvariantCulture, $"id={r.Id} region={r.Region} val={Format(r.Val)}")));

    private static string Format(long? val) =>
        val is null ? "null" : val.Value.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<CdfRow> Fold(IReadOnlyList<CdfChange> changes)
    {
        var live = new Dictionary<long, CdfRow>();
        foreach (CdfChange change in changes)
        {
            if (change.ChangeType == ChangeDataWriter.InsertChange)
            {
                live[change.Id] = new CdfRow(change.Id, change.Region, change.Val);
            }
            else
            {
                Assert.True(live.Remove(change.Id), $"delete must remove a live id (id={change.Id})");
            }
        }

        return [.. live.Values];
    }

    private CdfTable NewTable()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-cdf-golden-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new CdfTable(root);
    }
}
