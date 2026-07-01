using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class PhysicalLayoutTests
{
    [Theory]
    [InlineData(typeof(BooleanType), 1)]
    [InlineData(typeof(ByteType), 1)]
    [InlineData(typeof(ShortType), 2)]
    [InlineData(typeof(IntegerType), 4)]
    [InlineData(typeof(LongType), 8)]
    [InlineData(typeof(FloatType), 4)]
    [InlineData(typeof(DoubleType), 8)]
    [InlineData(typeof(DateType), 4)]
    [InlineData(typeof(TimestampType), 8)]
    public void FixedWidthAtomics_ReportExpectedByteWidth(Type clrType, int expectedBytes)
    {
        DataType type = InstanceOf(clrType);

        Assert.True(PhysicalLayoutResolver.TryResolve(type, out PhysicalLayout layout));
        Assert.Equal(PhysicalLayoutKind.FixedWidth, layout.Kind);
        Assert.True(layout.IsFixedWidth);
        Assert.Equal(expectedBytes, layout.FixedWidthBytes);
        Assert.Equal(layout, PhysicalLayoutResolver.Resolve(type));
    }

    [Theory]
    [InlineData(typeof(StringType))]
    [InlineData(typeof(BinaryType))]
    public void VariableLengthAtomics_ReportVariableLayout(Type clrType)
    {
        DataType type = InstanceOf(clrType);

        Assert.True(PhysicalLayoutResolver.TryResolve(type, out PhysicalLayout layout));
        Assert.Equal(PhysicalLayoutKind.Variable, layout.Kind);
        Assert.False(layout.IsFixedWidth);
        Assert.Equal(0, layout.FixedWidthBytes);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(18, 8)]
    [InlineData(19, 16)]
    [InlineData(38, 16)]
    public void Decimal_PhysicalWidthDependsOnPrecision(int precision, int expectedBytes)
    {
        var type = new DecimalType(precision, 0);

        Assert.True(PhysicalLayoutResolver.TryResolve(type, out PhysicalLayout layout));
        Assert.Equal(PhysicalLayoutKind.FixedWidth, layout.Kind);
        Assert.Equal(expectedBytes, layout.FixedWidthBytes);
        Assert.Equal(precision <= DecimalType.MaxCompactPrecision, type.IsCompact);
    }

    [Fact]
    public void NestedTypes_ReportNestedLayout()
    {
        DataType[] nested =
        {
            new ArrayType(IntegerType.Instance),
            new MapType(StringType.Instance, IntegerType.Instance),
            new StructType(new[] { new StructField("a", IntegerType.Instance) }),
        };

        foreach (DataType type in nested)
        {
            Assert.True(PhysicalLayoutResolver.TryResolve(type, out PhysicalLayout layout));
            Assert.Equal(PhysicalLayoutKind.Nested, layout.Kind);
        }
    }

    [Fact]
    public void NullType_HasNoPhysicalLayout()
    {
        Assert.False(PhysicalLayoutResolver.TryResolve(NullType.Instance, out PhysicalLayout layout));
        Assert.Equal(default, layout);

        UnsupportedTypeException ex =
            Assert.Throws<UnsupportedTypeException>(() => PhysicalLayoutResolver.Resolve(NullType.Instance));
        Assert.Contains("void", ex.Message);
    }

    [Fact]
    public void FixedWidth_RejectsNonPositiveWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicalLayout.FixedWidth(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicalLayout.FixedWidth(-4));
    }

    [Fact]
    public void PhysicalLayout_EqualityAndOperators()
    {
        Assert.Equal(PhysicalLayout.FixedWidth(4), PhysicalLayout.FixedWidth(4));
        Assert.True(PhysicalLayout.FixedWidth(4) == PhysicalLayout.FixedWidth(4));
        Assert.True(PhysicalLayout.FixedWidth(4) != PhysicalLayout.FixedWidth(8));
        Assert.NotEqual(PhysicalLayout.Variable, PhysicalLayout.Nested);
        Assert.Equal(PhysicalLayout.FixedWidth(8).GetHashCode(), PhysicalLayout.FixedWidth(8).GetHashCode());
    }

    private static DataType InstanceOf(Type clrType)
    {
        // Test-only reflection (test projects are exempt from the banned-API policy) to map a
        // CLR type to its singleton, keeping the [Theory] data declarative.
        System.Reflection.PropertyInfo instance =
            clrType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        return (DataType)instance.GetValue(null)!;
    }
}
