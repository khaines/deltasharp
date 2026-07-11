using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Unit tests for <see cref="DeltaReadEncoding.BuildConstantColumn"/>, the read-side inverse that
/// const/null-fills a partition column from its canonical <c>add.partitionValues</c> string (#499).
/// Focus: the Hive default-partition sentinel (<see cref="DeltaWriteEncoding.HiveDefaultPartition"/>) is
/// treated as NULL — a cross-engine robustness requirement so a foreign/non-canonical writer that records
/// the sentinel string literally on a <b>typed</b> (int/long/date) partition column does NOT crash the read
/// with an out-of-range parse error.
/// </summary>
public sealed class DeltaReadEncodingTests
{
    [Theory]
    [InlineData("integer")]
    [InlineData("long")]
    [InlineData("date")]
    public void BuildConstantColumn_HiveSentinel_OnTypedPartition_FillsNull_NoThrow(string typeName)
    {
        DataType type = TypeFor(typeName);

        // The exact sentinel a foreign writer may store literally in add.partitionValues.
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(
            type, DeltaWriteEncoding.HiveDefaultPartition, rowCount: 3);

        Assert.True(column.HasNulls);
        for (int r = 0; r < 3; r++)
        {
            Assert.True(column.IsNull(r), $"row {r} of a sentinel-valued {typeName} partition must be null");
        }
    }

    [Fact]
    public void BuildConstantColumn_JsonNull_FillsNull()
    {
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(IntegerType.Instance, value: null, rowCount: 2);

        Assert.True(column.IsNull(0));
        Assert.True(column.IsNull(1));
    }

    [Fact]
    public void BuildConstantColumn_NormalIntegerValue_StillParses()
    {
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(IntegerType.Instance, "42", rowCount: 2);

        Assert.False(column.HasNulls);
        Assert.Equal(42, column.GetValue<int>(0));
        Assert.Equal(42, column.GetValue<int>(1));
    }

    [Fact]
    public void BuildConstantColumn_NormalStringValue_MatchingSentinelText_IsNull_ButOtherStringParses()
    {
        // A genuine (non-sentinel) string value round-trips as data...
        ColumnVector normal = DeltaReadEncoding.BuildConstantColumn(StringType.Instance, "US", rowCount: 1);
        Assert.False(normal.IsNull(0));

        // ...and the sentinel on a string partition is still null (the read never materializes the literal).
        ColumnVector sentinel = DeltaReadEncoding.BuildConstantColumn(
            StringType.Instance, DeltaWriteEncoding.HiveDefaultPartition, rowCount: 1);
        Assert.True(sentinel.IsNull(0));
    }

    private static DataType TypeFor(string name) => name switch
    {
        "integer" => IntegerType.Instance,
        "long" => LongType.Instance,
        "date" => DateType.Instance,
        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "unknown type"),
    };
}
