using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// AC1 (STORY-02.2.2, #136): a v1 schema of primitive, decimal, date, timestamp, string/binary, and
/// nested columns survives the boundary unchanged. Flat types round-trip
/// DeltaSharp &#8594; Arrow &#8594; DeltaSharp (the engine builds those); nested types round-trip
/// Arrow &#8594; DeltaSharp &#8594; Arrow (the engine carries them as an opaque pass-through), since
/// the managed factory does not build nested vectors.
/// </summary>
public class ArrowBatchConverterRoundTripTests
{
    [Fact]
    public void RoundTrip_AllPrimitiveDecimalDateTimestampColumns_PreservesSchemaAndValues()
    {
        ManagedColumnBatch original = BuildAllTypesBatch();

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(original);
        using ArrowColumnBatch back = ArrowBatchConverter.FromArrow(arrow);

        // Schema is unchanged: field names, logical types, and nullability all round-trip.
        Assert.Equal(original.Schema, back.Schema);
        Assert.Equal(original.ColumnCount, back.ColumnCount);
        Assert.Equal(original.RowCount, back.RowCount);

        for (int c = 0; c < original.ColumnCount; c++)
        {
            ArrowConverterTestSupport.AssertColumnsEqual(original.Column(c), back.Column(c));
        }
    }

    [Fact]
    public void RoundTrip_ArrowSchemaTypes_AreTheExpectedEdgeTypes()
    {
        ManagedColumnBatch original = BuildAllTypesBatch();

        using RecordBatch arrow = ArrowBatchConverter.ToArrow(original);

        // The exported Arrow schema names the v1 edge types (decimal precision/scale and the
        // microsecond timestamp unit are carried exactly).
        Assert.IsType<Apache.Arrow.Types.BooleanType>(FieldType(arrow, "b"));
        Assert.IsType<Apache.Arrow.Types.Int8Type>(FieldType(arrow, "tiny"));
        Assert.IsType<Apache.Arrow.Types.Date32Type>(FieldType(arrow, "dt"));
        var ts = Assert.IsType<Apache.Arrow.Types.TimestampType>(FieldType(arrow, "ts"));
        Assert.Equal(Apache.Arrow.Types.TimeUnit.Microsecond, ts.Unit);
        var decc = Assert.IsType<Apache.Arrow.Types.Decimal128Type>(FieldType(arrow, "decc"));
        Assert.Equal(10, decc.Precision);
        Assert.Equal(2, decc.Scale);
        var decw = Assert.IsType<Apache.Arrow.Types.Decimal128Type>(FieldType(arrow, "decw"));
        Assert.Equal(30, decw.Precision);
        Assert.Equal(4, decw.Scale);
    }

    [Fact]
    public void RoundTrip_NestedStructAndList_PreservesSchemaAndValues()
    {
        StructArray structArray = ArrowConverterTestSupport.BuildStructArray(new[] { 10, 20, 30 });
        ListArray listArray = ArrowConverterTestSupport.BuildListArray(
            new[] { new[] { 1, 2 }, System.Array.Empty<int>(), new[] { 3 } });
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(
            ("s", structArray, true), ("l", listArray, true));

        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);

        // The DeltaSharp schema reflects the nested types (struct -> StructType, list -> ArrayType).
        StructType structType = Assert.IsType<StructType>(imported.Schema[0].DataType);
        Assert.Equal("v", structType[0].Name);
        Assert.IsType<IntegerType>(structType[0].DataType);
        ArrayType arrayType = Assert.IsType<ArrayType>(imported.Schema[1].DataType);
        Assert.IsType<IntegerType>(arrayType.ElementType);
        Assert.Equal(3, imported.RowCount);

        using RecordBatch exported = ArrowBatchConverter.ToArrow(imported);

        Assert.Equal(2, exported.ColumnCount);
        Assert.Equal(3, exported.Length);

        StructArray exportedStruct = Assert.IsType<StructArray>(exported.Column(0));
        Assert.Equal(structArray.Length, exportedStruct.Length);
        Assert.Equal(structArray.NullCount, exportedStruct.NullCount);
        var exportedChild = (Int32Array)exportedStruct.Fields[0];
        Assert.Equal(10, exportedChild.GetValue(0)!.Value);
        Assert.Equal(20, exportedChild.GetValue(1)!.Value);
        Assert.Equal(30, exportedChild.GetValue(2)!.Value);

        ListArray exportedList = Assert.IsType<ListArray>(exported.Column(1));
        Assert.Equal(3, exportedList.Length);
        var exportedValues = (Int32Array)exportedList.Values;
        Assert.Equal(3, exportedValues.Length); // 2 + 0 + 1 elements
        Assert.Equal(1, exportedValues.GetValue(0)!.Value);
        Assert.Equal(2, exportedValues.GetValue(1)!.Value);
        Assert.Equal(3, exportedValues.GetValue(2)!.Value);
    }

    private static Apache.Arrow.Types.IArrowType FieldType(RecordBatch batch, string name) =>
        batch.Schema.GetFieldByName(name).DataType;

    private static ManagedColumnBatch BuildAllTypesBatch()
    {
        const int rows = 3;
        var fields = new List<StructField>
        {
            new("b", BooleanType.Instance),
            new("tiny", ByteType.Instance),
            new("sh", ShortType.Instance),
            new("i", IntegerType.Instance),
            new("l", LongType.Instance),
            new("f", FloatType.Instance),
            new("d", DoubleType.Instance),
            new("dt", DateType.Instance),
            new("ts", TimestampType.Instance),
            new("decc", new DecimalType(10, 2)),
            new("decw", new DecimalType(30, 4)),
            new("s", StringType.Instance),
            new("bin", BinaryType.Instance),
        };

        var columns = new List<ColumnVector>();

        MutableColumnVector b = ColumnVectors.Create(BooleanType.Instance, rows);
        b.AppendValue(true);
        b.AppendNull();
        b.AppendValue(false);
        columns.Add(b);

        MutableColumnVector tiny = ColumnVectors.Create(ByteType.Instance, rows);
        tiny.AppendValue((byte)1);
        tiny.AppendValue((byte)200); // exercises the high bit (Int8 two's-complement reinterpret)
        tiny.AppendNull();
        columns.Add(tiny);

        MutableColumnVector sh = ColumnVectors.Create(ShortType.Instance, rows);
        sh.AppendValue((short)100);
        sh.AppendNull();
        sh.AppendValue((short)-100);
        columns.Add(sh);

        MutableColumnVector i = ColumnVectors.Create(IntegerType.Instance, rows);
        i.AppendValue(10);
        i.AppendNull();
        i.AppendValue(-30);
        columns.Add(i);

        MutableColumnVector l = ColumnVectors.Create(LongType.Instance, rows);
        l.AppendValue(1L);
        l.AppendValue(long.MaxValue);
        l.AppendNull();
        columns.Add(l);

        MutableColumnVector f = ColumnVectors.Create(FloatType.Instance, rows);
        f.AppendValue(1.5f);
        f.AppendNull();
        f.AppendValue(-3.5f);
        columns.Add(f);

        MutableColumnVector d = ColumnVectors.Create(DoubleType.Instance, rows);
        d.AppendValue(1.25d);
        d.AppendNull();
        d.AppendValue(-3.75d);
        columns.Add(d);

        MutableColumnVector dt = ColumnVectors.Create(DateType.Instance, rows);
        dt.AppendValue(18000);
        dt.AppendNull();
        dt.AppendValue(19000);
        columns.Add(dt);

        MutableColumnVector ts = ColumnVectors.Create(TimestampType.Instance, rows);
        ts.AppendValue(1_000_000L);
        ts.AppendNull();
        ts.AppendValue(2_000_000L);
        columns.Add(ts);

        MutableColumnVector decc = ColumnVectors.Create(new DecimalType(10, 2), rows);
        decc.AppendValue(12345L); // 123.45 unscaled
        decc.AppendNull();
        decc.AppendValue(-678L);
        columns.Add(decc);

        MutableColumnVector decw = ColumnVectors.Create(new DecimalType(30, 4), rows);
        Int128 wide = (Int128)long.MaxValue * 1000;
        decw.AppendValue(wide);
        decw.AppendNull();
        decw.AppendValue(-wide);
        columns.Add(decw);

        MutableColumnVector s = ColumnVectors.Create(StringType.Instance, rows);
        s.AppendBytes("hello"u8);
        s.AppendNull();
        s.AppendBytes("world"u8);
        columns.Add(s);

        MutableColumnVector bin = ColumnVectors.Create(BinaryType.Instance, rows);
        bin.AppendBytes(new byte[] { 1, 2, 3 });
        bin.AppendNull();
        bin.AppendBytes(new byte[] { 0xFF, 0x00 });
        columns.Add(bin);

        return new ManagedColumnBatch(new StructType(fields), columns, rows);
    }
}
