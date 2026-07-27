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

    [Fact]
    public void IsImmutableFrom_LogicalRenamePreservingIdentity_True()
    {
        // A column-mapping RENAME keeps (id, physical name) and changes the logical name — legal; the renamed
        // column changes its key so it is not compared, and no still-present column's identity changed.
        string renamed =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"renamed\",\"type\":\"string\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";
        ColumnMappingIdentity end = Identity(renamed);
        Assert.True(end.IsImmutableFrom(Identity(FlatSchema(1, 2))));   // "name"->"renamed", (id=2,col-B) preserved
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
