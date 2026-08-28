using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Parquet.Schema;
using Xunit;
using static DeltaSharp.Storage.Tests.Parquet.NestedValueModel;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #873 §3.3 golden level-stream cells (10, 10a, 10b): the recursive shredder's emitted <c>(rep, def)</c>
/// leaf streams are read straight off the wire with Parquet.Net's own primitives (bypassing DeltaSharp's read
/// path) and compared to the byte-exact tables the design's §2.4/§2.10.4 traces prescribe. A self-consistent
/// but non-canonical encoding — one the round-trip oracle would accept — is caught here.
/// </summary>
public sealed class NestedWithinNestedGoldenTests
{
    // 10 — array<array<int>> over the §2.4 4-row fixture (null / [] / [null,[]] / [[7,null],[9]]): the leaf
    // stream must equal (0,0)(0,1)(0,2)(1,3)(0,5)(2,4)(1,5) byte-for-byte. This fails if the outer level
    // miscounts inner-list occurrences OR if the rewritten NestedLevelGuard false-rejects slot (1,3).
    [Fact]
    public async Task Write_ArrayOfArray_EmitsExactDremelStream()
    {
        ArrayType type = DataTypes.CreateArrayType(DataTypes.CreateArrayType(DataTypes.IntegerType));
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr(null, Arr()),
            Arr(Arr(7, null), Arr(9)),
        };

        (DataField field, int[] def, int[] rep) = await WriteAndReadLeafAsync(
            type, rows, "c", "list", "element", "list", "element");

        Assert.Equal(5, field.MaxDefinitionLevel);
        Assert.Equal(2, field.MaxRepetitionLevel);
        Assert.Equal(new[] { 0, 1, 2, 3, 5, 4, 5 }, def);
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 2, 1 }, rep);
    }

    // 10a — array<struct<a:int>> over null / [] / [null] / [{a:7},{a:null}]: the struct-optional-group def
    // increment (struct-null element → def 2, present-null-field → def 3, present → def 4) and that the guard
    // admits a struct-null element at list position >= 1.
    [Fact]
    public async Task Write_ArrayOfStruct_EmitsExactDremelStream()
    {
        StructType inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
        });
        ArrayType type = DataTypes.CreateArrayType(inner);
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr((object?)null),
            Arr(Struct(7), Struct((object?)null)),
        };

        (DataField field, int[] def, int[] rep) = await WriteAndReadLeafAsync(
            type, rows, "c", "list", "element", "a");

        Assert.Equal(4, field.MaxDefinitionLevel);
        Assert.Equal(1, field.MaxRepetitionLevel);
        Assert.Equal(new[] { 0, 1, 2, 4, 3 }, def);
        Assert.Equal(new[] { 0, 0, 0, 0, 1 }, rep);
    }

    // 10b — map<string, array<long>>: BOTH the key stream (MaxRep 1) AND the value.element stream (MaxRep 2)
    // byte-for-byte over an entry whose inner value-list is EMPTY at entry >= 1 — pins the key/value decouple
    // (§2.10.5, unequal-length streams) and that the guard admits the shallower-level empty value-list.
    [Fact]
    public async Task Write_MapValueArray_EmitsExactDremelStream()
    {
        MapType type = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.CreateArrayType(DataTypes.LongType));
        var rows = new object?[]
        {
            Map(("k1", Arr(10L, null)), ("k2", Arr())),
        };

        (DataField keyField, int[] keyDef, int[] keyRep) = await WriteAndReadLeafAsync(
            type, rows, "c", "key_value", "key");
        Assert.Equal(2, keyField.MaxDefinitionLevel);
        Assert.Equal(1, keyField.MaxRepetitionLevel);
        Assert.Equal(new[] { 2, 2 }, keyDef);
        Assert.Equal(new[] { 0, 1 }, keyRep);

        (DataField valueField, int[] valueDef, int[] valueRep) = await ReadLeafOnlyAsync(
            LastBytes, "c", "key_value", "value", "list", "element");
        Assert.Equal(5, valueField.MaxDefinitionLevel);
        Assert.Equal(2, valueField.MaxRepetitionLevel);
        Assert.Equal(new[] { 5, 4, 3 }, valueDef);
        Assert.Equal(new[] { 0, 2, 1 }, valueRep);
    }

    private byte[] LastBytes = Array.Empty<byte>();

    private async Task<(DataField Field, int[] Def, int[] Rep)> WriteAndReadLeafAsync(
        DataType nestedType, IReadOnlyList<object?> rows, params string[] leafPath)
    {
        var schema = DataTypes.CreateStructType(new[] { new StructField("c", nestedType, nullable: true) });
        ColumnVector column = Build(nestedType, rows);
        var batch = new ManagedColumnBatch(schema, new[] { column }, rows.Count);
        LastBytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        return await NestedParquetLevelStreamTests.ReadLeafAsync(LastBytes, leafPath);
    }

    private static Task<(DataField Field, int[] Def, int[] Rep)> ReadLeafOnlyAsync(
        byte[] bytes, params string[] leafPath) =>
        NestedParquetLevelStreamTests.ReadLeafAsync(bytes, leafPath);
}
