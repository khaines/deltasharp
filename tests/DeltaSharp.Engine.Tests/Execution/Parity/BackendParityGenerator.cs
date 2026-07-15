using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Tests.Execution.Parity;

/// <summary>
/// A reproducible parity case: the fixed input <see cref="Schema"/>, a seeded <see cref="Batch"/>, and a
/// seeded <see cref="Expression"/> tree to differentially evaluate. <see cref="RootForm"/> records
/// whether the root produced a boolean predicate, a numeric value, or a timestamp_ntz cast value (for
/// diagnostics / coverage).
/// </summary>
internal sealed record GeneratedCase(
    StructType Schema, ColumnBatch Batch, PhysicalExpression Expression, string RootForm, int Rows);

/// <summary>
/// The deterministic schema + expression-tree + batch generator for the randomized half of the parity
/// suite (STORY-03.5.2 AC2/AC5). Given a <c>seed</c> it produces a <see cref="GeneratedCase"/> with
/// byte-identical output on every run and runtime (it draws from <see cref="DeterministicRng"/>, a fixed
/// SplitMix64 recurrence — never default-seeded <see cref="System.Random"/>).
/// </summary>
/// <remarks>
/// <para>
/// The grammar is intentionally constrained to trees the compiled tier can actually <b>fuse</b>
/// (fixed-width arithmetic / comparison / boolean / cast / null-check over the v1 carrier types), so the
/// compiled evaluator is a real <see cref="DeltaSharp.Engine.Execution.Expressions.CompiledExpressionEvaluator"/>
/// and the differential is non-vacuous. Decimal divide/remainder (rejected by both tiers) and string/binary
/// lanes (interpreter-only) are excluded here because they are covered by the existing #152 expression-level
/// suite and the golden fallback cases — this generator complements, not duplicates, those.
/// </para>
/// <para>
/// Every arithmetic node is built in <see cref="AnsiMode.Legacy"/>: overflow and zero-division become SQL
/// <c>NULL</c> instead of throwing, so a randomized case is a <b>pure value+validity</b> comparison with no
/// exception non-determinism. The batch deliberately mixes scattered nulls, integer extremes (to drive the
/// Legacy overflow→null path), and float/double <c>NaN</c>/<c>±0.0</c>/<c>∞</c> edges so the bit-exact
/// comparison is meaningfully exercised. ANSI exception parity is asserted by the golden suite instead.
/// </para>
/// </remarks>
internal static class BackendParityGenerator
{
    private static readonly DecimalType Dec102 = new(10, 2);

    // ----- the fixed fusable input schema (stable ordinals, referenced by diagnostics) -----

    private static readonly StructField[] Fields =
    [
        new("b0", DataTypes.BooleanType, true),    // 0
        new("i0", DataTypes.IntegerType, true),    // 1
        new("i1", DataTypes.IntegerType, true),    // 2
        new("l0", DataTypes.LongType, true),       // 3
        new("f0", DataTypes.FloatType, true),      // 4
        new("d0", DataTypes.DoubleType, true),     // 5
        new("d1", DataTypes.DoubleType, true),     // 6
        new("dec0", Dec102, true),                 // 7
        new("dt0", DataTypes.DateType, true),      // 8
        new("ts0", DataTypes.TimestampType, true), // 9
        new("i2", DataTypes.IntegerType, false),   // 10 (non-nullable: exercises the no-null fast path)
        new("sh0", DataTypes.ShortType, true),     // 11
        new("by0", DataTypes.ByteType, true),      // 12 (signed tinyint)
        new("tsn0", DataTypes.TimestampNtzType, true), // 13 (timezone-less wall-clock, #558)
    ];

    /// <summary>Numeric, non-decimal column ordinals usable as arithmetic operands and comparison operands.</summary>
    private static readonly (int Ordinal, DataType Type)[] NumericColumns =
    [
        (1, DataTypes.IntegerType), (2, DataTypes.IntegerType), (3, DataTypes.LongType),
        (4, DataTypes.FloatType), (5, DataTypes.DoubleType), (6, DataTypes.DoubleType),
        (10, DataTypes.IntegerType), (11, DataTypes.ShortType), (12, DataTypes.ByteType),
    ];

    /// <summary>The fixed input schema every randomized case is generated over.</summary>
    public static StructType Schema { get; } = new(Fields);

    /// <summary>Produces the deterministic case for <paramref name="seed"/>.</summary>
    public static GeneratedCase Generate(ulong seed)
    {
        var rng = new DeterministicRng(seed);
        int rows = rng.Next(1, 257); // 1..256 logical rows
        ColumnBatch batch = BuildBatch(rng, rows);

        // Draw the expression AFTER the batch so the row count does not perturb the tree shape's seed
        // stream in a way that hides bugs; both are pure functions of the same seed regardless. The root
        // is one of three value-carrying forms so the differential pins BOTH a boolean predicate, a
        // numeric value, AND a bare timestamp_ntz cast VALUE (the last is the #558 seam: a uniform +N
        // epoch-micros offset in a compiled ntz cast is invisible through an order-preserving comparison
        // but is caught element-wise when the ntz cast is projected as a top-level output).
        (PhysicalExpression expr, string rootForm) = rng.Next(3) switch
        {
            0 => (GenBoolean(rng, depth: 3), "predicate(boolean)"),
            1 => (GenNumeric(rng, depth: 3), "value(numeric)"),
            _ => (GenTemporalNtzValue(rng), "value(timestamp_ntz)"),
        };
        return new GeneratedCase(Schema, batch, expr, rootForm, rows);
    }

    // A timestamp_ntz cast projected as a TOP-LEVEL output VALUE — the ntz long is compared element-wise
    // by the differential oracle, NOT sunk into an order-preserving Comparison/IsNull. This is the #558
    // correctness seam: a uniform +N epoch-micros offset in a compiled ntz cast is invisible through
    // </=/> (it preserves ordering and equality against another equally-offset operand) but shows up
    // immediately as a per-row value mismatch here. Both the date->ntz (midnight wall-clock) and the
    // timestamp->ntz (identity on the epoch-micros lane) casts are drawn so both lowering arms are pinned.
    private static PhysicalExpression GenTemporalNtzValue(DeterministicRng rng) => rng.NextBool()
        ? new CastExpression(new ColumnReference(9, DataTypes.TimestampType, true), DataTypes.TimestampNtzType, AnsiMode.Legacy)
        : new CastExpression(new ColumnReference(8, DataTypes.DateType, true), DataTypes.TimestampNtzType, AnsiMode.Legacy);

    // ===== expression grammar (type-directed; every tree satisfies CompiledExpressionEvaluators.CanFuse) =====

    private static PhysicalExpression GenBoolean(DeterministicRng rng, int depth)
    {
        if (depth <= 0 || rng.Next(100) < 45)
        {
            return rng.Next(5) switch
            {
                0 => new ColumnReference(0, DataTypes.BooleanType, true),                       // b0
                1 => Comparison(rng, GenComparable(rng, depth - 1), GenComparable(rng, depth - 1)), // numeric/decimal compare
                2 => rng.NextBool()
                    ? Comparison(rng, GenTemporal(rng), GenTemporal(rng))                       // date/timestamp (LTZ) compare
                    : Comparison(rng, GenTemporalNtz(rng), GenTemporalNtz(rng)),                // timestamp_ntz compare
                3 => new IsNullExpression(GenLeafForNullCheck(rng), negated: rng.NextBool()),   // is[not]null
                _ => new CastExpression(GenNumeric(rng, depth - 1), DataTypes.BooleanType, AnsiMode.Legacy), // numeric -> bool
            };
        }

        return rng.Next(3) switch
        {
            0 => new LogicalExpression(GenBoolean(rng, depth - 1), GenBoolean(rng, depth - 1), LogicalOperator.And),
            1 => new LogicalExpression(GenBoolean(rng, depth - 1), GenBoolean(rng, depth - 1), LogicalOperator.Or),
            _ => new LogicalExpression(GenBoolean(rng, depth - 1)), // NOT
        };
    }

    private static ComparisonExpression Comparison(DeterministicRng rng, PhysicalExpression left, PhysicalExpression right)
    {
        ComparisonOperator op = (ComparisonOperator)rng.Next(6); // Equal..GreaterThanOrEqual
        return new ComparisonExpression(left, right, op);
    }

    private static PhysicalExpression GenNumeric(DeterministicRng rng, int depth)
    {
        if (depth <= 0 || rng.Next(100) < 50)
        {
            // Leaf: numeric column reference (mostly) or a small numeric literal. Decimal is intentionally
            // excluded from arithmetic operands here (decimal /,% is rejected by both tiers and is not part
            // of this generator's fusable grammar); decimal coverage comes via comparisons and null-checks.
            return rng.Next(10) <= 6 ? NumericColumn(rng) : NumericLiteral(rng);
        }

        return rng.Next(10) switch
        {
            // Arithmetic in Legacy mode (overflow/zero-divide -> SQL NULL, never throws).
            <= 7 => new ArithmeticExpression(
                GenNumeric(rng, depth - 1),
                GenNumeric(rng, depth - 1),
                (ArithmeticOperator)rng.Next(5), // Add..Remainder
                AnsiMode.Legacy),
            // Cast a numeric subtree to another core numeric type (always fusable, Legacy overflow -> NULL).
            _ => new CastExpression(GenNumeric(rng, depth - 1), CoreNumericTarget(rng), AnsiMode.Legacy),
        };
    }

    /// <summary>A comparison operand: a numeric subtree or a shallow decimal leaf (both fusable).</summary>
    private static PhysicalExpression GenComparable(DeterministicRng rng, int depth) =>
        rng.Next(4) == 0 ? GenDecimalLeaf(rng) : GenNumeric(rng, depth);

    private static PhysicalExpression GenLeafForNullCheck(DeterministicRng rng) => rng.Next(5) switch
    {
        0 => new ColumnReference(0, DataTypes.BooleanType, true),
        1 => GenNumeric(rng, depth: 1),
        2 => GenTemporal(rng),
        3 => new ColumnReference(13, DataTypes.TimestampNtzType, true),
        _ => GenDecimalLeaf(rng),
    };

    // A date/timestamp (LTZ family) operand: a bare date/timestamp column, a date<->timestamp cast, or a
    // timestamp_ntz cast INTO the LTZ family (ntz->timestamp / ntz->date). Every arm yields a date- or
    // timestamp-typed value, all of which mix safely under the kernel's date-vs-timestamp promotion.
    private static PhysicalExpression GenTemporal(DeterministicRng rng) => rng.Next(6) switch
    {
        0 => new ColumnReference(8, DataTypes.DateType, true),       // dt0
        1 => new ColumnReference(9, DataTypes.TimestampType, true),  // ts0
        2 => new CastExpression(new ColumnReference(8, DataTypes.DateType, true), DataTypes.TimestampType, AnsiMode.Legacy),
        3 => new CastExpression(new ColumnReference(9, DataTypes.TimestampType, true), DataTypes.DateType, AnsiMode.Legacy),
        4 => new CastExpression(new ColumnReference(13, DataTypes.TimestampNtzType, true), DataTypes.TimestampType, AnsiMode.Legacy),
        _ => new CastExpression(new ColumnReference(13, DataTypes.TimestampNtzType, true), DataTypes.DateType, AnsiMode.Legacy),
    };

    // A timestamp_ntz operand: a bare ntz column or a date/timestamp cast INTO timestamp_ntz. Every arm
    // yields a timestamp_ntz value, so Comparison(GenTemporalNtz, GenTemporalNtz) is always ntz-vs-ntz — a
    // raw date/timestamp-vs-ntz pair has no kernel promotion and is resolved by coercion, not this generator.
    private static PhysicalExpression GenTemporalNtz(DeterministicRng rng) => rng.Next(3) switch
    {
        0 => new ColumnReference(13, DataTypes.TimestampNtzType, true), // tsn0
        1 => new CastExpression(new ColumnReference(8, DataTypes.DateType, true), DataTypes.TimestampNtzType, AnsiMode.Legacy),
        _ => new CastExpression(new ColumnReference(9, DataTypes.TimestampType, true), DataTypes.TimestampNtzType, AnsiMode.Legacy),
    };

    private static ColumnReference NumericColumn(DeterministicRng rng)
    {
        (int ordinal, DataType type) = rng.Pick(NumericColumns);
        bool nullable = ordinal != 10; // i2 is the non-nullable column
        return new ColumnReference(ordinal, type, nullable);
    }

    private static Literal NumericLiteral(DeterministicRng rng) => rng.Next(5) switch
    {
        0 => Literal.OfInt(rng.Next(-1000, 1001)),
        1 => Literal.OfLong(rng.NextLong(-1_000_000, 1_000_000)),
        2 => Literal.OfFloat((float)((rng.NextDouble() - 0.5) * 200.0)),
        3 => Literal.OfDouble((rng.NextDouble() - 0.5) * 2000.0),
        _ => Literal.OfShort((short)rng.Next(-300, 301)),
    };

    private static PhysicalExpression GenDecimalLeaf(DeterministicRng rng) => rng.Next(3) switch
    {
        0 => new ColumnReference(7, Dec102, true),
        1 => Literal.OfDecimal(rng.NextLong(-9_999_999, 9_999_999), Dec102),
        // One shallow decimal arithmetic level (Add/Subtract/Multiply only — decimal /,% is rejected by
        // both tiers and is intentionally out of this generator's fusable grammar).
        _ => new ArithmeticExpression(
            new ColumnReference(7, Dec102, true),
            Literal.OfDecimal(rng.NextLong(-999_999, 999_999), Dec102),
            (ArithmeticOperator)rng.Next(3), // Add, Subtract, Multiply
            AnsiMode.Legacy),
    };

    private static DataType CoreNumericTarget(DeterministicRng rng) => rng.Next(6) switch
    {
        0 => DataTypes.IntegerType,
        1 => DataTypes.LongType,
        2 => DataTypes.FloatType,
        3 => DataTypes.DoubleType,
        4 => DataTypes.ShortType,
        _ => DataTypes.ByteType,
    };

    // ===== batch synthesis (scattered nulls + integral extremes + IEEE edge values) =====

    private static ColumnBatch BuildBatch(DeterministicRng rng, int rows)
    {
        var columns = new ColumnVector[Fields.Length];
        for (int c = 0; c < Fields.Length; c++)
        {
            columns[c] = BuildColumn(rng, Fields[c], rows);
        }

        return new ManagedColumnBatch(Schema, columns, rows);
    }

    private static ColumnVector BuildColumn(DeterministicRng rng, StructField field, int rows)
    {
        MutableColumnVector v = ColumnVectors.Create(field.DataType, Math.Max(rows, 1));
        for (int r = 0; r < rows; r++)
        {
            // ~1/6 nulls for nullable columns; the non-nullable i2 column never nulls.
            if (field.Nullable && rng.Next(6) == 0)
            {
                v.AppendNull();
                continue;
            }

            AppendValue(rng, v, field.DataType);
        }

        return v;
    }

    private static void AppendValue(DeterministicRng rng, MutableColumnVector v, DataType type)
    {
        switch (type)
        {
            case BooleanType:
                v.AppendValue(rng.NextBool());
                break;
            case ByteType:
                v.AppendValue(unchecked((byte)(sbyte)rng.Next(-128, 128)));
                break;
            case ShortType:
                v.AppendValue((short)rng.Next(short.MinValue, short.MaxValue + 1));
                break;
            case IntegerType:
                v.AppendValue(IntegerSample(rng));
                break;
            case LongType:
                v.AppendValue(LongSample(rng));
                break;
            case FloatType:
                v.AppendValue(FloatSample(rng));
                break;
            case DoubleType:
                v.AppendValue(DoubleSample(rng));
                break;
            case DateType:
                v.AppendValue(rng.Next(0, 30_000)); // epoch day
                break;
            case TimestampType:
                v.AppendValue(rng.NextLong(-2_000_000_000_000L, 2_000_000_000_000L)); // epoch micros
                break;
            case TimestampNtzType:
                v.AppendValue(rng.NextLong(-2_000_000_000_000L, 2_000_000_000_000L)); // epoch micros (timezone-less wall-clock)
                break;
            case DecimalType:
                v.AppendValue(rng.NextLong(-99_999_999L, 99_999_999L)); // compact decimal(10,2) unscaled
                break;
            default:
                throw new InvalidOperationException($"generator has no sampler for '{type.SimpleString}'");
        }
    }

    private static int IntegerSample(DeterministicRng rng) => rng.Next(20) switch
    {
        0 => int.MaxValue,             // drives Legacy overflow -> NULL on add/mul
        1 => int.MinValue,
        2 => 0,
        _ => rng.Next(-100_000, 100_001),
    };

    private static long LongSample(DeterministicRng rng) => rng.Next(20) switch
    {
        0 => long.MaxValue,
        1 => long.MinValue,
        2 => 0L,
        _ => rng.NextLong(-1_000_000_000L, 1_000_000_000L),
    };

    private static float FloatSample(DeterministicRng rng) => rng.Next(16) switch
    {
        0 => float.NaN,
        1 => float.PositiveInfinity,
        2 => float.NegativeInfinity,
        3 => -0.0f,
        4 => 0.0f,
        _ => (float)((rng.NextDouble() - 0.5) * 2000.0),
    };

    private static double DoubleSample(DeterministicRng rng) => rng.Next(16) switch
    {
        0 => double.NaN,
        1 => double.PositiveInfinity,
        2 => double.NegativeInfinity,
        3 => -0.0,
        4 => 0.0,
        _ => (rng.NextDouble() - 0.5) * 2000.0,
    };
}
