using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DeltaSharp;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Typed;

// ---------------------------------------------------------------------------------------------
// Model types for the Dataset<T> value-encoder tests (STORY-04.7.2 / #178). Property declaration
// order is significant: the derived schema and the decoder's property<->ordinal binding follow it
// (MetadataToken within a single declaring type), so tests below pin that order.
// ---------------------------------------------------------------------------------------------

/// <summary>A minimal settable-property POCO (reference type, public parameterless ctor).</summary>
public sealed class PersonPoco
{
    public string? Name { get; set; }

    public int Age { get; set; }
}

/// <summary>A record using <c>init</c>-only properties (still a settable bean with a parameterless ctor).</summary>
public sealed record PersonInitRecord
{
    public string? Name { get; init; }

    public int Age { get; init; }
}

/// <summary>Every ADR-0008 supported scalar (non-nullable forms) plus a reference/binary column.</summary>
public sealed class AllScalars
{
    public bool Bool { get; set; }

    public sbyte SByte { get; set; }

    public short Short { get; set; }

    public int Int { get; set; }

    public long Long { get; set; }

    public float Float { get; set; }

    public double Double { get; set; }

    public decimal Decimal { get; set; }

    public string? String { get; set; }

    public byte[]? Binary { get; set; }

    public DateOnly Date { get; set; }

    public DateTime Timestamp { get; set; }
}

/// <summary>Two same-typed (string) properties: because a bind-by-ordinal regression would SWAP them
/// undetectably (same type) when a row's field order differs from the property order, this model pins
/// that decode binds columns strictly BY NAME. Column reordering is a real Spark scenario (projections,
/// joins, SQL).</summary>
public sealed class TwoNames
{
    public string? First { get; set; }

    public string? Last { get; set; }
}

/// <summary>Nullable value-typed properties, for ADR-0008 null/non-null decode semantics.</summary>
public sealed class NullableScalars
{
    public int? MaybeInt { get; set; }

    public double? MaybeDouble { get; set; }

    public bool? MaybeBool { get; set; }
}

/// <summary>A single non-nullable value-typed property (SQL NULL into it is an error).</summary>
public sealed class NonNullableAge
{
    public int Age { get; set; }
}

// ----- AC3 unencodable shapes -----

public struct ValueTypeBean
{
    public int Age { get; set; }
}

public abstract class AbstractBean
{
    public int Age { get; set; }
}

public interface IBean
{
    int Age { get; }
}

/// <summary>A positional record: its only constructor takes parameters (no parameterless ctor).</summary>
public sealed record PositionalBean(string Name, int Age);

/// <summary>A get-only property (no public setter) of an otherwise supported type.</summary>
public sealed class GetOnlyBean
{
    public int Age { get; }
}

/// <summary>A property whose CLR type has no ADR-0008 schema mapping.</summary>
public sealed class UnsupportedTypeBean
{
    public object Payload { get; set; } = new();
}

public class DuplicateNameBase
{
    public int Id { get; set; }
}

/// <summary>Hides the base <c>Id</c> with a <c>new</c> property of a different type, so reflection
/// surfaces two properties named <c>Id</c> — a name that maps ambiguously to one column.</summary>
public sealed class DuplicateNameBean : DuplicateNameBase
{
    public new string? Id { get; set; }
}

/// <summary>
/// STORY-04.7.2 (#178): the <see cref="RowDecoder{T}"/> value encoder that lets a typed
/// <c>Dataset&lt;T&gt;</c> decode collected rows into <c>T</c>. Covers AC1 (deterministic
/// schema/mappings), AC2 (Row&#8594;T decode with ADR-0008 semantics and no row mutation), AC3
/// (deterministic diagnostics), and AC4 (AOT-safe reflection default + guarded compiled tier parity).
/// </summary>
public sealed class DatasetEncoderTests
{
    // ---------------------------------------------------------------------------------------------
    // AC1 — deterministic schema, property<->ordinal binding, and stability across derivations.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Decoder_Schema_MatchesDerivedSchema_WithDeterministicNamesTypesNullability()
    {
        RowDecoder<PersonPoco> decoder = RowDecoderFactory.Create<PersonPoco>();

        Assert.Equal(DatasetSchema.Derive<PersonPoco>(), decoder.Schema);
        Assert.Equal(2, decoder.Schema.Count);

        Assert.Equal("Name", decoder.Schema[0].Name);
        Assert.Equal(DataTypes.StringType, decoder.Schema[0].DataType);
        Assert.True(decoder.Schema[0].Nullable);

        Assert.Equal("Age", decoder.Schema[1].Name);
        Assert.Equal(DataTypes.IntegerType, decoder.Schema[1].DataType);
        Assert.False(decoder.Schema[1].Nullable);
    }

    [Fact]
    public void Decoder_BoundColumns_FollowSchemaOrder()
    {
        RowDecoder<PersonPoco> decoder = RowDecoderFactory.Create<PersonPoco>();

        // The i-th bound column is exactly the i-th schema field: the deterministic property<->ordinal map.
        Assert.Equal(new[] { "Name", "Age" }, decoder.BoundColumns);
        Assert.Equal(
            decoder.Schema.Fields.Select(f => f.Name).ToArray(),
            decoder.BoundColumns.ToArray());
    }

    [Fact]
    public void Decoder_IsDeterministic_AcrossRepeatedDerivations()
    {
        RowDecoder<PersonPoco> a = RowDecoderFactory.Create<PersonPoco>();
        RowDecoder<PersonPoco> b = RowDecoderFactory.Create<PersonPoco>();

        Assert.Equal(a.Schema, b.Schema);
        Assert.Equal(a.BoundColumns, b.BoundColumns);
    }

    // ---------------------------------------------------------------------------------------------
    // AC2 — Row -> T decode: round-trip values, ADR-0008 null/type semantics, and no row mutation.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Decode_ProducesTValues_MatchingSourceRow()
    {
        RowDecoder<PersonPoco> decoder = RowDecoderFactory.Create<PersonPoco>();
        var row = new Row(decoder.Schema, "Ada", 36);

        PersonPoco person = decoder.Decode(row);

        Assert.Equal("Ada", person.Name);
        Assert.Equal(36, person.Age);
    }

    [Fact]
    public void Decode_BindsColumnsByName_NotByOrdinal_WhenRowFieldOrderDiffers()
    {
        RowDecoder<TwoNames> decoder = RowDecoderFactory.Create<TwoNames>();

        // Build a row whose field ORDER is the reverse of T's property order ([Last, First] vs the
        // derived [First, Last]), keeping the same names/types, and with two same-typed (string)
        // columns so a bind-by-ordinal regression would silently SWAP the values. Decode must bind
        // BY NAME: First <- row["First"], Last <- row["Last"].
        var reversedSchema = new StructType(decoder.Schema.Fields.Reverse());
        var row = new Row(reversedSchema, "Smith", "John");

        TwoNames decoded = decoder.Decode(row);

        // Mutation sentinel: bind-by-ordinal (row[i]) yields First="Smith", Last="John".
        Assert.Equal("John", decoded.First);
        Assert.Equal("Smith", decoded.Last);
    }

    [Fact]
    public void Decode_RoundTrips_EverySupportedScalar()
    {
        RowDecoder<AllScalars> decoder = RowDecoderFactory.Create<AllScalars>();
        var date = new DateOnly(2021, 7, 4);
        var timestamp = new DateTime(2021, 7, 4, 13, 30, 15, DateTimeKind.Unspecified);
        byte[] blob = { 10, 20, 30 };
        var row = new Row(
            decoder.Schema,
            true, (sbyte)-5, (short)1200, 42, 9_000_000_000L, 1.5f, 2.5d, 12.34m, "text", blob, date, timestamp);

        AllScalars decoded = decoder.Decode(row);

        Assert.True(decoded.Bool);
        Assert.Equal((sbyte)-5, decoded.SByte);
        Assert.Equal((short)1200, decoded.Short);
        Assert.Equal(42, decoded.Int);
        Assert.Equal(9_000_000_000L, decoded.Long);
        Assert.Equal(1.5f, decoded.Float);
        Assert.Equal(2.5d, decoded.Double);
        Assert.Equal(12.34m, decoded.Decimal);
        Assert.Equal("text", decoded.String);
        Assert.Equal(blob, decoded.Binary);
        Assert.Equal(date, decoded.Date);
        Assert.Equal(timestamp, decoded.Timestamp);
    }

    [Fact]
    public void Decode_NullableValueTypes_DecodeNullAndNonNull()
    {
        RowDecoder<NullableScalars> decoder = RowDecoderFactory.Create<NullableScalars>();

        NullableScalars nonNull = decoder.Decode(new Row(decoder.Schema, 7, 3.5d, true));
        Assert.Equal(7, nonNull.MaybeInt);
        Assert.Equal(3.5d, nonNull.MaybeDouble);
        Assert.True(nonNull.MaybeBool);

        NullableScalars allNull = decoder.Decode(new Row(decoder.Schema, null, null, null));
        Assert.Null(allNull.MaybeInt);
        Assert.Null(allNull.MaybeDouble);
        Assert.Null(allNull.MaybeBool);
    }

    [Fact]
    public void Decode_ReferenceTypeNull_DecodesToNull()
    {
        RowDecoder<PersonPoco> decoder = RowDecoderFactory.Create<PersonPoco>();

        PersonPoco person = decoder.Decode(new Row(decoder.Schema, null, 1));

        Assert.Null(person.Name);
        Assert.Equal(1, person.Age);
    }

    [Fact]
    public void Decode_InitOnlyRecord_IsSupported()
    {
        RowDecoder<PersonInitRecord> decoder = RowDecoderFactory.Create<PersonInitRecord>();

        PersonInitRecord person = decoder.Decode(new Row(decoder.Schema, "Lin", 29));

        Assert.Equal("Lin", person.Name);
        Assert.Equal(29, person.Age);
    }

    [Fact]
    public void Decode_NullIntoNonNullableValueType_ThrowsDeterministicInvalidOperation()
    {
        RowDecoder<NonNullableAge> decoder = RowDecoderFactory.Create<NonNullableAge>();
        var row = new Row(decoder.Schema, new object?[] { null });

        InvalidOperationException ex1 = Assert.Throws<InvalidOperationException>(() => decoder.Decode(row));
        InvalidOperationException ex2 = Assert.Throws<InvalidOperationException>(() => decoder.Decode(row));

        Assert.Contains("Age", ex1.Message, StringComparison.Ordinal);
        Assert.Equal(ex1.Message, ex2.Message);
    }

    [Fact]
    public void Decode_WrongRuntimeType_ThrowsInvalidCast()
    {
        RowDecoder<PersonPoco> decoder = RowDecoderFactory.Create<PersonPoco>();
        // Age column carries a string, not an int.
        var row = new Row(decoder.Schema, "Ada", "thirty");

        Assert.Throws<InvalidCastException>(() => decoder.Decode(row));
    }

    [Fact]
    public void Decode_DoesNotMutateSourceRow_AndClonesBinary()
    {
        RowDecoder<AllScalars> decoder = RowDecoderFactory.Create<AllScalars>();
        byte[] blob = { 1, 2, 3 };
        var row = new Row(
            decoder.Schema,
            true, (sbyte)1, (short)2, 3, 4L, 5f, 6d, 7m, "hi", blob, new DateOnly(2020, 1, 1), new DateTime(2020, 1, 1));

        object?[] before = SnapshotCells(row);
        AllScalars decoded = decoder.Decode(row);
        object?[] after = SnapshotCells(row);

        // The row's cells are untouched by decoding — each is the very same object it started as.
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Same(before[i], after[i]);
        }

        // The binary column is COPIED into T (value semantics), so mutating the decoded array cannot
        // reach back into the row's array.
        Assert.NotSame(row["Binary"], decoded.Binary);
        Assert.Equal(blob, decoded.Binary);
        decoded.Binary![0] = 99;
        Assert.Equal(1, ((byte[])row["Binary"]!)[0]);
    }

    // ---------------------------------------------------------------------------------------------
    // AC3 — deterministic diagnostics: an unsupported shape/type names the offending member + reason.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Create_ValueType_ThrowsNamingType()
    {
        UnsupportedTypedSchemaException ex =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<ValueTypeBean>());
        Assert.Contains("ValueTypeBean", ex.Message, StringComparison.Ordinal);
        Assert.Contains("value type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AbstractType_ThrowsNamingType()
    {
        UnsupportedTypedSchemaException ex =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<AbstractBean>());
        Assert.Contains("AbstractBean", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_Interface_ThrowsNamingType()
    {
        UnsupportedTypedSchemaException ex =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<IBean>());
        Assert.Contains("IBean", ex.Message, StringComparison.Ordinal);
        Assert.Contains("interface", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_PositionalRecord_ThrowsNamingTypeAndGuidance()
    {
        UnsupportedTypedSchemaException ex =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<PositionalBean>());
        Assert.Contains("PositionalBean", ex.Message, StringComparison.Ordinal);
        Assert.Contains("parameterless constructor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_GetOnlyProperty_ThrowsNamingMemberAndType()
    {
        UnsupportedTypedSchemaException ex =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<GetOnlyBean>());
        Assert.Contains("GetOnlyBean.Age", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no public setter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_UnsupportedPropertyType_ThrowsNamingMemberAndType_Deterministically()
    {
        UnsupportedTypedSchemaException ex1 =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<UnsupportedTypeBean>());
        UnsupportedTypedSchemaException ex2 =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<UnsupportedTypeBean>());

        Assert.Contains("UnsupportedTypeBean.Payload", ex1.Message, StringComparison.Ordinal);
        Assert.Contains("Object", ex1.Message, StringComparison.Ordinal);
        Assert.Equal(ex1.Message, ex2.Message);
    }

    [Fact]
    public void Create_DuplicatePropertyName_ThrowsAmbiguousMapping_Deterministically()
    {
        UnsupportedTypedSchemaException ex1 =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<DuplicateNameBean>());
        UnsupportedTypedSchemaException ex2 =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<DuplicateNameBean>());

        Assert.Contains("ambiguous", ex1.Message, StringComparison.Ordinal);
        Assert.Contains("Id", ex1.Message, StringComparison.Ordinal);
        Assert.Equal(ex1.Message, ex2.Message);
    }

    [Fact]
    public void UnsupportedTypeDiagnostic_IsIdenticalBetweenSchemaAndEncoderPaths()
    {
        // AC3 determinism: same input -> identical message, whether raised by the schema deriver
        // (As<T>) or the encoder (Collect). Both reuse DatasetSchema.Derive.
        UnsupportedTypedSchemaException schemaEx =
            Assert.Throws<UnsupportedTypedSchemaException>(() => DatasetSchema.Derive<UnsupportedTypeBean>());
        UnsupportedTypedSchemaException encoderEx =
            Assert.Throws<UnsupportedTypedSchemaException>(() => RowDecoderFactory.Create<UnsupportedTypeBean>());

        Assert.Equal(schemaEx.Message, encoderEx.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // AC4 — AOT: reflection default is the fallback; the compiled tier is guarded by the runtime
    // feature and must decode identically to reflection (the parity oracle).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void UseCompiledSetters_TracksRuntimeFeature()
    {
        // The compiled tier's gate IS RuntimeFeature.IsDynamicCodeSupported — so under NativeAOT
        // (false) the encoder stays on the reflection tier with no dynamic code.
        Assert.Equal(RuntimeFeature.IsDynamicCodeSupported, RowDecoderFactory.UseCompiledSetters);
    }

    [Fact]
    public void ReflectionTier_IsAlwaysAvailable_AndDecodesCorrectly()
    {
        // Forcing reflection models the AOT (dynamic-code-unavailable) path; it must still work.
        RowDecoder<AllScalars> reflection = RowDecoderFactory.Create<AllScalars>(forceReflectionSetters: true);
        Assert.False(reflection.UsesCompiledSetters);

        AllScalars decoded = reflection.Decode(SampleAllScalarsRow(reflection));
        Assert.Equal(42, decoded.Int);
        Assert.Equal("text", decoded.String);
    }

    [Fact]
    public void DefaultTier_UsesCompiledWhenDynamicCodeSupported()
    {
        RowDecoder<AllScalars> defaultDecoder = RowDecoderFactory.Create<AllScalars>();

        Assert.Equal(RuntimeFeature.IsDynamicCodeSupported, defaultDecoder.UsesCompiledSetters);
    }

    [Fact]
    public void CompiledAndReflectionTiers_DecodeIdentically_ParityOracle()
    {
        RowDecoder<AllScalars> reflection = RowDecoderFactory.Create<AllScalars>(forceReflectionSetters: true);
        RowDecoder<AllScalars> compiled = RowDecoderFactory.Create<AllScalars>(forceReflectionSetters: false);

        Assert.False(reflection.UsesCompiledSetters);
        Assert.Equal(RuntimeFeature.IsDynamicCodeSupported, compiled.UsesCompiledSetters);

        AllScalars fromReflection = reflection.Decode(SampleAllScalarsRow(reflection));
        AllScalars fromCompiled = compiled.Decode(SampleAllScalarsRow(compiled));

        AssertAllScalarsEqual(fromReflection, fromCompiled);
    }

    [Fact]
    public void CompiledAndReflectionTiers_HandleNullsIdentically()
    {
        RowDecoder<NullableScalars> reflection = RowDecoderFactory.Create<NullableScalars>(forceReflectionSetters: true);
        RowDecoder<NullableScalars> compiled = RowDecoderFactory.Create<NullableScalars>(forceReflectionSetters: false);

        NullableScalars r = reflection.Decode(new Row(reflection.Schema, null, 3.5d, null));
        NullableScalars c = compiled.Decode(new Row(compiled.Schema, null, 3.5d, null));

        Assert.Equal(r.MaybeInt, c.MaybeInt);
        Assert.Equal(r.MaybeDouble, c.MaybeDouble);
        Assert.Equal(r.MaybeBool, c.MaybeBool);
    }

    private static Row SampleAllScalarsRow(RowDecoder<AllScalars> decoder) => new(
        decoder.Schema,
        true, (sbyte)-5, (short)1200, 42, 9_000_000_000L, 1.5f, 2.5d, 12.34m, "text", new byte[] { 7, 8 },
        new DateOnly(2021, 7, 4), new DateTime(2021, 7, 4, 13, 30, 15, DateTimeKind.Unspecified));

    private static object?[] SnapshotCells(Row row)
    {
        var cells = new object?[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            cells[i] = row[i];
        }

        return cells;
    }

    private static void AssertAllScalarsEqual(AllScalars a, AllScalars b)
    {
        Assert.Equal(a.Bool, b.Bool);
        Assert.Equal(a.SByte, b.SByte);
        Assert.Equal(a.Short, b.Short);
        Assert.Equal(a.Int, b.Int);
        Assert.Equal(a.Long, b.Long);
        Assert.Equal(a.Float, b.Float);
        Assert.Equal(a.Double, b.Double);
        Assert.Equal(a.Decimal, b.Decimal);
        Assert.Equal(a.String, b.String);
        Assert.Equal(a.Binary, b.Binary);
        Assert.Equal(a.Date, b.Date);
        Assert.Equal(a.Timestamp, b.Timestamp);
    }
}
