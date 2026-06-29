using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using Xunit;
using Decimal128Type = Apache.Arrow.Types.Decimal128Type;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// AC2 (STORY-02.2.2, #136): all-null, no-null, and mixed-null Arrow arrays keep their validity and
/// null counts across the boundary and back. Coverage spans a zero-copy primitive (int32), a
/// variable-width type (string), and the two materialized types (boolean, decimal128), plus
/// struct-level nullness for a nested column.
/// </summary>
public class ArrowBatchConverterValidityTests
{
    public static IEnumerable<object[]> NullPatterns =>
    [
        [new[] { false, false, false }], // no-null
        [new[] { true, true, true }],    // all-null
        [new[] { false, true, false }],  // mixed
        [new[] { true, false, true }],   // mixed (nulls at the ends)
    ];

    [Theory]
    [MemberData(nameof(NullPatterns))]
    public void RoundTrip_Int32Validity_Preserved(bool[] isNull) =>
        AssertValidityRoundTrips(BuildInt32(isNull), isNull);

    [Theory]
    [MemberData(nameof(NullPatterns))]
    public void RoundTrip_StringValidity_Preserved(bool[] isNull) =>
        AssertValidityRoundTrips(BuildString(isNull), isNull);

    [Theory]
    [MemberData(nameof(NullPatterns))]
    public void RoundTrip_BooleanValidity_Preserved(bool[] isNull) =>
        AssertValidityRoundTrips(BuildBoolean(isNull), isNull);

    [Theory]
    [MemberData(nameof(NullPatterns))]
    public void RoundTrip_DecimalValidity_Preserved(bool[] isNull) =>
        AssertValidityRoundTrips(BuildDecimal(isNull), isNull);

    [Theory]
    [MemberData(nameof(NullPatterns))]
    public void Import_StructLevelNullness_Preserved(bool[] structValid)
    {
        // The pattern's "isNull" doubles as struct validity: true = null row here, so invert.
        var valid = new bool[structValid.Length];
        for (int i = 0; i < valid.Length; i++)
        {
            valid[i] = !structValid[i];
        }

        StructArray structArray = ArrowConverterTestSupport.BuildStructArray(new[] { 1, 2, 3 }, valid);
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("s", structArray, true));

        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);
        AssertValidity(imported.Column(0), structValid);

        using RecordBatch exported = ArrowBatchConverter.ToArrow(imported);
        StructArray exportedStruct = Assert.IsType<StructArray>(exported.Column(0));
        Assert.Equal(structArray.NullCount, exportedStruct.NullCount);
        for (int i = 0; i < structValid.Length; i++)
        {
            Assert.Equal(structValid[i], exportedStruct.IsNull(i));
        }
    }

    private static void AssertValidityRoundTrips(IArrowArray sourceArray, bool[] isNull)
    {
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("c", sourceArray, true));

        using ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source);
        AssertValidity(imported.Column(0), isNull);

        // Export then re-import: the validity bitmap and null count must survive the full cycle.
        using RecordBatch exported = ArrowBatchConverter.ToArrow(imported);
        using ArrowColumnBatch reimported = ArrowBatchConverter.FromArrow(exported);
        AssertValidity(reimported.Column(0), isNull);
    }

    private static void AssertValidity(ColumnVector column, bool[] isNull)
    {
        int expectedNulls = 0;
        foreach (bool n in isNull)
        {
            if (n)
            {
                expectedNulls++;
            }
        }

        Assert.Equal(isNull.Length, column.Length);
        Assert.Equal(expectedNulls, column.NullCount);
        Assert.Equal(expectedNulls > 0, column.HasNulls);
        for (int i = 0; i < isNull.Length; i++)
        {
            Assert.Equal(isNull[i], column.IsNull(i));
        }
    }

    private static Int32Array BuildInt32(bool[] isNull)
    {
        var builder = new Int32Array.Builder();
        for (int i = 0; i < isNull.Length; i++)
        {
            _ = isNull[i] ? builder.AppendNull() : builder.Append((i * 10) + 1);
        }

        return builder.Build();
    }

    private static StringArray BuildString(bool[] isNull)
    {
        var builder = new StringArray.Builder();
        for (int i = 0; i < isNull.Length; i++)
        {
            _ = isNull[i] ? builder.AppendNull() : builder.Append($"value-{i}");
        }

        return builder.Build();
    }

    private static BooleanArray BuildBoolean(bool[] isNull)
    {
        var builder = new BooleanArray.Builder();
        for (int i = 0; i < isNull.Length; i++)
        {
            _ = isNull[i] ? builder.AppendNull() : builder.Append(i % 2 == 0);
        }

        return builder.Build();
    }

    private static Decimal128Array BuildDecimal(bool[] isNull)
    {
        var builder = new Decimal128Array.Builder(new Decimal128Type(10, 2));
        for (int i = 0; i < isNull.Length; i++)
        {
            _ = isNull[i] ? builder.AppendNull() : builder.Append((decimal)((i + 1) * 1.25m));
        }

        return builder.Build();
    }
}
