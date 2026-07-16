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

    private static readonly IReadOnlyDictionary<string, string> NoConfig =
        ImmutableDictionary<string, string>.Empty;

    private static readonly IReadOnlyDictionary<string, string> AppendOnlyConfig =
        new Dictionary<string, string> { ["delta.appendOnly"] = "true" };

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
    public void AppendOnly_IsRegistered_AsWriterFeatureOnly()
    {
        // #549: appendOnly is a WRITER-only legacy feature — registered so a writer-7 table naming it is
        // writable, but never a reader feature (a v1 reader reads an append-only table unchanged).
        Assert.Contains("appendOnly", ProtocolSupport.SupportedWriterFeatures);
        Assert.DoesNotContain("appendOnly", ProtocolSupport.SupportedReaderFeatures);
    }

    [Fact]
    public void EnsureWritable_AllowsV7Table_DeclaringAppendOnly()
    {
        // A writer-7 table enumerating appendOnly (writer-only) is writable; the feature is implemented
        // (enforced at commit), so the writer protocol gate opens.
        ProtocolSupport.EnsureWritable(new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening", "appendOnly"]));
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
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(new ProtocolAction(1, 2, [], []), NoConfig);

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
            new ProtocolAction(3, 7, ["columnMapping"], ["columnMapping"]), NoConfig);

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
            new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening"]), NoConfig);

        Assert.Equal(new[] { "typeWidening" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening" }, upgraded.WriterFeatures.ToArray());
    }

    [Fact]
    public void UpgradeProtocol_WithPreviewSpelling_DoesNotAddStableDuplicate()
    {
        // A table declaring the older `typeWidening-preview` spelling is already supported, so the upgrade
        // leaves its feature lists unchanged (never adds the stable name alongside the preview one).
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["typeWidening-preview"], ["typeWidening-preview"]), NoConfig);

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
    public void EnsureUpgradeable_LegacyAppendOnlyTable_IsAllowed()
    {
        // #549: an active delta.appendOnly=true no longer blocks the upgrade — UpgradeProtocol enumerates the
        // appendOnly writer feature and the committer enforces it, so append-only stays active. (Constraints /
        // invariants still fail closed — see the *_Throws tests below and #568.)
        TypeWideningFeature.EnsureUpgradeable(
            new ProtocolAction(1, 2, [], []),
            new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
            new Dictionary<string, string> { ["delta.appendOnly"] = "true" });
    }

    [Fact]
    public void UpgradeProtocol_LegacyAppendOnlyTable_EnumeratesAppendOnly_WriterOnly()
    {
        // A legacy writer-2 table with delta.appendOnly=true keeps append-only ACTIVE across the upgrade:
        // `appendOnly` is enumerated into writerFeatures (Delta "Active Features"). It is a WRITER-only
        // feature, so it never appears in readerFeatures. `typeWidening` is still added to both.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), AppendOnlyConfig);

        Assert.Equal(ProtocolSupport.TableFeaturesReaderVersion, upgraded.MinReaderVersion);
        Assert.Equal(ProtocolSupport.TableFeaturesWriterVersion, upgraded.MinWriterVersion);
        Assert.Contains("appendOnly", upgraded.WriterFeatures);
        Assert.DoesNotContain("appendOnly", upgraded.ReaderFeatures);
        Assert.Equal(new[] { "typeWidening" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening", "appendOnly" }, upgraded.WriterFeatures.ToArray());
        // The upgraded protocol is one this build can itself read and write back (appendOnly is registered).
        ProtocolSupport.EnsureReadable(upgraded);
        ProtocolSupport.EnsureWritable(upgraded);
    }

    [Fact]
    public void UpgradeProtocol_NoActiveAppendOnly_DoesNotEnumerateAppendOnly()
    {
        // delta.appendOnly is absent (or false) → the feature is not active, so it is NOT enumerated.
        ProtocolAction absent = TypeWideningFeature.UpgradeProtocol(new ProtocolAction(1, 2, [], []), NoConfig);
        Assert.DoesNotContain("appendOnly", absent.WriterFeatures);

        ProtocolAction disabled = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []),
            new Dictionary<string, string> { ["delta.appendOnly"] = "false" });
        Assert.DoesNotContain("appendOnly", disabled.WriterFeatures);
    }

    [Fact]
    public void UpgradeProtocol_AppendOnlyAlreadyEnumerated_DoesNotDuplicate()
    {
        // A writer-7 table that already names appendOnly upgrades to an identical writerFeatures list.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening", "appendOnly"]), AppendOnlyConfig);

        Assert.Equal(new[] { "typeWidening", "appendOnly" }, upgraded.WriterFeatures.ToArray());
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
    public void EnsureUpgradeable_LegacyTableWithCheckConstraint_Throws()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            TypeWideningFeature.EnsureUpgradeable(
                new ProtocolAction(1, 2, [], []),
                new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) }),
                new Dictionary<string, string> { ["delta.constraints.ck"] = "id > 0" }));
        Assert.Contains("#568", ex.Message, StringComparison.Ordinal);
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
        Assert.Contains("#568", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("struct")]      // struct< x:invariant >
    [InlineData("array")]       // array< struct< x:invariant > >
    [InlineData("map-value")]   // map< string, struct< x:invariant > >
    [InlineData("map-key")]     // map< struct< x:invariant >, long >
    public void EnsureUpgradeable_LegacyTableWithNestedColumnInvariant_Throws(string nesting)
    {
        // A column invariant reachable through a struct field, an array element, or a map key/value struct is
        // a valid Delta construct (Spark collects/enforces invariants through array/map elements). The guard
        // must recurse through ALL of these so such a foreign table is refused rather than silently
        // deactivated on upgrade to writer 7.
        var invariantLeaf = new StructField(
            "x", DataTypes.LongType, nullable: true,
            FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("delta.invariants", "{\"expression\":{\"expression\":\"x > 0\"}}"),
            }));
        var inner = new StructType(new[] { invariantLeaf });
        DataType nested = nesting switch
        {
            "struct" => inner,
            "array" => new ArrayType(inner),
            "map-value" => new MapType(DataTypes.StringType, inner),
            _ => new MapType(inner, DataTypes.LongType),
        };
        var schema = new StructType(new[] { new StructField("payload", nested, nullable: true) });

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            TypeWideningFeature.EnsureUpgradeable(
                new ProtocolAction(1, 2, [], []), schema, ImmutableDictionary<string, string>.Empty));
        Assert.Contains("#568", ex.Message, StringComparison.Ordinal);
    }
}
