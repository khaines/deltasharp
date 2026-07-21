using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Verifies <see cref="DeltaLogActionWriter"/> is the exact inverse of <see cref="DeltaLogActionReader"/>:
/// every action kind round-trips through serialize→parse with all replay-relevant fields preserved, and
/// re-serialization is byte-stable (the serializer is deterministic — a precondition of the commit
/// engine's stable-nonce recovery, design §2.11.4).
/// </summary>
public sealed class DeltaLogActionWriterTests
{
    private static ImmutableSortedDictionary<string, string> StringMap(params (string Key, string Value)[] entries)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in entries)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    private static ImmutableSortedDictionary<string, string?> NullableStringMap(params (string Key, string? Value)[] entries)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in entries)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    private static IReadOnlyList<DeltaAction> RoundTrip(params DeltaAction[] actions)
    {
        byte[] bytes = DeltaLogActionWriter.SerializeCommit(actions);
        return DeltaLogActionReader.ParseCommit(bytes, version: 1);
    }

    [Fact]
    public void SerializesAllSixActionKinds_PreservingReplayFields()
    {
        // AC (serializer): protocol/metaData/add/remove/txn/commitInfo each round-trip with every field
        // the reader extracts for replay, in file order.
        var protocol = new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
        var metadata = new MetadataAction(
            "t-1",
            "orders",
            "the orders table",
            new TableFormat("parquet", StringMap(("compression", "snappy"))),
            "{\"type\":\"struct\",\"fields\":[]}",
            ImmutableArray.Create("dt"),
            StringMap(("delta.appendOnly", "true")),
            1700000000000L);
        var add = new AddFileAction(
            "dt=2024/part-0.parquet",
            NullableStringMap(("dt", "2024")),
            1024L,
            1700000001000L,
            DataChange: true,
            Stats: null,
            Tags: StringMap(("z", "1")));
        var remove = new RemoveFileAction(
            "dt=2023/old.parquet",
            1700000002000L,
            DataChange: false,
            ExtendedFileMetadata: true,
            NullableStringMap(("dt", "2023")),
            512L,
            StringMap(("rk", "rv")));
        var txn = new TxnAction("stream-a", 42L, 1700000003000L);
        // #510: operation/operationParameters/timestamp/engineInfo are now TYPED commitInfo fields (the
        // Delta ecosystem reads them for DESCRIBE HISTORY); an arbitrary extra key stays flat Entries.
        var commitInfo = new CommitInfoAction(
            StringMap(("txnId", "nonce-1")),
            Timestamp: 1700000004000L,
            Operation: "WRITE",
            OperationParameters: StringMap(("mode", "\"Append\""), ("partitionBy", "[\"dt\"]")),
            EngineInfo: "test");

        IReadOnlyList<DeltaAction> actions =
            RoundTrip(commitInfo, protocol, metadata, add, remove, txn);

        Assert.Equal(6, actions.Count);

        var parsedCommitInfo = Assert.IsType<CommitInfoAction>(actions[0]);
        Assert.Equal(1700000004000L, parsedCommitInfo.Timestamp);
        Assert.Equal("WRITE", parsedCommitInfo.Operation);
        Assert.Equal("\"Append\"", parsedCommitInfo.OperationParameters!["mode"]);
        Assert.Equal("[\"dt\"]", parsedCommitInfo.OperationParameters!["partitionBy"]);
        Assert.Equal("test", parsedCommitInfo.EngineInfo);
        Assert.Equal("nonce-1", parsedCommitInfo.Entries["txnId"]);

        var parsedProtocol = Assert.IsType<ProtocolAction>(actions[1]);
        Assert.Equal(1, parsedProtocol.MinReaderVersion);
        Assert.Equal(2, parsedProtocol.MinWriterVersion);
        Assert.Empty(parsedProtocol.ReaderFeatures);
        Assert.Empty(parsedProtocol.WriterFeatures);

        var parsedMetadata = Assert.IsType<MetadataAction>(actions[2]);
        Assert.Equal("t-1", parsedMetadata.Id);
        Assert.Equal("orders", parsedMetadata.Name);
        Assert.Equal("the orders table", parsedMetadata.Description);
        Assert.Equal("parquet", parsedMetadata.Format.Provider);
        Assert.Equal("snappy", parsedMetadata.Format.Options["compression"]);
        Assert.Equal("{\"type\":\"struct\",\"fields\":[]}", parsedMetadata.SchemaString);
        Assert.Equal("dt", Assert.Single(parsedMetadata.PartitionColumns));
        Assert.Equal("true", parsedMetadata.Configuration["delta.appendOnly"]);
        Assert.Equal(1700000000000L, parsedMetadata.CreatedTime);

        var parsedAdd = Assert.IsType<AddFileAction>(actions[3]);
        Assert.Equal("dt=2024/part-0.parquet", parsedAdd.Path);
        Assert.Equal("2024", parsedAdd.PartitionValues["dt"]);
        Assert.Equal(1024L, parsedAdd.Size);
        Assert.Equal(1700000001000L, parsedAdd.ModificationTime);
        Assert.True(parsedAdd.DataChange);
        Assert.Null(parsedAdd.Stats);
        Assert.Equal("1", parsedAdd.Tags["z"]);

        var parsedRemove = Assert.IsType<RemoveFileAction>(actions[4]);
        Assert.Equal("dt=2023/old.parquet", parsedRemove.Path);
        Assert.Equal(1700000002000L, parsedRemove.DeletionTimestamp);
        Assert.False(parsedRemove.DataChange);
        Assert.True(parsedRemove.ExtendedFileMetadata);
        Assert.Equal("2023", parsedRemove.PartitionValues["dt"]);
        Assert.Equal(512L, parsedRemove.Size);
        Assert.Equal("rv", parsedRemove.Tags["rk"]);

        var parsedTxn = Assert.IsType<TxnAction>(actions[5]);
        Assert.Equal("stream-a", parsedTxn.AppId);
        Assert.Equal(42L, parsedTxn.Version);
        Assert.Equal(1700000003000L, parsedTxn.LastUpdated);
    }

    [Fact]
    public void SerializesCommitInfo_RoundTrippingOperationMetrics()
    {
        // #506: operationMetrics (a Delta Map<String,String>, like operationParameters) round-trips through
        // the writer/reader with each value preserved as its already-JSON-encoded token — a metric is a
        // quoted number-string like "3".
        var commitInfo = new CommitInfoAction(
            StringMap(("txnId", "nonce-2")),
            Timestamp: 1700000005000L,
            Operation: "OPTIMIZE",
            OperationParameters: StringMap(("predicate", "\"[]\"")),
            OperationMetrics: StringMap(
                ("numAddedFiles", "\"1\""),
                ("numRemovedFiles", "\"4\""),
                ("numAddedBytes", "\"2048\""),
                ("numRemovedBytes", "\"4096\""),
                ("numRows", "\"8\"")),
            EngineInfo: "test");

        var parsed = Assert.IsType<CommitInfoAction>(Assert.Single(RoundTrip(commitInfo)));

        Assert.Equal("OPTIMIZE", parsed.Operation);
        Assert.Equal("\"[]\"", parsed.OperationParameters!["predicate"]);
        Assert.Equal("\"1\"", parsed.OperationMetrics!["numAddedFiles"]);
        Assert.Equal("\"4\"", parsed.OperationMetrics!["numRemovedFiles"]);
        Assert.Equal("\"2048\"", parsed.OperationMetrics!["numAddedBytes"]);
        Assert.Equal("\"4096\"", parsed.OperationMetrics!["numRemovedBytes"]);
        Assert.Equal("\"8\"", parsed.OperationMetrics!["numRows"]);
    }

    [Fact]
    public void SerializesCommitInfo_OmitsOperationMetrics_WhenAbsent()
    {
        // A commitInfo without operationMetrics (e.g. a WRITE) omits the key entirely and round-trips to null
        // (backward compatible — the field is optional).
        var commitInfo = new CommitInfoAction(
            StringMap(("txnId", "nonce-3")),
            Operation: "WRITE",
            OperationParameters: StringMap(("mode", "\"Append\"")));

        var parsed = Assert.IsType<CommitInfoAction>(Assert.Single(RoundTrip(commitInfo)));

        Assert.Null(parsed.OperationMetrics);
    }

    [Fact]
    public void ReSerializationIsByteStable()
    {
        // Serializer is deterministic: parse(serialize(A)) then serialize again yields byte-identical
        // output. Sidesteps ImmutableArray/map record-equality (which are not structural) while proving
        // the transform is a true fixed point.
        var actions = new DeltaAction[]
        {
            new ProtocolAction(3, 7, ImmutableArray.Create("deletionVectors"), ImmutableArray.Create("appendOnly", "invariants")),
            new AddFileAction(
                "part-0.parquet",
                NullableStringMap(("region", "us"), ("dt", null)),
                2048L,
                1700000009000L,
                DataChange: true,
                Stats: new FileStatistics(
                    NumRecords: 100L,
                    MinValues: ImmutableSortedDictionary.CreateRange(
                        StringComparer.Ordinal,
                        new[]
                        {
                            KeyValuePair.Create("id", DeltaStatValue.OfLong(1L)),
                            KeyValuePair.Create("name", DeltaStatValue.OfString("alice")),
                            KeyValuePair.Create("score", DeltaStatValue.OfDouble(1.5)),
                            KeyValuePair.Create("active", DeltaStatValue.OfBoolean(true)),
                        }),
                    MaxValues: ImmutableSortedDictionary.CreateRange(
                        StringComparer.Ordinal,
                        new[] { KeyValuePair.Create("id", DeltaStatValue.OfLong(100L)) }),
                    NullCount: ImmutableSortedDictionary.CreateRange(
                        StringComparer.Ordinal,
                        new[] { KeyValuePair.Create("name", 3L) }),
                    TightBounds: true),
                Tags: ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)),
        };

        byte[] first = DeltaLogActionWriter.SerializeCommit(actions);
        IReadOnlyList<DeltaAction> reparsed = DeltaLogActionReader.ParseCommit(first, version: 1);
        byte[] second = DeltaLogActionWriter.SerializeCommit(reparsed);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SerializesFileStatistics_RoundTrippingEveryStatKind()
    {
        // add.stats is a JSON-encoded string; numRecords/minValues/maxValues/nullCount/tightBounds and
        // each DeltaStatKind (string/long/double/boolean) survive the round-trip.
        var stats = new FileStatistics(
            NumRecords: 42L,
            MinValues: ImmutableSortedDictionary.CreateRange(
                StringComparer.Ordinal,
                new[]
                {
                    KeyValuePair.Create("s", DeltaStatValue.OfString("aaa")),
                    KeyValuePair.Create("i", DeltaStatValue.OfLong(-7L)),
                    KeyValuePair.Create("d", DeltaStatValue.OfDouble(3.25)),
                    KeyValuePair.Create("b", DeltaStatValue.OfBoolean(false)),
                }),
            MaxValues: ImmutableSortedDictionary.CreateRange(
                StringComparer.Ordinal,
                new[] { KeyValuePair.Create("i", DeltaStatValue.OfLong(9000L)) }),
            NullCount: ImmutableSortedDictionary.CreateRange(
                StringComparer.Ordinal,
                new[] { KeyValuePair.Create("s", 0L), KeyValuePair.Create("i", 4L) }),
            TightBounds: false);
        var add = new AddFileAction(
            "p.parquet",
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            10L,
            20L,
            DataChange: true,
            Stats: stats,
            Tags: ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

        var parsed = Assert.IsType<AddFileAction>(Assert.Single(RoundTrip(add)));
        FileStatistics? actual = parsed.Stats;
        Assert.NotNull(actual);
        Assert.Equal(42L, actual!.NumRecords);
        Assert.Equal(DeltaStatValue.OfString("aaa"), actual.MinValues["s"]);
        Assert.Equal(DeltaStatValue.OfLong(-7L), actual.MinValues["i"]);
        Assert.Equal(DeltaStatValue.OfDouble(3.25), actual.MinValues["d"]);
        Assert.Equal(DeltaStatValue.OfBoolean(false), actual.MinValues["b"]);
        Assert.Equal(DeltaStatValue.OfLong(9000L), actual.MaxValues["i"]);
        Assert.Equal(0L, actual.NullCount["s"]);
        Assert.Equal(4L, actual.NullCount["i"]);
        Assert.False(actual.TightBounds);
    }

    [Fact]
    public void EmptyFileStatistics_SerializesToEmptyObject_RoundTrippingToEmpty()
    {
        // A non-null-but-empty FileStatistics serializes to "{}" and parses back to Empty (distinct from
        // an absent stats which parses to null).
        var add = new AddFileAction(
            "p.parquet",
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            10L,
            20L,
            DataChange: true,
            Stats: FileStatistics.Empty,
            Tags: ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

        Assert.Equal("{}", DeltaLogActionWriter.SerializeStats(FileStatistics.Empty));

        var parsed = Assert.IsType<AddFileAction>(Assert.Single(RoundTrip(add)));
        Assert.NotNull(parsed.Stats);
        Assert.Null(parsed.Stats!.NumRecords);
        Assert.Empty(parsed.Stats.MinValues);
        Assert.Empty(parsed.Stats.NullCount);
        Assert.Null(parsed.Stats.TightBounds);
    }

    [Fact]
    public void PreservesNullPartitionValue_DistinctFromAbsent()
    {
        // A JSON null partition value (a genuinely-null partition, distinct from an absent key) survives.
        var add = new AddFileAction(
            "p.parquet",
            NullableStringMap(("dt", null), ("region", "us")),
            1L,
            1L,
            DataChange: true,
            Stats: null,
            Tags: ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

        var parsed = Assert.IsType<AddFileAction>(Assert.Single(RoundTrip(add)));
        Assert.Null(parsed.PartitionValues["dt"]);
        Assert.Equal("us", parsed.PartitionValues["region"]);
    }

    [Fact]
    public void SerializeCommit_RejectsUnknownActionType()
    {
        // The action switch fails closed on an unmodeled DeltaAction subtype rather than emitting nothing.
        var ex = Assert.Throws<ArgumentException>(() =>
            DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { new UnknownTestAction() }));
        Assert.Contains("UnknownTestAction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializesOneActionPerLine_NewlineDelimited()
    {
        // Commit files are newline-delimited JSON, one action per line (design §2.10.2).
        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[]
        {
            new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
            new TxnAction("a", 1L, null),
        });

        string text = Encoding.UTF8.GetString(bytes);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("{\"protocol\":", lines[0]);
        Assert.StartsWith("{\"txn\":", lines[1]);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void OmitsOptionalFields_WhenAbsent_RoundTrippingToDefaults()
    {
        // Optional fields (name/description/createdTime, txn.lastUpdated, remove size/partitionValues,
        // empty feature lists) are omitted and round-trip to the reader's defaults.
        var metadata = new MetadataAction(
            "id-only",
            Name: null,
            Description: null,
            new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)),
            "{}",
            ImmutableArray<string>.Empty,
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
            CreatedTime: null);
        var remove = new RemoveFileAction(
            "gone.parquet",
            DeletionTimestamp: null,
            DataChange: true,
            ExtendedFileMetadata: false,
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            Size: null,
            StringMap());
        var txn = new TxnAction("app", 5L, LastUpdated: null);

        IReadOnlyList<DeltaAction> actions = RoundTrip(metadata, remove, txn);

        var parsedMetadata = Assert.IsType<MetadataAction>(actions[0]);
        Assert.Null(parsedMetadata.Name);
        Assert.Null(parsedMetadata.Description);
        Assert.Null(parsedMetadata.CreatedTime);
        Assert.Empty(parsedMetadata.PartitionColumns);
        Assert.Empty(parsedMetadata.Configuration);

        var parsedRemove = Assert.IsType<RemoveFileAction>(actions[1]);
        Assert.Null(parsedRemove.DeletionTimestamp);
        Assert.Null(parsedRemove.Size);
        Assert.Empty(parsedRemove.PartitionValues);

        var parsedTxn = Assert.IsType<TxnAction>(actions[2]);
        Assert.Null(parsedTxn.LastUpdated);
    }

    [Fact]
    public void EmitsEmptyPartitionValues_WhenExtendedFileMetadataAndUnpartitioned()
    {
        // An unpartitioned remove with extendedFileMetadata:true must emit partitionValues:{} (an empty
        // JSON object), not omit the key — strict cross-engine readers (delta-standalone/-rs/-spark)
        // require partitionValues to be present alongside the extended metadata (issue #511).
        var remove = new RemoveFileAction(
            "gone.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: true,
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            Size: 128L,
            StringMap());

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"partitionValues\":{}", text, StringComparison.Ordinal);

        // Round-trips back to an empty partition map.
        var parsed = Assert.IsType<RemoveFileAction>(Assert.Single(RoundTrip(remove)));
        Assert.True(parsed.ExtendedFileMetadata);
        Assert.Empty(parsed.PartitionValues);
        Assert.Equal(128L, parsed.Size);
    }

    [Fact]
    public void EmitsPopulatedPartitionValues_WhenExtendedFileMetadataAndPartitioned()
    {
        // A partitioned remove with extendedFileMetadata:true still emits its populated partitionValues
        // (regression guard for issue #511).
        var remove = new RemoveFileAction(
            "dt=2023/old.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: true,
            NullableStringMap(("dt", "2023")),
            Size: 512L,
            StringMap());

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"partitionValues\":{\"dt\":\"2023\"}", text, StringComparison.Ordinal);

        var parsed = Assert.IsType<RemoveFileAction>(Assert.Single(RoundTrip(remove)));
        Assert.Equal("2023", parsed.PartitionValues["dt"]);
    }

    [Fact]
    public void OmitsPartitionValues_WhenBareTombstone()
    {
        // A bare tombstone (extendedFileMetadata:false) with an empty partition map still omits the
        // partitionValues key entirely — the extended-metadata fix does not touch this path (issue #511).
        var remove = new RemoveFileAction(
            "gone.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: false,
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            Size: null,
            StringMap());

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\"partitionValues\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitsPopulatedTags_WhenExtendedFileMetadata()
    {
        // A remove with extendedFileMetadata:true and non-empty tags emits its populated tags map — Delta's
        // extended-metadata remove carries the extended trio size+partitionValues+tags (issue #491). Strict
        // cross-engine readers and tag-bearing interop require the tags key present, and it round-trips.
        var remove = new RemoveFileAction(
            "dt=2023/old.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: true,
            NullableStringMap(("dt", "2023")),
            Size: 512L,
            StringMap(("INSERTION_TIME", "1700000000000"), ("ZCUBE_ID", "abc")));

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        // Emitted in Ordinal key order (deterministic serialization).
        Assert.Contains(
            "\"tags\":{\"INSERTION_TIME\":\"1700000000000\",\"ZCUBE_ID\":\"abc\"}",
            text,
            StringComparison.Ordinal);

        var parsed = Assert.IsType<RemoveFileAction>(Assert.Single(RoundTrip(remove)));
        Assert.True(parsed.ExtendedFileMetadata);
        Assert.Equal("1700000000000", parsed.Tags["INSERTION_TIME"]);
        Assert.Equal("abc", parsed.Tags["ZCUBE_ID"]);
    }

    [Fact]
    public void EmitsEmptyTags_WhenExtendedFileMetadataAndUntagged()
    {
        // An untagged remove with extendedFileMetadata:true emits tags:{} (an empty object), mirroring the
        // partitionValues:{} behaviour from #511: under extendedFileMetadata the extended trio's keys are
        // present, not omitted, so strict cross-engine readers see a complete extended remove (issue #491).
        var remove = new RemoveFileAction(
            "gone.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: true,
            ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal),
            Size: 128L,
            StringMap());

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"tags\":{}", text, StringComparison.Ordinal);

        var parsed = Assert.IsType<RemoveFileAction>(Assert.Single(RoundTrip(remove)));
        Assert.Empty(parsed.Tags);
    }

    [Fact]
    public void EmitsExtendedTrio_WhenExtendedFileMetadata()
    {
        // The extended trio — size + partitionValues + tags — is now consistently gated on
        // extendedFileMetadata (issue #491 consolidation): an extended remove emits all three keys.
        var remove = new RemoveFileAction(
            "dt=2023/old.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: true,
            NullableStringMap(("dt", "2023")),
            Size: 512L,
            StringMap(("rk", "rv")));

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"partitionValues\":{\"dt\":\"2023\"}", text, StringComparison.Ordinal);
        Assert.Contains("\"size\":512", text, StringComparison.Ordinal);
        Assert.Contains("\"tags\":{\"rk\":\"rv\"}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsExtendedTrio_WhenBareTombstone()
    {
        // A bare tombstone (extendedFileMetadata:false) emits NONE of the extended trio — even a populated
        // Size or tags map is withheld, because the extended keys are meaningful only under
        // extendedFileMetadata:true (issue #491 consolidation).
        var remove = new RemoveFileAction(
            "gone.parquet",
            DeletionTimestamp: 1700000000000L,
            DataChange: true,
            ExtendedFileMetadata: false,
            NullableStringMap(("dt", "2023")),
            Size: 512L,
            StringMap(("rk", "rv")));

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { remove });
        string text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\"partitionValues\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"size\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tags\"", text, StringComparison.Ordinal);
    }

    private sealed record UnknownTestAction : DeltaAction;
}
