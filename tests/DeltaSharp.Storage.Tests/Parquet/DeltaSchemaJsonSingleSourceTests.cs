using System.Reflection;
using System.Text.Json;
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
/// <para>
/// These tests guard the consolidation itself, complementing (not replacing) the per-shape golden
/// tests in <c>DeltaSchemaJsonComplexTypeTests</c> and the per-metadata-kind test in
/// <c>DeltaSchemaJsonTypedMetadataTests</c>, which pin the emitted wire shape.
/// </para>
/// <para>
/// CAUTION for future edits: now that both paths resolve to one serializer, an assertion of the form
/// <c>Assert.Equal(SchemaJson.ToJson(x), DeltaSchemaJson.ToJson(x))</c> is <c>f(x) == f(x)</c> and
/// CANNOT fail for a serializer defect — it only guards the delegation seam. Every footer↔log
/// assertion below is therefore paired with a golden literal captured from the PRE-consolidation
/// serializer at commit a6ff45f. Never add a footer↔log assertion as a test's only assertion.
/// </para>
/// </remarks>
public sealed class DeltaSchemaJsonSingleSourceTests
{
    [Fact]
    public void DeltaSchemaJson_DeclaresNoSerializerOfItsOwn()
    {
        // Structural single-source-of-truth guard: Storage must hold ONLY the delegating entry point.
        // Re-introducing a local WriteType/WriteStruct/WriteMetadata copy (the #518 interim shape, whose
        // drift risk #679 exists to eliminate) fails here rather than waiting for a golden test to
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
    public void NoTypeInStorage_DeclaresASchemaTreeSerializer()
    {
        // Assembly-wide companion to the test above, which is type-scoped and so would miss the more
        // likely future accident: a schema serializer re-inlined into some OTHER Storage type (say
        // ParquetFileWriter) rather than back into DeltaSchemaJson. A schema-tree serializer is
        // identifiable by signature — it threads a Utf8JsonWriter alongside a schema node (DataType or
        // FieldMetadata). Storage must own no such method; that responsibility lives solely in
        // DeltaSharp.Abstractions.SchemaJson. Delta LOG action writers legitimately pair a
        // Utf8JsonWriter with action types (MetadataAction etc., declared in DeltaSharp.Storage.Delta),
        // which is why the predicate keys on the schema type system specifically rather than on the
        // writer alone.
        const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // The predicate must close the CLASS of schema-serializer signatures, not enumerate the ones we
        // happen to imagine. An earlier version keyed on "DataType-assignable or FieldMetadata", which a
        // re-inlined serializer evades completely just by threading StructField, MetadataValue (literally
        // the signature of the cross-assembly seam this PR deleted), or a `ref Utf8JsonWriter`. So:
        //   * unwrap by-ref/out/pointer/array so `ref Utf8JsonWriter` and `in StructField` still match;
        //   * treat ANY type from the schema type system (namespace DeltaSharp.Types in the Abstractions
        //     assembly) as a schema node, rather than naming four of its members;
        //   * recurse through generic arguments so IReadOnlyList<StructField>/ReadOnlySpan<StructField>
        //     match too.
        static Type Unwrap(Type t) => t.GetElementType() ?? t;

        static bool MentionsSchemaType(Type t)
        {
            Type actual = Unwrap(t);
            if (actual.Namespace == "DeltaSharp.Types" && actual.Assembly == typeof(DataType).Assembly)
            {
                return true;
            }

            return actual.IsGenericType && actual.GetGenericArguments().Any(MentionsSchemaType);
        }

        static bool IsJsonWriter(Type t) => Unwrap(t) == typeof(Utf8JsonWriter);

        string[] offenders = typeof(DeltaSchemaJson).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(DeclaredMembers))
            .Where(m =>
            {
                Type[] parameters = m.GetParameters().Select(x => x.ParameterType).ToArray();
                return Array.Exists(parameters, IsJsonWriter)
                    && Array.Exists(parameters, MentionsSchemaType);
            })
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Named rather than counted, so a failure says WHICH method reintroduced a serializer.
        Assert.Empty(offenders);
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

        // Golden captured from the pre-consolidation serializer at a6ff45f. This, not the equality
        // below, is what pins the wire shape.
        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"nested\",\"type\":{\"type\":\"map\"" +
            ",\"keyType\":\"string\",\"valueType\":{\"type\":\"array\"" +
            ",\"elementType\":{\"type\":\"struct\",\"fields\":[{\"name\":\"leaf\"" +
            ",\"type\":\"decimal(18,6)\",\"nullable\":false" +
            ",\"metadata\":{\"delta.columnMapping.id\":11" +
            ",\"delta.columnMapping.physicalName\":\"col-11\"}},{\"name\":\"flag\"" +
            ",\"type\":\"boolean\",\"nullable\":true,\"metadata\":{\"k.bool\":true" +
            ",\"k.double\":1.0,\"k.null\":null}}]},\"containsNull\":true}" +
            ",\"valueContainsNull\":false},\"nullable\":true" +
            ",\"metadata\":{\"delta.columnMapping.id\":3" +
            ",\"delta.columnMapping.physicalName\":\"col-3\"}},{\"name\":\"blank\"" +
            ",\"type\":{\"type\":\"struct\",\"fields\":[]},\"nullable\":false" +
            ",\"metadata\":{\"delta.columnMapping.id\":4" +
            ",\"delta.columnMapping.physicalName\":\"col-4\"}}]}";
        Assert.Equal(golden, footer);

        // Delegation seam (see class remarks): cannot fail for a serializer defect, only for a broken
        // or transforming delegation.
        Assert.Equal(log, footer);
    }

    [Fact]
    public void EveryAtomicTypeInTheTypeSystem_SerializesToItsPinnedTypeName()
    {
        // Breadth guard over the recursion base: pins the emitted type-name string for every atomic
        // type plus a decimal, so corrupting the shared default arm (e.g. uppercasing TypeName) is
        // caught. NOTE: post-#679 there is only ONE default arm, so the old framing of this test —
        // "a new DataType cannot land in one serializer's default arm and not the other's" — describes
        // an invariant that can no longer be violated, and the footer↔log assertion that expressed it
        // was inert. The golden below is the real assertion; it is captured from the pre-consolidation
        // serializer at a6ff45f.
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

        const string golden =
            "{\"type\":\"struct\",\"fields\":[{\"name\":\"c0\",\"type\":\"boolean\"" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"c1\",\"type\":\"byte\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"c2\",\"type\":\"short\"" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"c3\",\"type\":\"integer\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"c4\",\"type\":\"long\"" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"c5\",\"type\":\"float\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"c6\",\"type\":\"double\"" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"c7\",\"type\":\"string\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"c8\",\"type\":\"binary\"" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"c9\",\"type\":\"date\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"c10\",\"type\":\"timestamp\"" +
            ",\"nullable\":true,\"metadata\":{}},{\"name\":\"c11\",\"type\":\"timestamp_ntz\"" +
            ",\"nullable\":false,\"metadata\":{}},{\"name\":\"c12\",\"type\":\"decimal(38,18)\"" +
            ",\"nullable\":true,\"metadata\":{}}]}";
        Assert.Equal(golden, DeltaSchemaJson.ToJson(schema));

        // Delegation seam only (see class remarks).
        Assert.Equal(SchemaJson.ToJson(schema), DeltaSchemaJson.ToJson(schema));
    }
}
