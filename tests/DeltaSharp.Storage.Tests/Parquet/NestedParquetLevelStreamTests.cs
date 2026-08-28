using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The <b>level-stream differential</b>: asserts the EXACT definition/repetition arrays DeltaSharp writes for
/// each in-scope nested shape, against the literal tables the design (§2.3) prescribes.
/// </summary>
/// <remarks>
/// The round-trip oracle in <see cref="NestedParquetWriteTests"/> proves the encoding is <i>self-consistent</i>
/// with DeltaSharp's own reader. That is necessary but not sufficient: a shredder and a reader that share the
/// same off-by-one produce a file every OTHER engine (Spark, delta-rs, Trino) reads wrong while the round trip
/// stays green. These tests therefore read the level streams out of the written file with Parquet.Net's own
/// primitives and compare them to hard-coded arrays, so the wire encoding is pinned independently of the
/// DeltaSharp read path.
/// </remarks>
public sealed class NestedParquetLevelStreamTests
{
    [Fact]
    public async Task ArrayOfInt_EmitsTheNormativeListLevelTable()
    {
        // §2.3 list table. Container maxDef 2, nullable element leaf maxDef 3, maxRep 1.
        //   [10,20]     -> two slots: def 3,3   rep 0,1
        //   null list   -> one slot:  def 0     rep 0
        //   []          -> one slot:  def 1     rep 0
        //   [null]      -> one slot:  def 2     rep 0
        //   [30]        -> one slot:  def 3     rep 0
        var rows = new int?[]?[]
        {
            new int?[] { 10, 20 },
            null,
            Array.Empty<int?>(),
            new int?[] { null },
            new int?[] { 30 },
        };

        var type = DataTypes.CreateArrayType(DataTypes.IntegerType);
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
        byte[] bytes = await WriteAsync(schema, NestedVectors.IntList(type, rows));

        (DataField field, int[] def, int[] rep) = await ReadLeafAsync(bytes, "a", "list", "element");

        Assert.Equal(2, field.MaxDefinitionLevel - (field.IsNullable ? 1 : 0));
        Assert.Equal(3, field.MaxDefinitionLevel);
        Assert.Equal(1, field.MaxRepetitionLevel);
        Assert.Equal(new[] { 3, 3, 0, 1, 2, 3 }, def);
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 0 }, rep);
    }

    [Fact]
    public async Task StructOfScalars_EmitsTheNormativeStructLevelTable_AndNoRepetitionStream()
    {
        // §2.3 struct table. Container maxDef 1, nullable child leaf maxDef 2, maxRep 0 (no rep stream).
        //   {1,"x"}      -> a: def 2   b: def 2
        //   null struct  -> a: def 0   b: def 0   (BOTH children, cross-field parity)
        //   {null,"y"}   -> a: def 1   b: def 2
        //   {3,null}     -> a: def 2   b: def 1
        var rows = new (int? A, string? B)?[] { (1, "x"), null, (null, "y"), (3, null) };

        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        });
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", inner, nullable: true) });
        byte[] bytes = await WriteAsync(schema, NestedVectors.IntStringStruct(inner, rows));

        (DataField aField, int[] aDef, int[] aRep) = await ReadLeafAsync(bytes, "s", "a");
        (DataField bField, int[] bDef, int[] bRep) = await ReadLeafAsync(bytes, "s", "b");

        Assert.Equal(2, aField.MaxDefinitionLevel);
        Assert.Equal(0, aField.MaxRepetitionLevel);
        Assert.Equal(0, bField.MaxRepetitionLevel);
        Assert.Equal(new[] { 2, 0, 1, 2 }, aDef);
        Assert.Equal(new[] { 2, 0, 2, 1 }, bDef);

        // A non-repeated leaf carries NO repetition stream at all. Asserting the arrays are EMPTY is
        // vacuous — the helper substitutes an empty array whenever MaxRepetitionLevel is 0, so the assertion
        // would hold even if the writer had emitted a stream. ABSENCE is asserted separately below.
        Assert.Empty(aRep);
        Assert.Empty(bRep);
        await AssertNoRepetitionStreamAsync(bytes, "s", "a");
        await AssertNoRepetitionStreamAsync(bytes, "s", "b");
    }

    [Fact]
    public async Task StructWithRequiredChild_EmitsTheNormativeRequiredStructLevelTable_AndRoundTrips()
    {
        // §2.3's struct REQUIRED column, otherwise entirely unpinned. A REQUIRED child's leaf maxDef equals
        // the struct's own (1), so it has NO level of its own at which to be null — "struct present" and
        // "value present" are the SAME level — while its OPTIONAL sibling keeps two.
        //   {1,"x"}      -> req: def 1   opt: def 2
        //   null struct  -> req: def 0   opt: def 0   (a REQUIRED child still drops to 0 for a null struct)
        //   {3,null}     -> req: def 1   opt: def 1
        var rows = new (int? A, string? B)?[] { (1, "x"), null, (3, null) };

        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField("req", DataTypes.IntegerType, nullable: false),
            new StructField("opt", DataTypes.StringType, nullable: true),
        });
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", inner, nullable: true) });
        byte[] bytes = await WriteAsync(schema, NestedVectors.IntStringStruct(inner, rows));

        (DataField reqField, int[] reqDef, _) = await ReadLeafAsync(bytes, "s", "req");
        (DataField optField, int[] optDef, _) = await ReadLeafAsync(bytes, "s", "opt");

        Assert.False(reqField.IsNullable);
        Assert.Equal(1, reqField.MaxDefinitionLevel);
        Assert.Equal(0, reqField.MaxRepetitionLevel);
        Assert.True(optField.IsNullable);
        Assert.Equal(2, optField.MaxDefinitionLevel);
        Assert.Equal(0, optField.MaxRepetitionLevel);
        Assert.Equal(new[] { 1, 0, 1 }, reqDef);
        Assert.Equal(new[] { 2, 0, 1 }, optDef);

        // …and the same file round-trips through the real read path, so the level table above is not merely
        // self-consistent with the writer.
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? decoded = null;
        await foreach (ColumnBatch group in new ParquetFileReader().ReadAsync(
            stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            decoded = group;
        }

        Assert.NotNull(decoded);
        NestedVectors.AssertStructsEqual(
            rows, NestedVectors.ReadIntStringStruct((StructColumnVector)decoded!.Column(0)));
    }

    // Asserts that a leaf's repetition levels are ABSENT — Parquet.Net's RawColumnData refuses to surface a
    // repetition stream for an unrepeated column — rather than merely empty.
    private static async Task AssertNoRepetitionStreamAsync(byte[] bytes, params string[] path)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, default);
        DataField field = reader.Schema.DataFields.Single(f => f.Path.ToList().SequenceEqual(path));
        Assert.Equal(0, field.MaxRepetitionLevel);

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        RawColumnData raw = await rowGroup.ReadRawColumnDataBaseAsync(field, default);

        Exception? absent = null;
        try
        {
            _ = raw.RepetitionLevels.Length;
        }
        catch (Exception ex)
        {
            absent = ex;
        }

        Assert.NotNull(absent);
    }

    [Fact]
    public async Task MapOfStringToInt_EmitsTheNormativeMapLevelTable_WithAnIdenticalRepetitionStream()
    {
        // §2.3 map table. Map maxDef 2, REQUIRED key leaf maxDef 2, nullable value leaf maxDef 3, maxRep 1.
        //   {a:1, b:null} -> two slots: key def 2,2  value def 3,2  rep 0,1
        //   null map      -> one slot:  key def 0    value def 0    rep 0
        //   {}            -> one slot:  key def 1    value def 1    rep 0
        //   {c:3}         -> one slot:  key def 2    value def 3    rep 0
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("a", (int?)1), ("b", null) },
            null,
            Array.Empty<(string, int?)>(),
            new[] { ("c", (int?)3) },
        };

        var type = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType);
        var schema = DataTypes.CreateStructType(new[] { new StructField("m", type, nullable: true) });
        byte[] bytes = await WriteAsync(schema, NestedVectors.StringIntMap(type, rows));

        (DataField keyField, int[] keyDef, int[] keyRep) = await ReadLeafAsync(bytes, "m", "key_value", "key");
        (DataField valueField, int[] valueDef, int[] valueRep) =
            await ReadLeafAsync(bytes, "m", "key_value", "value");

        Assert.False(keyField.IsNullable);
        Assert.Equal(2, keyField.MaxDefinitionLevel);
        Assert.Equal(3, valueField.MaxDefinitionLevel);
        Assert.Equal(new[] { 2, 2, 0, 1, 2 }, keyDef);
        Assert.Equal(new[] { 3, 2, 0, 1, 3 }, valueDef);
        Assert.Equal(new[] { 0, 1, 0, 0, 0 }, keyRep);

        // Key and value share ONE repeated key_value group, so their repetition streams must be identical.
        Assert.Equal(keyRep, valueRep);
    }

    [Fact]
    public async Task ArrayWithRequiredElement_DropsOneDefinitionLevel()
    {
        // A REQUIRED element leaf has maxDef == the container's, so a present element and a present-but-empty
        // container are one level apart rather than two. Pins the §2.4a boundary from the LEGAL side.
        var type = DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: false);
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
        var rows = new int?[]?[] { new int?[] { 1, 2 }, null, Array.Empty<int?>() };

        byte[] bytes = await WriteAsync(schema, NestedVectors.IntList(type, rows));
        (DataField field, int[] def, int[] rep) = await ReadLeafAsync(bytes, "a", "list", "element");

        Assert.False(field.IsNullable);
        Assert.Equal(2, field.MaxDefinitionLevel);
        Assert.Equal(new[] { 2, 2, 0, 1 }, def);
        Assert.Equal(new[] { 0, 1, 0, 0 }, rep);
    }

    // Shared with NestedWriteModelEncoderTests (#844): the model-encoder differential writes vectors and reads
    // back raw level streams through the SAME two primitives this file already pins.
    internal static async Task<byte[]> WriteAsync(StructType schema, ColumnVector column)
    {
        var batch = new ManagedColumnBatch(schema, new[] { column }, column.Length);
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    // Reads one leaf's raw level streams straight out of the file with Parquet.Net, bypassing DeltaSharp's
    // read path entirely — that independence is what makes this a differential rather than a tautology.
    internal static async Task<(DataField Field, int[] Definitions, int[] Repetitions)> ReadLeafAsync(
        byte[] bytes, params string[] path)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, null, false, default);
        DataField field = reader.Schema.DataFields.Single(f => f.Path.ToList().SequenceEqual(path));

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        long numValues = rowGroup.GetMetadata(field)!.MetaData!.NumValues;

        int[]? def = null;
        int[]? rep = null;
        Memory<int>? defLevels = null;
        Memory<int>? repLevels = null;
        if (field.MaxDefinitionLevel > 0)
        {
            def = new int[numValues];
            defLevels = def;
        }

        if (field.MaxRepetitionLevel > 0)
        {
            rep = new int[numValues];
            repLevels = rep;
        }

        await ReadRawAsync(rowGroup, field, (int)numValues, defLevels, repLevels);
        return (field, def ?? Array.Empty<int>(), rep ?? Array.Empty<int>());
    }

    // Parquet.Net's ReadRawAsync is generic in the PHYSICAL value type, so the leaf's own CLR type selects the
    // closed instantiation. Only the shapes these tests author are needed.
    private static ValueTask ReadRawAsync(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int numValues,
        Memory<int>? definitions,
        Memory<int>? repetitions)
    {
        Type clrType = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
        if (clrType == typeof(int))
        {
            return rowGroup.ReadRawAsync<int>(field, new int[numValues], definitions, repetitions, default);
        }

        if (clrType == typeof(long))
        {
            return rowGroup.ReadRawAsync<long>(field, new long[numValues], definitions, repetitions, default);
        }

        if (clrType == typeof(string) || clrType == typeof(ReadOnlyMemory<char>))
        {
            return rowGroup.ReadRawAsync<ReadOnlyMemory<char>>(
                field, new ReadOnlyMemory<char>[numValues], definitions, repetitions, default);
        }

        throw new NotSupportedException($"No raw reader wired for {clrType.Name}.");
    }
}
