using System.Globalization;
using System.Text;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class SnapshotReconstructionTests
{
    private const string EmptySchema = """{\"type\":\"struct\",\"fields\":[]}""";

    private static IReadOnlyList<DeltaAction> Commit(long version, params string[] lines) =>
        DeltaLogActionReader.ParseCommit(Encoding.UTF8.GetBytes(string.Join('\n', lines)), version);

    private static string ProtocolLine => """{"protocol":{"minReaderVersion":1,"minWriterVersion":2}}""";

    private static string MetadataLine(string escapedSchema = EmptySchema) =>
        """{"metaData":{"id":"t","format":{"provider":"parquet","options":{}},"schemaString":"__SCHEMA__","partitionColumns":[],"configuration":{}}}"""
            .Replace("__SCHEMA__", escapedSchema, StringComparison.Ordinal);

    private static string Add(string path, long size = 1) =>
        """{"add":{"path":"__P__","partitionValues":{},"size":__S__,"modificationTime":1,"dataChange":true}}"""
            .Replace("__P__", path, StringComparison.Ordinal)
            .Replace("__S__", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Remove(string path) =>
        """{"remove":{"path":"__P__","deletionTimestamp":1,"dataChange":true}}"""
            .Replace("__P__", path, StringComparison.Ordinal);

    private static string Cdc(string path, long size = 1) =>
        """{"cdc":{"path":"__P__","partitionValues":{},"size":__S__,"dataChange":false}}"""
            .Replace("__P__", path, StringComparison.Ordinal)
            .Replace("__S__", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [Fact]
    public void Replay_IgnoresCdcActions_InvC1()
    {
        // INV C1 (CDF isolation): a commit sequence INCLUDING cdc actions yields an active-file set,
        // tombstones, metadata, and protocol IDENTICAL to the same sequence WITHOUT the cdc actions. A cdc
        // file is never part of active table state (§2.3) — a normal snapshot read of a CDF-enabled table is
        // byte-identical to the same table with CDF off.
        var withCdc = new SnapshotState();
        withCdc.ApplyAll(Commit(0, ProtocolLine, MetadataLine(), Add("A"), Add("B")));
        withCdc.ApplyAll(Commit(1, Remove("A"), Add("C"), Cdc("_change_data/cdc-1.parquet", 7)));
        withCdc.ApplyAll(Commit(2, Cdc("_change_data/cdc-2.parquet")));
        Snapshot withCdcSnap = withCdc.ToSnapshot(2, SnapshotLoadMetrics.Empty);

        var withoutCdc = new SnapshotState();
        withoutCdc.ApplyAll(Commit(0, ProtocolLine, MetadataLine(), Add("A"), Add("B")));
        withoutCdc.ApplyAll(Commit(1, Remove("A"), Add("C")));
        Snapshot withoutCdcSnap = withoutCdc.ToSnapshot(2, SnapshotLoadMetrics.Empty);

        Assert.Equal(
            withoutCdcSnap.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal),
            withCdcSnap.ActiveFiles.Select(a => a.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(
            withoutCdcSnap.Tombstones.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal),
            withCdcSnap.Tombstones.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(withoutCdcSnap.ActiveFileCount, withCdcSnap.ActiveFileCount);
        Assert.Equal(withoutCdcSnap.ActiveSizeInBytes, withCdcSnap.ActiveSizeInBytes);
        Assert.Equal(withoutCdcSnap.Protocol, withCdcSnap.Protocol);
        // MetadataAction record equality is reference-based for its immutable collections, so compare the
        // replay-relevant fields individually rather than the whole record.
        Assert.Equal(withoutCdcSnap.Metadata.Id, withCdcSnap.Metadata.Id);
        Assert.Equal(withoutCdcSnap.Metadata.SchemaString, withCdcSnap.Metadata.SchemaString);
        Assert.Equal(withoutCdcSnap.Metadata.Configuration, withCdcSnap.Metadata.Configuration);
    }

    [Fact]
    public void Replay_AcrossVersions_ComputesActiveFilesAndTombstones()
    {
        // STORY-05.2.1 AC4 / STORY-05.2.2 core: replay a multi-version history in ascending order; later
        // actions win. v0 adds A,B; v1 removes A + adds C; v2 removes C + re-adds A. Final active = {A,B};
        // C is a tombstone; A (removed then re-added) is active, NOT a tombstone.
        var state = new SnapshotState();
        state.ApplyAll(Commit(0, ProtocolLine, MetadataLine(), Add("A"), Add("B")));
        state.ApplyAll(Commit(1, Remove("A"), Add("C")));
        state.ApplyAll(Commit(2, Remove("C"), Add("A", size: 5)));

        Snapshot snapshot = state.ToSnapshot(version: 2, new SnapshotLoadMetrics(null, 3, 0, TimeSpan.Zero));

        Assert.Equal(2, snapshot.Version);
        Assert.Equal(["A", "B"], snapshot.ActiveFiles.Select(a => a.Path)); // path-ordered
        Assert.Equal(5L, snapshot.ActiveFiles.Single(a => a.Path == "A").Size); // the re-added A (size 5)
        Assert.Equal(["C"], snapshot.Tombstones.Select(r => r.Path));
        Assert.Equal(2, snapshot.ActiveFileCount);
        Assert.Equal(6L, snapshot.ActiveSizeInBytes); // A(5) + B(1)
        Assert.Equal(3, snapshot.Metrics.ReplayedCommitCount);
        Assert.Equal(2, snapshot.Metrics.ActiveFileCount);
    }

    [Fact]
    public void Snapshot_ExposesProtocolMetadataAndParsedSchema()
    {
        const string schema = """{\"type\":\"struct\",\"fields\":[{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}}]}""";
        var state = new SnapshotState();
        state.ApplyAll(Commit(0, ProtocolLine, MetadataLine(schema), Add("f")));

        Snapshot snapshot = state.ToSnapshot(0, SnapshotLoadMetrics.Empty);

        Assert.Equal(1, snapshot.Protocol.MinReaderVersion);
        Assert.Equal("t", snapshot.Metadata.Id);
        Assert.Single(snapshot.Schema.Fields); // schemaString parsed via the relocated SchemaJson (D-6)
        Assert.Equal("id", snapshot.Schema.Fields[0].Name);
    }

    [Fact]
    public void Txn_KeepsHighestVersionPerAppId()
    {
        var state = new SnapshotState();
        state.ApplyAll(Commit(0, ProtocolLine, MetadataLine()));
        state.ApplyAll(Commit(1, """{"txn":{"appId":"s","version":10}}"""));
        state.ApplyAll(Commit(2, """{"txn":{"appId":"s","version":7}}""")); // lower — must not regress

        Snapshot snapshot = state.ToSnapshot(2, SnapshotLoadMetrics.Empty);
        Assert.Equal(10L, snapshot.Transactions["s"]);
    }

    [Fact]
    public void ToSnapshot_FailsClosed_WhenProtocolMissing()
    {
        var state = new SnapshotState();
        state.ApplyAll(Commit(0, MetadataLine(), Add("f"))); // no protocol

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => state.ToSnapshot(0, SnapshotLoadMetrics.Empty));
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
    }

    [Fact]
    public void ToSnapshot_FailsClosed_WhenMetadataMissing()
    {
        var state = new SnapshotState();
        state.ApplyAll(Commit(0, ProtocolLine, Add("f"))); // no metaData

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => state.ToSnapshot(0, SnapshotLoadMetrics.Empty));
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
    }

    [Fact]
    public void Schema_FailsClosed_OnNonStructSchemaString()
    {
        var state = new SnapshotState();
        state.ApplyAll(Commit(0, ProtocolLine, MetadataLine(escapedSchema: "\\\"integer\\\"")));

        Snapshot snapshot = state.ToSnapshot(0, SnapshotLoadMetrics.Empty);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() => _ = snapshot.Schema);
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
    }
}
