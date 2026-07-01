using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class DataTypesFactoryTests
{
    [Fact]
    public void SingletonAccessors_ReturnTheSharedInstances()
    {
        Assert.Same(BooleanType.Instance, DataTypes.BooleanType);
        Assert.Same(ByteType.Instance, DataTypes.ByteType);
        Assert.Same(ShortType.Instance, DataTypes.ShortType);
        Assert.Same(IntegerType.Instance, DataTypes.IntegerType);
        Assert.Same(LongType.Instance, DataTypes.LongType);
        Assert.Same(FloatType.Instance, DataTypes.FloatType);
        Assert.Same(DoubleType.Instance, DataTypes.DoubleType);
        Assert.Same(StringType.Instance, DataTypes.StringType);
        Assert.Same(BinaryType.Instance, DataTypes.BinaryType);
        Assert.Same(DateType.Instance, DataTypes.DateType);
        Assert.Same(TimestampType.Instance, DataTypes.TimestampType);
        Assert.Same(NullType.Instance, DataTypes.NullType);
    }

    [Fact]
    public void CreateDecimalType_BuildsEquivalentType()
    {
        Assert.Equal(new DecimalType(12, 3), DataTypes.CreateDecimalType(12, 3));
    }

    [Fact]
    public void CreateArrayType_BuildsEquivalentType()
    {
        Assert.Equal(
            new ArrayType(IntegerType.Instance, containsNull: false),
            DataTypes.CreateArrayType(IntegerType.Instance, containsNull: false));
    }

    [Fact]
    public void CreateMapType_BuildsEquivalentType()
    {
        Assert.Equal(
            new MapType(StringType.Instance, LongType.Instance, valueContainsNull: false),
            DataTypes.CreateMapType(StringType.Instance, LongType.Instance, valueContainsNull: false));
    }

    [Fact]
    public void CreateStructTypeAndField_BuildEquivalentTypes()
    {
        StructField field = DataTypes.CreateStructField("a", IntegerType.Instance, nullable: false);
        StructType schema = DataTypes.CreateStructType(new[] { field });

        Assert.Equal(new StructField("a", IntegerType.Instance, nullable: false), field);
        Assert.Equal(new StructType(new[] { new StructField("a", IntegerType.Instance, nullable: false) }), schema);
    }
}
