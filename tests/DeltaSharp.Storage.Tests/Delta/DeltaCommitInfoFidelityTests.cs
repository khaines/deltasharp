using System.Collections.Immutable;
using System.Text.Json;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #510 — <c>commitInfo</c> fidelity for DESCRIBE HISTORY. Proves that each write operation records the
/// Delta-ecosystem provenance (<c>operation</c>, <c>operationParameters</c>, a deterministic
/// <c>timestamp</c> pinned by an injected <see cref="TimeProvider"/>, and <c>engineInfo</c>) alongside the
/// engine's idempotency <c>txnId</c>, and that these typed fields round-trip through the writer/reader with
/// <c>operationParameters</c> values serialized as valid JSON tokens.
/// </summary>
public sealed class DeltaCommitInfoFidelityTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    // A pinned, non-zero instant so the recorded commitInfo.timestamp is deterministic and assertable.
    private static readonly DateTimeOffset Instant = new(2031, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly StructType Schema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("value", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
    });

    public DeltaCommitInfoFidelityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "commitinfo-tests-" + Guid.NewGuid().ToString("N"));
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

    private DeltaTableWriter Writer() =>
        new(new DeltaLog(_backend), new DeltaCommitter(_backend, new FixedTimeProvider(Instant)), new FixedTimeProvider(Instant));

    private DeltaLog Log() => new(_backend);

    private static StagedDataFile Staged(string path, params (string Key, string? Value)[] partition)
    {
        ImmutableSortedDictionary<string, string?>.Builder builder =
            ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in partition)
        {
            builder[key] = value;
        }

        return new StagedDataFile(path, builder.ToImmutable(), Size: 1L, ModificationTime: 1L, Stats: null);
    }

    private async Task<CommitInfoAction> ReadCommitInfoAsync(long version)
    {
        IReadOnlyList<DeltaAction> committed = await Log().ReadCommitActionsAsync(version, CancellationToken.None);
        return committed.OfType<CommitInfoAction>().Single();
    }

    [Fact]
    public async Task Append_WritesWriteCommitInfo_WithPinnedTimestamp_EngineInfo_AndTxnId()
    {
        // Create the table (v0), then a blind append (v1). The append records operation="WRITE",
        // mode="Append", the injected timestamp, engineInfo, and still the idempotency txnId.
        await Writer().CreateOrAppendAsync(Schema, Array.Empty<string>(), new[] { Staged("a.parquet") });
        DeltaCommitResult result = await Writer().AppendAsync(Schema, new[] { Staged("b.parquet") });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version);
        Assert.Equal("WRITE", commitInfo.Operation);
        Assert.Equal("\"Append\"", commitInfo.OperationParameters!["mode"]);
        Assert.Equal("[]", commitInfo.OperationParameters!["partitionBy"]);
        Assert.Equal(Instant.ToUnixTimeMilliseconds(), commitInfo.Timestamp);
        Assert.StartsWith("DeltaSharp/", commitInfo.EngineInfo);
        Assert.True(commitInfo.Entries.ContainsKey("txnId"));
    }

    [Fact]
    public async Task Overwrite_WritesWriteCommitInfo_WithOverwriteMode()
    {
        await Writer().CreateOrAppendAsync(Schema, Array.Empty<string>(), new[] { Staged("a.parquet") });
        DeltaCommitResult result = await Writer().OverwriteAsync(Schema, new[] { Staged("b.parquet") });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version);
        Assert.Equal("WRITE", commitInfo.Operation);
        Assert.Equal("\"Overwrite\"", commitInfo.OperationParameters!["mode"]);
    }

    [Fact]
    public async Task CreateTable_WritesCreateTableCommitInfo_WithPartitionBy()
    {
        // A partitioned create (v0) records operation="CREATE TABLE" and partitionBy=["region"].
        DeltaCommitResult result = await Writer().CreateOrAppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { Staged("region=US/p.parquet", ("region", "US")) });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version);
        Assert.Equal("CREATE TABLE", commitInfo.Operation);
        Assert.Equal("[\"region\"]", commitInfo.OperationParameters!["partitionBy"]);
        Assert.Equal(Instant.ToUnixTimeMilliseconds(), commitInfo.Timestamp);
    }

    [Fact]
    public async Task OperationParameters_SerializeAsValidJsonTokens()
    {
        // The operationParameters values are JSON-encoded: mode is a JSON string ("Append") and partitionBy
        // is a JSON array (["region"]) — parse each to prove the on-disk shape is valid JSON, not raw text.
        DeltaCommitResult result = await Writer().CreateOrAppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { Staged("region=US/p.parquet", ("region", "US")) });
        await Writer().AppendAsync(
            PartitionedSchema, new[] { Staged("region=US/p2.parquet", ("region", "US")) });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version + 1);

        using JsonDocument modeDoc = JsonDocument.Parse(commitInfo.OperationParameters!["mode"]);
        Assert.Equal(JsonValueKind.String, modeDoc.RootElement.ValueKind);
        Assert.Equal("Append", modeDoc.RootElement.GetString());

        using JsonDocument partitionByDoc = JsonDocument.Parse(commitInfo.OperationParameters!["partitionBy"]);
        Assert.Equal(JsonValueKind.Array, partitionByDoc.RootElement.ValueKind);
        Assert.Equal("region", partitionByDoc.RootElement[0].GetString());
    }

    [Fact]
    public void TypedFields_RoundTripThroughWriterAndReader()
    {
        // A DeltaSharp-written commitInfo re-reads its own typed fields (write→read symmetry).
        var commitInfo = new CommitInfoAction(
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal).Add("txnId", "nonce-9"),
            Timestamp: 1_700_000_500_000L,
            Operation: "WRITE",
            OperationParameters: ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("mode", "\"Overwrite\"")
                .Add("partitionBy", "[\"a\",\"b\"]"),
            EngineInfo: "DeltaSharp/9.9.9");

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { commitInfo });
        IReadOnlyList<DeltaAction> parsed = DeltaLogActionReader.ParseCommit(bytes, version: 1);
        CommitInfoAction roundTripped = Assert.IsType<CommitInfoAction>(Assert.Single(parsed));

        Assert.Equal(1_700_000_500_000L, roundTripped.Timestamp);
        Assert.Equal("WRITE", roundTripped.Operation);
        Assert.Equal("\"Overwrite\"", roundTripped.OperationParameters!["mode"]);
        Assert.Equal("[\"a\",\"b\"]", roundTripped.OperationParameters!["partitionBy"]);
        Assert.Equal("DeltaSharp/9.9.9", roundTripped.EngineInfo);
        Assert.Equal("nonce-9", roundTripped.Entries["txnId"]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
