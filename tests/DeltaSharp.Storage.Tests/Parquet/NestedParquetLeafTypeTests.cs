using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Per-LEAF-TYPE parity for the nested write path (§2.3b): every physical lane the shredder's closed
/// <c>switch</c> can select is round-tripped through the real #571 read path in BOTH nested positions
/// (repeated element and non-repeated struct child).
/// </summary>
/// <remarks>
/// The shredder maps a DeltaSharp leaf type onto a closed <c>WriteAllPartsAsync&lt;T&gt;</c> instantiation by
/// hand (no <c>MakeGenericMethod</c>, for AOT). A wrong physical T is the classic silent-corruption failure
/// mode there: the write succeeds, the footer is well formed, and only the decoded VALUES are wrong. These
/// tests therefore compare values, not just shapes — and they run each type through the two positions with
/// different maximum definition levels, so a lane that only works when the leaf is the whole column is caught.
/// </remarks>
public sealed class NestedParquetLeafTypeTests
{
    public sealed record LeafCase(
        string Name,
        DataType Type,
        Action<MutableColumnVector, int> Append,
        Func<ColumnVector, int, object> Read,
        int ValueCount)
    {
        public override string ToString() => Name;
    }

    private static LeafCase Fixed<T>(string name, DataType type, Func<int, T> value, int count = 4)
        where T : unmanaged =>
        new(name, type,
            (vector, i) => vector.AppendValue(value(i)),
            (vector, i) => vector.GetValue<T>(i),
            count);

    public static TheoryData<LeafCase> LeafCases() => new()
    {
        Fixed("boolean", DataTypes.BooleanType, i => i % 2 == 0),
        Fixed("byte", DataTypes.ByteType, i => (byte)(i * 60)),
        Fixed("short", DataTypes.ShortType, i => (short)(i * 9_000 - 18_000)),
        Fixed("integer", DataTypes.IntegerType, i => i == 0 ? int.MinValue : i * 1_000_003),
        Fixed("long", DataTypes.LongType, i => i == 0 ? long.MinValue : i * 1_000_000_007L),
        Fixed("float", DataTypes.FloatType, i => i * 0.5f),
        Fixed("double", DataTypes.DoubleType, i => i * 0.25d),

        // IEEE-754 boundary lane: NaN, both infinities and NEGATIVE zero all have bit patterns a
        // "copy the payload" shredder preserves and a "normalize through a comparison" one silently loses.
        Fixed("byte-boundaries", DataTypes.ByteType, i => new byte[] { 0, 127, 128, 255 }[i]),
        Fixed("float-boundaries", DataTypes.FloatType,
            i => new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.0f }[i]),
        Fixed("double-boundaries", DataTypes.DoubleType,
            i => new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0d }[i]),

        // DateTime.MinValue / MaxValue at both DATE and TIMESTAMP resolution: the epoch conversion the
        // shredder applies on the way out has to be exactly inverted on the way back at the representable
        // extremes, where an off-by-one or an overflow is otherwise invisible.
        Fixed("date-extremes", DataTypes.DateType,
            i => new[] { MinEpochDay, MaxEpochDay, 0, -1 }[i]),
        Fixed("timestamp-extremes", DataTypes.TimestampType,
            i => new[] { MinEpochMicros, MaxEpochMicros, 0L, -1L }[i]),
        Fixed("timestamp-ntz-extremes", DataTypes.TimestampNtzType,
            i => new[] { MinEpochMicros, MaxEpochMicros, 0L, -1L }[i]),

        // Decimal boundaries: the NON-compact (Int128-backed) lane at the maximum supported precision, and
        // scale == precision (an all-fractional decimal, the branch where the scale factor is largest).
        new("decimal-precision-28", DataTypes.CreateDecimalType(28, 6),
            (vector, i) => vector.AppendValue(
                new[] { Int128.Parse("1234567890123456789012345678"), -Int128.Parse("1234567890123456789012345678"), Int128.Zero, Int128.One }[i]),
            (vector, i) => ParquetTypeMapping.ReadDecimal(
                vector, (DecimalType)DataTypes.CreateDecimalType(28, 6), i),
            4),
        new("decimal-scale-equals-precision", DataTypes.CreateDecimalType(10, 10),
            (vector, i) => vector.AppendValue(new[] { 9_999_999_999L, -9_999_999_999L, 0L, 1L }[i]),
            (vector, i) => ParquetTypeMapping.ReadDecimal(
                vector, (DecimalType)DataTypes.CreateDecimalType(10, 10), i),
            4),

        // DATE / TIMESTAMP / TIMESTAMP_NTZ are stored as epoch int/long and must survive the epoch
        // conversion the shredder applies on the way out and the reader applies on the way back in.
        Fixed("date", DataTypes.DateType, i => 19_000 + i),
        Fixed("date-epoch", DataTypes.DateType, i => i == 0 ? 0 : -i),
        Fixed("timestamp", DataTypes.TimestampType, i => 1_700_000_000_000_000L + (i * 1_000L)),
        Fixed("timestamp-ntz", DataTypes.TimestampNtzType, i => 1_700_000_000_000_123L + i),

        new("decimal", DataTypes.CreateDecimalType(18, 4),
            (vector, i) => vector.AppendValue(i == 0 ? -1_234_567L : 1_000L * (i + 1)),
            (vector, i) => ParquetTypeMapping.ReadDecimal(
                vector, (DecimalType)DataTypes.CreateDecimalType(18, 4), i),
            4),

        // Byte 0 / 127 / 128 / 255 (§3): the transcode lane must not be ASCII- or NUL-terminated, and a
        // continuation byte must not be truncated. Empty vs null is the other classic conflation.
        new("string", DataTypes.StringType,
            (vector, i) => vector.AppendBytes(StringPayload(i)),
            (vector, i) => Encoding.UTF8.GetString(vector.GetBytes(i)),
            StringPayloads.Length),
        new("binary", DataTypes.BinaryType,
            (vector, i) => vector.AppendBytes(BinaryPayloads[i]),
            (vector, i) => Convert.ToHexString(vector.GetBytes(i)),
            BinaryPayloads.Length),
    };

    private static readonly byte[][] StringPayloads =
    [
        Encoding.UTF8.GetBytes("plain"),
        [],
        Encoding.UTF8.GetBytes("\u0000\u007fmid\u0080\u00ff"),
        Encoding.UTF8.GetBytes("\ud83d\ude00 emoji \u00e9\u4e2d"),
        Encoding.UTF8.GetBytes(new string('x', 300)),
    ];

    private static readonly byte[][] BinaryPayloads =
    [
        [0x00, 0x7f, 0x80, 0xff],
        [],
        [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
        [0x01],
    ];

    private static byte[] StringPayload(int index) => StringPayloads[index];

    private const long TicksPerMicrosecond = 10L;

    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    private static readonly int MinEpochDay =
        (int)((DateTime.MinValue.Ticks - UnixEpochTicks) / TimeSpan.TicksPerDay);

    private static readonly int MaxEpochDay =
        (int)((DateTime.MaxValue.Date.Ticks - UnixEpochTicks) / TimeSpan.TicksPerDay);

    private static readonly long MinEpochMicros =
        (DateTime.MinValue.Ticks - UnixEpochTicks) / TicksPerMicrosecond;

    private static readonly long MaxEpochMicros =
        (DateTime.MaxValue.Ticks - UnixEpochTicks) / TicksPerMicrosecond;

    [Theory]
    [MemberData(nameof(LeafCases))]
    public async Task Array_RoundTripsEveryLeafTypeAndItsNullLanes(LeafCase leaf)
    {
        // Rows: [v0, null, v1] / null / [] / [v2 … vN-1] — every list Dremel branch crossed with the leaf's
        // own null lane, so a physical-T defect and a level defect cannot cancel out.
        var type = DataTypes.CreateArrayType(leaf.Type);
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });

        MutableColumnVector elements = ColumnVectors.Create(leaf.Type, leaf.ValueCount + 2);
        var expected = new List<object?>();
        leaf.Append(elements, 0);
        expected.Add(leaf.Read(elements, 0));
        elements.AppendNull();
        expected.Add(null);
        for (int i = 1; i < leaf.ValueCount; i++)
        {
            leaf.Append(elements, i);
            expected.Add(leaf.Read(elements, elements.Length - 1));
        }

        int[] offsets = [0, 3, 3, 3, leaf.ValueCount + 1];
        bool[] nulls = [false, true, false, false];
        var vector = new ListColumnVector(type, elements, offsets, nulls);

        ColumnBatch decoded = await WriteThenReadAsync(schema, vector);
        var actual = (ListColumnVector)decoded.Column(0);

        Assert.Equal(4, actual.Length);
        Assert.False(actual.IsNull(0));
        Assert.True(actual.IsNull(1));
        Assert.False(actual.IsNull(2));
        Assert.Equal(0, actual.ElementsAt(2).Length);

        var flattened = new List<object?>();
        foreach (int row in new[] { 0, 3 })
        {
            ColumnVector row_ = actual.ElementsAt(row);
            for (int e = 0; e < row_.Length; e++)
            {
                flattened.Add(row_.IsNull(e) ? null : leaf.Read(row_, e));
            }
        }

        Assert.Equal(expected, flattened);
    }

    [Theory]
    [MemberData(nameof(LeafCases))]
    public async Task Struct_RoundTripsEveryLeafTypeAndItsNullLanes(LeafCase leaf)
    {
        // The same lane at maxDefinitionLevel 2 with NO repetition stream: the struct position exercises the
        // non-repeated branch of both the shredder and the guard.
        var inner = DataTypes.CreateStructType(new[] { new StructField("v", leaf.Type, nullable: true) });
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", inner, nullable: true) });

        int rows = leaf.ValueCount + 2;
        MutableColumnVector child = ColumnVectors.Create(leaf.Type, rows);
        var expected = new List<object?>();
        for (int i = 0; i < leaf.ValueCount; i++)
        {
            leaf.Append(child, i);
            expected.Add(leaf.Read(child, i));
        }

        child.AppendNull();
        expected.Add(null);
        child.AppendNull();
        expected.Add(null);

        var nulls = new bool[rows];
        nulls[^1] = true;
        var vector = new StructColumnVector(inner, new ColumnVector[] { child }, nulls);

        ColumnBatch decoded = await WriteThenReadAsync(schema, vector);
        var actual = (StructColumnVector)decoded.Column(0);
        ColumnVector actualChild = actual.Child(0);

        Assert.Equal(rows, actual.Length);
        Assert.True(actual.IsNull(rows - 1));
        for (int i = 0; i < rows - 1; i++)
        {
            Assert.False(actual.IsNull(i));
            Assert.Equal(expected[i], actualChild.IsNull(i) ? null : leaf.Read(actualChild, i));
        }
    }

    [Theory]
    [MemberData(nameof(LeafCases))]
    public async Task Map_RoundTripsEveryLeafTypeInTheValueLane(LeafCase leaf)
    {
        // Keys stay string (the required lane); the value lane carries the type under test, including a null
        // value in a present entry — the branch that separates "absent entry" from "entry with a null value".
        var type = DataTypes.CreateMapType(DataTypes.StringType, leaf.Type);
        var schema = DataTypes.CreateStructType(new[] { new StructField("m", type, nullable: true) });

        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, leaf.ValueCount + 1);
        MutableColumnVector values = ColumnVectors.Create(leaf.Type, leaf.ValueCount + 1);
        var expected = new List<object?>();
        for (int i = 0; i < leaf.ValueCount; i++)
        {
            keys.AppendBytes(Encoding.UTF8.GetBytes($"k{i}"));
            leaf.Append(values, i);
            expected.Add(leaf.Read(values, i));
        }

        keys.AppendBytes(Encoding.UTF8.GetBytes("null-value"));
        values.AppendNull();
        expected.Add(null);

        int[] offsets = [0, leaf.ValueCount + 1, leaf.ValueCount + 1, leaf.ValueCount + 1];
        bool[] nulls = [false, true, false];
        var vector = new MapColumnVector(type, keys, values, offsets, nulls);

        ColumnBatch decoded = await WriteThenReadAsync(schema, vector);
        var actual = (MapColumnVector)decoded.Column(0);

        Assert.Equal(3, actual.Length);
        Assert.True(actual.IsNull(1));
        Assert.Equal(0, actual.KeysAt(2).Length);

        ColumnVector actualKeys = actual.KeysAt(0);
        ColumnVector actualValues = actual.ValuesAt(0);
        Assert.Equal(expected.Count, actualKeys.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(
                i == expected.Count - 1 ? "null-value" : $"k{i}",
                Encoding.UTF8.GetString(actualKeys.GetBytes(i)));
            Assert.Equal(expected[i], actualValues.IsNull(i) ? null : leaf.Read(actualValues, i));
        }
    }

    private static async Task<ColumnBatch> WriteThenReadAsync(StructType schema, ColumnVector column)
    {
        var batch = new ManagedColumnBatch(schema, new[] { column }, column.Length);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        List<ColumnBatch> decoded = await ParquetTestHelpers.ReadAllAsync(bytes, schema);
        return Assert.Single(decoded);
    }
}
