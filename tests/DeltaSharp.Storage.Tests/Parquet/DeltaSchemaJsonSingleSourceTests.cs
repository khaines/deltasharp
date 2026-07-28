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
        // identifiable by signature — it threads a Utf8JsonWriter alongside a schema node (DataType,
        // StructField, FieldMetadata, MetadataValue …). Storage must own no such type; that
        // responsibility lives solely in DeltaSharp.Abstractions.SchemaJson. Delta LOG action writers
        // legitimately pair a Utf8JsonWriter with action types (MetadataAction etc., declared in
        // DeltaSharp.Storage.Delta), which is why the predicate keys on the schema type system
        // specifically rather than on the writer alone.
        const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // The predicate must close the CLASS of schema-serializer signatures, not enumerate the ones we
        // happen to imagine. Two independent dimensions have to hold at once, and an earlier revision of
        // this test held only one of them at a time:
        //
        //   WHAT COUNTS AS A SCHEMA NODE — the union of two rules, because neither dominates the other.
        //     (a) assignable to DataType, or FieldMetadata. Misses StructField, MetadataValue (literally
        //         the signature of the cross-assembly seam this PR deleted) and IReadOnlyList<StructField>,
        //         none of which are DataTypes.
        //     (b) any type from the schema type system's namespace in the Abstractions assembly. Misses a
        //         Storage-LOCAL subclass of DataType, which is assignable but declared in namespace
        //         DeltaSharp.Storage.
        //   Keying on (b) alone silently NARROWS coverage relative to (a); keying on (a) alone is the
        //   evasion (b) was added to close. Both, plus by-ref/array unwrapping so `ref Utf8JsonWriter` and
        //   `in StructField` match, plus recursion through generic arguments.
        //
        //   WHERE THE SIGNATURE MAY LIVE — the whole type, not one member. Requiring both the writer and
        //   the schema node on the SAME parameter list is evaded by the most natural shape a re-inlined
        //   serializer would actually take: a stateful writer class that takes the Utf8JsonWriter in its
        //   constructor and the schema node on a method. So the parameter/field types of every declared
        //   member are unioned per type before the two rules are applied.
        static Type Unwrap(Type t) => t.GetElementType() ?? t;

        static bool MentionsSchemaType(Type t)
        {
            Type actual = Unwrap(t);
            if (typeof(DataType).IsAssignableFrom(actual) || actual == typeof(FieldMetadata))
            {
                return true;
            }

            if (actual.Namespace == "DeltaSharp.Types" && actual.Assembly == typeof(DataType).Assembly)
            {
                return true;
            }

            return actual.IsGenericType && actual.GetGenericArguments().Any(MentionsSchemaType);
        }

        static bool IsJsonWriter(Type t) => Unwrap(t) == typeof(Utf8JsonWriter);

        static IEnumerable<Type> SignatureTypes(Type t, BindingFlags flags) =>
            t.GetMethods(flags).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType)
                .Concat(t.GetConstructors(flags).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(t.GetFields(flags).Select(f => f.FieldType));

        string[] offenders = typeof(DeltaSchemaJson).Assembly
            .GetTypes()
            .Where(t =>
            {
                Type[] signature = SignatureTypes(t, DeclaredMembers).ToArray();
                return Array.Exists(signature, IsJsonWriter)
                    && Array.Exists(signature, MentionsSchemaType);
            })
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Named rather than counted, so a failure says WHICH type reintroduced a serializer.
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryStorageTypeTouchingUtf8JsonWriter_IsOnTheJsonWritingAllowlist()
    {
        // The guard above tries to RECOGNISE a schema node, and recognition is a game the guard
        // cannot win. Successive review rounds evaded it with StructField, MetadataValue, a by-ref
        // writer, constructor injection, a Storage-local DataType subclass, an open generic
        // parameter (`T` has IsGenericType == false, so recursion through generic ARGUMENTS never
        // reaches it), and plain `object`. Type erasure always defeats recognition; patching each
        // shape as it is found is an unwinnable sequence.
        //
        // So this guard changes the KEY. It does not ask what a parameter means; it asks who is
        // allowed to write JSON at all. A re-inlined schema serializer must emit JSON, so it must
        // touch Utf8JsonWriter, so it trips here no matter how it spells its schema argument --
        // generic, object, dynamic, or a Storage-local DTO. That converts "guess the shape" into
        // "edit a three-line list on purpose", which is exactly the deliberate act a tripwire
        // should require. The narrower signature guard above is KEPT: two guards, different keys,
        // and the narrower one gives the more specific diagnosis when it is the one that fires.
        //
        // The scan reaches method BODIES, not just signatures: a serializer can construct its own
        // Utf8JsonWriter as a local and never name it in any signature (DeltaSchemaJson itself did
        // exactly that before #679), which a signature-only allowlist would miss.
        const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // The complete set of Storage types permitted to touch Utf8JsonWriter. All three write Delta
        // LOG JSON (actions, commit info, deletion vector descriptors) -- none serializes a schema
        // tree. Adding an entry here is a deliberate act that should be justified in review; if the
        // new type serializes a schema tree, the answer is to delegate to SchemaJson instead.
        string[] allowlist =
        [
            "DeltaSharp.Storage.Delta.DeletionVectors.DeletionVectorDescriptor",
            "DeltaSharp.Storage.Delta.DeltaCommitInfo",
            "DeltaSharp.Storage.Delta.DeltaLogActionWriter",
        ];

        static Type Unwrap(Type t) => t.GetElementType() ?? t;

        static bool IsJsonWriter(Type t) => Unwrap(t) == typeof(Utf8JsonWriter);

        static IEnumerable<Type> TouchedTypes(Type t, BindingFlags flags)
        {
            foreach (MethodInfo method in t.GetMethods(flags))
            {
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }

            foreach (ConstructorInfo constructor in t.GetConstructors(flags))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }

            foreach (FieldInfo field in t.GetFields(flags))
            {
                yield return field.FieldType;
            }

            foreach (PropertyInfo property in t.GetProperties(flags))
            {
                yield return property.PropertyType;
            }

            // Method bodies: catches a writer that is only ever a local.
            foreach (MethodBase member in t.GetMethods(flags).Cast<MethodBase>().Concat(t.GetConstructors(flags)))
            {
                MethodBody? body = member.GetMethodBody();
                if (body is null)
                {
                    continue;
                }

                foreach (LocalVariableInfo local in body.LocalVariables)
                {
                    yield return local.LocalType;
                }
            }
        }

        // Compiler-generated nested types (async state machines, closures) inherit their outer
        // type's entry, so an allowlisted type may freely grow an async JSON-writing member without
        // this test demanding a new, unreadable `Foo+<Bar>d__7` entry.
        static Type Outermost(Type t)
        {
            Type outer = t;
            while (outer.DeclaringType is not null)
            {
                outer = outer.DeclaringType;
            }

            return outer;
        }

        string[] offenders = typeof(DeltaSchemaJson).Assembly
            .GetTypes()
            .Where(t => TouchedTypes(t, DeclaredMembers).Any(IsJsonWriter))
            .Select(t => Outermost(t).FullName!)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !allowlist.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These DeltaSharp.Storage types touch Utf8JsonWriter but are not on the JSON-writing "
            + $"allowlist in this test: {string.Join(", ", offenders)}. Storage owns no schema-tree "
            + $"serializer (#679) — schema JSON is produced solely by DeltaSharp.Abstractions.SchemaJson, "
            + $"and DeltaSchemaJson merely delegates to it. If the new type serializes a SCHEMA, delegate "
            + $"to SchemaJson instead of writing JSON here. If it genuinely writes Delta LOG JSON, add it "
            + $"to the allowlist deliberately and say why in review.");
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
