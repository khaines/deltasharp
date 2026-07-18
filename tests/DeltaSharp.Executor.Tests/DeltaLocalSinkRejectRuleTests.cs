using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Unit tests for <see cref="DeltaLocalSink.RowRejected"/> (#581/#596), the Delta constraint reject rule: a
/// row is rejected when the predicate result is <b>not TRUE</b> (null OR false), matching Delta's
/// <c>CheckDeltaInvariant.assertRule</c>. These pin the <b>null</b> half of the rule independently of the
/// interpreter's incidental value-lane content, so the contract-mandated <see cref="ColumnVector.IsNull(int)"/>
/// guard is load-bearing (a mutation dropping it fails here).
/// </summary>
public sealed class DeltaLocalSinkRejectRuleTests
{
    private static ColumnVector Bool(bool value, bool isNull)
    {
        MutableColumnVector v = ColumnVectors.Create(BooleanType.Instance, 1);
        v.AppendValue(value);
        if (isNull)
        {
            // SetNull only flips the validity bit; the value lane keeps `value`, so a null slot can hold `true`.
            v.SetNull(0);
        }

        return v;
    }

    [Fact]
    public void RowRejected_TrueResult_NotRejected()
    {
        Assert.False(DeltaLocalSink.RowRejected(Bool(value: true, isNull: false), row: 0));
    }

    [Fact]
    public void RowRejected_FalseResult_Rejected()
    {
        Assert.True(DeltaLocalSink.RowRejected(Bool(value: false, isNull: false), row: 0));
    }

    [Fact]
    public void RowRejected_NullResult_Rejected_EvenWhenValueLaneHoldsTrue()
    {
        // The crux: a null result is rejected because it is NOT TRUE — regardless of the value lane, which the
        // ColumnVector contract leaves unspecified at a null slot. Here the lane holds `true`, yet the row must
        // still be rejected via the IsNull guard (not via the value). Pins the guard against a fail-open drop.
        ColumnVector result = Bool(value: true, isNull: true);
        Assert.True(result.IsNull(0));
        Assert.True(result.GetValue<bool>(0)); // value lane is true at the null slot

        Assert.True(DeltaLocalSink.RowRejected(result, row: 0));
    }
}
