using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Protocol-registration teeth for the Delta <c>typeWidening</c> table feature (#495). Verified against
/// Delta PROTOCOL.md "Type Widening": the feature is BOTH a reader and a writer feature, gated at reader
/// version 3 / writer version 7. The older <c>typeWidening-preview</c> spelling is accepted on READ. A
/// table declaring the feature must load and be writable through this build.
/// </summary>
public sealed class TypeWideningProtocolTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public TypeWideningProtocolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "typewiden-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void TypeWidening_IsRegistered_AsBothReaderAndWriterFeature()
    {
        Assert.Contains("typeWidening", ProtocolSupport.SupportedReaderFeatures);
        Assert.Contains("typeWidening", ProtocolSupport.SupportedWriterFeatures);
    }

    [Fact]
    public void TypeWideningPreview_IsAccepted_OnBothReaderAndWriterFeatureLists()
    {
        Assert.Contains("typeWidening-preview", ProtocolSupport.SupportedReaderFeatures);
        Assert.Contains("typeWidening-preview", ProtocolSupport.SupportedWriterFeatures);
    }

    [Fact]
    public void EnsureReadable_AllowsV3V7Table_DeclaringTypeWidening()
    {
        ProtocolSupport.EnsureReadable(new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening"]));
    }

    [Fact]
    public void EnsureWritable_AllowsV7Table_DeclaringTypeWidening()
    {
        ProtocolSupport.EnsureWritable(new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening"]));
    }

    [Fact]
    public void EnsureReadable_AllowsPreviewSpelling_OnRead()
    {
        ProtocolSupport.EnsureReadable(new ProtocolAction(3, 7, ["typeWidening-preview"], ["typeWidening-preview"]));
    }

    [Fact]
    public void EnsureWritable_AllowsPreviewSpelling_OnWrite()
    {
        // Mirror of EnsureReadable_AllowsPreviewSpelling_OnRead: the older `typeWidening-preview` spelling is
        // also accepted on the write-side protocol gate (it is in SupportedWriterFeatures for interop).
        ProtocolSupport.EnsureWritable(new ProtocolAction(3, 7, ["typeWidening-preview"], ["typeWidening-preview"]));
    }

    [Fact]
    public void TypeWideningFeature_Protocol_CommitsV3V7WithFeatureInBothLists()
    {
        ProtocolAction protocol = TypeWideningFeature.Protocol();
        Assert.Equal(3, protocol.MinReaderVersion);
        Assert.Equal(7, protocol.MinWriterVersion);
        Assert.Contains("typeWidening", protocol.ReaderFeatures);
        Assert.Contains("typeWidening", protocol.WriterFeatures);
    }

    [Fact]
    public async Task LoadSnapshot_Serves_TypeWideningEnabledTable()
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("typeWidening"),
            DeltaTestHarness.MetadataWithConfig(("delta.enableTypeWidening", "true")),
            DeltaTestHarness.Add("a.parquet"));

        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal(["a.parquet"], snapshot.ActiveFiles.Select(a => a.Path));
    }

    // ---- #534: UpgradeProtocol — enable type widening on an existing table's protocol ----

    [Fact]
    public void UpgradeProtocol_FromPlainV1V2_ProducesV3V7WithFeatureInBothLists()
    {
        // A plain (no-feature) reader-1 / writer-2 table upgrades to the table-features versions (3/7) with the
        // stable `typeWidening` feature added to both lists — matching the create-time shape (Protocol()).
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(new ProtocolAction(1, 2, [], []));

        Assert.Equal(ProtocolSupport.TableFeaturesReaderVersion, upgraded.MinReaderVersion);
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, upgraded.MinWriterVersion);
        Assert.Equal(new[] { "typeWidening" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening" }, upgraded.WriterFeatures.ToArray());
        // The upgraded protocol is one this build can itself read and write back.
        ProtocolSupport.EnsureReadable(upgraded);
        ProtocolSupport.EnsureWritable(upgraded);
    }

    [Fact]
    public void UpgradeProtocol_PreservesExistingFeatures()
    {
        // An existing feature (columnMapping) is preserved and `typeWidening` is added — never dropped.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["columnMapping"], ["columnMapping"]));

        Assert.Contains("columnMapping", upgraded.ReaderFeatures);
        Assert.Contains("columnMapping", upgraded.WriterFeatures);
        Assert.Contains("typeWidening", upgraded.ReaderFeatures);
        Assert.Contains("typeWidening", upgraded.WriterFeatures);
    }

    [Fact]
    public void UpgradeProtocol_WhenFeatureAlreadyPresent_DoesNotDuplicate()
    {
        // Idempotent in shape: a table already declaring `typeWidening` upgrades to an identical feature list
        // (no duplicate entry, versions already at the floor).
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening"]));

        Assert.Equal(new[] { "typeWidening" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening" }, upgraded.WriterFeatures.ToArray());
    }

    [Fact]
    public void UpgradeProtocol_WithPreviewSpelling_DoesNotAddStableDuplicate()
    {
        // A table declaring the older `typeWidening-preview` spelling is already supported, so the upgrade
        // leaves its feature lists unchanged (never adds the stable name alongside the preview one).
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["typeWidening-preview"], ["typeWidening-preview"]));

        Assert.Equal(new[] { "typeWidening-preview" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening-preview" }, upgraded.WriterFeatures.ToArray());
    }

    // ---- #534/#549: EnsureUpgradeable — refuse dropping an active legacy feature ----

    [Fact]
    public void EnsureUpgradeable_LegacyCleanTable_IsAllowed()
    {
        // A plain legacy (writer 2) table with no active appendOnly / constraint / invariant is upgradeable.
        TypeWideningFeature.EnsureUpgradeable(
            new ProtocolAction(1, 2, [], []),
            new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
            ImmutableDictionary<string, string>.Empty);
    }

    [Fact]
    public void EnsureUpgradeable_TableFeaturesTable_WithAppendOnlyConfig_IsAllowed()
    {
        // A table already on writer 7 has all active features explicitly enumerated (and UpgradeProtocol
        // preserves them), so it is always upgradeable — even if delta.appendOnly is set (that feature would
        // already be in writerFeatures on such a table).
        TypeWideningFeature.EnsureUpgradeable(
            new ProtocolAction(3, 7, ["appendOnly"], ["appendOnly"]),
            new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
            new Dictionary<string, string> { ["delta.appendOnly"] = "true" });
    }

    [Fact]
    public void EnsureUpgradeable_LegacyAppendOnlyTable_Throws()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            TypeWideningFeature.EnsureUpgradeable(
                new ProtocolAction(1, 2, [], []),
                new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
                new Dictionary<string, string> { ["delta.appendOnly"] = "true" }));
        Assert.Contains("#549", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureUpgradeable_LegacyTableWithCheckConstraint_Throws()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            TypeWideningFeature.EnsureUpgradeable(
                new ProtocolAction(1, 2, [], []),
                new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
                new Dictionary<string, string> { ["delta.constraints.ck"] = "id > 0" }));
        Assert.Contains("#549", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureUpgradeable_LegacyTableWithColumnInvariant_Throws()
    {
        var invariantField = new StructField(
            "id", DataTypes.LongType, nullable: false,
            FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("delta.invariants", "{\"expression\":{\"expression\":\"id > 0\"}}"),
            }));

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            TypeWideningFeature.EnsureUpgradeable(
                new ProtocolAction(1, 2, [], []),
                new StructType(new[] { invariantField }),
                ImmutableDictionary<string, string>.Empty));
        Assert.Contains("#549", ex.Message, StringComparison.Ordinal);
    }
}
