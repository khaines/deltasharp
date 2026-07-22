using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end tests for <see cref="DeltaTableWriter.EnableChangeDataFeedAsync"/> (increment 1, §2.7): a
/// metadata-only protocol + metaData upgrade that adds the <c>changeDataFeed</c> writer feature and sets
/// <c>delta.enableChangeDataFeed=true</c>, is idempotent on re-enable, and — being writer-only — never adds
/// a reader feature (INV C1). Mirrors <see cref="DeltaSchemaEvolutionWriterTests"/> + <see cref="DeltaTestHarness"/>.
/// </summary>
public sealed class ChangeDataFeedEnablementTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public ChangeDataFeedEnablementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacdf-tests-" + Guid.NewGuid().ToString("N"));
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

    private static StructType Schema() =>
        new(new[] { new StructField("value", DataTypes.IntegerType, nullable: true) });

    // A writer-only table-features protocol (reader v1, writer v7) declaring `changeDataFeed` in
    // writerFeatures ONLY — CDF is writer-only, so it must not appear in readerFeatures (which would fail
    // EnsureReadable, since changeDataFeed is not a supported reader feature).
    private static string CdfWriterProtocolLine() =>
        """{"protocol":{"minReaderVersion":1,"minWriterVersion":7,"writerFeatures":["changeDataFeed"]}}""";

    private DeltaTableWriter Writer() => new(_backend);

    private Task<Snapshot> LoadAsync() => new DeltaLog(_backend).LoadSnapshotAsync(version: null);

    [Fact]
    public async Task EnableChangeDataFeed_OnLegacyTable_UpgradesProtocolAndSetsProperty()
    {
        // A plain legacy table (reader 1 / writer 2, CDF not enabled): enabling CDF commits a metadata-only
        // protocol + metaData upgrade — writer → 7 with `changeDataFeed` in writerFeatures and
        // `delta.enableChangeDataFeed=true` in config. Writer-only: no reader feature is added (INV C1).
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(Schema()));

        DeltaCommitResult result = await Writer().EnableChangeDataFeedAsync();

        Assert.False(result.Skipped);
        Assert.Equal(1L, result.Version);

        Snapshot after = await LoadAsync();
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, after.Protocol.MinWriterVersion);
        Assert.Contains(ChangeDataFeedFeature.Feature, after.Protocol.WriterFeatures);
        Assert.DoesNotContain(ChangeDataFeedFeature.Feature, after.Protocol.ReaderFeatures); // writer-only
        Assert.Equal("true", after.Metadata.Configuration[ChangeDataFeedFeature.PropertyKey]);
    }

    [Fact]
    public async Task EnableChangeDataFeed_WhenAlreadyEnabled_IsIdempotentNoOp()
    {
        // A table that already declares the feature AND sets the property: EnableChangeDataFeedAsync writes
        // NO new version, reporting the current version with Skipped=true.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            CdfWriterProtocolLine(),
            DeltaTestHarness.MetadataWithSchemaAndConfig(
                Schema(),
                new[] { (ChangeDataFeedFeature.PropertyKey, "true") }));

        DeltaCommitResult result = await Writer().EnableChangeDataFeedAsync();

        Assert.True(result.Skipped);
        Assert.Equal(0L, result.Version);
        Assert.Equal(0L, (await LoadAsync()).Version); // no new version published
    }

    [Fact]
    public async Task EnableChangeDataFeed_ReEnable_IsIdempotent()
    {
        // Enabling twice: the second call is a no-op (Skipped) and the table stays enabled at the same version.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.Protocol(minReader: 1, minWriter: 2),
            DeltaTestHarness.MetadataWithSchema(Schema()));

        DeltaCommitResult first = await Writer().EnableChangeDataFeedAsync();
        Assert.False(first.Skipped);

        DeltaCommitResult second = await Writer().EnableChangeDataFeedAsync();
        Assert.True(second.Skipped);
        Assert.Equal(first.Version, second.Version);

        Snapshot after = await LoadAsync();
        Assert.Contains(ChangeDataFeedFeature.Feature, after.Protocol.WriterFeatures);
        Assert.Equal("true", after.Metadata.Configuration[ChangeDataFeedFeature.PropertyKey]);
    }

    [Fact]
    public async Task EnableChangeDataFeed_PreservesExistingFeatures()
    {
        // Enabling CDF on a table that already declares another feature (deletionVectors, reader 3 / writer 7)
        // keeps that feature and ADDS changeDataFeed to writerFeatures only — an existing feature is not dropped.
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("deletionVectors"),
            DeltaTestHarness.MetadataWithSchema(Schema()));

        await Writer().EnableChangeDataFeedAsync();

        Snapshot after = await LoadAsync();
        Assert.Contains("deletionVectors", after.Protocol.WriterFeatures);
        Assert.Contains(ChangeDataFeedFeature.Feature, after.Protocol.WriterFeatures);
        Assert.DoesNotContain(ChangeDataFeedFeature.Feature, after.Protocol.ReaderFeatures);
        Assert.Equal("true", after.Metadata.Configuration[ChangeDataFeedFeature.PropertyKey]);
    }
}
