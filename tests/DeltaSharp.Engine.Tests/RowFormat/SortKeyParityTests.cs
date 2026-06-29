using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.RowFormat;

/// <summary>
/// STORY-02.4.2 AC2: rows containing decimals, timestamps, NaN, negative values, and nulls sort
/// identically under (a) a bytewise comparison of the <see cref="SortKeyEncoder"/> output and
/// (b) the scalar <see cref="RowOrderingComparer"/>. The comparator is the correctness oracle, so
/// parity is proven both pairwise (every pair compares to the same sign) and as a whole-list sort.
/// </summary>
public class SortKeyParityTests
{
    private static readonly StructType Schema = new(
    [
        new StructField("i", IntegerType.Instance),
        new StructField("d", DoubleType.Instance),
        new StructField("dec", new DecimalType(38, 6)),
        new StructField("ts", TimestampType.Instance),
        new StructField("s", StringType.Instance),
    ]);

    private static readonly object?[] Ints =
        [null, int.MinValue, -1000, -1, 0, 1, 1000, int.MaxValue];

    private static readonly object?[] Doubles =
    [
        null, double.NaN, double.NegativeInfinity, double.MinValue, -1.5d, -0.0d, 0.0d, 1.5d,
        double.MaxValue, double.PositiveInfinity, BitConverter.Int64BitsToDouble(0x7FF0_0000_0000_0007L),
    ];

    private static readonly object?[] Decimals =
    [
        null, Int128.MinValue, (Int128)(-100_000_000_000), (Int128)(-1), Int128.Zero, (Int128)1,
        (Int128)100_000_000_000, Int128.MaxValue,
    ];

    private static readonly object?[] Timestamps =
    [
        null, TemporalValues.MinEpochMicros, -1L, 0L, 1L, 1_700_000_000_000_000L, TemporalValues.MaxEpochMicros,
    ];

    private static readonly object?[] Strings =
        [null, string.Empty, "\u0000", "Z", "a", "ab", "b", "héllo", "z"];

    public static TheoryData<SortKeyOrdering[]> OrderingConfigurations()
    {
        var data = new TheoryData<SortKeyOrdering[]>();
        SortKeyOrdering af = new(SortKeyDirection.Ascending, NullSortOrder.NullsFirst);
        SortKeyOrdering al = new(SortKeyDirection.Ascending, NullSortOrder.NullsLast);
        SortKeyOrdering df = new(SortKeyDirection.Descending, NullSortOrder.NullsFirst);
        SortKeyOrdering dl = new(SortKeyDirection.Descending, NullSortOrder.NullsLast);

        data.Add([af, af, af, af, af]);
        data.Add([dl, dl, dl, dl, dl]);
        data.Add([af, dl, al, df, af]); // mixed directions and null placements per field
        data.Add([df, al, df, al, dl]);
        return data;
    }

    [Theory]
    [MemberData(nameof(OrderingConfigurations))]
    public void Bytewise_And_Scalar_Comparator_Agree_On_All_Pairs(SortKeyOrdering[] orderings)
    {
        int[] keyFields = [0, 1, 2, 3, 4];
        var encoder = new SortKeyEncoder(Schema, keyFields, orderings);
        var oracle = new RowOrderingComparer(Schema, keyFields, orderings);

        RowData[] rows = GenerateRows(count: 240, seed: 0xC0FFEE);
        byte[][] keys = new byte[rows.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            keys[i] = encoder.Encode(rows[i]);
        }

        for (int i = 0; i < rows.Length; i++)
        {
            for (int j = 0; j < rows.Length; j++)
            {
                int byBytes = Math.Sign(keys[i].AsSpan().SequenceCompareTo(keys[j]));
                int byScalar = Math.Sign(oracle.Compare(rows[i], rows[j]));
                Assert.True(
                    byBytes == byScalar,
                    $"Disagreement at rows ({i},{j}): memcmp sign {byBytes} vs comparator sign {byScalar}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(OrderingConfigurations))]
    public void Bytewise_And_Scalar_Comparator_Produce_The_Same_Sorted_Order(SortKeyOrdering[] orderings)
    {
        int[] keyFields = [0, 1, 2, 3, 4];
        var encoder = new SortKeyEncoder(Schema, keyFields, orderings);
        var oracle = new RowOrderingComparer(Schema, keyFields, orderings);

        RowData[] rows = GenerateRows(count: 240, seed: 0x1234_5678);
        byte[][] keys = new byte[rows.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            keys[i] = encoder.Encode(rows[i]);
        }

        // Enumerable.OrderBy is a stable sort, so a tie (compare == 0) keeps input order under both
        // comparers; with consistent comparers the two index orders must be identical.
        int[] byBytes = StableOrder(rows.Length, (x, y) => keys[x].AsSpan().SequenceCompareTo(keys[y]));
        int[] byScalar = StableOrder(rows.Length, (x, y) => oracle.Compare(rows[x], rows[y]));

        Assert.Equal(byScalar, byBytes);
    }

    [Fact]
    public void EdgeCaseRows_Parity_AllPairs()
    {
        // Hand-picked rows guaranteeing NaN, −0.0/+0.0, infinities, extreme decimals/timestamps, and
        // nulls in each position are exercised regardless of the pseudo-random generator.
        RowData[] rows =
        [
            new(Schema, null, null, null, null, null),
            new(Schema, int.MinValue, double.NegativeInfinity, Int128.MinValue, TemporalValues.MinEpochMicros, string.Empty),
            new(Schema, -1, -0.0d, (Int128)(-1), -1L, "\u0000"),
            new(Schema, 0, 0.0d, Int128.Zero, 0L, "a"),
            new(Schema, 0, double.NaN, Int128.Zero, 0L, "a"),
            new(Schema, 1, double.PositiveInfinity, (Int128)1, 1L, "z"),
            new(Schema, int.MaxValue, double.MaxValue, Int128.MaxValue, TemporalValues.MaxEpochMicros, "héllo"),
            new(Schema, 0, BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0000L)), Int128.Zero, 0L, "a"),
        ];

        SortKeyOrdering[] orderings =
        [
            new(SortKeyDirection.Ascending, NullSortOrder.NullsFirst),
            new(SortKeyDirection.Descending, NullSortOrder.NullsLast),
            new(SortKeyDirection.Ascending, NullSortOrder.NullsLast),
            new(SortKeyDirection.Descending, NullSortOrder.NullsFirst),
            new(SortKeyDirection.Ascending, NullSortOrder.NullsFirst),
        ];
        int[] keyFields = [0, 1, 2, 3, 4];
        var encoder = new SortKeyEncoder(Schema, keyFields, orderings);
        var oracle = new RowOrderingComparer(Schema, keyFields, orderings);

        byte[][] keys = new byte[rows.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            keys[i] = encoder.Encode(rows[i]);
        }

        for (int i = 0; i < rows.Length; i++)
        {
            for (int j = 0; j < rows.Length; j++)
            {
                Assert.Equal(
                    Math.Sign(oracle.Compare(rows[i], rows[j])),
                    Math.Sign(keys[i].AsSpan().SequenceCompareTo(keys[j])));
            }
        }
    }

    [Fact]
    public void NaN_And_NegativeZero_Rows_Are_Equal_Under_Both()
    {
        int[] keyFields = [1];
        SortKeyOrdering[] orderings = [SortKeyOrdering.Ascending];
        var encoder = new SortKeyEncoder(Schema, keyFields, orderings);
        var oracle = new RowOrderingComparer(Schema, keyFields, orderings);

        var negZero = new RowData(Schema, 0, -0.0d, Int128.Zero, 0L, "x");
        var posZero = new RowData(Schema, 0, 0.0d, Int128.Zero, 0L, "x");
        var nan1 = new RowData(Schema, 0, double.NaN, Int128.Zero, 0L, "x");
        var nan2 = new RowData(Schema, 0, BitConverter.Int64BitsToDouble(0x7FF0_0000_0000_0009L), Int128.Zero, 0L, "x");

        Assert.Equal(0, oracle.Compare(negZero, posZero));
        Assert.True(encoder.Encode(negZero).AsSpan().SequenceEqual(encoder.Encode(posZero)));

        Assert.Equal(0, oracle.Compare(nan1, nan2));
        Assert.True(encoder.Encode(nan1).AsSpan().SequenceEqual(encoder.Encode(nan2)));
    }

    private static int[] StableOrder(int count, Comparison<int> comparison)
    {
        // System.Linq.OrderBy is documented stable; order indices by the supplied comparison.
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        return [.. indices.OrderBy(i => i, Comparer<int>.Create(comparison))];
    }

    private static RowData[] GenerateRows(int count, ulong seed)
    {
        var rng = new Lcg(seed);
        RowData[] rows = new RowData[count];
        for (int r = 0; r < count; r++)
        {
            rows[r] = new RowData(
                Schema,
                Ints[rng.Next(Ints.Length)],
                Doubles[rng.Next(Doubles.Length)],
                Decimals[rng.Next(Decimals.Length)],
                Timestamps[rng.Next(Timestamps.Length)],
                Strings[rng.Next(Strings.Length)]);
        }

        return rows;
    }

    /// <summary>A tiny deterministic LCG so test data is reproducible without <c>System.Random</c>.</summary>
    private struct Lcg
    {
        private ulong _state;

        public Lcg(ulong seed) => _state = seed;

        public int Next(int bound)
        {
            _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)bound);
        }
    }
}
