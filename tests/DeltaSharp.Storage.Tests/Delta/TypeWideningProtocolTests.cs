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

    private static readonly StructType CleanSchema =
        new(new[] { new StructField("id", DataTypes.LongType, nullable: false) });

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
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(new ProtocolAction(1, 2, [], []), CleanSchema, NoConfig);

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
            new ProtocolAction(3, 7, ["columnMapping"], ["columnMapping"]), CleanSchema, NoConfig);

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
            new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening"]), CleanSchema, NoConfig);

        Assert.Equal(new[] { "typeWidening" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening" }, upgraded.WriterFeatures.ToArray());
    }

    [Fact]
    public void UpgradeProtocol_WithPreviewSpelling_DoesNotAddStableDuplicate()
    {
        // A table declaring the older `typeWidening-preview` spelling is already supported, so the upgrade
        // leaves its feature lists unchanged (never adds the stable name alongside the preview one).
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["typeWidening-preview"], ["typeWidening-preview"]), CleanSchema, NoConfig);

        Assert.Equal(new[] { "typeWidening-preview" }, upgraded.ReaderFeatures.ToArray());
        Assert.Equal(new[] { "typeWidening-preview" }, upgraded.WriterFeatures.ToArray());
    }

    // ---- #568: enumerate + register invariants / checkConstraints (enforced per row at the write seam) ----

    [Fact]
    public void CheckConstraints_And_Invariants_RegisteredAsWriterFeaturesOnly()
    {
        Assert.Contains("checkConstraints", ProtocolSupport.SupportedWriterFeatures);
        Assert.Contains("invariants", ProtocolSupport.SupportedWriterFeatures);
        Assert.DoesNotContain("checkConstraints", ProtocolSupport.SupportedReaderFeatures);
        Assert.DoesNotContain("invariants", ProtocolSupport.SupportedReaderFeatures);
    }

    [Fact]
    public void UpgradeProtocol_LegacyTableWithCheckConstraint_EnumeratesCheckConstraints_WriterOnly()
    {
        // A legacy writer-2 table declaring a delta.constraints.* CHECK constraint keeps it ACTIVE across the
        // upgrade: `checkConstraints` is enumerated into writerFeatures (never readerFeatures) and enforced
        // per row (#581), rather than being silently dropped.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []),
            CleanSchema,
            new Dictionary<string, string> { ["delta.constraints.positive_id"] = "id > 0" });

        Assert.Contains("checkConstraints", upgraded.WriterFeatures);
        Assert.DoesNotContain("checkConstraints", upgraded.ReaderFeatures);
        ProtocolSupport.EnsureWritable(upgraded); // the upgraded protocol is writable by this build
    }

    [Fact]
    public void UpgradeProtocol_NoConstraintOrInvariant_DoesNotEnumerateEither()
    {
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), CleanSchema, NoConfig);

        Assert.DoesNotContain("checkConstraints", upgraded.WriterFeatures);
        Assert.DoesNotContain("invariants", upgraded.WriterFeatures);
    }

    [Fact]
    public void UpgradeProtocol_LegacyAppendOnlyTable_EnumeratesAppendOnly_WriterOnly()
    {
        // A legacy writer-2 table with delta.appendOnly=true keeps append-only ACTIVE across the upgrade:
        // `appendOnly` is enumerated into writerFeatures (Delta "Active Features"). It is a WRITER-only
        // feature, so it never appears in readerFeatures. `typeWidening` is still added to both.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), CleanSchema, AppendOnlyConfig);

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
        ProtocolAction absent = TypeWideningFeature.UpgradeProtocol(new ProtocolAction(1, 2, [], []), CleanSchema, NoConfig);
        Assert.DoesNotContain("appendOnly", absent.WriterFeatures);

        ProtocolAction disabled = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), CleanSchema,
            new Dictionary<string, string> { ["delta.appendOnly"] = "false" });
        Assert.DoesNotContain("appendOnly", disabled.WriterFeatures);
    }

    [Fact]
    public void UpgradeProtocol_AppendOnlyAlreadyEnumerated_DoesNotDuplicate()
    {
        // A writer-7 table that already names appendOnly upgrades to an identical writerFeatures list.
        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(3, 7, ["typeWidening"], ["typeWidening", "appendOnly"]), CleanSchema, AppendOnlyConfig);

        Assert.Equal(new[] { "typeWidening", "appendOnly" }, upgraded.WriterFeatures.ToArray());
    }

    [Fact]
    public void UpgradeProtocol_MalformedAppendOnlyValue_FailsClosed()
    {
        // #549 (Architect/Balanced/Reliability): a legacy table carrying a non-boolean delta.appendOnly value
        // fails CLOSED on upgrade (MalformedAction) rather than silently NOT enumerating the feature — the
        // upgrade must not drop an ambiguous append-only guarantee. Mirrors AppendOnlyFeature.IsEnabled's
        // golden (Scala String.toBoolean) throw, reached transitively through UpgradeProtocol.
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() =>
            TypeWideningFeature.UpgradeProtocol(
                new ProtocolAction(1, 2, [], []), CleanSchema,
                new Dictionary<string, string> { ["delta.appendOnly"] = "garbage" }));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
    }

    [Fact]
    public void UpgradeProtocol_LegacyTableWithAllStructPathInvariant_EnumeratesInvariants()
    {
        // An invariant reached by an ALL-STRUCT path (`payload.x`) is collected + enforced (#606), so the
        // upgrade must enumerate the `invariants` writer feature so it stays active rather than silently
        // deactivating on upgrade to writer 7 (it is enforced per row, #581/#568).
        var schema = new StructType(new[] { new StructField("payload", InvariantInnerStruct(), nullable: true) });

        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), schema, ImmutableDictionary<string, string>.Empty);

        Assert.Contains("invariants", upgraded.WriterFeatures);
        Assert.DoesNotContain("invariants", upgraded.ReaderFeatures); // writer-only feature
        ProtocolSupport.EnsureWritable(upgraded);
    }

    [Fact]
    public void UpgradeProtocol_LegacyTableWithDeepStructPathInvariant_EnumeratesInvariants()
    {
        // Struct recursion must work beyond one level: an invariant reached by a TWO-level all-struct path
        // (`payload.inner.x`) is collected + enforced (#606's recursion), so enumeration must also recurse past
        // depth 1 (guards against a hypothetical "only look one struct deep" regression).
        var inner = new StructType(new[] { new StructField("inner", InvariantInnerStruct(), nullable: true) });
        var schema = new StructType(new[] { new StructField("payload", inner, nullable: true) });

        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), schema, ImmutableDictionary<string, string>.Empty);

        Assert.Contains("invariants", upgraded.WriterFeatures);
    }

    [Theory]
    [InlineData("map-value")]   // map< string, struct< x:invariant > >
    [InlineData("map-key")]     // map< struct< x:invariant >, long >
    public void UpgradeProtocol_LegacyTableWithInvariantUnderArrayOrMap_DoesNotEnumerateInvariants(string nesting)
    {
        // #612: feature enumeration recurses into STRUCTs only (Delta's Invariants.getFromSchema uses
        // checkComplexTypes = false), matching the invariant COLLECTION path (#606). An invariant reached THROUGH
        // an array element or a map key/value is IGNORED — never collected, never enforced — so the `invariants`
        // writer feature must NOT be declared for it (a table whose only invariant is under a collection would
        // otherwise over-declare a feature it does not enforce, diverging from Delta).
        StructType inner = InvariantInnerStruct();
        DataType nested = nesting switch
        {
            "array" => new ArrayType(inner),
            "map-value" => new MapType(DataTypes.StringType, inner),
            _ => new MapType(inner, DataTypes.LongType),
        };
        var schema = new StructType(new[] { new StructField("payload", nested, nullable: true) });

        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), schema, ImmutableDictionary<string, string>.Empty);

        Assert.DoesNotContain("invariants", upgraded.WriterFeatures);
    }

    [Fact]
    public void UpgradeProtocol_LegacyTableWithInvariantOnTopLevelArrayField_EnumeratesInvariants()
    {
        // The #612 restriction governs descent INTO a collection's elements only. An invariant declared DIRECTLY
        // on a top-level array/map FIELD (the field itself is on an all-struct path from the root) is still
        // collected (#606's CollectInvariants), so it is still enumerated.
        var arrayFieldWithInvariant = new StructField(
            "tags", new ArrayType(DataTypes.StringType), nullable: true,
            FieldMetadata.FromEntries(new[]
            {
                new KeyValuePair<string, string>("delta.invariants", "{\"expression\":{\"expression\":\"size(tags) > 0\"}}"),
            }));
        var schema = new StructType(new[] { arrayFieldWithInvariant });

        ProtocolAction upgraded = TypeWideningFeature.UpgradeProtocol(
            new ProtocolAction(1, 2, [], []), schema, ImmutableDictionary<string, string>.Empty);

        Assert.Contains("invariants", upgraded.WriterFeatures);
    }

    // A struct `{ x:long }` whose field `x` carries a `delta.invariants` (`x > 0`).
    private static StructType InvariantInnerStruct() =>
        new(new[]
        {
            new StructField(
                "x", DataTypes.LongType, nullable: true,
                FieldMetadata.FromEntries(new[]
                {
                    new KeyValuePair<string, string>("delta.invariants", "{\"expression\":{\"expression\":\"x > 0\"}}"),
                })),
        });
}
