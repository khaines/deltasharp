using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Differential parity suite for the optional compiled expression-fusion tier (STORY-03.4.2, #152).
/// The central acceptance criterion — and the ADR-0001 invariant — is that the compiled fast path is
/// <em>byte-identical</em> to the interpreted reference (STORY-03.4.1) for both values and validity. Every
/// fusable case is evaluated twice (interpreted vs. compiled over the same batch) and the result vectors
/// are compared lane-by-lane, including raw IEEE bits for float/double so <c>-0.0</c> and <c>NaN</c>
/// payloads are held to bit equality.
/// <para>
/// The suite is <em>non-vacuous</em>: every parity assertion first proves the compiled evaluator is a
/// <see cref="CompiledExpressionEvaluator"/> (a real fused delegate, not a silent fallback), so mutating a
/// lowering either changes a produced value — failing the lane comparison — or disables fusion — failing
/// the type assertion. Compiled-tier assertions run only where <see cref="RuntimeFeature.IsDynamicCodeSupported"/>
/// is true; on a NativeAOT host the tier is elided and these tests early-return (there is nothing to compare).
/// </para>
/// </summary>
public sealed class CompiledExpressionFusionTests
{
    private const string InterpretedBackend = "interpreted-vectorized";

    private static readonly DecimalType Dec102 = new(10, 2);
    private static readonly DecimalType Dec92 = new(9, 2);
    private static readonly DecimalType Dec3810 = new(38, 10);

    // =====================================================================================
    // Differential harness: interpreted vs. compiled, byte-identical values + validity
    // =====================================================================================

    private static ExpressionEvaluator Interpreted(PhysicalExpression expr, StructType schema)
        => ExpressionEvaluators.Build(expr, schema, InterpretedBackend, OperatorKind.Project);

    private static ExpressionEvaluator Compiled(PhysicalExpression expr, StructType schema)
        => new CompiledBackend().BuildExpressionEvaluator(expr, schema, OperatorKind.Project);

    private static ColumnVector Run(ExpressionEvaluator evaluator, ColumnBatch batch)
    {
        var ledger = new BatchEvaluationMemory(BoundedExecutionMemory.Unbounded);
        try
        {
            return evaluator.Evaluate(batch, ledger, CancellationToken.None);
        }
        finally
        {
            ledger.Release();
        }
    }

    /// <summary>
    /// Asserts the compiled fast path is byte-identical to the interpreted reference over <paramref name="batch"/>.
    /// When <paramref name="fusable"/> is true (the default) it also proves a real fused delegate was produced;
    /// when false it proves the build transparently fell back to the interpreter for the same node.
    /// </summary>
    private static void AssertParity(PhysicalExpression expr, StructType schema, ColumnBatch batch, bool fusable = true)
    {
        ExpressionEvaluator interpreted = Interpreted(expr, schema);
        ExpressionEvaluator compiled = Compiled(expr, schema);

        if (fusable)
        {
            // Non-vacuity: a fusable tree MUST be served by the compiled lowering, so any lane mismatch
            // below is a real codegen defect rather than a quietly-skipped path.
            Assert.IsType<CompiledExpressionEvaluator>(compiled);
        }
        else
        {
            Assert.IsNotType<CompiledExpressionEvaluator>(compiled);
        }

        AssertVectorsIdentical(Run(interpreted, batch), Run(compiled, batch));
    }

    /// <summary>Lane-by-lane equality of validity and the raw stored carrier (bit-exact for float/double).</summary>
    private static void AssertVectorsIdentical(ColumnVector expected, ColumnVector actual)
    {
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected.IsNull(i), actual.IsNull(i));
            if (expected.IsNull(i))
            {
                continue;
            }

            switch (expected.Type)
            {
                case BooleanType:
                    Assert.Equal(expected.GetValue<bool>(i), actual.GetValue<bool>(i));
                    break;
                case ByteType:
                    Assert.Equal(expected.GetValue<byte>(i), actual.GetValue<byte>(i));
                    break;
                case ShortType:
                    Assert.Equal(expected.GetValue<short>(i), actual.GetValue<short>(i));
                    break;
                case IntegerType or DateType:
                    Assert.Equal(expected.GetValue<int>(i), actual.GetValue<int>(i));
                    break;
                case LongType or TimestampType or TimestampNtzType:
                    Assert.Equal(expected.GetValue<long>(i), actual.GetValue<long>(i));
                    break;
                case FloatType:
                    // Bit equality so -0.0 and NaN payloads are held identical, not merely numerically equal.
                    Assert.Equal(
                        BitConverter.SingleToInt32Bits(expected.GetValue<float>(i)),
                        BitConverter.SingleToInt32Bits(actual.GetValue<float>(i)));
                    break;
                case DoubleType:
                    Assert.Equal(
                        BitConverter.DoubleToInt64Bits(expected.GetValue<double>(i)),
                        BitConverter.DoubleToInt64Bits(actual.GetValue<double>(i)));
                    break;
                case DecimalType decimalType:
                    if (decimalType.IsCompact)
                    {
                        Assert.Equal(expected.GetValue<long>(i), actual.GetValue<long>(i));
                    }
                    else
                    {
                        Assert.Equal(expected.GetValue<Int128>(i), actual.GetValue<Int128>(i));
                    }

                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"unhandled carrier type '{expected.Type.SimpleString}'");
            }
        }
    }

    // =====================================================================================
    // AC: parity oracle across arithmetic / comparison / boolean / cast / null (theory-driven)
    // =====================================================================================

    public static TheoryData<string> ParityCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (Case sample in Cases)
        {
            data.Add(sample.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ParityCaseNames))]
    public void Parity_CompiledEqualsInterpreted(string caseName)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return; // compiled tier elided on this host: nothing to compare against
        }

        Case sample = Cases.Single(c => c.Name == caseName);
        AssertParity(sample.Expr, sample.Schema, sample.Batch, sample.Fusable);
    }

    // =====================================================================================
    // AC: ANSI overflow / divide-by-zero (single-error batches -> exact message parity)
    // =====================================================================================

    [Fact]
    public void Parity_IntegerOverflow_Ansi_BothThrowIdentical()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);
        ColumnBatch batch = Batch(schema, IntCol(int.MaxValue), IntCol(1)); // single row, single error

        ArithmeticOverflowException fromInterpreted =
            Assert.Throws<ArithmeticOverflowException>(() => Run(Interpreted(expr, schema), batch));
        ArithmeticOverflowException fromCompiled =
            Assert.Throws<ArithmeticOverflowException>(() => Run(Compiled(expr, schema), batch));
        Assert.Equal(fromInterpreted.Message, fromCompiled.Message);
    }

    [Fact]
    public void Parity_CastOverflow_Ansi_BothThrowIdentical()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        // 2^63 is exactly the long upper-exclusive boundary: ANSI rejects it on both tiers.
        StructType schema = Schema(F("a", DataTypes.DoubleType, false));
        var expr = new CastExpression(Ref(0, DataTypes.DoubleType), DataTypes.LongType);
        ColumnBatch batch = Batch(schema, DoubleCol(9223372036854775808.0));

        ArithmeticOverflowException fromInterpreted =
            Assert.Throws<ArithmeticOverflowException>(() => Run(Interpreted(expr, schema), batch));
        ArithmeticOverflowException fromCompiled =
            Assert.Throws<ArithmeticOverflowException>(() => Run(Compiled(expr, schema), batch));
        Assert.Equal(fromInterpreted.Message, fromCompiled.Message);
    }

    [Fact]
    public void Parity_DoubleDivideByZero_Ansi_BothThrowIdentical()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.DoubleType, false), F("b", DataTypes.DoubleType, false));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.DoubleType), Ref(1, DataTypes.DoubleType), ArithmeticOperator.Divide);
        ColumnBatch batch = Batch(schema, DoubleCol(1.0), DoubleCol(0.0));

        DivideByZeroException fromInterpreted =
            Assert.Throws<DivideByZeroException>(() => Run(Interpreted(expr, schema), batch));
        DivideByZeroException fromCompiled =
            Assert.Throws<DivideByZeroException>(() => Run(Compiled(expr, schema), batch));
        Assert.Equal(fromInterpreted.Message, fromCompiled.Message);
    }

    [Fact]
    public void Parity_IntegerRemainderByZero_Ansi_BothThrowIdentical()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.LongType, false), F("b", DataTypes.LongType, false));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.LongType), Ref(1, DataTypes.LongType), ArithmeticOperator.Remainder);
        ColumnBatch batch = Batch(schema, LongCol(7), LongCol(0));

        DivideByZeroException fromInterpreted =
            Assert.Throws<DivideByZeroException>(() => Run(Interpreted(expr, schema), batch));
        DivideByZeroException fromCompiled =
            Assert.Throws<DivideByZeroException>(() => Run(Compiled(expr, schema), batch));
        Assert.Equal(fromInterpreted.Message, fromCompiled.Message);
    }

    // Multi-error-kind batch: the interpreter is subtree/child-major (it evaluates the WHOLE left
    // subtree across all rows, then the right) while the compiled kernel is row-major (per row: left,
    // then right). When two subtrees fault under ANSI with DIFFERENT error kinds on DIFFERENT rows, the
    // eval order decides which fault is reached first, so the throwing row, exception type, and message
    // legitimately differ between tiers. The honest, test-backed guarantee is only that BOTH tiers abort
    // with SOME ANSI error (no wrong/at-rest value). We therefore assert each throws an ANSI arithmetic
    // error and deliberately do NOT assert type-equality. See the multi-error caveat in
    // docs/engineering/design/compiled-expression-fusion.md.
    [Fact]
    public void Parity_MultiErrorKindBatch_Ansi_BothTiersThrowAnsiError()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        // (a + b) + (c % d) under ANSI. Row 0: a+b is fine but c%d divides by zero (right subtree).
        // Row 1: a+b overflows int (left subtree) but c%d is fine. So the interpreter — evaluating the
        // whole left subtree first — raises ArithmeticOverflowException at row 1, while the compiled
        // kernel — row-major — hits the divide-by-zero at row 0 and raises DivideByZeroException.
        StructType schema = Schema(
            F("a", DataTypes.IntegerType, false),
            F("b", DataTypes.IntegerType, false),
            F("c", DataTypes.IntegerType, false),
            F("d", DataTypes.IntegerType, false));
        var left = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);
        var right = new ArithmeticExpression(Ref(2, DataTypes.IntegerType), Ref(3, DataTypes.IntegerType), ArithmeticOperator.Remainder);
        var expr = new ArithmeticExpression(left, right, ArithmeticOperator.Add);
        ColumnBatch batch = Batch(
            schema,
            IntCol(0, int.MaxValue), // a
            IntCol(0, 1),            // b: row 1 -> a+b overflows
            IntCol(7, 5),            // c
            IntCol(0, 1));           // d: row 0 -> c%d divides by zero

        // Non-vacuity: this whole-integral tree fuses, so both faults are produced by the real tiers.
        Assert.IsType<CompiledExpressionEvaluator>(Compiled(expr, schema));

        Exception fromInterpreted = Assert.ThrowsAny<Exception>(() => Run(Interpreted(expr, schema), batch));
        Exception fromCompiled = Assert.ThrowsAny<Exception>(() => Run(Compiled(expr, schema), batch));

        // Both tiers must abort with an ANSI arithmetic error (overflow OR divide-by-zero). Type-equality
        // is intentionally not asserted — the tiers may reach different faults first (see comment above).
        Assert.True(
            IsAnsiArithmeticError(fromInterpreted),
            $"interpreted tier threw a non-ANSI exception: {fromInterpreted.GetType()}");
        Assert.True(
            IsAnsiArithmeticError(fromCompiled),
            $"compiled tier threw a non-ANSI exception: {fromCompiled.GetType()}");
    }

    private static bool IsAnsiArithmeticError(Exception e) =>
        e is ArithmeticOverflowException or DivideByZeroException;

    // Headline fusion win: the fused per-row kernel eliminates intermediate vectors AND avoids per-row
    // boxing. Integral arithmetic (a + b) lowers through CompiledScalarOps.TryIntegral, whose long?
    // result flows the FallibleAssign nullable-struct path (HasValue/Unwrap take long? BY VALUE — no
    // box). We drive the raw FusedRowKernel over many rows into a pre-reserved output and assert the
    // STEADY-STATE per-row heap allocation is exactly zero via GC.GetAllocatedBytesForCurrentThread().
    //
    // Why poll for steady state: Expression.Compile delegates participate in tiered compilation. The
    // first invocations may run in tier-0 (minopts), where the JIT can box the nullable temp; tier-1
    // (the optimized steady state that a hot loop actually runs in) eliminates it. Tier-1 promotion is
    // asynchronous on a background thread, so we loop full passes — giving the background JIT time —
    // until a pass allocates nothing, then assert that allocation-free steady state was reached. A real
    // regression (a per-row box that tier-1 cannot remove, e.g. ~24 bytes/row for a boxed Nullable<long>)
    // never reaches zero and fails the assertion; the deliberate-box non-vacuity probe confirms this.
    [Fact]
    public void FusedKernel_PerRow_IsAllocationFree()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);

        const int rows = 8192;
        var aValues = new int?[rows];
        var bValues = new int?[rows];
        for (int i = 0; i < rows; i++)
        {
            aValues[i] = i;
            bValues[i] = rows - i;
        }

        ColumnBatch batch = Batch(schema, IntCol(aValues), IntCol(bValues));
        CompiledFusion fusion = CompiledExpressionLowering.Lower(expr);
        var inputs = new ColumnVector[fusion.SlotOrdinals.Length];
        for (int slot = 0; slot < inputs.Length; slot++)
        {
            inputs[slot] = batch.SelectedColumn(fusion.SlotOrdinals[slot]);
        }

        // Each pass writes into a fresh output sized to `rows` (its backing array is the single permitted
        // setup allocation and is created BEFORE the measurement window, so an append never reallocates
        // inside the measured loop). Poll until a full pass is allocation-free (tier-1 reached).
        const int maxAttempts = 200;
        long perRowBytes = long.MaxValue;
        for (int attempt = 0; attempt < maxAttempts && perRowBytes != 0; attempt++)
        {
            MutableColumnVector output = ColumnVectors.Create(expr.Type, rows);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int row = 0; row < rows; row++)
            {
                fusion.Kernel(inputs, row, output);
            }

            perRowBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(rows, output.Length); // sanity: every row produced exactly one lane
            if (perRowBytes != 0)
            {
                Thread.Sleep(5); // let the background JIT promote the kernel to tier-1, then retry
            }
        }

        Assert.True(
            perRowBytes == 0,
            $"fused kernel still allocated {perRowBytes} bytes over {rows} rows after {maxAttempts} passes; " +
            "the steady-state per-row path is not allocation-free.");
    }

    // =====================================================================================
    // AC: selection- and slice-aware evaluation (deterministic output order)
    // =====================================================================================

    [Fact]
    public void Parity_OverContiguousSelection()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        (StructType schema, ColumnBatch full, PhysicalExpression expr) = SelectionFixture();
        AssertParity(expr, schema, full.WithSelection(new SelectionVector([0, 2, 4])));
    }

    [Fact]
    public void Parity_OverUnorderedSelection()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        (StructType schema, ColumnBatch full, PhysicalExpression expr) = SelectionFixture();
        AssertParity(expr, schema, full.WithSelection(new SelectionVector([4, 0, 2])));
    }

    [Fact]
    public void Parity_OverSlice()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        (StructType schema, ColumnBatch full, PhysicalExpression expr) = SelectionFixture();
        AssertParity(expr, schema, full.Slice(1, 3));
    }

    private static (StructType Schema, ColumnBatch Full, PhysicalExpression Expr) SelectionFixture()
    {
        StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true));
        ColumnBatch full = Batch(schema, IntCol(10, 20, null, 40, 50), IntCol(1, null, 3, 4, 5));
        var expr = new ComparisonExpression(
            new ArithmeticExpression(Ref(0, DataTypes.IntegerType, true), Ref(1, DataTypes.IntegerType, true), ArithmeticOperator.Add),
            Literal.OfInt(25),
            ComparisonOperator.GreaterThan);
        return (schema, full, expr);
    }

    // =====================================================================================
    // AC: wide randomized differential (strong non-vacuity over many rows + scattered nulls)
    // =====================================================================================

    [Fact]
    public void Parity_WideRandomizedIntegralPredicate()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var rng = new Random(0xC0FFEE);
        const int rows = 257; // prime, larger than any internal block boundary
        int?[] a = new int?[rows];
        int?[] b = new int?[rows];
        int?[] c = new int?[rows];
        for (int i = 0; i < rows; i++)
        {
            a[i] = rng.Next(8) == 0 ? null : rng.Next(-1000, 1000);
            b[i] = rng.Next(8) == 0 ? null : rng.Next(-1000, 1000);
            c[i] = rng.Next(8) == 0 ? null : rng.Next(-1000, 1000);
        }

        StructType schema = Schema(
            F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true), F("c", DataTypes.IntegerType, true));
        ColumnBatch batch = Batch(schema, IntCol(a), IntCol(b), IntCol(c));

        // ((a + b) * c) > 0 — magnitudes are bounded so the int result never overflows in ANSI mode.
        var expr = new ComparisonExpression(
            new ArithmeticExpression(
                new ArithmeticExpression(Ref(0, DataTypes.IntegerType, true), Ref(1, DataTypes.IntegerType, true), ArithmeticOperator.Add),
                Ref(2, DataTypes.IntegerType, true),
                ArithmeticOperator.Multiply),
            Literal.OfInt(0),
            ComparisonOperator.GreaterThan);

        AssertParity(expr, schema, batch);
    }

    [Fact]
    public void Parity_WideRandomizedDoubleExpression()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var rng = new Random(0xBEEF);
        const int rows = 211;
        double?[] a = new double?[rows];
        double?[] b = new double?[rows];
        for (int i = 0; i < rows; i++)
        {
            a[i] = rng.Next(8) == 0 ? null : (rng.NextDouble() - 0.5) * 2000.0;
            b[i] = rng.Next(8) == 0 ? null : (rng.NextDouble() - 0.5) * 2000.0;
        }

        StructType schema = Schema(F("a", DataTypes.DoubleType, true), F("b", DataTypes.DoubleType, true));
        ColumnBatch batch = Batch(schema, DoubleCol(a), DoubleCol(b));

        // (a * b) - a, then a comparison: exercises primitive double *, - and CompareDouble together.
        var expr = new ComparisonExpression(
            new ArithmeticExpression(
                new ArithmeticExpression(Ref(0, DataTypes.DoubleType, true), Ref(1, DataTypes.DoubleType, true), ArithmeticOperator.Multiply),
                Ref(0, DataTypes.DoubleType, true),
                ArithmeticOperator.Subtract),
            Ref(1, DataTypes.DoubleType, true),
            ComparisonOperator.LessThanOrEqual);

        AssertParity(expr, schema, batch);
    }

    // =====================================================================================
    // AC: codegen is optional, never a correctness dependency — transparent fallback parity
    // =====================================================================================

    [Fact]
    public void Fallback_StringComparison_UsesInterpreterAndStillCorrect()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.StringType, true), F("b", DataTypes.StringType, true));
        var expr = new ComparisonExpression(Ref(0, DataTypes.StringType, true), Ref(1, DataTypes.StringType, true), ComparisonOperator.Equal);
        ColumnBatch batch = Batch(schema, StrCol("x", "y", null, "z"), StrCol("x", "Y", null, "z"));

        // A string lane has no fixed-width carrier: the whole tree must route to the interpreter.
        AssertParity(expr, schema, batch, fusable: false);
    }

    [Fact]
    public void Fallback_IsNullOverString_UsesInterpreterAndStillCorrect()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.StringType, true));
        var expr = new IsNullExpression(Ref(0, DataTypes.StringType, true), negated: false);
        ColumnBatch batch = Batch(schema, StrCol("x", null, "z"));

        AssertParity(expr, schema, batch, fusable: false);
    }

    [Fact]
    public void Fallback_DecimalDivide_RejectedIdenticallyByBothTiers()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", Dec102, false), F("b", Dec102, false));
        var expr = new ArithmeticExpression(Ref(0, Dec102), Ref(1, Dec102), ArithmeticOperator.Divide);

        // Decimal value rounding is deferred by the type system; both tiers reject at build time.
        Assert.Throws<UnsupportedOperatorException>(() => Interpreted(expr, schema));
        Assert.Throws<UnsupportedOperatorException>(() => Compiled(expr, schema));
    }

    [Fact]
    public void Fallback_UnsupportedCast_RejectedIdenticallyByBothTiers()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        // double -> decimal is not in the interpreted v1 cast matrix; the compiled tier must defer too.
        StructType schema = Schema(F("a", DataTypes.DoubleType, false));
        var expr = new CastExpression(Ref(0, DataTypes.DoubleType), Dec102);

        Assert.Throws<UnsupportedOperatorException>(() => Interpreted(expr, schema));
        Assert.Throws<UnsupportedOperatorException>(() => Compiled(expr, schema));
    }

    [Fact]
    public void BadColumnReference_RejectedIdenticallyByBothTiers()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.IntegerType, false));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(5, DataTypes.IntegerType), ArithmeticOperator.Add);

        ArgumentException fromInterpreted = Assert.Throws<ArgumentException>(() => Interpreted(expr, schema));
        ArgumentException fromCompiled = Assert.Throws<ArgumentException>(() => Compiled(expr, schema));
        Assert.Equal(fromInterpreted.Message, fromCompiled.Message);
        Assert.Equal(fromInterpreted.ParamName, fromCompiled.ParamName);
    }

    // =====================================================================================
    // Non-vacuity guard: a fusable tree is genuinely served by a compiled delegate
    // =====================================================================================

    [Fact]
    public void Fusion_ProducesCompiledEvaluator_ForFusableTree()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));
        var expr = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);

        // If fusion were silently disabled, this fails — keeping the entire parity suite honest.
        Assert.IsType<CompiledExpressionEvaluator>(Compiled(expr, schema));
    }

    // =====================================================================================
    // Delegate cache: compile-once-per-shape, reuse, bounded eviction, metrics
    // =====================================================================================

    [Fact]
    public void Cache_CompilesOncePerShape_ReusesAcrossInstances()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var backend = new CompiledBackend();
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));

        // Two distinct node instances with the same structural shape.
        PhysicalExpression first = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);
        PhysicalExpression second = new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add);

        backend.BuildExpressionEvaluator(first, schema, OperatorKind.Project);
        CompiledExpressionCacheMetrics afterFirst = backend.ExpressionCacheMetrics;
        Assert.Equal(1, afterFirst.Compilations);
        Assert.Equal(0, afterFirst.Hits);
        Assert.Equal(1, afterFirst.Count);

        backend.BuildExpressionEvaluator(second, schema, OperatorKind.Project);
        CompiledExpressionCacheMetrics afterSecond = backend.ExpressionCacheMetrics;
        Assert.Equal(1, afterSecond.Compilations); // no recompile for the same shape
        Assert.Equal(1, afterSecond.Hits);
        Assert.Equal(1, afterSecond.Count);
    }

    [Fact]
    public void Cache_DistinctShapes_CompileSeparately()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var backend = new CompiledBackend();
        StructType schema = Schema(F("a", DataTypes.IntegerType, false), F("b", DataTypes.IntegerType, false));

        backend.BuildExpressionEvaluator(
            new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Add),
            schema, OperatorKind.Project);
        backend.BuildExpressionEvaluator(
            new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Ref(1, DataTypes.IntegerType), ArithmeticOperator.Subtract),
            schema, OperatorKind.Project);

        CompiledExpressionCacheMetrics metrics = backend.ExpressionCacheMetrics;
        Assert.Equal(2, metrics.Compilations);
        Assert.Equal(0, metrics.Hits);
        Assert.Equal(2, metrics.Count);
    }

    [Fact]
    public void Cache_BoundedCapacity_EvictsOldestShapes()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var cache = new CompiledExpressionCache(capacity: 2);

        // Three structurally distinct shapes (distinct baked literal) overflow a capacity-2 cache.
        cache.GetOrCompile(new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(1), ArithmeticOperator.Add));
        cache.GetOrCompile(new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(2), ArithmeticOperator.Add));
        cache.GetOrCompile(new ArithmeticExpression(Ref(0, DataTypes.IntegerType), Literal.OfInt(3), ArithmeticOperator.Add));

        CompiledExpressionCacheMetrics metrics = cache.Metrics;
        Assert.Equal(3, metrics.Compilations);
        Assert.True(metrics.Evictions >= 1, $"expected >=1 eviction, saw {metrics.Evictions}");
        Assert.True(metrics.Count <= 2, $"expected bounded count <=2, saw {metrics.Count}");
    }

    [Fact]
    public void Cache_SameShapeAcrossDifferentLiteralValues_AreDistinctKeys()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var cache = new CompiledExpressionCache();
        cache.GetOrCompile(new ArithmeticExpression(Ref(0, DataTypes.DoubleType), Literal.OfDouble(0.0), ArithmeticOperator.Add));
        cache.GetOrCompile(new ArithmeticExpression(Ref(0, DataTypes.DoubleType), Literal.OfDouble(-0.0), ArithmeticOperator.Add));

        // +0.0 and -0.0 are different bit patterns, so they bake into different kernels (two compiles).
        Assert.Equal(2, cache.Metrics.Compilations);
        Assert.Equal(2, cache.Metrics.Count);
    }

    // =====================================================================================
    // Case table (happy-path differential matrix)
    // =====================================================================================

    private sealed record Case(string Name, PhysicalExpression Expr, StructType Schema, ColumnBatch Batch, bool Fusable = true);

    private static readonly IReadOnlyList<Case> Cases = BuildCases();

    private static IReadOnlyList<Case> BuildCases()
    {
        var list = new List<Case>();
        void Add(string name, PhysicalExpression expr, StructType schema, ColumnBatch batch)
            => list.Add(new Case(name, expr, schema, batch));

        // ---- integral arithmetic (Add/Subtract/Multiply/Remainder keep the wider integral type) ----
        {
            StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true));
            ColumnReference a = Ref(0, DataTypes.IntegerType, true);
            ColumnReference b = Ref(1, DataTypes.IntegerType, true);
            ColumnBatch batch = Batch(schema, IntCol(8, 0, null, 7, -5, 13), IntCol(2, 5, 3, null, -5, -1));
            Add("int_add", new ArithmeticExpression(a, b, ArithmeticOperator.Add), schema, batch);
            Add("int_sub", new ArithmeticExpression(a, b, ArithmeticOperator.Subtract), schema, batch);
            Add("int_mul", new ArithmeticExpression(a, b, ArithmeticOperator.Multiply), schema, batch);
            Add("int_rem", new ArithmeticExpression(a, b, ArithmeticOperator.Remainder), schema, batch); // includes b=-1 -> 0
            Add("int_div_to_double", new ArithmeticExpression(a, b, ArithmeticOperator.Divide), schema, batch); // Spark `/` -> double
            Add("int_add_literal", new ArithmeticExpression(a, Literal.OfInt(100), ArithmeticOperator.Add), schema, batch);
            Add("int_add_null_literal", new ArithmeticExpression(a, Literal.Null(DataTypes.IntegerType), ArithmeticOperator.Add), schema, batch);
            Add(
                "int_nested_add_mul",
                new ArithmeticExpression(new ArithmeticExpression(a, b, ArithmeticOperator.Add), Literal.OfInt(3), ArithmeticOperator.Multiply),
                schema,
                batch);
        }

        // ---- long / short / byte integral (narrowing carriers) ----
        {
            StructType schema = Schema(F("a", DataTypes.LongType, true), F("b", DataTypes.LongType, true));
            ColumnBatch batch = Batch(schema, LongCol(100, -3, null, 9), LongCol(7, 4, 2, null));
            Add("long_add", new ArithmeticExpression(Ref(0, DataTypes.LongType, true), Ref(1, DataTypes.LongType, true), ArithmeticOperator.Add), schema, batch);
            Add("long_mul", new ArithmeticExpression(Ref(0, DataTypes.LongType, true), Ref(1, DataTypes.LongType, true), ArithmeticOperator.Multiply), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.ShortType, true), F("b", DataTypes.ShortType, true));
            ColumnBatch batch = Batch(schema, ShortCol(30, -7, null, 12), ShortCol(5, 9, 1, null));
            Add("short_add", new ArithmeticExpression(Ref(0, DataTypes.ShortType, true), Ref(1, DataTypes.ShortType, true), ArithmeticOperator.Add), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.ByteType, true), F("b", DataTypes.ByteType, true));
            ColumnBatch batch = Batch(schema, ByteCol(10, -4, null, 7), ByteCol(3, 8, 1, null));
            Add("byte_sub", new ArithmeticExpression(Ref(0, DataTypes.ByteType, true), Ref(1, DataTypes.ByteType, true), ArithmeticOperator.Subtract), schema, batch);
        }

        // ---- floating arithmetic (Single + Double, incl. divide / remainder) ----
        {
            StructType schema = Schema(F("a", DataTypes.DoubleType, true), F("b", DataTypes.DoubleType, true));
            ColumnReference a = Ref(0, DataTypes.DoubleType, true);
            ColumnReference b = Ref(1, DataTypes.DoubleType, true);
            ColumnBatch batch = Batch(schema, DoubleCol(1.5, -2.0, null, 7.0, 0.0), DoubleCol(0.5, 4.0, 3.0, null, 2.0));
            Add("double_add", new ArithmeticExpression(a, b, ArithmeticOperator.Add), schema, batch);
            Add("double_sub", new ArithmeticExpression(a, b, ArithmeticOperator.Subtract), schema, batch);
            Add("double_mul", new ArithmeticExpression(a, b, ArithmeticOperator.Multiply), schema, batch);
            Add("double_div", new ArithmeticExpression(a, b, ArithmeticOperator.Divide), schema, batch);
            Add("double_rem", new ArithmeticExpression(a, b, ArithmeticOperator.Remainder), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.FloatType, true), F("b", DataTypes.FloatType, true));
            ColumnReference a = Ref(0, DataTypes.FloatType, true);
            ColumnReference b = Ref(1, DataTypes.FloatType, true);
            ColumnBatch batch = Batch(schema, FloatCol(1.5f, -2.0f, null, 7.0f), FloatCol(0.5f, 4.0f, 3.0f, null));
            Add("float_add", new ArithmeticExpression(a, b, ArithmeticOperator.Add), schema, batch);
            Add("float_mul", new ArithmeticExpression(a, b, ArithmeticOperator.Multiply), schema, batch);
            Add("float_rem", new ArithmeticExpression(a, b, ArithmeticOperator.Remainder), schema, batch);
        }

        // ---- NaN / -0.0 edges (bit-exact comparison required) ----
        {
            StructType schema = Schema(F("a", DataTypes.DoubleType, true), F("b", DataTypes.DoubleType, true));
            ColumnBatch batch = Batch(
                schema,
                DoubleCol(double.NaN, -0.0, 0.0, double.PositiveInfinity, 1.0),
                DoubleCol(1.0, 0.0, -0.0, double.PositiveInfinity, double.NaN));
            Add("double_nan_negzero_mul", new ArithmeticExpression(Ref(0, DataTypes.DoubleType, true), Ref(1, DataTypes.DoubleType, true), ArithmeticOperator.Multiply), schema, batch);
            Add("double_nan_eq", new ComparisonExpression(Ref(0, DataTypes.DoubleType, true), Ref(1, DataTypes.DoubleType, true), ComparisonOperator.Equal), schema, batch);
            Add("double_nan_lt", new ComparisonExpression(Ref(0, DataTypes.DoubleType, true), Ref(1, DataTypes.DoubleType, true), ComparisonOperator.LessThan), schema, batch);
            Add("double_negzero_eq", new ComparisonExpression(Ref(0, DataTypes.DoubleType, true), Ref(1, DataTypes.DoubleType, true), ComparisonOperator.GreaterThanOrEqual), schema, batch);
        }

        // ---- decimal arithmetic (compact + wide carriers) ----
        {
            StructType schema = Schema(F("a", Dec102, true), F("b", Dec92, true));
            ColumnReference a = Ref(0, Dec102, true);
            ColumnReference b = Ref(1, Dec92, true);
            ColumnBatch batch = Batch(schema, DecimalCol(Dec102, 12345, -6789, null, 500), DecimalCol(Dec92, 100, 250, 7, null));
            Add("decimal_add_compact", new ArithmeticExpression(a, b, ArithmeticOperator.Add), schema, batch);
            Add("decimal_sub_compact", new ArithmeticExpression(a, b, ArithmeticOperator.Subtract), schema, batch);
            Add("decimal_mul_wide", new ArithmeticExpression(a, b, ArithmeticOperator.Multiply), schema, batch); // result precision > 18 -> Int128 carrier
        }

        // ---- comparison across kinds ----
        {
            StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true));
            ColumnReference a = Ref(0, DataTypes.IntegerType, true);
            ColumnReference b = Ref(1, DataTypes.IntegerType, true);
            ColumnBatch batch = Batch(schema, IntCol(3, 5, null, 9, 9), IntCol(5, 5, 7, null, 2));
            Add("cmp_int_eq", new ComparisonExpression(a, b, ComparisonOperator.Equal), schema, batch);
            Add("cmp_int_ne", new ComparisonExpression(a, b, ComparisonOperator.NotEqual), schema, batch);
            Add("cmp_int_lt", new ComparisonExpression(a, b, ComparisonOperator.LessThan), schema, batch);
            Add("cmp_int_le", new ComparisonExpression(a, b, ComparisonOperator.LessThanOrEqual), schema, batch);
            Add("cmp_int_gt", new ComparisonExpression(a, b, ComparisonOperator.GreaterThan), schema, batch);
            Add("cmp_int_ge", new ComparisonExpression(a, b, ComparisonOperator.GreaterThanOrEqual), schema, batch);
            Add("cmp_int_literal", new ComparisonExpression(a, Literal.OfInt(5), ComparisonOperator.GreaterThan), schema, batch);
        }
        {
            StructType schema = Schema(F("a", Dec102, true), F("b", Dec92, true));
            ColumnBatch batch = Batch(schema, DecimalCol(Dec102, 12345, 500, null), DecimalCol(Dec92, 1230, 600, 7));
            Add("cmp_decimal_lt", new ComparisonExpression(Ref(0, Dec102, true), Ref(1, Dec92, true), ComparisonOperator.LessThan), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.BooleanType, true), F("b", DataTypes.BooleanType, true));
            ColumnBatch batch = Batch(schema, BoolCol(true, false, null, true), BoolCol(true, true, false, null));
            Add("cmp_bool_eq", new ComparisonExpression(Ref(0, DataTypes.BooleanType, true), Ref(1, DataTypes.BooleanType, true), ComparisonOperator.Equal), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.DateType, true), F("b", DataTypes.TimestampType, true));
            ColumnBatch batch = Batch(schema, DateCol(1, 2, null, 4), TimestampCol(86_400_000_000L, 0L, 5L, null));
            Add("cmp_date_timestamp_lt", new ComparisonExpression(Ref(0, DataTypes.DateType, true), Ref(1, DataTypes.TimestampType, true), ComparisonOperator.LessThan), schema, batch);
        }

        // ---- logical (Kleene three-valued) ----
        {
            StructType schema = Schema(F("a", DataTypes.BooleanType, true), F("b", DataTypes.BooleanType, true));
            ColumnReference a = Ref(0, DataTypes.BooleanType, true);
            ColumnReference b = Ref(1, DataTypes.BooleanType, true);
            ColumnBatch batch = Batch(
                schema,
                BoolCol(true, true, false, null, null, true),
                BoolCol(true, false, null, false, true, null));
            Add("logical_and", new LogicalExpression(a, b, LogicalOperator.And), schema, batch);
            Add("logical_or", new LogicalExpression(a, b, LogicalOperator.Or), schema, batch);
            Add("logical_not", new LogicalExpression(a), schema, batch);
            Add(
                "logical_and_or",
                new LogicalExpression(new LogicalExpression(a, b, LogicalOperator.And), new LogicalExpression(b), LogicalOperator.Or),
                schema,
                batch);
        }
        {
            // comparison-fed boolean predicate: (a < b) AND (a > 0)
            StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true));
            ColumnReference a = Ref(0, DataTypes.IntegerType, true);
            ColumnReference b = Ref(1, DataTypes.IntegerType, true);
            ColumnBatch batch = Batch(schema, IntCol(3, -2, null, 5), IntCol(7, 1, 4, null));
            Add(
                "predicate_and_of_comparisons",
                new LogicalExpression(
                    new ComparisonExpression(a, b, ComparisonOperator.LessThan),
                    new ComparisonExpression(a, Literal.OfInt(0), ComparisonOperator.GreaterThan),
                    LogicalOperator.And),
                schema,
                batch);
        }

        // ---- cast matrix (supported conversions only) ----
        {
            StructType schema = Schema(F("a", DataTypes.IntegerType, true));
            ColumnBatch batch = Batch(schema, IntCol(7, -3, null, 100));
            Add("cast_int_to_long", new CastExpression(Ref(0, DataTypes.IntegerType, true), DataTypes.LongType), schema, batch);
            Add("cast_int_to_double", new CastExpression(Ref(0, DataTypes.IntegerType, true), DataTypes.DoubleType), schema, batch);
            Add("cast_int_to_short", new CastExpression(Ref(0, DataTypes.IntegerType, true), DataTypes.ShortType), schema, batch);
            Add("cast_int_to_bool", new CastExpression(Ref(0, DataTypes.IntegerType, true), DataTypes.BooleanType), schema, batch);
            Add("cast_int_to_decimal", new CastExpression(Ref(0, DataTypes.IntegerType, true), Dec102), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.DoubleType, true));
            ColumnBatch batch = Batch(schema, DoubleCol(7.9, -3.2, null, 100.0));
            Add("cast_double_to_int_trunc", new CastExpression(Ref(0, DataTypes.DoubleType, true), DataTypes.IntegerType), schema, batch);
            Add("cast_double_to_float", new CastExpression(Ref(0, DataTypes.DoubleType, true), DataTypes.FloatType), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.BooleanType, true));
            ColumnBatch batch = Batch(schema, BoolCol(true, false, null));
            Add("cast_bool_to_int", new CastExpression(Ref(0, DataTypes.BooleanType, true), DataTypes.IntegerType), schema, batch);
            Add("cast_bool_to_decimal", new CastExpression(Ref(0, DataTypes.BooleanType, true), Dec102), schema, batch);
        }
        {
            StructType schema = Schema(F("a", Dec102, true));
            ColumnBatch batch = Batch(schema, DecimalCol(Dec102, 12345, -6789, null));
            Add("cast_decimal_to_long_trunc", new CastExpression(Ref(0, Dec102, true), DataTypes.LongType), schema, batch);
            Add("cast_decimal_to_wide_decimal", new CastExpression(Ref(0, Dec102, true), Dec3810), schema, batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.DateType, true), F("b", DataTypes.TimestampType, true));
            ColumnBatch batch = Batch(schema, DateCol(1, 2, null), TimestampCol(86_400_000_000L, 0L, null));
            Add("cast_date_to_timestamp", new CastExpression(Ref(0, DataTypes.DateType, true), DataTypes.TimestampType), schema, batch);
            Add("cast_timestamp_to_date", new CastExpression(Ref(1, DataTypes.TimestampType, true), DataTypes.DateType), schema, batch);
        }
        {
            // timestamp_ntz casts (#558): timezone-less, so date->ntz is midnight wall-clock and
            // timestamp<->ntz is an identity on the epoch-microsecond lane. Both tiers must agree.
            StructType schema = Schema(
                F("d", DataTypes.DateType, true),
                F("t", DataTypes.TimestampType, true),
                F("n", DataTypes.TimestampNtzType, true));
            ColumnBatch batch = Batch(
                schema,
                DateCol(1, 2, null),
                TimestampCol(86_400_000_000L, 0L, null),
                TimestampNtzCol(86_400_000_500L, -500L, null));
            Add("cast_date_to_timestamp_ntz", new CastExpression(Ref(0, DataTypes.DateType, true), DataTypes.TimestampNtzType), schema, batch);
            Add("cast_timestamp_ntz_to_date", new CastExpression(Ref(2, DataTypes.TimestampNtzType, true), DataTypes.DateType), schema, batch);
            Add("cast_timestamp_to_timestamp_ntz", new CastExpression(Ref(1, DataTypes.TimestampType, true), DataTypes.TimestampNtzType), schema, batch);
            Add("cast_timestamp_ntz_to_timestamp", new CastExpression(Ref(2, DataTypes.TimestampNtzType, true), DataTypes.TimestampType), schema, batch);
        }

        // ---- null-check (IsNull / IsNotNull) ----
        {
            StructType schema = Schema(F("a", DataTypes.IntegerType, true));
            ColumnBatch batch = Batch(schema, IntCol(1, null, 3, null));
            Add("isnull", new IsNullExpression(Ref(0, DataTypes.IntegerType, true), negated: false), schema, batch);
            Add("isnotnull", new IsNullExpression(Ref(0, DataTypes.IntegerType, true), negated: true), schema, batch);
            Add(
                "isnotnull_of_arithmetic",
                new IsNullExpression(new ArithmeticExpression(Ref(0, DataTypes.IntegerType, true), Literal.OfInt(1), ArithmeticOperator.Add), negated: true),
                schema,
                batch);
        }

        // ---- legacy (non-ANSI) overflow / divide -> SQL NULL on both tiers ----
        {
            StructType schema = Schema(F("a", DataTypes.IntegerType, true), F("b", DataTypes.IntegerType, true));
            ColumnBatch batch = Batch(schema, IntCol(int.MaxValue, 5, null), IntCol(1, 5, 3));
            Add(
                "legacy_int_add_overflow_nulls",
                new ArithmeticExpression(Ref(0, DataTypes.IntegerType, true), Ref(1, DataTypes.IntegerType, true), ArithmeticOperator.Add, AnsiMode.Legacy),
                schema,
                batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.DoubleType, true), F("b", DataTypes.DoubleType, true));
            ColumnBatch batch = Batch(schema, DoubleCol(1.0, 8.0, null), DoubleCol(0.0, 2.0, 3.0));
            Add(
                "legacy_double_divzero_nulls",
                new ArithmeticExpression(Ref(0, DataTypes.DoubleType, true), Ref(1, DataTypes.DoubleType, true), ArithmeticOperator.Divide, AnsiMode.Legacy),
                schema,
                batch);
        }
        {
            StructType schema = Schema(F("a", DataTypes.DoubleType, true));
            ColumnBatch batch = Batch(schema, DoubleCol(9223372036854775808.0, 5.0, null));
            Add(
                "legacy_cast_double_to_long_overflow_nulls",
                new CastExpression(Ref(0, DataTypes.DoubleType, true), DataTypes.LongType, AnsiMode.Legacy),
                schema,
                batch);
        }

        return list;
    }

    // =====================================================================================
    // Column / schema builders (mirrors InterpretedExpressionEvaluatorTests so inputs are identical)
    // =====================================================================================

    private static StructField F(string name, DataType type, bool nullable) => new(name, type, nullable);

    private static StructType Schema(params StructField[] fields) => new(fields);

    private static ColumnReference Ref(int ordinal, DataType type, bool nullable = false) => new(ordinal, type, nullable);

    private static ColumnVector IntCol(params int?[] values) => BuildCol(DataTypes.IntegerType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector LongCol(params long?[] values) => BuildCol(DataTypes.LongType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector ShortCol(params short?[] values) => BuildCol(DataTypes.ShortType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector DoubleCol(params double?[] values) => BuildCol(DataTypes.DoubleType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector FloatCol(params float?[] values) => BuildCol(DataTypes.FloatType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector BoolCol(params bool?[] values) => BuildCol(DataTypes.BooleanType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector DateCol(params int?[] values) => BuildCol(DataTypes.DateType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector TimestampCol(params long?[] values) => BuildCol(DataTypes.TimestampType, values, static (v, x) => v.AppendValue(x));

    private static ColumnVector TimestampNtzCol(params long?[] values) => BuildCol(DataTypes.TimestampNtzType, values, static (v, x) => v.AppendValue(x));

    // Signed tinyint: stored as a CLR byte but interpreted as sbyte (Spark tinyint is signed).
    private static ColumnVector ByteCol(params int?[] signedValues)
        => BuildCol(DataTypes.ByteType, signedValues, static (v, x) => v.AppendValue(unchecked((byte)(sbyte)x)));

    // Compact decimal (precision <= 18): unscaled mantissa stored as long.
    private static ColumnVector DecimalCol(DecimalType type, params long?[] unscaled)
        => BuildCol(type, unscaled, static (v, x) => v.AppendValue(x));

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

    private static ColumnVector BuildCol<T>(DataType type, T?[] values, Action<MutableColumnVector, T> append)
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
}
