using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class MapColumnVectorTests
{
    private static readonly MapType StringToInt = new(StringType.Instance, IntegerType.Instance);

    private static MutableColumnVector Ints(params int[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, Math.Max(values.Length, 1));
        foreach (int value in values)
        {
            v.AppendValue(value);
        }

        return v;
    }

    private static MutableColumnVector Keys(params string[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, Math.Max(values.Length, 1));
        foreach (string value in values)
        {
            v.AppendBytes(Encoding.UTF8.GetBytes(value));
        }

        return v;
    }

    private static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    // A 4-row map backed by parallel children keys=[k0,k1,k2,k3], values=[0,1,2,3]:
    //   row 0 -> {k0:0, k1:1}  (offsets 0..2)
    //   row 1 -> {}            (offsets 2..2, EMPTY map)
    //   row 2 -> {}            (offsets 2..2, marked NULL)
    //   row 3 -> {k2:2, k3:3}  (offsets 2..4)
    private static MapColumnVector Sample() =>
        new(
            StringToInt,
            Keys("k0", "k1", "k2", "k3"),
            Ints(0, 1, 2, 3),
            new[] { 0, 2, 2, 2, 4 },
            nulls: new[] { false, false, true, false });

    [Fact]
    public void FromChildrenAndOffsets_ExposesPerRowEntriesAndOffsets()
    {
        MapColumnVector map = Sample();

        Assert.Equal(StringToInt, map.Type);
        Assert.Equal(4, map.Length);
        Assert.Equal(0, map.Offset);

        Assert.Equal(2, map.EntryLength(0));
        Assert.Equal(0, map.EntryLength(1));
        Assert.Equal(0, map.EntryLength(2));
        Assert.Equal(2, map.EntryLength(3));

        // Row 0 entries (keys pair positionally with values).
        ColumnVector k0 = map.KeysAt(0);
        ColumnVector v0 = map.ValuesAt(0);
        Assert.Equal("k0", Utf8(k0.GetBytes(0)));
        Assert.Equal("k1", Utf8(k0.GetBytes(1)));
        Assert.Equal(0, v0.GetValue<int>(0));
        Assert.Equal(1, v0.GetValue<int>(1));

        // Row 3 entries — mutation-sensitive on the offset re-basing of a later row.
        Assert.Equal("k2", Utf8(map.KeysAt(3).GetBytes(0)));
        Assert.Equal("k3", Utf8(map.KeysAt(3).GetBytes(1)));
        Assert.Equal(3, map.ValuesAt(3).GetValue<int>(1));

        // Flattened children.
        Assert.Equal(4, map.Keys.Length);
        Assert.Equal(4, map.Values.Length);
        Assert.Equal("k2", Utf8(map.Keys.GetBytes(2)));
    }

    [Fact]
    public void NullMap_IsDistinctFromEmptyMap()
    {
        MapColumnVector map = Sample();

        Assert.True(map.HasNulls);
        Assert.Equal(1, map.NullCount);

        Assert.Equal(0, map.EntryLength(1));
        Assert.Equal(0, map.EntryLength(2));
        Assert.False(map.IsNull(1)); // empty map
        Assert.True(map.IsNull(2)); // null map
    }

    [Fact]
    public void Builder_AppendsEntriesThenClosesRows()
    {
        var map = new MapColumnVector(StringToInt, capacity: 4);
        var keys = (MutableColumnVector)map.Keys;
        var values = (MutableColumnVector)map.Values;

        keys.AppendBytes("k0"u8);
        values.AppendValue(0);
        keys.AppendBytes("k1"u8);
        values.AppendValue(1);
        map.EndMap(); // row 0 -> {k0:0, k1:1}

        map.EndMap(); // row 1 -> {} empty

        map.AppendNull(); // row 2 -> null map

        keys.AppendBytes("k2"u8);
        values.AppendValue(2);
        keys.AppendBytes("k3"u8);
        values.AppendValue(3);
        map.EndMap(); // row 3 -> {k2:2, k3:3}

        Assert.Equal(4, map.Length);
        Assert.Equal(1, map.NullCount);
        Assert.Equal(2, map.EntryLength(0));
        Assert.Equal(0, map.EntryLength(1));
        Assert.False(map.IsNull(1));
        Assert.True(map.IsNull(2));
        Assert.Equal(2, map.EntryLength(3));
        Assert.Equal("k3", Utf8(map.KeysAt(3).GetBytes(1)));
        Assert.Equal(3, map.ValuesAt(3).GetValue<int>(1));
    }

    [Fact]
    public void Commit_RejectsUnequalKeyAndValueCounts()
    {
        var map = new MapColumnVector(StringToInt, capacity: 2);
        var keys = (MutableColumnVector)map.Keys;

        keys.AppendBytes("k"u8); // a key with no value
        Assert.Throws<InvalidOperationException>(() => map.EndMap());
        Assert.Equal(0, map.Length);
    }

    [Fact]
    public void Slice_ReBasesRowsEntriesAndValidity()
    {
        MapColumnVector map = Sample();
        ColumnVector slice = map.Slice(1, 3); // parent rows 1,2,3 -> logical 0,1,2
        var sl = Assert.IsType<MapColumnVector>(slice);

        Assert.Equal(3, sl.Length);
        Assert.Equal(1, sl.Offset);
        Assert.Equal(1, sl.NullCount);

        Assert.Equal(0, sl.EntryLength(0)); // parent row 1 (empty)
        Assert.Equal(0, sl.EntryLength(1)); // parent row 2 (null)
        Assert.Equal(2, sl.EntryLength(2)); // parent row 3

        Assert.False(sl.IsNull(0)); // empty
        Assert.True(sl.IsNull(1)); // null

        // Re-basing works: slice row 2's entries are the parent row 3's entries.
        Assert.Equal("k2", Utf8(sl.KeysAt(2).GetBytes(0)));
        Assert.Equal("k3", Utf8(sl.KeysAt(2).GetBytes(1)));
        Assert.Equal(2, sl.ValuesAt(2).GetValue<int>(0));
    }

    [Fact]
    public void Select_ThrowsNotSupportedNamingTheMap()
    {
        MapColumnVector map = Sample();
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => map.Select(new SelectionVector(new[] { 3, 0 })));
        Assert.Contains("map", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScalarAccessors_AreUnavailable()
    {
        MapColumnVector map = Sample();
        Assert.Throws<InvalidOperationException>(() => map.GetValues<int>().Length);
        Assert.Throws<InvalidOperationException>(() => map.GetBytes(0));
    }

    [Fact]
    public void FromChildrenAndOffsets_RejectsInconsistentInputs()
    {
        // Key/value children not parallel.
        Assert.Throws<ArgumentException>(() =>
            new MapColumnVector(StringToInt, Keys("a", "b"), Ints(1), new[] { 0, 1 }));

        // Key type mismatch (declared string, supplied int).
        Assert.Throws<ArgumentException>(() =>
            new MapColumnVector(StringToInt, Ints(1), Ints(1), new[] { 0, 1 }));

        // Offsets exceed the entry count.
        Assert.Throws<ArgumentException>(() =>
            new MapColumnVector(StringToInt, Keys("a"), Ints(1), new[] { 0, 5 }));
    }

    [Fact]
    public void PerRowAccessors_RejectOutOfRange()
    {
        MapColumnVector map = Sample();
        Assert.Throws<ArgumentOutOfRangeException>(() => map.EntryLength(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.KeysAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ValuesAt(4));
    }

    [Fact]
    public void FromChildren_RejectsValueTypeMismatch()
    {
        // The value child's type must match the declared value type. Keys are correct (string); the value
        // child is string where int is declared -> fail-closed (the mirror of the key-type guard).
        Assert.Throws<ArgumentException>(() =>
            new MapColumnVector(StringToInt, Keys("k"), Keys("v"), new[] { 0, 1 }));
    }

    [Fact]
    public void Constructor_RejectsNegativeCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapColumnVector(StringToInt, capacity: -1));

    [Fact]
    public void EndMap_RejectsMoreValuesThanKeys()
    {
        // Per-row parallelism: keys and values must advance equally. One extra value -> fail-closed.
        var map = new MapColumnVector(StringToInt, capacity: 4);
        var keys = (MutableColumnVector)map.Keys;
        var values = (MutableColumnVector)map.Values;
        keys.AppendBytes("k"u8);
        values.AppendValue(1);
        values.AppendValue(2);
        Assert.Throws<InvalidOperationException>(() => map.EndMap());
    }

    [Fact]
    public void EndMap_AfterSlice_IsRejected()
    {
        var map = new MapColumnVector(StringToInt, capacity: 4);
        var keys = (MutableColumnVector)map.Keys;
        var values = (MutableColumnVector)map.Values;
        keys.AppendBytes("k"u8);
        values.AppendValue(1);
        map.EndMap();
        _ = map.Slice(0, 1);
        keys.AppendBytes("k2"u8);
        values.AppendValue(2);
        Assert.Throws<InvalidOperationException>(() => map.EndMap());
    }

    [Fact]
    public void Slice_OverflowingRange_IsRejected()
    {
        var empty = new MapColumnVector(StringToInt, capacity: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Slice(int.MaxValue, 1));
    }

    [Fact]
    public void FromChildren_RejectsNullKey()
    {
        // MapType keys are always non-null; a null in the key child must fail-closed at the wrap ctor.
        MutableColumnVector nullableKeys = ColumnVectors.Create(StringType.Instance, 1);
        nullableKeys.AppendNull();
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            new MapColumnVector(StringToInt, nullableKeys, Ints(1), new[] { 0, 1 }));
        Assert.Contains("keys must not be null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EndMap_RejectsNullKey()
    {
        var map = new MapColumnVector(StringToInt, capacity: 2);
        var keys = (MutableColumnVector)map.Keys;
        var values = (MutableColumnVector)map.Values;
        keys.AppendNull();
        values.AppendValue(1);
        Assert.Throws<InvalidOperationException>(() => map.EndMap());
    }

    [Fact]
    public void NullMapRow_WithMaskedEntries_ReadsEmpty()
    {
        // A wrap-ctor null map row whose offsets physically span entries must surface as EMPTY (masked
        // entries never leak); IsNull distinguishes it from an empty map.
        var map = new MapColumnVector(StringToInt, Keys("a", "b"), Ints(1, 2), new[] { 0, 2 }, new[] { true });
        Assert.True(map.IsNull(0));
        Assert.Equal(0, map.EntryLength(0));
        Assert.Equal(0, map.KeysAt(0).Length);
        Assert.Equal(0, map.ValuesAt(0).Length);
    }
}
