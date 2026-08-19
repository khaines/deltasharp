using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit tests for <see cref="ColumnMappingIdentity"/> (#671) — the value type both halves of the change-feed
/// column-mapping identity-immutability gate compare. These exercise the comparison branches DIRECTLY (not
/// through the CDF read door), because several are unreachable end-to-end: nested column mapping is rejected
/// fail-closed UPSTREAM (so the recursion in <c>Collect</c> can only be exercised here), and the value-compare
/// branches (mode / partition columns / per-column field id + physical name / added-dropped-renamed columns)
/// are otherwise only covered incidentally. Each negative asserts the exact branch that must fail closed; each
/// positive guards against a false positive that would reject a legitimate table.
/// </summary>
public sealed class ColumnMappingIdentityTests
{
    // A flat 2-column mapped schema: logical (id: long, name: string) with the given field ids + physical names.
    private static string FlatSchema(
        long idFieldId, long nameFieldId, string idPhysical = "col-A", string namePhysical = "col-B") =>
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":" + idFieldId + ",\"delta.columnMapping.physicalName\":\"" + idPhysical + "\"}},"
        + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":" + nameFieldId + ",\"delta.columnMapping.physicalName\":\"" + namePhysical + "\"}}]}";

    private static string StructSchema(params string[] fields) =>
        "{\"type\":\"struct\",\"fields\":[" + string.Join(",", fields) + "]}";

    private static string MappedField(
        string name, string typeJson, long fieldId, string physicalName, bool nullable = true) =>
        "{\"name\":\"" + name + "\",\"type\":" + typeJson + ",\"nullable\":"
        + (nullable ? "true" : "false") + ",\"metadata\":"
        + "{\"delta.columnMapping.id\":" + fieldId + ",\"delta.columnMapping.physicalName\":\""
        + physicalName + "\"}}";

    private static string UnmappedField(string name, string typeJson, bool nullable = true) =>
        "{\"name\":\"" + name + "\",\"type\":" + typeJson + ",\"nullable\":"
        + (nullable ? "true" : "false") + ",\"metadata\":{}}";

    private static MetadataAction Meta(string schemaJson, string mode = "id", params string[] partitionColumns) =>
        new(
            Id: "t",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: schemaJson,
            PartitionColumns: ImmutableArray.Create(partitionColumns),
            Configuration: ImmutableSortedDictionary<string, string>.Empty.Add(ColumnMapping.ModeKey, mode),
            CreatedTime: null);

    private static ColumnMappingIdentity Identity(
        string schemaJson, string mode = "id", params string[] partitionColumns) =>
        ColumnMappingIdentity.FromMetadata(Meta(schemaJson, mode, partitionColumns));

    [Fact]
    public void IsImmutableFrom_IdenticalIdentity_True()
    {
        ColumnMappingIdentity end = Identity(FlatSchema(1, 2));
        Assert.True(end.IsImmutableFrom(Identity(FlatSchema(1, 2))));
    }

    [Fact]
    public void IsImmutableFrom_ModeDiffers_False()
    {
        ColumnMappingIdentity end = Identity(FlatSchema(1, 2), mode: "name");
        Assert.False(end.IsImmutableFrom(Identity(FlatSchema(1, 2), mode: "id")));
    }

    [Fact]
    public void IsImmutableFrom_FieldIdReassignedSameMode_False()
    {
        ColumnMappingIdentity end = Identity(FlatSchema(2, 1));         // swapped ids
        Assert.False(end.IsImmutableFrom(Identity(FlatSchema(1, 2))));  // original ids — a still-present column changed
    }

    [Fact]
    public void IsImmutableFrom_PhysicalNameChangedSameMode_False()
    {
        ColumnMappingIdentity end = Identity(FlatSchema(1, 2, namePhysical: "col-Z"));
        Assert.False(end.IsImmutableFrom(Identity(FlatSchema(1, 2, namePhysical: "col-B"))));
    }

    [Fact]
    public void IsImmutableFrom_PartitionColumnsDiffer_False()
    {
        // Guards the partition-column arm specifically (reliability-chaos R2 HIGH: otherwise mutation-vacuous).
        ColumnMappingIdentity end = Identity(FlatSchema(1, 2));                       // no partitions
        Assert.False(end.IsImmutableFrom(Identity(FlatSchema(1, 2), "id", "name")));  // partitioned by "name"
    }

    [Fact]
    public void IsImmutableFrom_PartitionColumnOrderDiffers_False()
    {
        ColumnMappingIdentity end = Identity(FlatSchema(1, 2), "id", "id", "name");
        Assert.False(end.IsImmutableFrom(Identity(FlatSchema(1, 2), "id", "name", "id")));  // ordinal + ordered
    }

    [Fact]
    public void IsImmutableFrom_NestedFieldIdReassigned_False()
    {
        // Guards the Collect recursion into nested structs (reliability-chaos R2 MEDIUM: otherwise
        // mutation-vacuous — nested column mapping is rejected upstream, so this is the ONLY way to reach it).
        // Top-level ids are identical; only the NESTED `payload.inner` field id changes (3 -> 99).
        static string Nested(long innerId) =>
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"payload\",\"type\":{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"inner\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":" + innerId + ",\"delta.columnMapping.physicalName\":\"col-C\"}}]},"
            + "\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

        ColumnMappingIdentity end = Identity(Nested(99));
        Assert.False(end.IsImmutableFrom(Identity(Nested(3))));   // caught only if the recursion descends
        Assert.True(end.IsImmutableFrom(Identity(Nested(99))));   // identical nested identity — no false positive
    }

    [Fact]
    public void IsImmutableFrom_LiteralDotAndNestedPathKeysDoNotCollide()
    {
        static string LiteralAndNested(long literalId, string literalPhysicalName) =>
            StructSchema(
                MappedField("a.b", "\"long\"", literalId, literalPhysicalName, nullable: true),
                MappedField(
                    "a",
                    StructSchema(MappedField("b", "\"long\"", 3, "col-nested-b", nullable: true)),
                    2,
                    "col-struct-a",
                    nullable: true));

        ColumnMappingIdentity end = Identity(LiteralAndNested(99, "col-literal-z"));
        ColumnMappingIdentity historical = Identity(LiteralAndNested(1, "col-literal-ab"));

        Assert.False(end.IsImmutableFrom(historical));
        Assert.True(historical.IsImmutableFrom(Identity(LiteralAndNested(1, "col-literal-ab"))));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void IsImmutableFrom_LiteralDotNestedCollapseReassignment_False(string mode)
    {
        static string NestedThenLiteral(long literalId, string literalPhysicalName) =>
            StructSchema(
                MappedField(
                    "a",
                    StructSchema(MappedField("b", "\"long\"", 2, "col-2", nullable: true)),
                    1,
                    "col-1",
                    nullable: true),
                MappedField("a.b", "\"long\"", literalId, literalPhysicalName, nullable: true));

        static string LiteralThenNested(long literalId, string literalPhysicalName) =>
            StructSchema(
                MappedField("a.b", "\"long\"", literalId, literalPhysicalName, nullable: true),
                MappedField(
                    "a",
                    StructSchema(MappedField("b", "\"long\"", 7, "col-7", nullable: true)),
                    1,
                    "col-1",
                    nullable: true));

        ColumnMappingIdentity end = Identity(NestedThenLiteral(7, "col-7"), mode);
        ColumnMappingIdentity historical = Identity(LiteralThenNested(99, "col-99"), mode);

        Assert.False(end.IsImmutableFrom(historical));
    }

    [Fact]
    public void IsImmutableFrom_LiteralDotVsNestedSameIdentityIsUpstreamBoundary_True()
    {
        string literalOnly = StructSchema(MappedField("a.b", "\"long\"", 7, "col-shared", nullable: true));
        string nestedOnly = StructSchema(
            MappedField(
                "a",
                StructSchema(MappedField("b", "\"long\"", 7, "col-shared", nullable: true)),
                6,
                "col-struct-a",
                nullable: true));

        // This cross-structure masquerade is not enforced inside ColumnMappingIdentity; mapped complex columns
        // are rejected upstream by ColumnMapping.EnsureLeaf/ColumnMappingProjection.ResolvePhysicalNames today,
        // and #676 must extend that coverage before nested column mapping is enabled.
        Assert.True(Identity(literalOnly).IsImmutableFrom(Identity(nestedOnly)));
        Assert.True(Identity(nestedOnly).IsImmutableFrom(Identity(literalOnly)));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void IsImmutableFrom_AddsLiteralDotColumnWithFreshIdentity_True(string mode)
    {
        string historical = StructSchema(
            MappedField(
                "a",
                StructSchema(MappedField("b", "\"long\"", 3, "col-nested-b", nullable: true)),
                2,
                "col-struct-a",
                nullable: true));
        string end = StructSchema(
            MappedField(
                "a",
                StructSchema(MappedField("b", "\"long\"", 3, "col-nested-b", nullable: true)),
                2,
                "col-struct-a",
                nullable: true),
            MappedField("a.b", "\"long\"", 99, "col-fresh-literal", nullable: true));

        Assert.True(Identity(end, mode).IsImmutableFrom(Identity(historical, mode)));
    }

    [Fact]
    public void IsImmutableFrom_NoneModeDotNamedAndNestedAddDrop_True()
    {
        string literalOnly = StructSchema(UnmappedField("a.b", "\"long\"", nullable: true));
        string nestedOnly = StructSchema(
            UnmappedField(
                "a",
                StructSchema(UnmappedField("b", "\"long\"", nullable: true)),
                nullable: true));

        Assert.True(Identity(literalOnly, "none").IsImmutableFrom(Identity(nestedOnly, "none")));
        Assert.True(Identity(nestedOnly, "none").IsImmutableFrom(Identity(literalOnly, "none")));
    }

    [Fact]
    public void IsImmutableFrom_FlatLiteralDotSchemaParity_Unchanged()
    {
        string flat = StructSchema(
            MappedField("a.b", "\"long\"", 1, "col-literal-ab", nullable: false),
            MappedField("c", "\"string\"", 2, "col-c", nullable: true));
        string changedPhysicalName = StructSchema(
            MappedField("a.b", "\"long\"", 1, "col-literal-z", nullable: false),
            MappedField("c", "\"string\"", 2, "col-c", nullable: true));

        Assert.True(Identity(flat).IsImmutableFrom(Identity(flat)));
        Assert.False(Identity(changedPhysicalName).IsImmutableFrom(Identity(flat)));
    }

    [Fact]
    public void IsImmutableFrom_NestedStructPathSegmentsRoundTrip()
    {
        static string Nested(long leafId, string leafPhysicalName) =>
            StructSchema(
                MappedField(
                    "a",
                    StructSchema(
                        MappedField(
                            "b",
                            StructSchema(MappedField("c", "\"long\"", leafId, leafPhysicalName, nullable: true)),
                            2,
                            "col-struct-b",
                            nullable: true)),
                    1,
                    "col-struct-a",
                    nullable: true));

        ColumnMappingIdentity end = Identity(Nested(3, "col-leaf-c"));

        Assert.True(end.IsImmutableFrom(Identity(Nested(3, "col-leaf-c"))));
        Assert.False(end.IsImmutableFrom(Identity(Nested(99, "col-leaf-c"))));
        Assert.False(end.IsImmutableFrom(Identity(Nested(3, "col-leaf-z"))));
    }

    [Fact]
    public void IsImmutableFrom_AddedColumnInEnd_True()
    {
        // Schema evolution: END adds a third mapped column absent from history — legal, must NOT fail closed.
        string evolved =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"name\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}},"
            + "{\"name\":\"added\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":3,\"delta.columnMapping.physicalName\":\"col-C\"}}]}";
        ColumnMappingIdentity end = Identity(evolved);
        Assert.True(end.IsImmutableFrom(Identity(FlatSchema(1, 2))));
    }

    [Fact]
    public void IsImmutableFrom_DroppedColumnInEnd_True()
    {
        // History had an extra column END no longer carries — only COMMON columns are compared, so legal.
        string withExtra =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"gone\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":9,\"delta.columnMapping.physicalName\":\"col-Z\"}}]}";
        ColumnMappingIdentity end = Identity(
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}}]}");
        Assert.True(end.IsImmutableFrom(Identity(withExtra)));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void IsImmutableFrom_LogicalRenamePreservingIdentity_True(string mode)
    {
        // A column-mapping RENAME keeps (id, physical name) and changes the logical name — legal; the renamed
        // column changes its key so it is not compared, and no still-present column's identity changed.
        string renamed =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"renamed\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";
        ColumnMappingIdentity end = Identity(renamed, mode);
        Assert.True(end.IsImmutableFrom(Identity(FlatSchema(1, 2), mode)));   // "name"->"renamed", (id=2,col-B)
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void IsImmutableFrom_DoubleRenamePreservingNestedIdentity_True(string mode)
    {
        string historical = StructSchema(
            MappedField(
                "a.b",
                StructSchema(MappedField("c", "\"long\"", 7, "col-leaf", nullable: true)),
                6,
                "col-struct",
                nullable: true));
        string end = StructSchema(
            MappedField(
                "a",
                StructSchema(MappedField("b.c", "\"long\"", 7, "col-leaf", nullable: true)),
                6,
                "col-struct",
                nullable: true));

        Assert.True(Identity(end, mode).IsImmutableFrom(Identity(historical, mode)));
    }

    /// <summary>
    /// Documents the current #676 boundary: mapped structs below array elements or map values are not collected
    /// by this gate today because mapped complex types are rejected fail-closed before CDF reads reach it.
    /// Enabling nested column mapping must extend <c>Collect</c> to descend array element and map key/value
    /// structs before that upstream rejection is relaxed.
    /// </summary>
    [Fact]
    public void IsImmutableFrom_ArrayAndMapNestedStructIdentitiesCurrentlyUncovered_True()
    {
        static string Schema(long arrayLeafId, long mapLeafId) =>
            StructSchema(
                MappedField(
                    "items",
                    "{\"type\":\"array\",\"elementType\":"
                    + StructSchema(MappedField("leaf", "\"long\"", arrayLeafId, "col-array-leaf", nullable: true))
                    + ",\"containsNull\":true}",
                    1,
                    "col-items",
                    nullable: true),
                MappedField(
                    "lookup",
                    "{\"type\":\"map\",\"keyType\":\"string\",\"valueType\":"
                    + StructSchema(MappedField("value", "\"long\"", mapLeafId, "col-map-value", nullable: true))
                    + ",\"valueContainsNull\":true}",
                    2,
                    "col-lookup",
                    nullable: true));

        ColumnMappingIdentity end = Identity(Schema(arrayLeafId: 99, mapLeafId: 100));

        Assert.True(end.IsImmutableFrom(Identity(Schema(arrayLeafId: 3, mapLeafId: 4))));
    }

    [Fact]
    public void IsImmutableFrom_DropAndReaddReusingIdentity_True_RenameEquivalent()
    {
        // Documented rename-equivalent residual (Architect R2 LOW; Security R2 adjudicated closed-by-design):
        // END drops "name"(id=2,col-B) and adds a NEW logical "attacker"(id=2,col-B) REUSING the id/physical.
        // This is byte-identical in metadata to renaming "name"->"attacker", so it CANNOT be rejected without
        // rejecting legal renames; it passes here and grants no capability beyond the _delta_log-write model.
        string dropReadd =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"attacker\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";
        ColumnMappingIdentity end = Identity(dropReadd);
        Assert.True(end.IsImmutableFrom(Identity(FlatSchema(1, 2))));
    }

    [Fact]
    public void FromMetadata_UnparseableSchemaString_Throws()
    {
        Assert.Throws<SchemaValidationException>(() => ColumnMappingIdentity.FromMetadata(Meta("not-valid-json{")));
    }

    [Fact]
    public void FromMetadata_ParseableNonStructSchemaString_ThrowsFailClosed()
    {
        // A Delta table schema is ALWAYS a struct; a valid-JSON but non-struct top-level type (e.g. a bare
        // "long") is a forged/inconsistent log. It must FAIL CLOSED, not yield zero columns (which would
        // silently exempt the version from the per-column identity compare). (dotnet-runtime R2 MEDIUM.)
        Assert.Throws<SchemaValidationException>(() => ColumnMappingIdentity.FromMetadata(Meta("\"long\"")));
    }

    [Fact]
    public void FromMetadata_UnrecognizedMode_ThrowsProtocol()
    {
        Assert.Throws<DeltaProtocolException>(() => ColumnMappingIdentity.FromMetadata(Meta(FlatSchema(1, 2), "bogus")));
    }
}
