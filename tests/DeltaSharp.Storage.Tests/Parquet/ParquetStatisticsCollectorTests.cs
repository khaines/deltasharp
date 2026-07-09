using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Unit tests for <see cref="ParquetStatisticsCollector"/> — the write-time <c>add.stats</c> extractor
/// (STORY-05.6.3 AC1/AC2). They assert the recorded record count, per-scalar-column
/// <c>min</c>/<c>max</c>/<c>nullCount</c> and stat kinds (AC1), and the documented omit/bound rules (AC2):
/// nested/binary/unsupported omission, string truncation → <c>tightBounds=false</c>, all-null omission,
/// non-finite float handling, and the indexing horizon.
/// </summary>
public sealed class ParquetStatisticsCollectorTests
{
    private static StructType Schema(string name, DataType type, bool nullable = true) =>
        new(new[] { new StructField(name, type, nullable) });

    private static ColumnBatch Batch(StructType schema, MutableColumnVector column) =>
        new ManagedColumnBatch(schema, new ColumnVector[] { column }, column.Length);

    private static FileStatistics Collect(StructType schema, ColumnBatch batch, StatisticsPolicy? policy = null) =>
        ParquetStatisticsCollector.Collect(schema, new[] { batch }, policy ?? StatisticsPolicy.Default);

    private static MutableColumnVector Longs(params long?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, Math.Max(1, values.Length));
        foreach (long? value in values)
        {
            if (value is long l)
            {
                v.AppendValue(l);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    // ---------------------------------------------------------------- AC1: per-type extraction

    [Fact]
    public void Long_RecordsSignedMinMaxNullCountAndRecordCount()
    {
        StructType schema = Schema("id", DataTypes.LongType);
        FileStatistics stats = Collect(schema, Batch(schema, Longs(10L, null, -5L, 40L)));

        Assert.Equal(4L, stats.NumRecords);
        Assert.Equal(DeltaStatValue.OfLong(-5L), stats.MinValues["id"]);
        Assert.Equal(DeltaStatValue.OfLong(40L), stats.MaxValues["id"]);
        Assert.Equal(1L, stats.NullCount["id"]);
        Assert.True(stats.TightBounds!.Value);
        Assert.Equal(DeltaStatKind.Long, stats.MinValues["id"].Kind);
    }

    [Fact]
    public void Byte_ComparesAsSignedTinyint()
    {
        StructType schema = Schema("b", DataTypes.ByteType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.ByteType, 4);
        foreach (byte raw in new byte[] { 0x00, 0xFF, 0x7F, 0x80 })
        {
            v.AppendValue(raw);
        }

        FileStatistics stats = Collect(schema, Batch(schema, v));

        // 0xFF => -1, 0x80 => -128, 0x7F => 127; signed range is [-128, 127].
        Assert.Equal(DeltaStatValue.OfLong(-128L), stats.MinValues["b"]);
        Assert.Equal(DeltaStatValue.OfLong(127L), stats.MaxValues["b"]);
    }

    [Fact]
    public void Short_And_Integer_StoreLongKind()
    {
        StructType shortSchema = Schema("s", DataTypes.ShortType);
        MutableColumnVector shorts = ColumnVectors.Create(DataTypes.ShortType, 3);
        foreach (short value in new short[] { 7, -3, 20 })
        {
            shorts.AppendValue(value);
        }

        FileStatistics shortStats = Collect(shortSchema, Batch(shortSchema, shorts));
        Assert.Equal(DeltaStatValue.OfLong(-3L), shortStats.MinValues["s"]);
        Assert.Equal(DeltaStatValue.OfLong(20L), shortStats.MaxValues["s"]);

        StructType intSchema = Schema("i", DataTypes.IntegerType);
        MutableColumnVector ints = ColumnVectors.Create(DataTypes.IntegerType, 3);
        foreach (int value in new[] { 100, -100, 0 })
        {
            ints.AppendValue(value);
        }

        FileStatistics intStats = Collect(intSchema, Batch(intSchema, ints));
        Assert.Equal(DeltaStatValue.OfLong(-100L), intStats.MinValues["i"]);
        Assert.Equal(DeltaStatValue.OfLong(100L), intStats.MaxValues["i"]);
    }

    [Fact]
    public void Boolean_MinIsFalseMaxIsTrue()
    {
        StructType schema = Schema("flag", DataTypes.BooleanType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.BooleanType, 3);
        v.AppendValue(true);
        v.AppendNull();
        v.AppendValue(false);

        FileStatistics stats = Collect(schema, Batch(schema, v));

        Assert.Equal(DeltaStatValue.OfBoolean(false), stats.MinValues["flag"]);
        Assert.Equal(DeltaStatValue.OfBoolean(true), stats.MaxValues["flag"]);
        Assert.Equal(1L, stats.NullCount["flag"]);
    }

    [Fact]
    public void Boolean_AllTrue_MinAndMaxAreTrue()
    {
        StructType schema = Schema("flag", DataTypes.BooleanType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.BooleanType, 2);
        v.AppendValue(true);
        v.AppendValue(true);

        FileStatistics stats = Collect(schema, Batch(schema, v));

        Assert.Equal(DeltaStatValue.OfBoolean(true), stats.MinValues["flag"]);
        Assert.Equal(DeltaStatValue.OfBoolean(true), stats.MaxValues["flag"]);
    }

    [Fact]
    public void Double_StoresDoubleKind_ExcludesNaN_CanonicalizesNegativeZero()
    {
        StructType schema = Schema("d", DataTypes.DoubleType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.DoubleType, 4);
        v.AppendValue(1.5d);
        v.AppendValue(double.NaN);
        v.AppendValue(3.0d);
        v.AppendValue(-0.0d);

        FileStatistics stats = Collect(schema, Batch(schema, v));

        // NaN is excluded from the bounds; -0.0 canonicalizes to +0.0 so the min encodes as "0".
        Assert.Equal(DeltaStatValue.OfDouble(0.0d), stats.MinValues["d"]);
        Assert.Equal(DeltaStatValue.OfDouble(3.0d), stats.MaxValues["d"]);
        Assert.Equal(DeltaStatKind.Double, stats.MinValues["d"].Kind);
        Assert.Equal("0", stats.MinValues["d"].Raw);
    }

    [Fact]
    public void Double_AllNaN_OmitsBounds_ButRecordsRecordCount()
    {
        StructType schema = Schema("d", DataTypes.DoubleType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.DoubleType, 2);
        v.AppendValue(double.NaN);
        v.AppendValue(double.NaN);

        FileStatistics stats = Collect(schema, Batch(schema, v));

        Assert.Equal(2L, stats.NumRecords);
        Assert.False(stats.MinValues.ContainsKey("d"));
        Assert.False(stats.MaxValues.ContainsKey("d"));
        Assert.Equal(0L, stats.NullCount["d"]);
    }

    [Fact]
    public void Double_Infinity_OmitsTheInfiniteBoundOnly()
    {
        StructType schema = Schema("d", DataTypes.DoubleType);
        MutableColumnVector positive = ColumnVectors.Create(DataTypes.DoubleType, 2);
        positive.AppendValue(1.0d);
        positive.AppendValue(double.PositiveInfinity);
        FileStatistics positiveStats = Collect(schema, Batch(schema, positive));
        Assert.Equal(DeltaStatValue.OfDouble(1.0d), positiveStats.MinValues["d"]);
        Assert.False(positiveStats.MaxValues.ContainsKey("d")); // +Infinity has no JSON-number bound.

        MutableColumnVector negative = ColumnVectors.Create(DataTypes.DoubleType, 2);
        negative.AppendValue(double.NegativeInfinity);
        negative.AppendValue(1.0d);
        FileStatistics negativeStats = Collect(schema, Batch(schema, negative));
        Assert.False(negativeStats.MinValues.ContainsKey("d"));
        Assert.Equal(DeltaStatValue.OfDouble(1.0d), negativeStats.MaxValues["d"]);
    }

    [Fact]
    public void Float_WidensToDoubleBound()
    {
        StructType schema = Schema("f", DataTypes.FloatType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.FloatType, 2);
        v.AppendValue(1.5f);
        v.AppendValue(2.5f);

        FileStatistics stats = Collect(schema, Batch(schema, v));

        Assert.Equal(DeltaStatValue.OfDouble(1.5d), stats.MinValues["f"]);
        Assert.Equal(DeltaStatValue.OfDouble(2.5d), stats.MaxValues["f"]);
    }

    [Fact]
    public void Date_And_Timestamp_StoreLaneValuesAsLong()
    {
        StructType dateSchema = Schema("d", DataTypes.DateType);
        MutableColumnVector dates = ColumnVectors.Create(DataTypes.DateType, 3);
        foreach (int epochDay in new[] { 0, 100, 50 })
        {
            dates.AppendValue(epochDay);
        }

        FileStatistics dateStats = Collect(dateSchema, Batch(dateSchema, dates));
        Assert.Equal(DeltaStatValue.OfLong(0L), dateStats.MinValues["d"]);
        Assert.Equal(DeltaStatValue.OfLong(100L), dateStats.MaxValues["d"]);

        StructType tsSchema = Schema("ts", DataTypes.TimestampType);
        MutableColumnVector timestamps = ColumnVectors.Create(DataTypes.TimestampType, 2);
        timestamps.AppendValue(1000L);
        timestamps.AppendValue(5L);

        FileStatistics tsStats = Collect(tsSchema, Batch(tsSchema, timestamps));
        Assert.Equal(DeltaStatValue.OfLong(5L), tsStats.MinValues["ts"]);
        Assert.Equal(DeltaStatValue.OfLong(1000L), tsStats.MaxValues["ts"]);
    }

    [Fact]
    public void Decimal_StoresNumericStringMinMax()
    {
        DataType dec = DataTypes.CreateDecimalType(10, 2);
        StructType schema = Schema("amount", dec);
        MutableColumnVector v = ColumnVectors.Create(dec, 3);
        foreach (long unscaled in new[] { 12345L, -678L, 0L })
        {
            v.AppendValue(unscaled);
        }

        FileStatistics stats = Collect(schema, Batch(schema, v));

        // Delta encodes decimal bounds as the literal numeric text, stored as a String stat.
        Assert.Equal(DeltaStatKind.String, stats.MinValues["amount"].Kind);
        Assert.Equal("-6.78", stats.MinValues["amount"].Raw);
        Assert.Equal("123.45", stats.MaxValues["amount"].Raw);
    }

    [Fact]
    public void String_ShortValues_AreTightAndNotTruncated()
    {
        StructType schema = Schema("name", DataTypes.StringType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 3);
        foreach (string text in new[] { "banana", "apple", "cherry" })
        {
            v.AppendBytes(System.Text.Encoding.UTF8.GetBytes(text));
        }

        FileStatistics stats = Collect(schema, Batch(schema, v));

        Assert.Equal(DeltaStatValue.OfString("apple"), stats.MinValues["name"]);
        Assert.Equal(DeltaStatValue.OfString("cherry"), stats.MaxValues["name"]);
        Assert.True(stats.TightBounds!.Value);
    }

    // ---------------------------------------------------------------- AC2: omit / bound rules

    [Fact]
    public void String_LongValue_IsTruncated_AndDisablesTightBounds()
    {
        var policy = new StatisticsPolicy(stringTruncationLength: 5);
        StructType schema = Schema("s", DataTypes.StringType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 2);
        v.AppendBytes(System.Text.Encoding.UTF8.GetBytes("abcdefgh"));
        v.AppendBytes(System.Text.Encoding.UTF8.GetBytes("zzzzzzzz"));

        FileStatistics stats = Collect(schema, Batch(schema, v), policy);

        Assert.Equal("abcde", stats.MinValues["s"].Raw); // prefix-truncated to 5 chars
        Assert.Equal("zzzzz", stats.MaxValues["s"].Raw);
        Assert.False(stats.TightBounds!.Value); // truncation forfeits tight bounds for the whole file
    }

    [Fact]
    public void StringTruncation_DoesNotSplitSurrogatePair()
    {
        var policy = new StatisticsPolicy(stringTruncationLength: 2);
        StructType schema = Schema("s", DataTypes.StringType);
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, 1);

        // "a" + U+1F600 (a surrogate pair): a naive 2-char cut would split the pair.
        v.AppendBytes(System.Text.Encoding.UTF8.GetBytes("a\U0001F600b"));

        FileStatistics stats = Collect(schema, Batch(schema, v), policy);

        Assert.Equal("a", stats.MinValues["s"].Raw); // pulled back before the high surrogate
        Assert.False(stats.TightBounds!.Value);
    }

    [Fact]
    public void AllNull_Column_RecordsNullCount_OmitsMinMax()
    {
        StructType schema = Schema("id", DataTypes.LongType);
        FileStatistics stats = Collect(schema, Batch(schema, Longs(null, null, null)));

        Assert.Equal(3L, stats.NumRecords);
        Assert.Equal(3L, stats.NullCount["id"]);
        Assert.False(stats.MinValues.ContainsKey("id"));
        Assert.False(stats.MaxValues.ContainsKey("id"));
    }

    [Fact]
    public void EmptyBatch_RecordsZeroCounts_NoBounds()
    {
        StructType schema = Schema("id", DataTypes.LongType);
        ColumnBatch empty = new ManagedColumnBatch(schema, new ColumnVector[] { Longs() }, rowCount: 0);

        FileStatistics stats = ParquetStatisticsCollector.Collect(schema, new[] { empty }, StatisticsPolicy.Default);

        Assert.Equal(0L, stats.NumRecords);
        Assert.Equal(0L, stats.NullCount["id"]);
        Assert.False(stats.MinValues.ContainsKey("id"));
    }

    [Fact]
    public void Binary_Column_IsOmittedFromStatisticsEntirely()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("blob", DataTypes.BinaryType, nullable: true),
        });
        MutableColumnVector id = Longs(1L, 2L);
        MutableColumnVector blob = ColumnVectors.Create(DataTypes.BinaryType, 2);
        blob.AppendBytes(new byte[] { 1, 2, 3 });
        blob.AppendNull();

        FileStatistics stats = ParquetStatisticsCollector.Collect(
            schema, new[] { new ManagedColumnBatch(schema, new ColumnVector[] { id, blob }, 2) }, StatisticsPolicy.Default);

        Assert.Equal(2L, stats.NumRecords);
        Assert.True(stats.MinValues.ContainsKey("id"));
        Assert.False(stats.MinValues.ContainsKey("blob"));
        Assert.False(stats.NullCount.ContainsKey("blob")); // binary omitted: no nullCount either
    }

    [Fact]
    public void IndexingHorizon_OmitsColumnsBeyondTheLimit()
    {
        var policy = new StatisticsPolicy(maxIndexedColumns: 1);
        var schema = new StructType(new[]
        {
            new StructField("a", DataTypes.LongType, nullable: false),
            new StructField("b", DataTypes.LongType, nullable: false),
        });
        MutableColumnVector a = Longs(1L, 2L);
        MutableColumnVector b = Longs(3L, 4L);

        FileStatistics stats = ParquetStatisticsCollector.Collect(
            schema, new[] { new ManagedColumnBatch(schema, new ColumnVector[] { a, b }, 2) }, policy);

        Assert.True(stats.MinValues.ContainsKey("a"));
        Assert.False(stats.MinValues.ContainsKey("b")); // beyond the horizon
        Assert.False(stats.NullCount.ContainsKey("b"));
    }

    [Fact]
    public void RecordCount_SumsAcrossBatches()
    {
        StructType schema = Schema("id", DataTypes.LongType);
        var batches = new[]
        {
            Batch(schema, Longs(1L, 2L)),
            Batch(schema, Longs(3L, 4L, 5L)),
        };

        FileStatistics stats = ParquetStatisticsCollector.Collect(schema, batches, StatisticsPolicy.Default);

        Assert.Equal(5L, stats.NumRecords);
        Assert.Equal(DeltaStatValue.OfLong(1L), stats.MinValues["id"]);
        Assert.Equal(DeltaStatValue.OfLong(5L), stats.MaxValues["id"]);
    }

    [Fact]
    public void Selection_ReflectsOnlySelectedRows()
    {
        StructType schema = Schema("id", DataTypes.LongType);
        MutableColumnVector v = Longs(10L, 999L, 20L);
        ColumnBatch selected = new ManagedColumnBatch(schema, new ColumnVector[] { v }, 3)
            .WithSelection(new SelectionVector(new[] { 0, 2 }));

        FileStatistics stats = ParquetStatisticsCollector.Collect(schema, new[] { selected }, StatisticsPolicy.Default);

        // The unselected 999 must not affect the bounds or the record count.
        Assert.Equal(2L, stats.NumRecords);
        Assert.Equal(DeltaStatValue.OfLong(10L), stats.MinValues["id"]);
        Assert.Equal(DeltaStatValue.OfLong(20L), stats.MaxValues["id"]);
    }

    // ---------------------------------------------------------------- StatisticsPolicy

    [Fact]
    public void Policy_SupportsScalars_OmitsNestedBinaryVoidAndWideDecimal()
    {
        StatisticsPolicy policy = StatisticsPolicy.Default;

        Assert.True(policy.IsSupportedForStatistics(DataTypes.LongType));
        Assert.True(policy.IsSupportedForStatistics(DataTypes.StringType));
        Assert.True(policy.IsSupportedForStatistics(DataTypes.TimestampType));
        Assert.True(policy.IsSupportedForStatistics(DataTypes.CreateDecimalType(28, 2)));

        Assert.False(policy.IsSupportedForStatistics(DataTypes.BinaryType));
        Assert.False(policy.IsSupportedForStatistics(DataTypes.NullType));
        Assert.False(policy.IsSupportedForStatistics(DataTypes.CreateDecimalType(38, 2))); // beyond writable precision
        Assert.False(policy.IsSupportedForStatistics(DataTypes.CreateArrayType(DataTypes.LongType)));
        Assert.False(policy.IsSupportedForStatistics(
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType)));
        Assert.False(policy.IsSupportedForStatistics(
            new StructType(new[] { new StructField("x", DataTypes.LongType) })));
    }

    [Fact]
    public void Policy_RejectsNonPositiveHorizons()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatisticsPolicy(stringTruncationLength: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatisticsPolicy(maxIndexedColumns: 0));
    }
}
