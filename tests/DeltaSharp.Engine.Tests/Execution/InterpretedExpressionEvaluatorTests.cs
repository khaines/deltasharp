using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Exercises the interpreted, vectorized batch expression evaluator (STORY-03.4.1): the AOT-clean
/// semantic baseline (ADR-0001 parity oracle) that lets filters and projections evaluate real
/// arithmetic, comparison, boolean (Kleene 3VL), cast, and null-check expressions — not just a
/// <see cref="ColumnReference"/>. Tests prove the four acceptance criteria: (1) per-node values AND
/// validity match Spark parity; (2) evaluation is selection- and slice-aware with deterministic output
/// order; (3) ANSI/null contracts (decimal overflow, timestamp casts, NaN comparisons, null inputs)
/// follow the EPIC-02 type system + #143 null propagation; (4) the tier carries no runtime code
/// generation or reflection-emit (guarded here; proven authoritatively by the AOT publish gate).
/// </summary>
public class InterpretedExpressionEvaluatorTests
{
    private const string BackendName = "interpreted-vectorized";

    // ----- type + reference shorthands -----

    private static readonly DecimalType Dec102 = new(10, 2);
    private static readonly DecimalType Dec380 = new(38, 0);

    private static StructField F(string name, DataType type, bool nullable) => new(name, type, nullable);

    private static StructType Schema(params StructField[] fields) => new(fields);

    private static ColumnReference Ref(int ordinal, DataType type, bool nullable = false) => new(ordinal, type, nullable);

    // ----- column builders (logical row order, nulls via null entries) -----

    private static ColumnVector IntCol(params int?[] values) => Build(DataTypes.IntegerType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector LongCol(params long?[] values) => Build(DataTypes.LongType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector ShortCol(params short?[] values) => Build(DataTypes.ShortType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector DoubleCol(params double?[] values) => Build(DataTypes.DoubleType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector FloatCol(params float?[] values) => Build(DataTypes.FloatType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector BoolCol(params bool?[] values) => Build(DataTypes.BooleanType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector DateCol(params int?[] values) => Build(DataTypes.DateType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector TimestampCol(params long?[] values) => Build(DataTypes.TimestampType, values, (v, x) => v.AppendValue(x));

    private static ColumnVector TimestampNtzCol(params long?[] values) => Build(DataTypes.TimestampNtzType, values, (v, x) => v.AppendValue(x));

    // Signed tinyint: stored as a CLR byte but interpreted as sbyte (Spark tinyint is signed).
    private static ColumnVector ByteCol(params int?[] signedValues)
        => Build(DataTypes.ByteType, signedValues, (v, x) => v.AppendValue(unchecked((byte)(sbyte)x)));

    // Compact decimal (precision <= 18): unscaled mantissa stored as long.
    private static ColumnVector DecimalCol(DecimalType type, params long?[] unscaled)
        => Build(type, unscaled, (v, x) => v.AppendValue(x));

    // Wide decimal (precision > 18): unscaled mantissa stored as Int128.
    private static ColumnVector WideDecimalCol(DecimalType type, params Int128?[] unscaled)
        => Build(type, unscaled, (v, x) => v.AppendValue(x));

    private static ColumnVector StrCol(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, Math.Max(values.Length, 1));
        foreach (string? x in values)
        {
            if (x is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(x));
            }
        }

        return v;
    }

    private static ColumnVector Build<T>(DataType type, T?[] values, Action<MutableColumnVector, T> append)
        where T : struct
    {
        MutableColumnVector v = ColumnVectors.Create(type, Math.Max(values.Length, 1));
        foreach (T? x in values)
        {
            if (x.HasValue)
            {
                append(v, x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
        => new ManagedColumnBatch(schema, columns, columns.Length > 0 ? columns[0].Length : 0);

    // ----- evaluate + read result -----

    private static ColumnVector Eval(PhysicalExpression expression, StructType schema, ColumnBatch batch, IExecutionMemory? memory = null)
    {
        ExpressionEvaluator evaluator = ExpressionEvaluators.Build(expression, schema, BackendName, OperatorKind.Project);
        var ledger = new BatchEvaluationMemory(memory ?? BoundedExecutionMemory.Unbounded);
        try
        {
            return evaluator.Evaluate(batch, ledger, CancellationToken.None);
        }
        finally
        {
            // The result vector is an independent managed allocation; releasing the ledger only returns
            // the budget accounting, so the returned vector stays valid for assertions.
            ledger.Release();
        }
    }

    private static T?[] Read<T>(ColumnVector v, Func<ColumnVector, int, T> get)
        where T : struct
    {
        var result = new T?[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = v.IsNull(i) ? null : get(v, i);
        }

        return result;
    }

    private static bool?[] Bools(ColumnVector v) => Read(v, static (c, i) => c.GetValue<bool>(i));

    private static int?[] Ints(ColumnVector v) => Read(v, static (c, i) => c.GetValue<int>(i));

    private static long?[] Longs(ColumnVector v) => Read(v, static (c, i) => c.GetValue<long>(i));

    private static double?[] Doubles(ColumnVector v) => Read(v, static (c, i) => c.GetValue<double>(i));

    private static float?[] Floats(ColumnVector v) => Read(v, static (c, i) => c.GetValue<float>(i));

    // Signed tinyint read-back: reinterpret the stored byte through sbyte.
    private static int?[] SignedBytes(ColumnVector v) => Read(v, static (c, i) => (int)(sbyte)c.GetValue<byte>(i));

    private static decimal?[] Decimals(ColumnVector v)
    {
        var type = (DecimalType)v.Type;
        decimal divisor = 1m;
        for (int s = 0; s < type.Scale; s++)
        {
            divisor *= 10m;
        }

        var result = new decimal?[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            if (v.IsNull(i))
            {
                continue;
            }

            Int128 unscaled = type.IsCompact ? v.GetValue<long>(i) : v.GetValue<Int128>(i);
            result[i] = (decimal)unscaled / divisor;
        }

        return result;
    }

    // =====================================================================================
    // AC1 — per-node values + validity (arithmetic, comparison, boolean, cast, null-check)
    // =====================================================================================

    [Theory]
    [InlineData(ArithmeticOperator.Add, new[] { 8, 10 })]
    [InlineData(ArithmeticOperator.Subtract, new[] { 4, 4 })]
    [InlineData(ArithmeticOperator.Multiply, new[] { 12, 21 })]
    [InlineData(ArithmeticOperator.Remainder, new[] { 0, 1 })]
    public void Arithmetic_Integral_ProducesWiderIntegralValues(ArithmeticOperator op, int[] expected)
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(6, 7), IntCol(2, 3));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), op);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(DataTypes.IntegerType, result.Type);
        Assert.Equal(expected.Select(x => (int?)x), Ints(result));
    }

    [Fact]
    public void Arithmetic_Divide_NonDecimal_PromotesToDouble()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(6, 7), IntCol(2, 3));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Divide);

        ColumnVector result = Eval(expr, schema, batch);

        // Spark: integral `/` always yields a double result (6/2 = 3.0, 7/3 = 2.333...).
        Assert.Equal(DataTypes.DoubleType, result.Type);
        double?[] values = Doubles(result);
        Assert.Equal(3.0d, values[0]!.Value, 12);
        Assert.Equal(7.0d / 3.0d, values[1]!.Value, 12);
    }

    [Fact]
    public void Arithmetic_PropagatesNullFromEitherOperand()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true));
        ColumnBatch batch = Batch(schema, IntCol(5, null, 3), IntCol(2, 2, null));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType, true), Ref(1, DataTypes.IntegerType, true), ArithmeticOperator.Add);

        ColumnVector result = Eval(expr, schema, batch);

        // propagate-on-any-null: only the first lane has two non-null operands.
        Assert.Equal(new int?[] { 7, null, null }, Ints(result));
    }

    [Fact]
    public void Arithmetic_MixedWidth_WidensToWiderType()
    {
        StructType schema = Schema(F("a", DataTypes.ShortType, false), F("b", DataTypes.LongType, false));
        ColumnBatch batch = Batch(schema, ShortCol(3, 4), LongCol(10, 20));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.ShortType), Ref(1, DataTypes.LongType), ArithmeticOperator.Multiply);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(DataTypes.LongType, result.Type);
        Assert.Equal(new long?[] { 30, 80 }, Longs(result));
    }

    [Fact]
    public void Arithmetic_Decimal_AddAndMultiply_AreExact()
    {
        StructType schema = Schema(F("a", Dec102, false), F("b", Dec102, false));
        ColumnBatch batch = Batch(schema, DecimalCol(Dec102, 150, 225), DecimalCol(Dec102, 50, 25));

        ColumnVector sum = Eval(new ArithmeticExpression(Ref(0, Dec102), Ref(1, Dec102), ArithmeticOperator.Add), schema, batch);
        ColumnVector product = Eval(new ArithmeticExpression(Ref(0, Dec102), Ref(1, Dec102), ArithmeticOperator.Multiply), schema, batch);

        // 1.50 + 0.50 = 2.00; 2.25 + 0.25 = 2.50 (scale preserved at 2).
        Assert.Equal(new decimal?[] { 2.00m, 2.50m }, Decimals(sum));

        // 1.50 * 0.50 = 0.75; 2.25 * 0.25 = 0.5625 (scale = sum of operand scales = 4).
        Assert.Equal(new decimal?[] { 0.75m, 0.5625m }, Decimals(product));
    }

    [Theory]
    [InlineData(ComparisonOperator.Equal, new[] { false, true, false })]
    [InlineData(ComparisonOperator.NotEqual, new[] { true, false, true })]
    [InlineData(ComparisonOperator.LessThan, new[] { true, false, false })]
    [InlineData(ComparisonOperator.LessThanOrEqual, new[] { true, true, false })]
    [InlineData(ComparisonOperator.GreaterThan, new[] { false, false, true })]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, new[] { false, true, true })]
    public void Comparison_AllOperators_ProduceBooleanValues(ComparisonOperator op, bool[] expected)
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(1, 2, 3), IntCol(2, 2, 2));
        var expr = new ComparisonExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), op);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(DataTypes.BooleanType, result.Type);
        Assert.Equal(expected.Select(x => (bool?)x), Bools(result));
    }

    [Fact]
    public void Comparison_PropagatesNull()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(5, null), IntCol(3, 3));
        var expr = new ComparisonExpression(Ref(0, DataTypes.IntegerType, true), Ref(1, DataTypes.IntegerType), ComparisonOperator.GreaterThan);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new bool?[] { true, null }, Bools(result));
    }

    [Fact]
    public void Comparison_String_UsesByteLexicographicOrder()
    {
        StructType schema = Schema(F("s", DataTypes.StringType, true));
        ColumnBatch batch = Batch(schema, StrCol("apple", "banana", "apple"));
        var literal = Literal.OfString("banana");

        ColumnVector equal = Eval(new ComparisonExpression(Ref(0, DataTypes.StringType, true), Literal.OfString("apple"), ComparisonOperator.Equal), schema, batch);
        ColumnVector less = Eval(new ComparisonExpression(Ref(0, DataTypes.StringType, true), literal, ComparisonOperator.LessThan), schema, batch);

        Assert.Equal(new bool?[] { true, false, true }, Bools(equal));
        Assert.Equal(new bool?[] { true, false, true }, Bools(less)); // "apple" < "banana"; "banana" not < "banana"
    }

    [Fact]
    public void Boolean_KleeneAnd_RescuesWithFalse()
    {
        ColumnVector result = EvalLogical(LogicalOperator.And);

        // left  = T T T F F F N N N
        // right = T F N T F N T F N
        Assert.Equal(
            new bool?[] { true, false, null, false, false, false, null, false, null },
            Bools(result));
    }

    [Fact]
    public void Boolean_KleeneOr_RescuesWithTrue()
    {
        ColumnVector result = EvalLogical(LogicalOperator.Or);

        Assert.Equal(
            new bool?[] { true, true, true, true, false, null, true, null, null },
            Bools(result));
    }

    [Fact]
    public void Boolean_KleeneNot_FlipsAndPreservesNull()
    {
        StructType schema = Schema(F("p", DataTypes.BooleanType, true));
        ColumnBatch batch = Batch(schema, BoolCol(true, false, null));
        var expr = new LogicalExpression(Ref(0, DataTypes.BooleanType, true));

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new bool?[] { false, true, null }, Bools(result));
    }

    private static ColumnVector EvalLogical(LogicalOperator op)
    {
        StructType schema = Schema(F("l", DataTypes.BooleanType, true), F("r", DataTypes.BooleanType, true));
        ColumnBatch batch = Batch(
            schema,
            BoolCol(true, true, true, false, false, false, null, null, null),
            BoolCol(true, false, null, true, false, null, true, false, null));
        var expr = new LogicalExpression(Ref(0, DataTypes.BooleanType, true), Ref(1, DataTypes.BooleanType, true), op);
        return Eval(expr, schema, batch);
    }

    [Fact]
    public void NullCheck_IsNull_AndIsNotNull_AreNeverNull()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, true));
        ColumnBatch batch = Batch(schema, IntCol(1, null, 3));

        ColumnVector isNull = Eval(new IsNullExpression(Ref(0, DataTypes.IntegerType, true), negated: false), schema, batch);
        ColumnVector isNotNull = Eval(new IsNullExpression(Ref(0, DataTypes.IntegerType, true), negated: true), schema, batch);

        Assert.False(isNull.HasNulls);
        Assert.False(isNotNull.HasNulls);
        Assert.Equal(new bool?[] { false, true, false }, Bools(isNull));
        Assert.Equal(new bool?[] { true, false, true }, Bools(isNotNull));
    }

    [Fact]
    public void Literal_BroadcastsConstant_AndTypedNull()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(10, 20, 30));

        ColumnVector ones = Eval(Literal.OfInt(1), schema, batch);
        ColumnVector nulls = Eval(Literal.Null(DataTypes.IntegerType), schema, batch);

        Assert.Equal(new int?[] { 1, 1, 1 }, Ints(ones));
        Assert.Equal(new int?[] { null, null, null }, Ints(nulls));
    }

    [Fact]
    public void Cast_NumericCore_ConvertsValues()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(1, 2, 3));

        ColumnVector toLong = Eval(new CastExpression(Ref(0, DataTypes.IntegerType), DataTypes.LongType), schema, batch);
        ColumnVector toDouble = Eval(new CastExpression(Ref(0, DataTypes.IntegerType), DataTypes.DoubleType), schema, batch);
        ColumnVector toDecimal = Eval(new CastExpression(Ref(0, DataTypes.IntegerType), Dec102), schema, batch);

        Assert.Equal(new long?[] { 1, 2, 3 }, Longs(toLong));
        Assert.Equal(new double?[] { 1, 2, 3 }, Doubles(toDouble));
        Assert.Equal(new decimal?[] { 1.00m, 2.00m, 3.00m }, Decimals(toDecimal));
    }

    [Fact]
    public void Cast_FloatingToIntegral_TruncatesTowardZero()
    {
        StructType schema = Schema(F("d", DataTypes.DoubleType, false));
        ColumnBatch batch = Batch(schema, DoubleCol(1.9d, -1.9d, 2.5d, -2.5d));
        var expr = new CastExpression(Ref(0, DataTypes.DoubleType), DataTypes.IntegerType);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int?[] { 1, -1, 2, -2 }, Ints(result));
    }

    [Fact]
    public void Cast_DecimalToIntegral_TruncatesTowardZero()
    {
        StructType schema = Schema(F("a", Dec102, false));
        ColumnBatch batch = Batch(schema, DecimalCol(Dec102, 250, -250, 99)); // 2.50, -2.50, 0.99
        var expr = new CastExpression(Ref(0, Dec102), DataTypes.IntegerType);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new int?[] { 2, -2, 0 }, Ints(result));
    }

    [Fact]
    public void Cast_BooleanRoundTrip_UsesZeroAndNonZero()
    {
        StructType intSchema = Schema(F("a", DataTypes.IntegerType, false));
        ColumnBatch intBatch = Batch(intSchema, IntCol(0, 1, 2, -1));
        ColumnVector toBool = Eval(new CastExpression(Ref(0, DataTypes.IntegerType), DataTypes.BooleanType), intSchema, intBatch);
        Assert.Equal(new bool?[] { false, true, true, true }, Bools(toBool));

        StructType boolSchema = Schema(F("p", DataTypes.BooleanType, false));
        ColumnBatch boolBatch = Batch(boolSchema, BoolCol(true, false));
        ColumnVector toInt = Eval(new CastExpression(Ref(0, DataTypes.BooleanType), DataTypes.IntegerType), boolSchema, boolBatch);
        Assert.Equal(new int?[] { 1, 0 }, Ints(toInt));
    }

    [Fact]
    public void Cast_PropagatesNull()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, true));
        ColumnBatch batch = Batch(schema, IntCol(1, null, 3));
        var expr = new CastExpression(Ref(0, DataTypes.IntegerType, true), DataTypes.LongType);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(new long?[] { 1, null, 3 }, Longs(result));
    }

    // =====================================================================================
    // AC2 — selection-aware, slice-aware, deterministic output order
    // =====================================================================================

    [Fact]
    public void Evaluate_OverSelection_ProcessesOnlySelectedRows()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch full = Batch(schema, IntCol(1, 2, 3, 4, 5), IntCol(10, 20, 30, 40, 50));
        ColumnBatch selected = full.WithSelection(new SelectionVector([0, 2, 4]));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);

        ColumnVector result = Eval(expr, schema, selected);

        // Only logical rows 0,2,4 (physical 1+10, 3+30, 5+50) are computed, in selection order.
        Assert.Equal(3, result.Length);
        Assert.Equal(new int?[] { 11, 33, 55 }, Ints(result));
    }

    [Fact]
    public void Evaluate_OverUnorderedSelection_PreservesSelectionOrder()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch full = Batch(schema, IntCol(1, 2, 3, 4, 5), IntCol(10, 20, 30, 40, 50));
        ColumnBatch selected = full.WithSelection(new SelectionVector([4, 0, 2]));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);

        ColumnVector result = Eval(expr, schema, selected);

        // Output row order is exactly the selection order (deterministic), not the physical order.
        Assert.Equal(new int?[] { 55, 11, 33 }, Ints(result));
    }

    [Fact]
    public void Evaluate_OverSlice_HonorsOffsetAndLength()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch full = Batch(schema, IntCol(1, 2, 3, 4, 5), IntCol(10, 20, 30, 40, 50));
        ColumnBatch slice = full.Slice(1, 3); // physical rows 1,2,3
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);

        ColumnVector result = Eval(expr, schema, slice);

        Assert.Equal(3, result.Length);
        Assert.Equal(new int?[] { 22, 33, 44 }, Ints(result));
    }

    // =====================================================================================
    // AC3 — ANSI + null contracts (decimal overflow, timestamp casts, NaN, nulls)
    // =====================================================================================

    [Fact]
    public void Arithmetic_IntegerOverflow_Ansi_Throws()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(int.MaxValue), IntCol(1));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Ansi);

        Assert.Throws<ArithmeticOverflowException>(() => Eval(expr, schema, batch));
    }

    [Fact]
    public void Arithmetic_IntegerOverflow_Legacy_Nulls()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(int.MaxValue, 1), IntCol(1, 1));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add, AnsiMode.Legacy);

        ColumnVector result = Eval(expr, schema, batch);

        // Legacy: the overflowing lane becomes SQL NULL; the in-range lane computes normally.
        Assert.Equal(new int?[] { null, 2 }, Ints(result));
    }

    [Theory]
    [InlineData(ArithmeticOperator.Divide)]
    [InlineData(ArithmeticOperator.Remainder)]
    public void Arithmetic_DivideByZero_Ansi_Throws(ArithmeticOperator op)
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(10), IntCol(0));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), op, AnsiMode.Ansi);

        Assert.Throws<DivideByZeroException>(() => Eval(expr, schema, batch));
    }

    [Fact]
    public void Arithmetic_DivideByZero_Legacy_Nulls()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(10, 9), IntCol(0, 3));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Divide, AnsiMode.Legacy);

        ColumnVector result = Eval(expr, schema, batch);

        // Zero divisor yields NULL (not IEEE Infinity) for non-decimal `/` under Legacy.
        Assert.Equal(new double?[] { null, 3.0d }, Doubles(result));
    }

    [Fact]
    public void Arithmetic_DecimalOverflow_Ansi_Throws()
    {
        Int128 tenToNineteen = (Int128)10_000_000_000_000_000_000UL;
        StructType schema = Schema(F("a", Dec380, false), F("b", Dec380, false));
        ColumnBatch batch = Batch(schema, WideDecimalCol(Dec380, tenToNineteen), WideDecimalCol(Dec380, tenToNineteen));
        var expr = new ArithmeticExpression(Ref(0, Dec380), Ref(1, Dec380), ArithmeticOperator.Multiply, AnsiMode.Ansi);

        // 1e19 * 1e19 = 1e38, which overflows decimal(38,0)'s precision.
        Assert.Throws<ArithmeticOverflowException>(() => Eval(expr, schema, batch));
    }

    [Fact]
    public void Arithmetic_DecimalOverflow_Legacy_Nulls()
    {
        Int128 tenToNineteen = (Int128)10_000_000_000_000_000_000UL;
        StructType schema = Schema(F("a", Dec380, false), F("b", Dec380, false));
        ColumnBatch batch = Batch(schema, WideDecimalCol(Dec380, tenToNineteen), WideDecimalCol(Dec380, tenToNineteen));
        var expr = new ArithmeticExpression(Ref(0, Dec380), Ref(1, Dec380), ArithmeticOperator.Multiply, AnsiMode.Legacy);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.True(result.IsNull(0));
    }

    [Fact]
    public void Cast_DecimalOverflow_Ansi_Throws_Legacy_Nulls()
    {
        var wide = new DecimalType(10, 2);
        var narrow = new DecimalType(4, 2);
        StructType schema = Schema(F("a", wide, false));
        ColumnBatch batch = Batch(schema, DecimalCol(wide, 1234567)); // 12345.67 does not fit decimal(4,2)

        Assert.Throws<ArithmeticOverflowException>(() => Eval(new CastExpression(Ref(0, wide), narrow, AnsiMode.Ansi), schema, batch));

        ColumnVector legacy = Eval(new CastExpression(Ref(0, wide), narrow, AnsiMode.Legacy), schema, batch);
        Assert.True(legacy.IsNull(0));
    }

    [Fact]
    public void Cast_DateTimestampRoundTrip_MatchesMicrosPerDay()
    {
        // Spark's date<->timestamp factor is 86_400_000_000 micros/day. The oracle and the input
        // fixtures are derived from this HARD-CODED literal — never TemporalValues.MicrosPerDay, the same
        // constant the cast path reads — so a mutation to that constant moves only the implementation and
        // this test catches the drift (deriving the oracle from MicrosPerDay would move oracle and impl
        // together and pass vacuously).
        const long microsPerDay = 86_400_000_000L;
        const int epochDay = 19_000; // a fixed in-range UTC calendar day (~2022), well inside [Min,Max]EpochDay

        StructType dateSchema = Schema(F("d", DataTypes.DateType, false));
        ColumnBatch dateBatch = Batch(dateSchema, DateCol(epochDay));
        ColumnVector toTimestamp = Eval(new CastExpression(Ref(0, DataTypes.DateType), DataTypes.TimestampType), dateSchema, dateBatch);
        Assert.Equal(new long?[] { epochDay * microsPerDay }, Longs(toTimestamp));

        StructType tsSchema = Schema(F("t", DataTypes.TimestampType, false));
        ColumnBatch tsBatch = Batch(tsSchema, TimestampCol((epochDay * microsPerDay) + 500));
        ColumnVector toDate = Eval(new CastExpression(Ref(0, DataTypes.TimestampType), DataTypes.DateType), tsSchema, tsBatch);
        Assert.Equal(new int?[] { epochDay }, Ints(toDate)); // floor to whole days
    }

    [Fact]
    public void Cast_DateToTimestampNtz_IsMidnightWallClock()
    {
        // date -> timestamp_ntz is the midnight WALL-CLOCK instant: identical epoch math to date->timestamp
        // (micros/day), but a timezone-LESS value that is never shifted by a session/host zone (#558).
        const long microsPerDay = 86_400_000_000L;
        const int epochDay = 19_000; // ~2022, well inside [Min,Max]EpochDay

        StructType schema = Schema(F("d", DataTypes.DateType, false));
        ColumnBatch batch = Batch(schema, DateCol(epochDay));
        ColumnVector toNtz = Eval(new CastExpression(Ref(0, DataTypes.DateType), DataTypes.TimestampNtzType), schema, batch);

        Assert.Equal(DataTypes.TimestampNtzType, toNtz.Type);
        Assert.Equal(new long?[] { epochDay * microsPerDay }, Longs(toNtz));
    }

    [Fact]
    public void Cast_TimestampNtzToDate_FloorsToWholeDays()
    {
        const long microsPerDay = 86_400_000_000L;
        const int epochDay = 19_000;

        StructType schema = Schema(F("n", DataTypes.TimestampNtzType, false));
        ColumnBatch batch = Batch(schema, TimestampNtzCol((epochDay * microsPerDay) + 500));
        ColumnVector toDate = Eval(new CastExpression(Ref(0, DataTypes.TimestampNtzType), DataTypes.DateType), schema, batch);

        Assert.Equal(DataTypes.DateType, toDate.Type);
        Assert.Equal(new int?[] { epochDay }, Ints(toDate)); // floor to whole days
    }

    [Fact]
    public void Cast_TimestampTimestampNtz_IsIdentityOnTheLong_AndRoundTrips()
    {
        // timestamp <-> timestamp_ntz reinterprets the SAME epoch-microsecond lane with NO session-zone
        // shift (DeltaSharp has no session zone), so the stored long is preserved bit-for-bit in both
        // directions and a ts -> ntz -> ts round-trip is the identity (#558). Endpoints include the epoch,
        // a modern instant, and the [Min,Max] supported bounds plus a null.
        var samples = new long?[] { 0L, 1_700_000_000_000_000L, -62_135_596_800_000_000L, 253_402_300_799_999_999L, null };

        StructType tsSchema = Schema(F("t", DataTypes.TimestampType, true));
        ColumnBatch tsBatch = Batch(tsSchema, TimestampCol(samples));
        ColumnVector toNtz = Eval(new CastExpression(Ref(0, DataTypes.TimestampType, true), DataTypes.TimestampNtzType), tsSchema, tsBatch);
        Assert.Equal(DataTypes.TimestampNtzType, toNtz.Type);
        Assert.Equal(samples, Longs(toNtz));

        StructType ntzSchema = Schema(F("n", DataTypes.TimestampNtzType, true));
        ColumnBatch ntzBatch = Batch(ntzSchema, TimestampNtzCol(samples));
        ColumnVector backToTs = Eval(new CastExpression(Ref(0, DataTypes.TimestampNtzType, true), DataTypes.TimestampType), ntzSchema, ntzBatch);
        Assert.Equal(DataTypes.TimestampType, backToTs.Type);
        Assert.Equal(samples, Longs(backToTs));

        // The chained round-trip cast(cast(t AS timestamp_ntz) AS timestamp) reproduces the input exactly.
        var roundTrip = new CastExpression(
            new CastExpression(Ref(0, DataTypes.TimestampType, true), DataTypes.TimestampNtzType), DataTypes.TimestampType);
        Assert.Equal(samples, Longs(Eval(roundTrip, tsSchema, tsBatch)));
    }

    [Fact]
    public void Literal_TimestampNtz_BroadcastsWallClockMicros()
    {
        // #558: Literal.OfTimestampNtz broadcasts a timezone-less wall-clock micros constant as a
        // timestamp_ntz column (mirrors Literal.OfTimestamp, distinct logical type).
        StructType schema = Schema(F("a", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(10, 20, 30));

        ColumnVector broadcast = Eval(Literal.OfTimestampNtz(1_700_000_000_000_000L), schema, batch);

        Assert.Equal(DataTypes.TimestampNtzType, broadcast.Type);
        Assert.Equal(new long?[] { 1_700_000_000_000_000L, 1_700_000_000_000_000L, 1_700_000_000_000_000L }, Longs(broadcast));
    }

    [Fact]
    public void Cast_DoubleToLong_RejectsTwoToThe63_Ansi_Throws_Legacy_Nulls()
    {
        // 2^63 is one past the largest bigint (long.MaxValue == 2^63 - 1). As a double it is the exact
        // boundary the (long) conversion silently saturates to long.MaxValue, because long.MaxValue is not
        // representable as a double and an inclusive `> long.MaxValue` guard promotes it to (double)2^63 and
        // lets 2^63 through. The guard must reject against the double-exclusive bound 2^63, so 2^63 overflows
        // (throws under ANSI, NULLs under Legacy) rather than clamping.
        const double twoToThe63 = 9223372036854775808.0; // 2^63 — the first double strictly above long.MaxValue
        StructType schema = Schema(F("a", DataTypes.DoubleType, false));
        ColumnBatch batch = Batch(schema, DoubleCol(twoToThe63));

        Assert.Throws<ArithmeticOverflowException>(
            () => Eval(new CastExpression(Ref(0, DataTypes.DoubleType), DataTypes.LongType, AnsiMode.Ansi), schema, batch));

        ColumnVector legacy = Eval(new CastExpression(Ref(0, DataTypes.DoubleType), DataTypes.LongType, AnsiMode.Legacy), schema, batch);
        Assert.True(legacy.IsNull(0));
    }

    [Fact]
    public void Cast_DoubleToLong_AcceptsLargestInRangeDouble()
    {
        // The largest double strictly below 2^63 is 2^63 - 1024 = 9223372036854774784 (doubles step by
        // 1024 in [2^62, 2^63)). It is a valid bigint and must still cast — the fix rejects 2^63 and above,
        // not the in-range neighbour just below it.
        const double largestInRange = 9223372036854774784.0; // 2^63 - 1024
        StructType schema = Schema(F("a", DataTypes.DoubleType, false));
        ColumnBatch batch = Batch(schema, DoubleCol(largestInRange));

        ColumnVector result = Eval(new CastExpression(Ref(0, DataTypes.DoubleType), DataTypes.LongType, AnsiMode.Ansi), schema, batch);

        Assert.Equal(new long?[] { 9223372036854774784L }, Longs(result));
    }

    [Fact]
    public void Comparison_Double_FollowsSparkNaNOrdering()
    {
        StructType schema = Schema(F("a", DataTypes.DoubleType, false), F("b", DataTypes.DoubleType, false));
        ColumnBatch batch = Batch(
            schema,
            DoubleCol(double.NaN, double.NaN, 1.0d, -0.0d),
            DoubleCol(double.NaN, 1.0d, double.NaN, 0.0d));

        ColumnVector equal = Eval(new ComparisonExpression(Ref(0, DataTypes.DoubleType), Ref(1, DataTypes.DoubleType), ComparisonOperator.Equal), schema, batch);
        ColumnVector greater = Eval(new ComparisonExpression(Ref(0, DataTypes.DoubleType), Ref(1, DataTypes.DoubleType), ComparisonOperator.GreaterThan), schema, batch);
        ColumnVector less = Eval(new ComparisonExpression(Ref(0, DataTypes.DoubleType), Ref(1, DataTypes.DoubleType), ComparisonOperator.LessThan), schema, batch);

        // NaN == NaN (true), NaN is greatest, and -0.0 == +0.0.
        Assert.Equal(new bool?[] { true, false, false, true }, Bools(equal));
        Assert.Equal(new bool?[] { false, true, false, false }, Bools(greater));
        Assert.Equal(new bool?[] { false, false, true, false }, Bools(less));
    }

    [Fact]
    public void Comparison_SignedTinyint_ReinterpretsByteAsSigned()
    {
        StructType schema = Schema(F("t", DataTypes.ByteType, false));
        ColumnBatch batch = Batch(schema, ByteCol(-1, 5)); // stored bytes 255, 5
        var expr = new ComparisonExpression(Ref(0, DataTypes.ByteType), Literal.OfByte(0), ComparisonOperator.LessThan);

        ColumnVector result = Eval(expr, schema, batch);

        // -1 < 0 is true; 5 < 0 is false. (Unsigned 255 < 0 would be false — proves signedness.)
        Assert.Equal(new bool?[] { true, false }, Bools(result));
    }

    [Fact]
    public void Arithmetic_SignedTinyint_StoresSignedResult()
    {
        StructType schema = Schema(F("t", DataTypes.ByteType, false));
        ColumnBatch batch = Batch(schema, ByteCol(100));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.ByteType), Literal.OfByte(-1), ArithmeticOperator.Add);

        ColumnVector result = Eval(expr, schema, batch);

        Assert.Equal(DataTypes.ByteType, result.Type);
        Assert.Equal(new int?[] { 99 }, SignedBytes(result));
    }

    // =====================================================================================
    // Filter / Project integration (general predicate + computed projection)
    // =====================================================================================

    private static readonly StructType TwoCol = Schema(F("id", DataTypes.IntegerType, false), F("v", DataTypes.IntegerType, false));

    private static ExecutionContext Ctx(IExecutionMemory? memory = null)
        => new(memory ?? BoundedExecutionMemory.Unbounded, CancellationToken.None);

    private static InMemoryScanOperator ScanTwoCol(params ColumnBatch[] batches) => new(TwoCol, batches);

    private static List<ColumnBatch> Drain(IBatchStream stream)
    {
        var batches = new List<ColumnBatch>();
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static List<int> ColumnInts(ColumnBatch batch, int ordinal)
    {
        ColumnVector column = batch.SelectedColumn(ordinal);
        var values = new List<int>(column.Length);
        for (int i = 0; i < column.Length; i++)
        {
            values.Add(column.GetValue<int>(i));
        }

        return values;
    }

    [Fact]
    public void Filter_ComparisonPredicate_SelectsPassingRows()
    {
        ColumnBatch batch = Batch(TwoCol, IntCol(1, 2, 3, 4), IntCol(10, 5, 30, 5));
        var predicate = new ComparisonExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(6), ComparisonOperator.GreaterThan);
        var filter = new FilterOperator(ScanTwoCol(batch), predicate);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(filter, Ctx());
        List<ColumnBatch> output = Drain(stream);

        // v > 6 passes rows with v in {10, 30} -> ids {1, 3}.
        Assert.Single(output);
        Assert.Equal([1, 3], ColumnInts(output[0], 0));
    }

    [Fact]
    public void Filter_BooleanAndPredicate_AppliesKleeneAndSelection()
    {
        ColumnBatch batch = Batch(TwoCol, IntCol(1, 2, 3, 4), IntCol(10, 5, 30, 5));
        var left = new ComparisonExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(6), ComparisonOperator.GreaterThan);
        var right = new ComparisonExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(3), ComparisonOperator.LessThan);
        var predicate = new LogicalExpression(left, right, LogicalOperator.And);
        var filter = new FilterOperator(ScanTwoCol(batch), predicate);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(filter, Ctx());
        List<ColumnBatch> output = Drain(stream);

        // (v > 6) AND (id < 3): only id 1 (v=10).
        Assert.Single(output);
        Assert.Equal([1], ColumnInts(output[0], 0));
    }

    [Fact]
    public void Project_ComputedColumn_MaterializesValues()
    {
        ColumnBatch batch = Batch(TwoCol, IntCol(1, 2, 3), IntCol(10, 20, 30));
        StructType outSchema = Schema(F("id", DataTypes.IntegerType, false), F("doubled", DataTypes.IntegerType, false));
        var doubled = new ArithmeticExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(2), ArithmeticOperator.Multiply);
        var project = new ProjectOperator(ScanTwoCol(batch), outSchema, [Ref(0, DataTypes.IntegerType), doubled]);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(project, Ctx());
        List<ColumnBatch> output = Drain(stream);

        Assert.Single(output);
        Assert.Equal([1, 2, 3], ColumnInts(output[0], 0));
        Assert.Equal([20, 40, 60], ColumnInts(output[0], 1));
    }

    [Fact]
    public void FilterThenProject_ComputesOverSelection()
    {
        ColumnBatch batch = Batch(TwoCol, IntCol(1, 2, 3, 4), IntCol(10, 5, 30, 5));
        var predicate = new ComparisonExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(6), ComparisonOperator.GreaterThan);
        var filter = new FilterOperator(ScanTwoCol(batch), predicate);

        StructType outSchema = Schema(F("id", DataTypes.IntegerType, false), F("vplus", DataTypes.IntegerType, false));
        var vPlus = new ArithmeticExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(1), ArithmeticOperator.Add);
        var project = new ProjectOperator(filter, outSchema, [Ref(0, DataTypes.IntegerType), vPlus]);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(project, Ctx());
        List<ColumnBatch> output = Drain(stream);

        // Filter keeps ids {1,3} (v in {10,30}); project emits a selection-free batch with v+1.
        Assert.Single(output);
        Assert.Null(output[0].Selection);
        Assert.Equal([1, 3], ColumnInts(output[0], 0));
        Assert.Equal([11, 31], ColumnInts(output[0], 1));
    }

    [Fact]
    public void Project_ComputedColumn_Ansi_PropagatesOverflowException()
    {
        ColumnBatch batch = Batch(TwoCol, IntCol(1), IntCol(int.MaxValue));
        StructType outSchema = Schema(F("overflow", DataTypes.IntegerType, false));
        var expr = new ArithmeticExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(1), ArithmeticOperator.Add, AnsiMode.Ansi);
        var project = new ProjectOperator(ScanTwoCol(batch), outSchema, [expr]);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(project, Ctx());

        Assert.Throws<ArithmeticOverflowException>(() => Drain(stream));
    }

    [Fact]
    public void Project_ComputedColumn_ReservesBoundedMemory()
    {
        ColumnBatch batch = Batch(TwoCol, IntCol(1, 2, 3), IntCol(10, 20, 30));
        StructType outSchema = Schema(F("doubled", DataTypes.IntegerType, false));
        var doubled = new ArithmeticExpression(Ref(1, DataTypes.IntegerType), Literal.OfInt(2), ArithmeticOperator.Multiply);
        var project = new ProjectOperator(ScanTwoCol(batch), outSchema, [doubled]);

        // A 1-byte budget cannot satisfy the computed vector reservation.
        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(project, Ctx(new BoundedExecutionMemory(1)));

        Assert.Throws<ExecutionMemoryException>(() => Drain(stream));
    }

    [Fact]
    public void Project_StringColumnReference_OverSelection_ReservesGatheredValueBytes()
    {
        // A string column-reference projected UNDER A SELECTION takes the project's gather branch, which
        // must reserve the gathered VALUE bytes — not merely the validity footprint EstimateFixedWidthBytes
        // counts for a var-width type — or a wide string column drains shared executor memory unmetered.
        // Two selected rows of 50 KB each (100 KB of value bytes) must NOT fit a 64 KB budget.
        StructType schema = Schema(F("id", DataTypes.IntegerType, false), F("name", DataTypes.StringType, false));
        string big = new('x', 50_000);
        ColumnBatch batch = Batch(schema, IntCol(1, 2, 3, 4), StrCol(big, big, big, big))
            .WithSelection(new SelectionVector([0, 2])); // 2 logical rows -> 100 KB of selected value bytes

        var scan = new InMemoryScanOperator(schema, [batch]);
        StructType outSchema = Schema(F("name", DataTypes.StringType, false), F("idplus", DataTypes.IntegerType, false));

        // Mix the string column-reference (gathered under the selection) with a computed column so the
        // project takes the materializing path; an all-column-reference project stays a zero-copy reorder.
        var idPlus = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(1), ArithmeticOperator.Add);
        var project = new ProjectOperator(scan, outSchema, [Ref(1, DataTypes.StringType), idPlus]);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(project, Ctx(new BoundedExecutionMemory(64 * 1024)));

        Assert.Throws<ExecutionMemoryException>(() => Drain(stream));
    }

    [Fact]
    public void Project_FixedWidthColumnReference_OverSelection_StaysWithinBudget()
    {
        // The control: a FIXED-WIDTH column-reference gathered under a selection is already fully accounted
        // by ReserveVector (value buffer + validity), so the same 64 KB budget the string case overruns
        // comfortably holds two gathered int rows — proving the gather path itself is sound and the fix
        // meters only the previously-unmetered var-width value bytes.
        StructType schema = Schema(F("id", DataTypes.IntegerType, false), F("v", DataTypes.IntegerType, false));
        ColumnBatch batch = Batch(schema, IntCol(1, 2, 3, 4), IntCol(10, 20, 30, 40))
            .WithSelection(new SelectionVector([0, 2]));

        var scan = new InMemoryScanOperator(schema, [batch]);
        StructType outSchema = Schema(F("v", DataTypes.IntegerType, false), F("idplus", DataTypes.IntegerType, false));
        var idPlus = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(1), ArithmeticOperator.Add);
        var project = new ProjectOperator(scan, outSchema, [Ref(1, DataTypes.IntegerType), idPlus]);

        using IBatchStream stream = InterpretedVectorizedBackend.Instance.Open(project, Ctx(new BoundedExecutionMemory(64 * 1024)));
        List<ColumnBatch> output = Drain(stream);

        // The gathered, selection-free batch carries the selected rows: v in {10, 30}, id+1 in {2, 4}.
        Assert.Single(output);
        Assert.Null(output[0].Selection);
        Assert.Equal([10, 30], ColumnInts(output[0], 0));
        Assert.Equal([2, 4], ColumnInts(output[0], 1));
    }

    // =====================================================================================
    // AC4 — interpreted tier carries no runtime code generation / reflection-emit
    // =====================================================================================

    [Fact]
    public void InterpretedTier_DeclaresNoDynamicCodeRequirement()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Assembly engine = typeof(ExpressionEvaluator).Assembly;
        var offenders = new List<string>();

        foreach (Type type in engine.GetTypes())
        {
            string? ns = type.Namespace;
            bool isEvaluatorTier = ns == "DeltaSharp.Engine.Execution.Expressions";
            bool isExpressionNode = ns == "DeltaSharp.Engine.Execution" && typeof(PhysicalExpression).IsAssignableFrom(type);
            if (!isEvaluatorTier && !isExpressionNode)
            {
                continue;
            }

            // The optional compiled-fusion tier (STORY-03.4.2) is the codegen path: its types are
            // explicitly annotated [RequiresDynamicCode] at the type level and are reachable only
            // behind the IsCompiledBackendAvailable feature guard, so NativeAOT elides them. They are
            // *expected* to require dynamic code — skip them. Every interpreted-baseline type (and any
            // member of an unannotated type) must still declare no dynamic-code requirement.
            if (type.GetCustomAttribute<RequiresDynamicCodeAttribute>() is not null)
            {
                continue;
            }

            foreach (MethodBase member in type.GetMembers(All).OfType<MethodBase>())
            {
                if (member.GetCustomAttribute<RequiresDynamicCodeAttribute>() is not null)
                {
                    offenders.Add($"{type.Name}.{member.Name}");
                }
            }
        }

        // The interpreted evaluator is the AOT-clean baseline: no node or kernel may require dynamic
        // code. Expression.Compile fusion (STORY-03.4.2) lives in sibling types that opt in with a
        // type-level [RequiresDynamicCode] (skipped above); an unannotated type must never leak one.
        Assert.Empty(offenders);
    }
}
