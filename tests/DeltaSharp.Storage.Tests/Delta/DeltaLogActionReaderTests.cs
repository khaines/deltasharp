using System.Text;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class DeltaLogActionReaderTests
{
    private static ReadOnlyMemory<byte> Commit(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Join('\n', lines));

    [Fact]
    public void ParsesProtocolMetadataAddRemoveTxnCommitInfo_PreservingReplayFields()
    {
        // AC1: valid protocol/metaData/add/remove/txn/commitInfo actions preserve every field needed for
        // replay. One commit exercises all six action kinds in file order.
        ReadOnlyMemory<byte> content = Commit(
            """{"commitInfo":{"timestamp":1700000004000,"operation":"WRITE","operationParameters":{"mode":"Append","partitionBy":["dt"]},"engineInfo":"test","readVersion":6}}""",
            """{"protocol":{"minReaderVersion":1,"minWriterVersion":2}}""",
            """{"metaData":{"id":"t-1","name":"orders","format":{"provider":"parquet","options":{}},"schemaString":"{\"type\":\"struct\",\"fields\":[]}","partitionColumns":["dt"],"configuration":{"delta.appendOnly":"true"},"createdTime":1700000000000}}""",
            """{"add":{"path":"dt=2024/part-0.parquet","partitionValues":{"dt":"2024"},"size":1024,"modificationTime":1700000001000,"dataChange":true,"tags":{"z":"1"}}}""",
            """{"remove":{"path":"dt=2023/old.parquet","deletionTimestamp":1700000002000,"dataChange":false,"extendedFileMetadata":true,"partitionValues":{"dt":"2023"},"size":512}}""",
            """{"txn":{"appId":"stream-a","version":42,"lastUpdated":1700000003000}}""");

        IReadOnlyList<DeltaAction> actions = DeltaLogActionReader.ParseCommit(content, version: 7);

        Assert.Equal(6, actions.Count);

        var protocol = Assert.IsType<ProtocolAction>(actions[1]);
        Assert.Equal(1, protocol.MinReaderVersion);
        Assert.Equal(2, protocol.MinWriterVersion);
        Assert.Empty(protocol.ReaderFeatures);

        var metadata = Assert.IsType<MetadataAction>(actions[2]);
        Assert.Equal("t-1", metadata.Id);
        Assert.Equal("orders", metadata.Name);
        Assert.Equal("parquet", metadata.Format.Provider);
        Assert.Equal("dt", Assert.Single(metadata.PartitionColumns));
        Assert.Equal("true", metadata.Configuration["delta.appendOnly"]);
        Assert.Equal(1700000000000L, metadata.CreatedTime);

        var add = Assert.IsType<AddFileAction>(actions[3]);
        Assert.Equal("dt=2024/part-0.parquet", add.Path);
        Assert.Equal("2024", add.PartitionValues["dt"]);
        Assert.Equal(1024L, add.Size);
        Assert.True(add.DataChange);
        Assert.Equal("1", add.Tags["z"]);

        var remove = Assert.IsType<RemoveFileAction>(actions[4]);
        Assert.Equal("dt=2023/old.parquet", remove.Path);
        Assert.Equal(1700000002000L, remove.DeletionTimestamp);
        Assert.False(remove.DataChange);
        Assert.True(remove.ExtendedFileMetadata);
        Assert.Equal(512L, remove.Size);
        Assert.Empty(remove.Tags); // no "tags" key present → reader resolves to an empty map (issue #491)

        var txn = Assert.IsType<TxnAction>(actions[5]);
        Assert.Equal("stream-a", txn.AppId);
        Assert.Equal(42L, txn.Version);

        var commitInfo = Assert.IsType<CommitInfoAction>(actions[0]);
        // #510: operation/operationParameters/timestamp/engineInfo are typed; operationParameters values are
        // preserved as their raw JSON tokens ("Append" is a quoted string, ["dt"] is an array).
        Assert.Equal(1700000004000L, commitInfo.Timestamp);
        Assert.Equal("WRITE", commitInfo.Operation);
        Assert.Equal("\"Append\"", commitInfo.OperationParameters!["mode"]);
        Assert.Equal("[\"dt\"]", commitInfo.OperationParameters!["partitionBy"]);
        Assert.Equal("test", commitInfo.EngineInfo);
        Assert.Equal("6", commitInfo.Entries["readVersion"]); // numeric scalar preserved as text
    }

    [Fact]
    public void ParsesStats_WithSchemaAwareTypesAndNullSemantics()
    {
        // AC3: add.stats (a JSON string) round-trips numRecords + typed min/max + null counts.
        ReadOnlyMemory<byte> content = Commit(
            """{"add":{"path":"p.parquet","partitionValues":{},"size":10,"modificationTime":1,"stats":"{\"numRecords\":100,\"minValues\":{\"id\":1,\"name\":\"a\",\"score\":0.5},\"maxValues\":{\"id\":9,\"name\":\"z\"},\"nullCount\":{\"id\":0,\"name\":3},\"tightBounds\":true}"}}""");

        var add = Assert.IsType<AddFileAction>(DeltaLogActionReader.ParseCommit(content, 1)[0]);
        FileStatistics stats = Assert.IsType<FileStatistics>(add.Stats);

        Assert.Equal(100L, stats.NumRecords);
        Assert.True(stats.TightBounds);
        Assert.Equal(DeltaStatKind.Long, stats.MinValues["id"].Kind);
        Assert.Equal("1", stats.MinValues["id"].Raw);
        Assert.Equal(DeltaStatKind.String, stats.MinValues["name"].Kind);
        Assert.Equal(DeltaStatKind.Double, stats.MinValues["score"].Kind);
        Assert.Equal(9L, long.Parse(stats.MaxValues["id"].Raw, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0L, stats.NullCount["id"]);
        Assert.Equal(3L, stats.NullCount["name"]);
    }

    [Fact]
    public void PartitionValue_DistinguishesJsonNullFromAbsent()
    {
        // AC3: a JSON-null partition value (a null partition) is preserved as a present key with a null
        // value — distinct from an absent key.
        ReadOnlyMemory<byte> content = Commit(
            """{"add":{"path":"p.parquet","partitionValues":{"dt":null},"size":1,"modificationTime":1}}""");

        var add = Assert.IsType<AddFileAction>(DeltaLogActionReader.ParseCommit(content, 1)[0]);
        Assert.True(add.PartitionValues.ContainsKey("dt"));
        Assert.Null(add.PartitionValues["dt"]);
    }

    [Fact]
    public void SkipsBlankLinesAndTrailingNewline()
    {
        ReadOnlyMemory<byte> content = Commit(
            """{"txn":{"appId":"a","version":1}}""",
            "",
            "   ",
            "");

        Assert.Single(DeltaLogActionReader.ParseCommit(content, 1));
    }

    [Fact]
    public void IgnoresUnknownActionKey_ForForwardCompatibility()
    {
        // An unrecognized top-level action key is skipped (Delta's forward-compat rule); a table that
        // truly requires it advertises a reader feature that protocol negotiation rejects up front.
        ReadOnlyMemory<byte> content = Commit(
            """{"someFutureAction":{"x":1}}""",
            """{"txn":{"appId":"a","version":1}}""");

        IReadOnlyList<DeltaAction> actions = DeltaLogActionReader.ParseCommit(content, 1);
        Assert.Single(actions);
        Assert.IsType<TxnAction>(actions[0]);
    }

    [Theory]
    [InlineData("""{"add":{"partitionValues":{},"size":1,"modificationTime":1}}""")] // missing required path
    [InlineData("""{"protocol":{"minWriterVersion":2}}""")]                            // missing minReaderVersion
    [InlineData("""{"metaData":{"id":"t","format":{"provider":"parquet"},"partitionColumns":[]}}""")] // missing schemaString
    [InlineData("""{"add":{"path":"p","partitionValues":{},"size":"big","modificationTime":1}}""")] // wrong-typed size
    [InlineData("""{"txn":{"appId":"a","version":1},"add":{"path":"p"}}""")]           // two action keys on one line
    [InlineData("not json at all")]                                                    // malformed JSON
    [InlineData("""["array","not","object"]""")]                                       // top-level not an object
    public void FailsClosed_OnMalformedOrInvalidAction(string line)
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => DeltaLogActionReader.ParseCommit(Commit(line), version: 3));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        Assert.Contains("version 3", ex.Message, StringComparison.Ordinal); // error names the version
    }

    [Fact]
    public void FailsClosed_OnMalformedStatsJson()
    {
        ReadOnlyMemory<byte> content = Commit(
            """{"add":{"path":"p","partitionValues":{},"size":1,"modificationTime":1,"stats":"{not valid json"}}""");

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => DeltaLogActionReader.ParseCommit(content, 1));
        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
    }

    [Fact]
    public void AddDataChange_DefaultsTrueWhenAbsent()
    {
        ReadOnlyMemory<byte> content = Commit(
            """{"add":{"path":"p","partitionValues":{},"size":1,"modificationTime":1}}""");
        var add = Assert.IsType<AddFileAction>(DeltaLogActionReader.ParseCommit(content, 1)[0]);
        Assert.True(add.DataChange);
        Assert.Null(add.Stats);
        Assert.Empty(add.Tags);
    }
}
