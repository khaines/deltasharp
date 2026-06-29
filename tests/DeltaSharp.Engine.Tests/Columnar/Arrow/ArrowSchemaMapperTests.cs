using Apache.Arrow;
using DeltaSharp.Engine.Columnar.Arrow;
using DeltaSharp.Engine.Types;
using Xunit;
using ArrowDate32Type = Apache.Arrow.Types.Date32Type;
using ArrowDate64Type = Apache.Arrow.Types.Date64Type;
using ArrowDecimal128Type = Apache.Arrow.Types.Decimal128Type;
using ArrowInt32Type = Apache.Arrow.Types.Int32Type;
using ArrowListType = Apache.Arrow.Types.ListType;
using ArrowMapType = Apache.Arrow.Types.MapType;
using ArrowNullType = Apache.Arrow.Types.NullType;
using ArrowStringType = Apache.Arrow.Types.StringType;
using ArrowStructType = Apache.Arrow.Types.StructType;
using ArrowTimestampType = Apache.Arrow.Types.TimestampType;
using ArrowTimeUnit = Apache.Arrow.Types.TimeUnit;
using ArrowUInt32Type = Apache.Arrow.Types.UInt32Type;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Schema-on-read mapping for the Arrow boundary (STORY-02.2.2, #136): every supported Arrow type
/// resolves to exactly one v1 <see cref="DataType"/> (recursively for nested types), and every gap
/// raises a precise <see cref="UnsupportedTypeException"/> — the "no silent coercion or data loss"
/// guarantee the boundary depends on.
/// </summary>
public class ArrowSchemaMapperTests
{
    [Fact]
    public void ToDeltaType_Primitives_MapToAtomicTypes()
    {
        Assert.Equal(IntegerType.Instance, ArrowSchemaMapper.ToDeltaType(ArrowInt32Type.Default));
        Assert.Equal(DateType.Instance, ArrowSchemaMapper.ToDeltaType(ArrowDate32Type.Default));
        Assert.Equal(StringType.Instance, ArrowSchemaMapper.ToDeltaType(ArrowStringType.Default));
    }

    [Fact]
    public void ToDeltaType_Decimal_PreservesPrecisionAndScale()
    {
        DecimalType decimalType = Assert.IsType<DecimalType>(
            ArrowSchemaMapper.ToDeltaType(new ArrowDecimal128Type(18, 4)));

        Assert.Equal(18, decimalType.Precision);
        Assert.Equal(4, decimalType.Scale);
    }

    [Fact]
    public void ToDeltaType_MicrosecondTimestamp_MapsToTimestamp()
    {
        DataType mapped = ArrowSchemaMapper.ToDeltaType(new ArrowTimestampType(ArrowTimeUnit.Microsecond, "UTC"));
        Assert.Equal(TimestampType.Instance, mapped);
    }

    [Fact]
    public void ToDeltaType_NestedTypes_RecurseIntoChildren()
    {
        var structType = new ArrowStructType(new[] { new Field("v", ArrowInt32Type.Default, nullable: true) });
        StructType mappedStruct = Assert.IsType<StructType>(ArrowSchemaMapper.ToDeltaType(structType));
        Assert.Equal("v", mappedStruct[0].Name);
        Assert.IsType<IntegerType>(mappedStruct[0].DataType);

        var listType = new ArrowListType(new Field("item", ArrowStringType.Default, nullable: false));
        ArrayType mappedArray = Assert.IsType<ArrayType>(ArrowSchemaMapper.ToDeltaType(listType));
        Assert.IsType<StringType>(mappedArray.ElementType);
        Assert.False(mappedArray.ContainsNull); // the list value field's nullability is carried

        var mapType = new ArrowMapType(ArrowInt32Type.Default, ArrowStringType.Default);
        MapType mappedMap = Assert.IsType<MapType>(ArrowSchemaMapper.ToDeltaType(mapType));
        Assert.IsType<IntegerType>(mappedMap.KeyType);
        Assert.IsType<StringType>(mappedMap.ValueType);
    }

    [Fact]
    public void ToDeltaType_NonMicrosecondTimestamp_Throws()
    {
        Assert.Throws<UnsupportedTypeException>(
            () => ArrowSchemaMapper.ToDeltaType(new ArrowTimestampType(ArrowTimeUnit.Nanosecond, "UTC")));
    }

    [Fact]
    public void ToDeltaType_UnsupportedTypes_Throw()
    {
        Assert.Throws<UnsupportedTypeException>(() => ArrowSchemaMapper.ToDeltaType(ArrowUInt32Type.Default));
        Assert.Throws<UnsupportedTypeException>(() => ArrowSchemaMapper.ToDeltaType(ArrowDate64Type.Default));
        Assert.Throws<UnsupportedTypeException>(() => ArrowSchemaMapper.ToDeltaType(ArrowNullType.Default));
    }

    [Fact]
    public void FromArrow_UnsupportedColumn_ThrowsAtBoundary()
    {
        UInt32Array unsupported = new UInt32Array.Builder().Append(1u).AppendNull().Append(3u).Build();
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("u", unsupported, true));

        // The boundary refuses the batch rather than silently dropping or coercing the column.
        Assert.Throws<UnsupportedTypeException>(() => ArrowBatchConverter.FromArrow(source));
    }
}
