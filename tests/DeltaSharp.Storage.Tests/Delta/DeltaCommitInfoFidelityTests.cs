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

    // Reads the RAW on-disk commitInfo JSON object for a version, so a test can assert the actual JSON value
    // KIND of an operationParameters entry (a Map<String,String> value must be a JSON string, never a bare
    // array/number) — the delta-standalone/legacy-Spark conformance the reader-based helper cannot see.
    private async Task<JsonElement> ReadRawOperationParametersAsync(long version)
    {
        string path = Path.Combine(_root, "_delta_log", version.ToString("D20") + ".json");
        string[] lines = await File.ReadAllLinesAsync(path);
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("commitInfo", out JsonElement commitInfo)
                && commitInfo.TryGetProperty("operationParameters", out JsonElement parameters))
            {
                return parameters.Clone();
            }
        }

        throw new InvalidOperationException($"No commitInfo.operationParameters found in version {version}.");
    }

    // Asserts an on-disk operationParameters LIST value is a double-encoded JSON string whose content parses
    // to a JSON array with the expected elements (delta-spark Map<String,String> shape).
    private static void AssertDoubleEncodedArray(
        JsonElement operationParameters, string key, params string[] expected)
    {
        JsonElement value = operationParameters.GetProperty(key);
        Assert.Equal(JsonValueKind.String, value.ValueKind); // NOT a bare array
        using JsonDocument content = JsonDocument.Parse(value.GetString()!);
        Assert.Equal(JsonValueKind.Array, content.RootElement.ValueKind);
        Assert.Equal(expected, content.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task Append_WritesWriteCommitInfo_WithPinnedTimestamp_EngineInfo_AndTxnId()
    {
        // Create the table (v0), then a blind append (v1). The append records operation="WRITE",
        // mode="Append", isBlindAppend=true, the injected timestamp, engineInfo, and still the txnId.
        await Writer().CreateOrAppendAsync(Schema, Array.Empty<string>(), new[] { Staged("a.parquet") });
        DeltaCommitResult result = await Writer().AppendAsync(Schema, new[] { Staged("b.parquet") });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version);
        Assert.Equal("WRITE", commitInfo.Operation);
        Assert.Equal("\"Append\"", commitInfo.OperationParameters!["mode"]); // scalar: plain JSON string
        Assert.Equal("\"[]\"", commitInfo.OperationParameters!["partitionBy"]); // list: double-encoded string
        Assert.Equal("\"true\"", commitInfo.OperationParameters!["isBlindAppend"]);
        Assert.Equal(Instant.ToUnixTimeMilliseconds(), commitInfo.Timestamp);
        Assert.StartsWith("DeltaSharp/", commitInfo.EngineInfo);
        Assert.True(commitInfo.Entries.ContainsKey("txnId"));

        // On disk, partitionBy is a JSON STRING whose content is the empty array (NOT a bare array).
        JsonElement parameters = await ReadRawOperationParametersAsync(result.Version);
        AssertDoubleEncodedArray(parameters, "partitionBy");
        Assert.Equal(JsonValueKind.String, parameters.GetProperty("mode").ValueKind);
        Assert.Equal("Append", parameters.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.String, parameters.GetProperty("isBlindAppend").ValueKind);
        Assert.Equal("true", parameters.GetProperty("isBlindAppend").GetString());
    }

    [Fact]
    public async Task Overwrite_WritesWriteCommitInfo_WithOverwriteMode_AndNotBlindAppend()
    {
        await Writer().CreateOrAppendAsync(Schema, Array.Empty<string>(), new[] { Staged("a.parquet") });
        DeltaCommitResult result = await Writer().OverwriteAsync(Schema, new[] { Staged("b.parquet") });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version);
        Assert.Equal("WRITE", commitInfo.Operation);
        Assert.Equal("\"Overwrite\"", commitInfo.OperationParameters!["mode"]);
        Assert.Equal("\"false\"", commitInfo.OperationParameters!["isBlindAppend"]); // full overwrite is not blind
    }

    [Fact]
    public async Task CreateTable_WritesCreateTableCommitInfo_WithPartitionBy()
    {
        // A partitioned create (v0) records operation="CREATE TABLE" and partitionBy content ["region"].
        DeltaCommitResult result = await Writer().CreateOrAppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { Staged("region=US/p.parquet", ("region", "US")) });

        CommitInfoAction commitInfo = await ReadCommitInfoAsync(result.Version);
        Assert.Equal("CREATE TABLE", commitInfo.Operation);
        Assert.Equal(Instant.ToUnixTimeMilliseconds(), commitInfo.Timestamp);

        // The stored token is a double-encoded JSON string: parse it to a string, then parse THAT to the array.
        using JsonDocument outer = JsonDocument.Parse(commitInfo.OperationParameters!["partitionBy"]);
        Assert.Equal(JsonValueKind.String, outer.RootElement.ValueKind);
        using JsonDocument inner = JsonDocument.Parse(outer.RootElement.GetString()!);
        Assert.Equal(new[] { "region" }, inner.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray());

        // On disk, partitionBy is a JSON STRING whose content parses to ["region"].
        JsonElement parameters = await ReadRawOperationParametersAsync(result.Version);
        AssertDoubleEncodedArray(parameters, "partitionBy", "region");
    }

    [Fact]
    public async Task OperationParameters_SerializeAsValidJsonTokens()
    {
        // operationParameters is a Map<String,String>: mode is a JSON string ("Append") whose content is a
        // scalar; partitionBy/predicate are JSON strings whose CONTENT parses to a JSON array — proving the
        // double-encoded, strict-reader-safe shape on disk (not a bare array).
        DeltaCommitResult result = await Writer().CreateOrAppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { Staged("region=US/p.parquet", ("region", "US")) });
        await Writer().AppendAsync(
            PartitionedSchema, new[] { Staged("region=US/p2.parquet", ("region", "US")) });

        JsonElement parameters = await ReadRawOperationParametersAsync(result.Version + 1);

        JsonElement mode = parameters.GetProperty("mode");
        Assert.Equal(JsonValueKind.String, mode.ValueKind);
        Assert.Equal("Append", mode.GetString());

        AssertDoubleEncodedArray(parameters, "partitionBy", "region");
    }

    [Fact]
    public void TypedFields_RoundTripThroughWriterAndReader()
    {
        // A DeltaSharp-written commitInfo re-reads its own typed fields (write→read symmetry), including the
        // double-encoded list tokens produced by DeltaCommitInfo.
        string partitionByToken = DeltaCommitInfo.JsonEncodedArray(new[] { "a", "b" });
        var commitInfo = new CommitInfoAction(
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal).Add("txnId", "nonce-9"),
            Timestamp: 1_700_000_500_000L,
            Operation: "WRITE",
            OperationParameters: ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("mode", DeltaCommitInfo.JsonString("Overwrite"))
                .Add("partitionBy", partitionByToken)
                .Add("isBlindAppend", DeltaCommitInfo.JsonBoolString(false)),
            EngineInfo: "DeltaSharp/9.9.9");

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { commitInfo });
        IReadOnlyList<DeltaAction> parsed = DeltaLogActionReader.ParseCommit(bytes, version: 1);
        CommitInfoAction roundTripped = Assert.IsType<CommitInfoAction>(Assert.Single(parsed));

        Assert.Equal(1_700_000_500_000L, roundTripped.Timestamp);
        Assert.Equal("WRITE", roundTripped.Operation);
        Assert.Equal("\"Overwrite\"", roundTripped.OperationParameters!["mode"]);
        Assert.Equal(partitionByToken, roundTripped.OperationParameters!["partitionBy"]);
        Assert.Equal("\"false\"", roundTripped.OperationParameters!["isBlindAppend"]);
        Assert.Equal("DeltaSharp/9.9.9", roundTripped.EngineInfo);
        Assert.Equal("nonce-9", roundTripped.Entries["txnId"]);

        // The partitionBy token's content still parses to the array after the round-trip.
        using JsonDocument content = JsonDocument.Parse(JsonDocument.Parse(partitionByToken).RootElement.GetString()!);
        Assert.Equal(new[] { "a", "b" }, content.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void LegacyMinimalCommitInfo_WithOnlyEntries_RoundTrips()
    {
        // Backward compat: an older/minimal commitInfo carrying ONLY flat Entries (all typed fields null)
        // round-trips through writer→reader without error and preserves its Entries.
        var commitInfo = new CommitInfoAction(
            ImmutableSortedDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("txnId", "legacy-nonce")
                .Add("readVersion", "6"));

        byte[] bytes = DeltaLogActionWriter.SerializeCommit(new DeltaAction[] { commitInfo });
        IReadOnlyList<DeltaAction> parsed = DeltaLogActionReader.ParseCommit(bytes, version: 1);
        CommitInfoAction roundTripped = Assert.IsType<CommitInfoAction>(Assert.Single(parsed));

        Assert.Null(roundTripped.Timestamp);
        Assert.Null(roundTripped.Operation);
        Assert.Null(roundTripped.OperationParameters);
        Assert.Null(roundTripped.EngineInfo);
        Assert.Equal("legacy-nonce", roundTripped.Entries["txnId"]);
        Assert.Equal("6", roundTripped.Entries["readVersion"]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
