using System.Reflection;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #679: the Parquet footer schema string (<c>org.apache.spark.sql.parquet.row.metadata</c>, stamped
/// by <c>ParquetFileWriter</c> through <see cref="DeltaSchemaJson"/>) and the Delta log
/// <c>metaData.schemaString</c> (stamped by <c>DeltaTableWriter</c> through <c>SchemaJson</c>) must be
/// byte-identical. #518 achieved that by maintaining a *copy* of the serializer in Storage and pinning
/// it with byte-parity tests; #679 removes the copy so the property holds **structurally** — there is a
/// single serializer and the footer path merely delegates to it.
/// </summary>
/// <remarks>
/// These tests guard the consolidation itself, complementing (not replacing) the per-shape byte-parity
/// tests in <c>DeltaSchemaJsonComplexTypeTests</c> and the per-metadata-kind parity test in
/// <c>DeltaSchemaJsonTypedMetadataTests</c>, which continue to pin the emitted wire shape.
/// </remarks>
public sealed class DeltaSchemaJsonSingleSourceTests
{
    [Fact]
    public void DeltaSchemaJson_DeclaresNoSerializerOfItsOwn()
    {
        // Structural single-source-of-truth guard: Storage must hold ONLY the delegating entry point.
        // Re-introducing a local WriteType/WriteStruct/WriteMetadata copy (the #518 interim shape, whose
        // drift risk #679 exists to eliminate) fails here rather than waiting for a byte-parity test to
        // notice after the two implementations have already diverged.
        const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        string[] declared = typeof(DeltaSchemaJson)
            .GetMethods(DeclaredMembers)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { nameof(DeltaSchemaJson.ToJson) }, declared);
    }

    [Fact]
    public void ToJson_RejectsNullSchema_WithItsOwnParameterName()
    {
        // The delegation must not leak the shared serializer's parameter name to this call site.
        var ex = Assert.Throws<ArgumentNullException>(() => DeltaSchemaJson.ToJson(null!));
        Assert.Equal("schema", ex.ParamName);
    }

    [Fact]
    public void FooterAndLog_AreByteIdentical_ForComplexTypesCarryingTypedMetadata()
    {
        // The combined worst case the two guards previously covered only separately: complex nesting
        // (array/map/struct, incl. an empty struct and a parameterized decimal) AND typed field metadata
        // on both outer and nested leaf fields — the #191/#676 column-mapping shape, where a numeric id
        // must stay an unquoted JSON integer (#330) at every depth.
        FieldMetadata Mapping(long id, string physical) => FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(id)),
            new KeyValuePair<string, MetadataValue>("delta.columnMapping.physicalName", MetadataValue.String(physical)),
        });

        var leafStruct = new StructType(new[]
        {
            new StructField("leaf", DataTypes.CreateDecimalType(18, 6), nullable: false, Mapping(11, "col-11")),
            new StructField("flag", DataTypes.BooleanType, nullable: true, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("k.bool", MetadataValue.Boolean(true)),
                new KeyValuePair<string, MetadataValue>("k.double", MetadataValue.Double(1.0)),
                new KeyValuePair<string, MetadataValue>("k.null", MetadataValue.Null),
            })),
        });

        var schema = new StructType(new[]
        {
            new StructField(
                "nested",
                DataTypes.CreateMapType(
                    DataTypes.StringType,
                    DataTypes.CreateArrayType(leafStruct, containsNull: true),
                    valueContainsNull: false),
                nullable: true,
                Mapping(3, "col-3")),
            new StructField("blank", StructType.Empty, nullable: false, Mapping(4, "col-4")),
        });

        string log = SchemaJson.ToJson(schema);
        string footer = DeltaSchemaJson.ToJson(schema);

        Assert.Equal(log, footer);

        // Sanity-pin the non-trivial bits so the equality above cannot pass on two empty strings.
        Assert.Contains("\"delta.columnMapping.id\":11", footer, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"decimal(18,6)\"", footer, StringComparison.Ordinal);
        Assert.Contains("\"type\":{\"type\":\"struct\",\"fields\":[]}", footer, StringComparison.Ordinal);
        Assert.Contains("\"k.double\":1.0", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterAndLog_AreByteIdentical_ForEveryAtomicTypeInTheTypeSystem()
    {
        // Breadth guard over the recursion base: every atomic type name must render identically on both
        // paths, so adding a DataType cannot land in one serializer's default arm and not the other's.
        var atomics = new DataType[]
        {
            DataTypes.BooleanType,
            DataTypes.ByteType,
            DataTypes.ShortType,
            DataTypes.IntegerType,
            DataTypes.LongType,
            DataTypes.FloatType,
            DataTypes.DoubleType,
            DataTypes.StringType,
            DataTypes.BinaryType,
            DataTypes.DateType,
            DataTypes.TimestampType,
            DataTypes.TimestampNtzType,
            DataTypes.CreateDecimalType(38, 18),
        };

        var schema = new StructType(atomics
            .Select((type, i) => new StructField($"c{i}", type, nullable: i % 2 == 0))
            .ToArray());

        Assert.Equal(SchemaJson.ToJson(schema), DeltaSchemaJson.ToJson(schema));
    }
}
