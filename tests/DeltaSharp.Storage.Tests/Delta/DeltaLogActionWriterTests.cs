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
            512L);
        var txn = new TxnAction("stream-a", 42L, 1700000003000L);
        var commitInfo = new CommitInfoAction(StringMap(("operation", "WRITE"), ("engineInfo", "test")));

        IReadOnlyList<DeltaAction> actions =
            RoundTrip(commitInfo, protocol, metadata, add, remove, txn);

        Assert.Equal(6, actions.Count);

        var parsedCommitInfo = Assert.IsType<CommitInfoAction>(actions[0]);
        Assert.Equal("WRITE", parsedCommitInfo.Entries["operation"]);
        Assert.Equal("test", parsedCommitInfo.Entries["engineInfo"]);

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

        var parsedTxn = Assert.IsType<TxnAction>(actions[5]);
        Assert.Equal("stream-a", parsedTxn.AppId);
        Assert.Equal(42L, parsedTxn.Version);
        Assert.Equal(1700000003000L, parsedTxn.LastUpdated);
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
            Size: null);
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

    private sealed record UnknownTestAction : DeltaAction;
}
