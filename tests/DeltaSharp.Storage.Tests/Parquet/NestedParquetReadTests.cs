using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Parquet.Serialization;
using Parquet.Serialization.Attributes;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Read-path decode tests for the three single-level nested Parquet shapes (#571): struct-of-scalar,
/// array-of-scalar, and map(scalar → scalar). Real nested Parquet is authored with Parquet.Net's typed
/// <see cref="ParquetSerializer"/> (which emits the standard Dremel 3-level shapes) — the DeltaSharp
/// <c>ParquetFileWriter</c> is still scalar-only — then decoded through <see cref="ParquetFileReader"/>
/// into the #570 nested column vectors. Each case asserts values <b>and</b> the null mask at every level:
/// a null struct vs a null field, a null list vs an empty list vs a null element, a null map vs an empty
/// map vs a null value. Out-of-scope nested shapes must fail closed (never a silent/partial read).
/// </summary>
public sealed class NestedParquetReadTests
{
    private sealed class Inner
    {
        public int A { get; set; }

        public string? B { get; set; }
    }

    private sealed class StructRow
    {
        public int Id { get; set; }

        public Inner? S { get; set; }
    }

    private sealed class Wide
    {
        public long L { get; set; }

        public double D { get; set; }

        public bool Flag { get; set; }
    }

    private sealed class WideRow
    {
        public int Id { get; set; }

        public Wide? W { get; set; }
    }

    private sealed class TwoStructRow
    {
        public int Id { get; set; }

        public Wide? A { get; set; }

        public Wide? B { get; set; }
    }

    private sealed class ListRow
    {
        public int Id { get; set; }

        public List<int?>? Arr { get; set; }
    }

    private sealed class StrListRow
    {
        public int Id { get; set; }

        public List<string?>? Names { get; set; }
    }

    private sealed class MapRow
    {
        public int Id { get; set; }

        public Dictionary<string, int?>? M { get; set; }
    }

    private sealed class StrMapRow
    {
        public int Id { get; set; }

        public Dictionary<string, string?>? Sm { get; set; }
    }

    // ----- #546 nested type-widening promotion fixtures (narrow physical → wide requested) -----

    private sealed class FloatListRow
    {
        public int Id { get; set; }

        public List<float?>? Arr { get; set; }
    }

    private sealed class LongListRow
    {
        public int Id { get; set; }

        public List<long?>? Arr { get; set; }
    }

    private sealed class IntKeyMapRow
    {
        public int Id { get; set; }

        public Dictionary<int, string?>? M { get; set; }
    }

    // A DATE list element (DateOnly → Parquet DATE physical) for the #546 date→timestamp_ntz promotion (O2).
    private sealed class DateListRow
    {
        public int Id { get; set; }

        public List<DateOnly?>? Arr { get; set; }
    }

    // A TIMESTAMP list element (DateTime → Parquet micros TIMESTAMP) for the micros-identity (non-promotion)
    // companion of the #546 date→timestamp_ntz promotion (O2).
    private sealed class TimestampListRow
    {
        public int Id { get; set; }

        public List<DateTime?>? Arr { get; set; }
    }

    // BINARY leaves in all three nested positions (#832 M5b): the nested leaf physical-type check has a
    // dedicated BinaryType arm that no nested test exercised, so nested binary could have been broken (or the
    // arm deleted) without a single failure. Mirrors the string-leaf row shapes above.
    private sealed class BinaryInner
    {
        public int A { get; set; }

        public byte[]? Bin { get; set; }
    }

    private sealed class BinaryStructRow
    {
        public int Id { get; set; }

        public BinaryInner? S { get; set; }
    }

    private sealed class BinaryListRow
    {
        public int Id { get; set; }

        public List<byte[]?>? Blobs { get; set; }
    }

    private sealed class BinaryMapRow
    {
        public int Id { get; set; }

        public Dictionary<string, byte[]?>? Bm { get; set; }
    }

    // A file column that is array<struct> (a nested type within a nested type), for the A8 decode-path guard.
    private sealed class NestedListRow
    {
        public int Id { get; set; }

        public List<Inner>? Items { get; set; }
    }

    private sealed class ArrayOfArrayRow
    {
        public int Id { get; set; }

        public List<List<int?>?>? Outer { get; set; }
    }

    private sealed class SoA
    {
        public List<int?>? Xs { get; set; }
    }

    private sealed class SoARow
    {
        public int Id { get; set; }

        public SoA? S { get; set; }
    }

    private sealed class Mid
    {
        public Inner? Deep { get; set; }
    }

    private sealed class SoSRow
    {
        public int Id { get; set; }

        public Mid? M { get; set; }
    }

    private sealed class MapOfStructRow
    {
        public int Id { get; set; }

        public Dictionary<string, Inner?>? M { get; set; }
    }

    private sealed class MapOfMapRow
    {
        public int Id { get; set; }

        public Dictionary<string, Dictionary<string, int?>?>? M { get; set; }
    }

    private sealed class ArrayOfMapRow
    {
        public int Id { get; set; }

        public List<Dictionary<string, int?>?>? Arr { get; set; }
    }

    private sealed class MapOfArrayRow
    {
        public int Id { get; set; }

        public Dictionary<string, List<int?>?>? M { get; set; }
    }

    private sealed class StructWithArray
    {
        public List<int?>? Xs { get; set; }
    }

    private sealed class ArrayOfStructOfArrayRow
    {
        public int Id { get; set; }

        public List<StructWithArray>? Arr { get; set; }
    }

    // array<struct<long,double,bool>>: exercises MULTIPLE distinct scalar physical leaf types at depth 2 under
    // a repeated ancestor (the "all-scalar-leaves-at-depth" §3.1 cell).
    private sealed class ArrayOfWideRow
    {
        public int Id { get; set; }

        public List<Wide>? Arr { get; set; }
    }

    private sealed class StructWithMap
    {
        public Dictionary<string, int?>? M { get; set; }
    }

    private sealed class MapOfStructOfMapRow
    {
        public int Id { get; set; }

        public Dictionary<string, StructWithMap?>? M { get; set; }
    }

    // Recursive canonical description of a nested cell — values AND null-structure. null cell => "null";
    // list => "[e0,e1,...]"; map => "{k0=v0,...}" (entries sorted by rendered key so ordering is not asserted);
    // struct => "(Name=..,Name=..)"; scalar => its literal. Used by the 585a round-trip cells to assert an
    // EXACT value+structure identity across arbitrary depth.
    private static string Describe(ColumnVector v, int index)
    {
        if (v.IsNull(index))
        {
            return "null";
        }

        switch (v)
        {
            case ListColumnVector list:
                {
                    (int start, int len) = list.RawElementSpan(index);
                    var parts = new List<string>(len);
                    for (int j = 0; j < len; j++)
                    {
                        parts.Add(Describe(list.Elements, start + j));
                    }

                    return "[" + string.Join(",", parts) + "]";
                }

            case MapColumnVector map:
                {
                    (int start, int len) = map.RawEntrySpan(index);
                    ColumnVector keys = map.Keys;
                    ColumnVector values = map.Values;
                    var parts = new List<string>(len);
                    for (int j = 0; j < len; j++)
                    {
                        parts.Add(Describe(keys, start + j) + "=" + Describe(values, start + j));
                    }

                    parts.Sort(StringComparer.Ordinal);
                    return "{" + string.Join(",", parts) + "}";
                }

            case StructColumnVector st:
                {
                    var structType = (StructType)st.Type;
                    var parts = new List<string>(st.FieldCount);
                    for (int f = 0; f < st.FieldCount; f++)
                    {
                        parts.Add(structType[f].Name + "=" + Describe(st.Child(f), index));
                    }

                    return "(" + string.Join(",", parts) + ")";
                }

            default:
                return v.Type switch
                {
                    IntegerType => v.GetValue<int>(index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    LongType => v.GetValue<long>(index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringType => Utf8(v, index),
                    BooleanType => v.GetValue<bool>(index) ? "true" : "false",
                    DoubleType => v.GetValue<double>(index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FloatType => v.GetValue<float>(index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => "<" + v.Type.SimpleString + ">",
                };
        }
    }

    // All-nullable struct fields, so a PRESENT struct can have every field null — distinct from a NULL struct.
    private sealed class AllNullableInner
    {
        public int? A { get; set; }

        public string? B { get; set; }
    }

    private sealed class AllNullableRow
    {
        public int Id { get; set; }

        public AllNullableInner? S { get; set; }
    }

    // DATE / TIMESTAMP / DECIMAL leaves inside a struct (highest-value untested nested-leaf conversions).
    private sealed class DateInner
    {
        public DateOnly D { get; set; }

        public DateTime Ts { get; set; }

        [ParquetDecimal(18, 4)]
        public decimal Dec { get; set; }
    }

    private sealed class DateRow
    {
        public int Id { get; set; }

        public DateInner? S { get; set; }
    }

    [Fact]
    public async Task Struct_ReadsFields_WithNullFieldAndNullStructRow()
    {
        var rows = new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 10, B = "x" } },
            new() { Id = 2, S = new Inner { A = 20, B = null } },
            new() { Id = 3, S = null },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType structType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("Id", DataTypes.IntegerType, nullable: false),
            new StructField("S", structType, nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        Assert.Equal(3, batch.RowCount);

        // A scalar sibling stays on the existing fast path and coexists with the nested column (name mode).
        ColumnVector id = batch.Column("Id");
        Assert.Equal(new[] { 1, 2, 3 }, id.GetValues<int>().ToArray());

        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        Assert.False(s.IsNull(0));
        Assert.False(s.IsNull(1));
        Assert.True(s.IsNull(2)); // the whole struct is null on row 3

        ColumnVector a = s.Child("A");
        Assert.Equal(10, a.GetValue<int>(0));
        Assert.Equal(20, a.GetValue<int>(1));
        Assert.True(a.IsNull(2)); // a null struct materializes null children

        ColumnVector b = s.Child("B");
        Assert.Equal("x", Utf8(b, 0));
        Assert.True(b.IsNull(1)); // a present struct with a null field
        Assert.True(b.IsNull(2)); // a null struct materializes null children
    }

    [Fact]
    public async Task Struct_RequiredStringLeaf_PresentStructNullField_FailsClosed()
    {
        // #813: a present struct with a NULL string field is a LEAF-ATTRIBUTABLE null. Requesting that leaf as
        // required (nullable:false) must fail closed — the exact #807 top-level failure class, one nesting level
        // down. Row 2 (S present, B=null) is the violation; row 3 (S null) is an ANCESTOR null and must NOT be
        // what triggers it.
        var rows = new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 10, B = "x" } },
            new() { Id = 2, S = new Inner { A = 20, B = null } }, // present struct, null field → leaf-attributable
            new() { Id = 3, S = null },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("S", new StructType(new[]
            {
                new StructField("B", DataTypes.StringType, nullable: false), // REQUIRED string leaf
            }), nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#813", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Struct_RequiredStringLeaf_OnlyAncestorNull_DoesNotReject()
    {
        // The discriminating negative: when the ONLY nulls in a required leaf lane come from a null ANCESTOR
        // (a null struct), the guard must NOT fire — an ancestor-null is legitimate. Here B is non-null whenever
        // S is present, so no leaf-attributable null exists; the read succeeds and B.IsNull tracks the null S.
        var rows = new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 10, B = "x" } },
            new() { Id = 2, S = null }, // ancestor (struct) null → B null via the ancestor, NOT leaf-attributable
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("S", new StructType(new[]
            {
                new StructField("B", DataTypes.StringType, nullable: false), // REQUIRED — but no leaf-null exists
            }), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        ColumnVector b = s.Child("B");
        Assert.Equal("x", Utf8(b, 0));
        Assert.True(b.IsNull(1)); // null because the whole struct is null (ancestor null), accepted
    }

    [Fact]
    public async Task Struct_RequiredValueLeaf_PresentStructNullField_FailsClosed()
    {
        // #813 is broader than string/binary: a required VALUE-typed nested leaf (int) is exposed too, because
        // the nested path had no nullability guard at all. Present struct with a null int field → leaf-attributable
        // → required request must fail closed.
        var rows = new List<AllNullableRow>
        {
            new() { Id = 1, S = new AllNullableInner { A = 10, B = "x" } },
            new() { Id = 2, S = new AllNullableInner { A = null, B = "y" } }, // present struct, null int field
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("S", new StructType(new[]
            {
                new StructField("A", DataTypes.IntegerType, nullable: false), // REQUIRED int leaf
            }), nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_RequiredElement_NullInPresentList_FailsClosed()
    {
        // #813 list-element guard site (keys on ArrayType.ContainsNull): a null element in a PRESENT list is
        // leaf-attributable; requesting the element as required (containsNull:false) must fail closed.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 10, 20 } },
            new() { Id = 2, Arr = new List<int?> { 30, null } }, // present list, null element → leaf-attributable
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: false), nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Contains("#813", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_RequiredElement_NullOrEmptyList_DoesNotReject()
    {
        // Discriminating negative for the list site: a NULL list and an EMPTY list are ancestor-level nulls, not
        // element-attributable — a required element request must still read them.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 10, 20 } },
            new() { Id = 2, Arr = null },            // null list (ancestor)
            new() { Id = 3, Arr = new List<int?>() }, // empty list (ancestor-level)
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: false), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested); // succeeds — no leaf-attributable null
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        Assert.True(arr.IsNull(1));            // null list
        Assert.Equal(0, arr.ElementLength(2)); // empty list
    }

    [Fact]
    public async Task Map_RequiredValue_NullInPresentEntry_FailsClosed()
    {
        // #813 map-value guard site (keys on MapType.ValueContainsNull): a null value in a PRESENT entry is
        // leaf-attributable; a required-value (valueContainsNull:false) request must fail closed.
        var rows = new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["a"] = 1, ["b"] = null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "M", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Map_RequiredValue_NullOrEmptyMap_DoesNotReject()
    {
        // Discriminating negative for the map site: a NULL map and an EMPTY map are ancestor-level; a required
        // value request must still read them.
        var rows = new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["a"] = 1 } },
            new() { Id = 2, M = null },                                                    // null map
            new() { Id = 3, M = new Dictionary<string, int?>(StringComparer.Ordinal) },    // empty map
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "M", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: false),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested); // succeeds
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));
        Assert.True(m.IsNull(1)); // null map
    }

    [Fact]
    public async Task Struct_RequiredPhysicallyRequiredLeaf_AncestorNull_DoesNotReject()
    {
        // The load-bearing `!leaf.IsNullable` gate: a physically-REQUIRED leaf (int A) under an optional struct
        // can only be nulled by the ANCESTOR (a null struct), whose definition level ALIASES maxDef-1 (because
        // the required leaf adds no +1). The guard must NOT fire — IsNullable reports own-optionality, so the
        // gate skips a physically-required leaf. Requesting A as non-nullable must still read the null-struct row.
        var rows = new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 10, B = "x" } },
            new() { Id = 2, S = null }, // null struct → A null via ancestor; A is physically required
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("S", new StructType(new[]
            {
                new StructField("A", DataTypes.IntegerType, nullable: false), // required, physically-required leaf
            }), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested); // succeeds — ancestor-null not over-rejected
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        Assert.Equal(10, s.Child("A").GetValue<int>(0));
        Assert.True(s.Child("A").IsNull(1)); // null via the null struct (ancestor), accepted
    }

    [Fact]
    public async Task Struct_DecodesLongDoubleBoolLeaves()
    {
        var rows = new List<WideRow>
        {
            new() { Id = 1, W = new Wide { L = 5_000_000_000L, D = 3.5, Flag = true } },
            new() { Id = 2, W = null },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("W", wideType, nullable: true) });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var w = Assert.IsType<StructColumnVector>(batch.Column("W"));

        Assert.Equal(5_000_000_000L, w.Child("L").GetValue<long>(0));
        Assert.Equal(3.5, w.Child("D").GetValue<double>(0));
        Assert.True(w.Child("Flag").GetValue<bool>(0));

        Assert.True(w.IsNull(1));
        Assert.True(w.Child("L").IsNull(1));
        Assert.True(w.Child("D").IsNull(1));
        Assert.True(w.Child("Flag").IsNull(1));
    }

    [Fact]
    public async Task Array_ReadsElements_WithEmptyNullListAndNullElement()
    {
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 10, 20 } },
            new() { Id = 2, Arr = new List<int?>() },
            new() { Id = 3, Arr = null },
            new() { Id = 4, Arr = new List<int?> { 40, null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        Assert.False(arr.IsNull(0));
        Assert.False(arr.IsNull(1)); // an empty list is NOT a null list
        Assert.True(arr.IsNull(2)); // a null list
        Assert.False(arr.IsNull(3));

        Assert.Equal(2, arr.ElementLength(0));
        Assert.Equal(0, arr.ElementLength(1)); // empty
        Assert.Equal(0, arr.ElementLength(2)); // null contributes no elements
        Assert.Equal(2, arr.ElementLength(3));

        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(10, e0.GetValue<int>(0));
        Assert.Equal(20, e0.GetValue<int>(1));

        ColumnVector e3 = arr.ElementsAt(3);
        Assert.Equal(40, e3.GetValue<int>(0));
        Assert.True(e3.IsNull(1)); // a null element inside a present list
    }

    [Fact]
    public async Task Array_OfStrings_DecodesAndNullElement()
    {
        var rows = new List<StrListRow>
        {
            new() { Id = 1, Names = new List<string?> { "a", "b" } },
            new() { Id = 2, Names = null },
            new() { Id = 3, Names = new List<string?> { "c", null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Names", DataTypes.CreateArrayType(DataTypes.StringType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var names = Assert.IsType<ListColumnVector>(batch.Column("Names"));

        Assert.Equal("a", Utf8(names.ElementsAt(0), 0));
        Assert.Equal("b", Utf8(names.ElementsAt(0), 1));
        Assert.True(names.IsNull(1));

        ColumnVector e3 = names.ElementsAt(2);
        Assert.Equal("c", Utf8(e3, 0));
        Assert.True(e3.IsNull(1));
    }

    [Fact]
    public async Task Map_ReadsEntries_WithEmptyNullMapAndNullValue()
    {
        var rows = new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k1"] = 100, ["k2"] = 200 } },
            new() { Id = 2, M = new Dictionary<string, int?>(StringComparer.Ordinal) },
            new() { Id = 3, M = null },
            new() { Id = 4, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k4"] = null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));

        Assert.False(m.IsNull(0));
        Assert.False(m.IsNull(1)); // an empty map is NOT a null map
        Assert.True(m.IsNull(2)); // a null map
        Assert.False(m.IsNull(3));

        Assert.Equal(2, m.EntryLength(0));
        Assert.Equal(0, m.EntryLength(1));
        Assert.Equal(0, m.EntryLength(2));
        Assert.Equal(1, m.EntryLength(3));

        // Map entry ordering is not part of the contract, so assert entries as a set.
        Dictionary<string, int?> entries0 = ReadIntMap(m, 0);
        Assert.Equal(100, entries0["k1"]);
        Assert.Equal(200, entries0["k2"]);

        ColumnVector k3 = m.KeysAt(3);
        ColumnVector v3 = m.ValuesAt(3);
        Assert.Equal("k4", Utf8(k3, 0));
        Assert.True(v3.IsNull(0)); // present key, null value
    }

    [Fact]
    public async Task Map_OfStringToString_DecodesValuesAndNull()
    {
        var rows = new List<StrMapRow>
        {
            new()
            {
                Id = 1,
                Sm = new Dictionary<string, string?>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = null },
            },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Sm",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.StringType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var sm = Assert.IsType<MapColumnVector>(batch.Column("Sm"));
        Assert.Equal(2, sm.EntryLength(0));

        ColumnVector keys = sm.KeysAt(0);
        ColumnVector vals = sm.ValuesAt(0);
        var read = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int i = 0; i < 2; i++)
        {
            read[Utf8(keys, i)] = vals.IsNull(i) ? null : Utf8(vals, i);
        }

        Assert.Equal("1", read["a"]);
        Assert.Null(read["b"]);
    }

    [Fact]
    public async Task Struct_BinaryField_DecodesEmptyLargeAndNull()
    {
        // #832 (M5b): the nested leaf physical-type check has a BinaryType arm that NO nested test reached —
        // mutating it to `false` left the whole suite green. Cover binary in a STRUCT field, over the same
        // payload vector the top-level LargeAndEmptyStringBinary_RoundTrip pins: an empty array (distinct from
        // null), a >64 KB payload (crosses the variable-width single-page/buffer boundary), and a null field
        // inside a present struct.
        byte[] large = LargeBinary();
        var rows = new List<BinaryStructRow>
        {
            new() { Id = 1, S = new BinaryInner { A = 1, Bin = new byte[] { 0x00, 0xFF, 0x10 } } },
            new() { Id = 2, S = new BinaryInner { A = 2, Bin = Array.Empty<byte>() } },
            new() { Id = 3, S = new BinaryInner { A = 3, Bin = large } },
            new() { Id = 4, S = new BinaryInner { A = 4, Bin = null } },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType structType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
            DataTypes.CreateStructField("Bin", DataTypes.BinaryType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("S", structType, nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        ColumnVector bin = s.Child("Bin");

        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, bin.GetBytes(0).ToArray());
        Assert.False(bin.IsNull(1));
        Assert.Empty(bin.GetBytes(1).ToArray());   // an empty binary is NOT a null binary
        Assert.Equal(large, bin.GetBytes(2).ToArray());
        Assert.True(bin.IsNull(3));                // a present struct with a null binary field
    }

    [Fact]
    public async Task Array_OfBinary_DecodesEmptyLargeAndNullElement()
    {
        // Same #832 (M5b) coverage for binary as a LIST ELEMENT — the repeated-leaf position, where the
        // variable-width payload is interleaved with repetition levels.
        byte[] large = LargeBinary();
        var rows = new List<BinaryListRow>
        {
            new()
            {
                Id = 1,
                Blobs = new List<byte[]?> { new byte[] { 0x01, 0x02 }, Array.Empty<byte>(), large, null },
            },
            new() { Id = 2, Blobs = null },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Blobs", DataTypes.CreateArrayType(DataTypes.BinaryType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var blobs = Assert.IsType<ListColumnVector>(batch.Column("Blobs"));

        Assert.False(blobs.IsNull(0));
        Assert.Equal(4, blobs.ElementLength(0));

        ColumnVector e0 = blobs.ElementsAt(0);
        Assert.Equal(new byte[] { 0x01, 0x02 }, e0.GetBytes(0).ToArray());
        Assert.False(e0.IsNull(1));
        Assert.Empty(e0.GetBytes(1).ToArray());    // an empty element is NOT a null element
        Assert.Equal(large, e0.GetBytes(2).ToArray());
        Assert.True(e0.IsNull(3));                 // a null element inside a present list

        Assert.True(blobs.IsNull(1));              // a null list
    }

    [Fact]
    public async Task Map_OfStringToBinary_DecodesEmptyLargeAndNullValue()
    {
        // Same #832 (M5b) coverage for binary as a MAP VALUE — the third and last nested position, and the
        // only one where the binary leaf is read positionally against a parallel (string) key stream.
        byte[] large = LargeBinary();
        var rows = new List<BinaryMapRow>
        {
            new()
            {
                Id = 1,
                Bm = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
                {
                    ["small"] = new byte[] { 0x7F },
                    ["empty"] = Array.Empty<byte>(),
                    ["large"] = large,
                    ["null"] = null,
                },
            },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Bm",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.BinaryType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var bm = Assert.IsType<MapColumnVector>(batch.Column("Bm"));
        Assert.Equal(4, bm.EntryLength(0));

        // Map entry ordering is not part of the contract, so index the entries by key.
        ColumnVector keys = bm.KeysAt(0);
        ColumnVector values = bm.ValuesAt(0);
        var read = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        for (int i = 0; i < 4; i++)
        {
            read[Utf8(keys, i)] = values.IsNull(i) ? null : values.GetBytes(i).ToArray();
        }

        Assert.Equal(new byte[] { 0x7F }, read["small"]);
        Assert.Empty(read["empty"]!);              // present key, empty (NOT null) value
        Assert.Equal(large, read["large"]);
        Assert.Null(read["null"]);                 // present key, null value
    }

    // The >64 KB binary payload vector, byte-for-byte identical to the top-level
    // LargeAndEmptyStringBinary_RoundTrip fixture so nested and flat binary decode are pinned to one shape.
    private static byte[] LargeBinary()
    {
        var large = new byte[70_000];
        for (int i = 0; i < large.Length; i++)
        {
            large[i] = (byte)(i % 251);
        }

        return large;
    }

    [Fact]
    public async Task Map_MismatchedValueRepetition_FailsClosed_CorruptData()
    {
        // F1 (Critical, red-team): a crafted 3-level map whose VALUE repetition stream disagrees with the
        // KEY's — same TOTAL entry count (4), different per-row distribution — must fail closed, never
        // silently mis-pair values across rows/keys. The reader consumes the value child positionally against
        // the key-driven offsets, so before the fix this decoded WITHOUT error as {10:100,20:200},{30:300,
        // 40:400} even though the value stream [0,1,1,0] declares row0=3 values / row1=1 value.
        //   key   rep [0,1,0,1] => row0{k10,k20}, row1{k30,k40}
        //   value rep [0,1,1,0] => row0 3 values, row1 1 value  (DIVERGENT, equal total)
        byte[] bytes = await ParquetTestHelpers.WriteIntMapWithRepLevelsAsync(
            ids: new int?[] { 1, 2 },
            keys: new int?[] { 10, 20, 30, 40 }, keyRep: new[] { 0, 1, 0, 1 },
            values: new int?[] { 100, 200, 300, 400 }, valueRep: new[] { 0, 1, 1, 0 });

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("repetition levels diverge", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_MatchingValueRepetition_DecodesCorrectly()
    {
        // F1 regression: a well-formed low-level-authored map (matching key/value reps [0,1,0,1], INCLUDING a
        // null value) must still decode — the rep-equality guard accepts a valid shared-group repetition
        // stream, proving it does not false-positive on the same low-level authoring door the crafted test
        // uses. (Empty-map / null-map coverage is in Map_ReadsEntries_WithEmptyNullMapAndNullValue.)
        byte[] bytes = await ParquetTestHelpers.WriteIntMapWithRepLevelsAsync(
            ids: new int?[] { 1, 2 },
            keys: new int?[] { 10, 20, 30, 40 }, keyRep: new[] { 0, 1, 0, 1 },
            values: new int?[] { 100, null, 300, 400 }, valueRep: new[] { 0, 1, 0, 1 });

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.IntegerType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));

        Assert.Equal(2, m.EntryLength(0));
        Assert.Equal(2, m.EntryLength(1));

        Assert.Equal(100, ReadIntMapEntry(m, row: 0, key: 10));
        Assert.Null(ReadIntMapEntry(m, row: 0, key: 20)); // present key, null value
        Assert.Equal(300, ReadIntMapEntry(m, row: 1, key: 30));
        Assert.Equal(400, ReadIntMapEntry(m, row: 1, key: 40));
    }

    [Fact]
    public void ValidateParallelDefinition_RejectsEntryPresenceDisagreement_CorruptData()
    {
        // R6 (Critical, red-team): the DEFINITION-level analog of the R4 map rep-parity guard. A crafted map
        // where key and value DEF disagree on which slots are present entries — passing rep-parity and
        // level-range — must fail closed, never silently mis-pair values. mapMaxDef = 2 (key required, its max
        // def == the map's own level): keyDef=[2,1] says slot0 is a present entry and slot1 an empty-map
        // placeholder; valueDef=[1,2] says the opposite. Front-filling the value child from slot1 would then
        // pair value(slot1) with key(slot0). This crafted stream is not authorable via the released
        // Parquet.Net write door (definition levels are derived from value nullability), so the guard is pinned
        // by a direct unit test of the now-internal helper.
        DeltaStorageException mismatch = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 2, 1 }, valueDef: new[] { 1, 2 }, mapMaxDef: 2, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, mismatch.Kind);
        Assert.Contains("disagree on entry presence", mismatch.Message, StringComparison.Ordinal);

        // A length disagreement (key and value declare different slot counts) also fails closed.
        DeltaStorageException lengths = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 2, 2 }, valueDef: new[] { 2 }, mapMaxDef: 2, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, lengths.Kind);
    }

    [Fact]
    public void ValidateParallelDefinition_RejectsContainerStateDisagreement_CorruptData()
    {
        // R7 (Critical, red-team): the container-state sub-case of the map def contract. When BOTH key and
        // value def sit BELOW mapMaxDef the slot is a non-entry placeholder — but the SPECIFIC state must still
        // agree: null-map (def 0) vs empty-map (def 1). Entry-presence parity passes (both "not present"), yet
        // the file is self-contradictory (key says empty, value says null). A decoder of untrusted input must
        // fail closed here rather than silently resolve it to the key's authoritative view. mapMaxDef = 2.
        DeltaStorageException emptyVsNull = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 1 }, valueDef: new[] { 0 }, mapMaxDef: 2, "col")); // key empty, value null
        Assert.Equal(StorageErrorKind.CorruptData, emptyVsNull.Kind);
        Assert.Contains("disagree on container state", emptyVsNull.Message, StringComparison.Ordinal);

        DeltaStorageException nullVsEmpty = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 0 }, valueDef: new[] { 1 }, mapMaxDef: 2, "col")); // key null, value empty
        Assert.Equal(StorageErrorKind.CorruptData, nullVsEmpty.Kind);
        Assert.Contains("disagree on container state", nullVsEmpty.Message, StringComparison.Ordinal);

        // Mixed: a valid present entry followed by a contradictory non-entry slot still fails closed.
        DeltaStorageException mixed = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateParallelDefinition(
                keyDef: new[] { 2, 1 }, valueDef: new[] { 2, 0 }, mapMaxDef: 2, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, mixed.Kind);
        Assert.Contains("container state", mixed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateParallelDefinition_AcceptsWellFormedStreams_NoOverRejection()
    {
        // R6/R7 no-over-rejection: every VALID key/value definition combination still passes. A present value
        // may carry a HIGHER def than the required key (a nullable value: def 3 present vs def 2 null, both >=
        // mapMaxDef 2), and empty-map (def 1) / null-map (def 0) placeholders agree EXACTLY on both leaves.
        // Two present entries, the second value present-but-null (valueDef 2, still an entry).
        NestedParquetColumnReader.ValidateParallelDefinition(
            keyDef: new[] { 2, 2 }, valueDef: new[] { 3, 2 }, mapMaxDef: 2, "col");
        // Empty map (both placeholders at def 1) and null map (both at def 0) — container states match exactly.
        NestedParquetColumnReader.ValidateParallelDefinition(new[] { 1 }, new[] { 1 }, mapMaxDef: 2, "col");
        NestedParquetColumnReader.ValidateParallelDefinition(new[] { 0 }, new[] { 0 }, mapMaxDef: 2, "col");
        // Mixed rows — present entry, empty map, null map — all agreeing slot-by-slot on presence AND state.
        NestedParquetColumnReader.ValidateParallelDefinition(
            keyDef: new[] { 2, 1, 0 }, valueDef: new[] { 3, 1, 0 }, mapMaxDef: 2, "col");
        // Null level arrays are vacuously parallel (defensive; real map leaves always carry def streams).
        NestedParquetColumnReader.ValidateParallelDefinition(null, null, mapMaxDef: 2, "col");
    }

    [Fact]
    public async Task StructField_RepeatedScalarLeaf_FailsClosed_CorruptData()
    {
        // R8 (High, red-team): a struct whose scalar field 'A' is a 1-level repeated primitive — its FILE leaf
        // declares MaxRepetitionLevel=1 (structurally an array), not 0. ReadStructAsync discards a field's
        // repetition stream, so without the leaf-structural-level guard the leaf's two element occurrences
        // [10,20] (one real row, rep [0,1]) would masquerade as two struct rows. The reader must reject the
        // repeated leaf at shape resolution — BEFORE any reconstruction — as it contradicts the requested
        // struct-scalar shape. Authorable end-to-end via the low-level writer (ParquetSerializer only emits
        // 3-level lists, which are caught earlier as "file column is itself nested").
        byte[] bytes = await ParquetTestHelpers.WriteStructWithRepeatedFieldAsync(
            new int?[] { 1 }, new int?[] { 10, 20 }, new[] { 0, 1 });
        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[] { DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: true) }),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.Contains("max repetition level", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructField_RepeatedScalarLeaf_ForgedRowCount_FailsClosed_CorruptData()
    {
        // R8: the full masquerade — the repeated leaf's two element values PLUS a footer NumRows forged from
        // 1 to 2, so the flat "numValues == rowCount" struct-field check would otherwise pass and yield two
        // phantom struct rows. The leaf-structural-level guard fires at shape resolution, BEFORE the row-count
        // logic, closing the masquerade regardless of the forged count.
        byte[] bytes = await ParquetTestHelpers.WriteStructWithRepeatedFieldAsync(
            new int?[] { 1 }, new int?[] { 10, 20 }, new[] { 0, 1 });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 2);
        var requested = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[] { DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: true) }),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(forged, requested));
        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.Contains("max repetition level", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeafStructuralLevels_RejectsWrongRepetition_CorruptData()
    {
        // R8 unit pin: a leaf whose MaxRepetitionLevel contradicts its navigated position fails closed. The
        // list/map positions (expected maxRep 1) can't be authored with a wrong-maxRep leaf end-to-end
        // (Parquet.Net's ListField/MapField ctors force element/key/value maxRep=1), so the guard is pinned
        // directly. A repeated primitive (isArray -> maxRep 1) at a struct-field position (expects 0):
        var repeatedLeaf = new global::Parquet.Schema.DataField("x", typeof(int), isArray: true); // maxRep 1
        DeltaStorageException struApt = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                repeatedLeaf, expectedMaxRepetition: 0, containerMaxDef: 0, "struct field 'x'"));
        Assert.Equal(StorageErrorKind.CorruptData, struApt.Kind);
        Assert.Contains("max repetition level", struApt.Message, StringComparison.Ordinal);

        // A non-repeated primitive (maxRep 0) at a list-element / map-key/value position (expects 1):
        var scalarLeaf = new global::Parquet.Schema.DataField("x", typeof(int)); // maxRep 0
        DeltaStorageException listApt = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                scalarLeaf, expectedMaxRepetition: 1, containerMaxDef: 0, "array column 'x' element"));
        Assert.Equal(StorageErrorKind.CorruptData, listApt.Kind);
        Assert.Contains("max repetition level", listApt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeafStructuralLevels_RejectsWrongDefinition_CorruptData()
    {
        // R8 unit pin: a leaf whose MaxDefinitionLevel sits outside [containerMaxDef, containerMaxDef+1] fails
        // closed (a phantom optional/repeated ancestor, or fewer than its own container's) — it would shift
        // the null-classification thresholds. Real leaves with known levels from a map schema: key (maxDef 2),
        // value (maxDef 3), both maxRep 1.
        global::Parquet.Schema.DataField[] mapLeaves = MapLeaves();
        global::Parquet.Schema.DataField valueLeaf = mapLeaves[1]; // maxRep 1, maxDef 3

        // maxDef 3 ABOVE a container whose own level is 1 (would allow at most 2): reject.
        DeltaStorageException above = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                valueLeaf, expectedMaxRepetition: 1, containerMaxDef: 1, "array column 'x' element"));
        Assert.Equal(StorageErrorKind.CorruptData, above.Kind);
        Assert.Contains("max definition level", above.Message, StringComparison.Ordinal);

        // maxDef 0 BELOW a container whose own level is 2 (impossible: a leaf can't have fewer levels than its
        // parent): reject.
        var scalarLeaf = new global::Parquet.Schema.DataField("x", typeof(int)); // maxRep 0, maxDef 0
        DeltaStorageException below = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.ValidateLeafStructuralLevels(
                scalarLeaf, expectedMaxRepetition: 0, containerMaxDef: 2, "struct field 'x'"));
        Assert.Equal(StorageErrorKind.CorruptData, below.Kind);
        Assert.Contains("max definition level", below.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLeafStructuralLevels_AcceptsValidPositions_NoOverRejection()
    {
        // R8 no-over-rejection: every VALID single-level position passes, with nullability advisory (both the
        // required = containerMaxDef and optional = containerMaxDef+1 definition levels accepted).
        global::Parquet.Schema.DataField[] mapLeaves = MapLeaves();
        global::Parquet.Schema.DataField keyLeaf = mapLeaves[0];   // maxRep 1, maxDef 2 (required)
        global::Parquet.Schema.DataField valueLeaf = mapLeaves[1]; // maxRep 1, maxDef 3 (optional)
        var scalarLeaf = new global::Parquet.Schema.DataField("x", typeof(int)); // maxRep 0, maxDef 0

        // Struct required scalar field: maxRep 0, maxDef 0 == containerMaxDef 0.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(scalarLeaf, 0, 0, "struct field 'x'");
        // Map key (required): maxRep 1, maxDef 2 == containerMaxDef 2.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(keyLeaf, 1, 2, "map column 'x' key");
        // Map value (optional): maxRep 1, maxDef 3 == containerMaxDef 2 + 1.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(valueLeaf, 1, 2, "map column 'x' value");
        // A repeated leaf used as a required list element (maxDef == containerMaxDef): maxRep 1, maxDef 2.
        NestedParquetColumnReader.ValidateLeafStructuralLevels(keyLeaf, 1, 2, "array column 'x' element");
    }

    private static global::Parquet.Schema.DataField[] MapLeaves()
    {
        var mapSchema = new global::Parquet.Schema.ParquetSchema(
            new global::Parquet.Schema.MapField(
                "M",
                new global::Parquet.Schema.DataField<int>("key"),
                new global::Parquet.Schema.DataField<int?>("value")));
        return mapSchema.GetDataFields(); // [M/key (RL 1, DL 2), M/value (RL 1, DL 3)]
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FoldsReconstructedChild_FailsClosed()
    {
        // F2 (High, red-team): the eager-decode ceiling must bound the RAW leaf decode PLUS the reconstructed
        // #570 child ColumnVector, not the raw buffers alone. A list of 7000 nullable ints has an int element
        // leaf whose raw decode is 7000*(4 value + 4 def + 4 rep) = 84,000 bytes (< the 100,000-byte ceiling)
        // but whose raw+child is 7000*(12 + 4 value + 1 null-mask) = 119,000 bytes (> the ceiling). Without the
        // reconstruction fold it would pass; with it, the leaf is rejected before allocation.
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = Enumerable.Range(0, 7000).Select(i => (int?)i).ToList() },
        });

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 100_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // The LeafNumValues (per-leaf) guard fired — not the flat EnsureDecodeCeiling — proving the fold is in
        // the leaf ceiling: its message names the leaf and the raw+reconstruction overrun.
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
        Assert.Contains("eager decode would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FoldsVariableWidthChildPayload_FailsClosed()
    {
        // R5-F1 (High, red-team): for a VARIABLE-width leaf (string/binary) the reconstructed #570 child copies
        // the actual UTF-8 payload, not just the per-value handle — so the leaf ceiling must budget that
        // payload too. A 1000-element list of UNIQUE ~61-byte strings has an element leaf whose:
        //   raw + fixed-handle = 1000 * (16 handle + 4 def + 4 rep + 16 child-handle + 1 null-mask) = 41,000
        //     (< the 100,000-byte ceiling — WITHOUT the payload term the leaf passes),
        //   TotalUncompressedSize U ~= 65,000, so the child byte store (doubling) is budgeted at 2*U ~= 130,000
        //     (> the ceiling on its own) — WITH the payload term the leaf is rejected before allocation.
        // The flat EnsureDecodeCeiling (sum of leaf U ~= 65,000) still passes, so the LeafNumValues guard is
        // the gate (its "Nested leaf" message confirms which guard fired). Strings are unique so U reflects the
        // real payload (a dictionary-encoded repeat column is a separate, reader-wide residual).
        byte[] bytes = await WriteAsync(new List<StrListRow>
        {
            new()
            {
                Id = 1,
                Names = Enumerable.Range(0, 1000)
                    .Select(i => (string?)$"str-{i:D6}-{new string('x', 50)}").ToList(),
            },
        });

        var requested = new StructType(new[]
        {
            new StructField(
                "Names", DataTypes.CreateArrayType(DataTypes.StringType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 100_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
        Assert.Contains("eager decode would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FixedWidthUnaffectedByPayloadTerm_Decodes()
    {
        // R5-F1 regression: the variable-width payload term must NOT change fixed-width behavior. The same
        // 1000-element int list — raw+child = 1000 * (4 + 4 + 4 + 4 + 1) = 17,000 — decodes cleanly under the
        // very ceiling that rejects the string list above, proving the payload budget is variable-width only.
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = Enumerable.Range(0, 1000).Select(i => (int?)i).ToList() },
        });

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 100_000));

        ColumnBatch batch = await ReadSingleAsync(reader, bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        Assert.Equal(1000, arr.ElementLength(0));
        Assert.Equal(0, arr.ElementsAt(0).GetValue<int>(0));
        Assert.Equal(999, arr.ElementsAt(0).GetValue<int>(999));
    }

    [Fact]
    public async Task NestedDecodeCeiling_AggregatesLeafBudgetsAcrossStructFields_FailsClosed()
    {
        // R8 (Critical, red-team): the eager-decode ceiling must bound a nested column's leaves CUMULATIVELY,
        // not each leaf independently. A struct<L:long, D:double, Flag:bool> over 2000 present rows reconstructs
        // three leaf children whose per-leaf raw+child budgets are ~42,000 / ~42,000 / ~14,000 bytes: each is
        // individually under a 60,000-byte ceiling (so a PER-LEAF-only check passes all three), but their
        // COMBINED peak (~98,000) exceeds it. The flat EnsureDecodeCeiling (sum of the leaves' raw
        // UncompressedBytes, ~34,000) also passes, so only the shared NestedDecodeBudget catches the overrun —
        // proving a wide struct can no longer allocate K x the ceiling.
        var rows = new List<WideRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new WideRow { Id = i, W = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 } });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("W", wideType, nullable: true) });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 60_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
        Assert.Contains("eager decode would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_StructWithinAggregateBudget_Decodes()
    {
        // R8 regression (no over-reject): the SAME struct<L,D,Flag> over 2000 rows decodes cleanly under a
        // ceiling comfortably above its cumulative ~98,000-byte reconstruction peak — the shared budget only
        // rejects the genuine aggregate overrun above, never a within-budget wide struct.
        var rows = new List<WideRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new WideRow { Id = i, W = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 } });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("W", wideType, nullable: true) });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 400_000));

        ColumnBatch batch = await ReadSingleAsync(reader, bytes, requested);
        var w = Assert.IsType<StructColumnVector>(batch.Column("W"));
        Assert.Equal(2000, batch.RowCount);
        Assert.Equal(0L, w.Child("L").GetValue<long>(0));
        Assert.Equal(1999L, w.Child("L").GetValue<long>(1999));
        Assert.Equal(1999 * 1.5, w.Child("D").GetValue<double>(1999));
    }

    [Fact]
    public async Task NestedDecodeCeiling_ChargesListStructuralArrays_FailsClosed()
    {
        // R9 finding 1 (Critical, red-team): a list/map's OWN per-row structural arrays (offsets + null mask)
        // must be charged to the reconstruction budget too, not just the element leaf. 10,000 single-element
        // int lists have an element leaf budget of ~170,000 bytes (10,000 * 17) and a structural cost of
        // ~50,000 (10,000 * (4 offset + 1 null)); each is individually under a 180,000-byte ceiling (so the
        // element-leaf-only budget passes), but their combined ~220,000 exceeds it. The flat EnsureDecodeCeiling
        // (raw U ~40,000, and its (iii) structural bound ~50,000) also passes, so only charging the structure
        // to the shared budget catches the overrun.
        var rows = new List<ListRow>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            rows.Add(new ListRow { Id = i, Arr = new List<int?> { i } });
        }

        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 180_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_ChargesListCopiedOffsetsAndBitmap_FailsClosed()
    {
        // R10 finding (Critical, red-team): the structural charge must cover the FULL live peak — the transient
        // offsets+nulls the reconstruction builds AND the final copy ListColumnVector makes (CopyValidatedOffsets
        // + NestedValidity.Build bitmap), which coexist at the copy. 10,000 single-element int lists have an
        // element leaf budget of ~170,000; charging only the transient structure (~50,000) totals ~220,000, but
        // the true peak with the copied offsets + bitmap (~100,000 structural) is ~270,000. A 240,000-byte
        // ceiling therefore admits the transient-only charge yet must reject the full one — pinning that the
        // budget charges the copied structural arrays, not just the transient ones.
        var rows = new List<ListRow>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            rows.Add(new ListRow { Id = i, Arr = new List<int?> { i } });
        }

        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 240_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_SharesBudgetAcrossNestedColumns_FailsClosed()
    {
        // R9 finding 2 (Critical, red-team): the budget must be ONE per row-group read, shared across every
        // nested column — not a fresh ceiling per column. Two struct<L,D,Flag> columns over 2000 rows each
        // reconstruct ~98,000 bytes apiece; each is under a 110,000-byte ceiling (so a per-column budget passes
        // both), but their combined ~196,000 exceeds it. The flat EnsureDecodeCeiling (raw U of all six leaves
        // ~65,000) also passes, so only a shared row-group budget catches the combined overrun.
        var rows = new List<TwoStructRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new TwoStructRow
            {
                Id = i,
                A = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 },
                B = new Wide { L = -i, D = i * 2.5, Flag = (i & 1) == 1 },
            });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[]
        {
            new StructField("A", wideType, nullable: true),
            new StructField("B", wideType, nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 110_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("would exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedDecodeCeiling_TwoNestedColumnsWithinBudget_Decodes()
    {
        // R9 finding 2 regression (no over-reject): the SAME two struct columns over 2000 rows decode cleanly
        // under a ceiling above their combined ~196,000-byte reconstruction peak — the shared budget rejects
        // only a genuine combined overrun, never within-budget multi-column reads.
        var rows = new List<TwoStructRow>(2000);
        for (int i = 0; i < 2000; i++)
        {
            rows.Add(new TwoStructRow
            {
                Id = i,
                A = new Wide { L = i, D = i * 1.5, Flag = (i & 1) == 0 },
                B = new Wide { L = -i, D = i * 2.5, Flag = (i & 1) == 1 },
            });
        }

        byte[] bytes = await WriteAsync(rows);

        StructType wideType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
            DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
        });
        var requested = new StructType(new[]
        {
            new StructField("A", wideType, nullable: true),
            new StructField("B", wideType, nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 600_000));

        ColumnBatch batch = await ReadSingleAsync(reader, bytes, requested);
        Assert.Equal(2000, batch.RowCount);
        var a = Assert.IsType<StructColumnVector>(batch.Column("A"));
        var b = Assert.IsType<StructColumnVector>(batch.Column("B"));
        Assert.Equal(0L, a.Child("L").GetValue<long>(0));
        Assert.Equal(-1999L, b.Child("L").GetValue<long>(1999));
    }

    [Fact]
    public async Task ArrayOfStructVoidLeaf_FailsClosed_UnsupportedFeature()
    {
        // 585a lifts the nested-within-nested READ reject for DECODABLE shapes (array<struct<int>> now round
        // trips), but an UNSUPPORTED scalar leaf at ANY depth still fails closed: array<struct<void>> has a
        // NullType leaf with no Parquet physical representation. The reject must be deterministic (before any
        // decode) and BOUNDED — the leaf's hostile struct-field name is sanitized (control chars stripped) and
        // only the leaf KIND is rendered, never a recursive SimpleString of the foreign field names.
        const string hostileFieldName = "n\r\n[CRITICAL] forged";
        StructType element =
            DataTypes.CreateStructType(new[] { DataTypes.CreateStructField(hostileFieldName, DataTypes.NullType) });
        var requested = new StructType(new[]
        {
            new StructField("X", DataTypes.CreateArrayType(element), nullable: true),
        });

        byte[] bytes = await WriteAsync(new List<StructRow> { new() { Id = 1, S = null } });
        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("no supported scalar Parquet read mapping", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileFieldName, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', error.Message);
        Assert.DoesNotContain('\n', error.Message);
    }

    [Fact]
    public async Task WrongContainerKind_FailsClosed_SchemaMismatch()
    {
        // The file column 'S' is physically a struct; requesting it as an array is a structural mismatch and
        // must fail closed rather than mis-decode.
        var rows = new List<StructRow> { new() { Id = 1, S = new Inner { A = 10, B = "x" } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("S", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task ListRow_ForgedNumRows_FailsClosed_BeforeOverCeilingAllocation()
    {
        // A1 (HIGH DoS): a forged footer NumRows must be rejected by the eager-decode ceiling BEFORE the
        // rowCount-scaled offsets/nulls arrays are allocated. The nested container's per-row structural width
        // (int offset + bool null = 5 bytes) is folded into the first leaf's row-count bound, so a tiny file
        // claiming 50,000,000 rows fails closed without the ~250 MB allocation.
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 1, 2 } },
            new() { Id = 2, Arr = new List<int?> { 3 } },
        });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 50_000_000);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        // A 4 MiB ceiling: 50,000,000 rows × 5 structural bytes = 250 MB ≫ ceiling, so the row-count bound
        // rejects it. The default 4 GiB ceiling only rejects rowCount > ~858M, so real row groups are unaffected.
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 4L * 1024 * 1024));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, forged, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // The ceiling message (from EnsureDecodeCeiling, which runs BEFORE any per-column decode/allocation)
        // proves the rejection is PRE-allocation — not the post-allocation cross-check, which would also throw
        // CorruptData but only after the 250 MB offsets buffer had already been allocated.
        Assert.Contains("eager-decode ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains("5-byte column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapRow_ForgedNumRows_FailsClosed_BeforeOverCeilingAllocation()
    {
        // A1 (HIGH DoS), map variant: the key leaf drives the entry structure and carries the folded 5-byte
        // structural width, so a forged NumRows is rejected before the map's offsets/nulls allocation.
        byte[] bytes = await WriteAsync(new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k1"] = 1, ["k2"] = 2 } },
        });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 50_000_000);

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 4L * 1024 * 1024));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, forged, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("eager-decode ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains("5-byte column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructRow_ForgedNumRows_FailsClosed_BeforeOverCeilingAllocation()
    {
        // A1 (HIGH DoS), struct variant: a struct's per-row structural width is 1 byte (the null mask only —
        // no offsets), folded into the first field leaf's row-count bound. This pins the struct arm of
        // NestedContainerStructuralWidth (list/map assert "5-byte column"; struct must assert "1-byte column"),
        // which the list/map-only forge tests leave unexercised.
        byte[] bytes = await WriteAsync(new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 1, B = "x" } },
            new() { Id = 2, S = new Inner { A = 2, B = "y" } },
        });
        byte[] forged = await ParquetTestHelpers.ForgeRowGroupNumRowsAsync(bytes, rowGroup: 0, forgedNumRows: 50_000_000);

        StructType inner = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[] { new StructField("S", inner, nullable: true) });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 4L * 1024 * 1024));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, forged, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        // Pre-allocation ceiling rejection (EnsureDecodeCeiling); "1-byte column" proves it is the struct
        // structural-width fold that fired, not a list/map path.
        Assert.Contains("eager-decode ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains("1-byte column", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nested_UnderIdMode_FailsClosed_UnsupportedFeature()
    {
        // A2: nested column container binding under column-mapping id mode is not supported yet. The reader
        // fails closed before any nested id-mode resolution until an end-to-end container contract lands.
        byte[] bytes = await WriteAsync(new List<ListRow> { new() { Id = 1, Arr = new List<int?> { 1, 2 } } });
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
        {
            using var stream = new MemoryStream(bytes, writable: false);
            await foreach (ColumnBatch _ in new ParquetFileReader().ReadAsync(
                stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
                resolveByFieldId: true, CancellationToken.None))
            {
            }
        });

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task NestedLeafDecodeCeiling_FailsClosed_CorruptData()
    {
        // A3: the per-leaf eager-decode ceiling bounds a nested leaf's declared value count independently of
        // the row count. 20,000 element slots under a 120,000-byte ceiling clears the container's row-count
        // bound but must trip the leaf guard (the "Nested leaf" message confirms which guard fired).
        byte[] bytes = await WriteAsync(new List<ListRow>
        {
            new() { Id = 1, Arr = Enumerable.Range(0, 20_000).Select(i => (int?)i).ToList() },
        });
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });
        var reader = new ParquetFileReader(new ParquetDecodeLimits(maxRowGroupDecodedBytes: 120_000));

        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => EnumerateAsync(reader, bytes, requested));

        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("Nested leaf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeafTypeMismatch_FailsClosed_SchemaMismatch()
    {
        // A4: a nested leaf whose physical type disagrees with the requested type must fail closed (no widening
        // for nested leaves — that is #546). File 'A' is int32; requesting it as long is a SchemaMismatch.
        byte[] bytes = await WriteAsync(new List<StructRow> { new() { Id = 1, S = new Inner { A = 10, B = "x" } } });
        StructType wrong = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[] { new StructField("S", wrong, nullable: true) });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public async Task NestedLeafDateTimestampConfusion_FailsClosed_SchemaMismatch()
    {
        // A4 (silent-corruption case): DATE and TIMESTAMP both decode as a CLR DateTime, so mis-reading one as
        // the other would land in the wrong epoch lane (day vs micros) with NO exception unless the physical-
        // type guard distinguishes the logical annotations. Both directions must fail closed.
        byte[] bytes = await WriteAsync(new List<DateRow>
        {
            new()
            {
                Id = 1,
                S = new DateInner
                {
                    D = new DateOnly(2024, 1, 2),
                    Ts = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                    Dec = 1.5m,
                },
            },
        });

        // File 'D' is a DATE leaf; requesting it as TIMESTAMP must be rejected.
        var dateAsTimestamp = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[]
                    { DataTypes.CreateStructField("D", DataTypes.TimestampType, nullable: false) }),
                nullable: true),
        });
        DeltaStorageException e1 =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, dateAsTimestamp));
        Assert.Equal(StorageErrorKind.SchemaMismatch, e1.Kind);

        // File 'Ts' is a TIMESTAMP leaf; requesting it as DATE must be rejected.
        var timestampAsDate = new StructType(new[]
        {
            new StructField(
                "S",
                DataTypes.CreateStructType(new[]
                    { DataTypes.CreateStructField("Ts", DataTypes.DateType, nullable: false) }),
                nullable: true),
        });
        DeltaStorageException e2 =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, timestampAsDate));
        Assert.Equal(StorageErrorKind.SchemaMismatch, e2.Kind);
    }

    [Fact]
    public void ValidateLevelRange_RejectsOutOfRangeLevel_CorruptData()
    {
        // A5: an out-of-range Dremel level cannot be produced by a conforming writer, so the guard is unit-
        // tested directly. A def level above the leaf max would otherwise be silently coerced to a spurious
        // present-null (a WRONG read), so it must fail closed.
        DeltaStorageException over = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateLevelRange(new[] { 0, 1, 5 }, maxLevel: 3, "col.leaf", "definition"));
        Assert.Equal(StorageErrorKind.CorruptData, over.Kind);
        Assert.Contains("definition level 5", over.Message, StringComparison.Ordinal);

        // The unsigned compare also rejects a negative level.
        DeltaStorageException negative = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateLevelRange(new[] { -1 }, maxLevel: 2, "col.leaf", "repetition"));
        Assert.Equal(StorageErrorKind.CorruptData, negative.Kind);

        // In-range levels (including exactly maxLevel) and a null array are accepted with no throw.
        NestedParquetColumnReader.ValidateLevelRange(new[] { 0, 1, 2, 3 }, maxLevel: 3, "col.leaf", "definition");
        NestedParquetColumnReader.ValidateLevelRange(null, maxLevel: 3, "col.leaf", "definition");
    }

    [Fact]
    public void BuildRepeatedStructure_RejectsInvalidStateTransitions_CorruptData()
    {
        // R5-F2 (Critical, red-team): a structurally-invalid list Dremel stream that passes ValidateLevelRange
        // must fail closed rather than decode a phantom element. containerMaxDef = 2 for a standard optional
        // list-of-optional-element (element leaf maxDef 3): def 0 = null list, 1 = empty list, 2 = null
        // element, 3 = present element. These crafted streams cannot be authored via the released Parquet.Net
        // write door (definition levels are derived from value nullability, never below the element's own null
        // level), so the guard is pinned by a direct unit test of BuildRepeatedStructure.

        // Empty-list marker (def=1, rep=0 opening row0) then a continuation (rep=1) of that same row: a row
        // whose container is empty has NO element occurrence and must never be continued.
        DeltaStorageException emptyThenContinue = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 1, 2 }, rep: new[] { 0, 1 }, numValues: 2, thisMaxDef: 2, thisMaxRep: 1,
                parentMaxDef: 0, parentMaxRep: 0, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col"));
        Assert.Equal(StorageErrorKind.CorruptData, emptyThenContinue.Kind);
        Assert.Contains("has no continuation", emptyThenContinue.Message, StringComparison.Ordinal);

        // Present opener (def=3) then a continuation slot that is itself a sub-container placeholder (def=1 <
        // containerMaxDef) — a continuation must be a real element occurrence, not an empty/null marker.
        DeltaStorageException continuationIsMarker = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 3, 1 }, rep: new[] { 0, 1 }, numValues: 2, thisMaxDef: 2, thisMaxRep: 1,
                parentMaxDef: 0, parentMaxRep: 0, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col"));
        Assert.Equal(StorageErrorKind.CorruptData, continuationIsMarker.Kind);

        // A leading non-zero repetition level cannot open a row.
        DeltaStorageException leadingRep = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 3 }, rep: new[] { 1 }, numValues: 1, thisMaxDef: 2, thisMaxRep: 1,
                parentMaxDef: 0, parentMaxRep: 0, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col"));
        Assert.Equal(StorageErrorKind.CorruptData, leadingRep.Kind);
    }

    [Fact]
    public void BuildRepeatedStructure_AcceptsAllValidPermutations_NoOverRejection()
    {
        // R5-F2 no-over-rejection: every VALID single-level list permutation must still decode unchanged. The
        // guard rejects only genuine state-transition contradictions, not any well-formed null/empty/present
        // stream (containerMaxDef = 2).
        AssertRepeated(def: new[] { 3 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 1 }, expectedNulls: new[] { false }); // [42]
        AssertRepeated(def: new[] { 1 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 0 }, expectedNulls: new[] { false }); // [] empty
        AssertRepeated(def: new[] { 0 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 0 }, expectedNulls: new[] { true }); // null list
        AssertRepeated(def: new[] { 2 }, rep: new[] { 0 }, rowCount: 1,
            expectedOffsets: new[] { 0, 1 }, expectedNulls: new[] { false }); // [null] one null element
        AssertRepeated(def: new[] { 3, 3 }, rep: new[] { 0, 1 }, rowCount: 1,
            expectedOffsets: new[] { 0, 2 }, expectedNulls: new[] { false }); // [10,20]
        AssertRepeated(def: new[] { 3, 2 }, rep: new[] { 0, 1 }, rowCount: 1,
            expectedOffsets: new[] { 0, 2 }, expectedNulls: new[] { false }); // [10,null]
        AssertRepeated(def: new[] { 3, 1 }, rep: new[] { 0, 0 }, rowCount: 2,
            expectedOffsets: new[] { 0, 1, 1 }, expectedNulls: new[] { false, false }); // [10],[]
        AssertRepeated(def: new[] { 1, 3 }, rep: new[] { 0, 0 }, rowCount: 2,
            expectedOffsets: new[] { 0, 0, 1 }, expectedNulls: new[] { false, false }); // [],[10] (rowComplete reset)
        AssertRepeated(def: new[] { 0, 3, 1 }, rep: new[] { 0, 0, 0 }, rowCount: 3,
            expectedOffsets: new[] { 0, 0, 1, 1 }, expectedNulls: new[] { true, false, false }); // null,[10],[]
    }

    private static void AssertRepeated(
        int[] def, int[] rep, int rowCount, int[] expectedOffsets, bool[] expectedNulls)
    {
        var offsets = new int[rowCount + 1];
        var nulls = new bool[rowCount];
        int total = NestedParquetColumnReader.BuildRepeatedStructure(
            def, rep, def.Length, thisMaxDef: 2, thisMaxRep: 1, parentMaxDef: 0, parentMaxRep: 0, ownerCells: rowCount,
            offsets, nulls, "col");
        Assert.Equal(expectedOffsets, offsets);
        Assert.Equal(expectedNulls, nulls);
        Assert.Equal(expectedOffsets[^1], total);
    }

    [Fact]
    public void BuildStructNullMask_RejectsCrossFieldDefDivergence_CorruptData()
    {
        // R5-F2 (Critical, red-team): a crafted struct Dremel stream where fields DISAGREE on the struct's
        // presence at the same row must fail closed rather than decode a phantom field under a null struct.
        // structMaxDef = 1 (optional struct; required field A maxDef 1, optional field B maxDef 2): field def
        // < 1 means the struct is absent. Such divergent streams cannot be authored via the released write door
        // (definition levels are derived from value nullability), so the guard is pinned by a direct unit test.

        // Field A says "null struct" (def 0) while field B says "present" (def 1) at the same row.
        int[]?[] aNullBPresent = { new[] { 0 }, new[] { 1 } };
        DeltaStorageException e1 = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildStructNullMask(aNullBPresent, structMaxDef: 1, rowCount: 1, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, e1.Kind);
        Assert.Contains("disagree on the struct's presence", e1.Message, StringComparison.Ordinal);

        // The reverse divergence (A present, B null-struct) is caught either driving direction.
        int[]?[] aPresentBNull = { new[] { 1 }, new[] { 0 } };
        DeltaStorageException e2 = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildStructNullMask(aPresentBNull, structMaxDef: 1, rowCount: 1, "col"));
        Assert.Equal(StorageErrorKind.CorruptData, e2.Kind);
    }

    [Fact]
    public void BuildStructNullMask_AcceptsAgreeingFields_NoOverRejection()
    {
        // R5-F2 no-over-rejection: every VALID struct permutation still yields the correct null mask. The guard
        // rejects only genuine cross-field divergence, not a present struct with a null field.
        // Null struct (both fields def 0).
        Assert.Equal(new[] { true }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 0 }, new[] { 0 } }, structMaxDef: 1, rowCount: 1, "col"));

        // Present struct, field B null (A def 1 present, B def 1 struct-present-field-null) — fields agree.
        Assert.Equal(new[] { false }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 1 }, new[] { 1 } }, structMaxDef: 1, rowCount: 1, "col"));

        // Present struct, both fields present (A def 1, B def 2).
        Assert.Equal(new[] { false }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 1 }, new[] { 2 } }, structMaxDef: 1, rowCount: 1, "col"));

        // Multi-row: row0 present (A 1, B 2), row1 null (A 0, B 0) — per-row agreement.
        Assert.Equal(new[] { false, true }, NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 1, 0 }, new[] { 2, 0 } }, structMaxDef: 1, rowCount: 2, "col"));

        // A required struct (structMaxDef 0) has no null mask.
        Assert.Null(NestedParquetColumnReader.BuildStructNullMask(
            new int[]?[] { new[] { 0 } }, structMaxDef: 0, rowCount: 1, "col"));
    }

    [Fact]
    public async Task ZeroFieldStruct_FailsClosed_UnsupportedFeature()
    {
        // A6: a zero-field struct reconstructs a length-0 vector and (pre-fix) surfaced a raw ArgumentException
        // from the batch ctor rather than the DeltaStorageException contract. Reject it fail-closed.
        byte[] bytes = await WriteAsync(new List<StructRow> { new() { Id = 1, S = new Inner { A = 1, B = "x" } } });
        var requested = new StructType(new[]
        {
            new StructField("S", DataTypes.CreateStructType(Array.Empty<StructField>()), nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task Array_EmptyListAdjacentToNullElementList_DisambiguatesLevels()
    {
        // arch adjacency trap: an EMPTY list [] immediately followed by a list holding a single NULL element
        // [null] must decode to distinct shapes — 0 elements vs 1 present-but-null element — even though a
        // naive length delta would conflate them.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?>() },
            new() { Id = 2, Arr = new List<int?> { null } },
            new() { Id = 3, Arr = new List<int?> { 7 } },
        };
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        Assert.False(arr.IsNull(0));
        Assert.Equal(0, arr.ElementLength(0)); // [] — empty, not null

        Assert.False(arr.IsNull(1));
        Assert.Equal(1, arr.ElementLength(1)); // [null] — one present-but-null element
        Assert.True(arr.ElementsAt(1).IsNull(0));

        Assert.Equal(1, arr.ElementLength(2));
        Assert.Equal(7, arr.ElementsAt(2).GetValue<int>(0));
    }

    [Fact]
    public async Task Struct_NullStructAdjacentToPresentAllNullFields_Disambiguated()
    {
        // arch adjacency trap: a NULL struct (the whole struct absent) vs a PRESENT struct whose every field is
        // null must decode to distinct struct-level null masks — IsNull(struct) true vs false — even though both
        // leave all child leaves null.
        var rows = new List<AllNullableRow>
        {
            new() { Id = 1, S = null },
            new() { Id = 2, S = new AllNullableInner { A = null, B = null } },
        };
        byte[] bytes = await WriteAsync(rows);
        StructType st = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: true),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[] { new StructField("S", st, nullable: true) });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        Assert.True(s.IsNull(0)); // null struct
        Assert.True(s.Child("A").IsNull(0));
        Assert.True(s.Child("B").IsNull(0));

        Assert.False(s.IsNull(1)); // present struct with all-null fields
        Assert.True(s.Child("A").IsNull(1));
        Assert.True(s.Child("B").IsNull(1));
    }

    [Fact]
    public async Task ZeroRowFile_NestedColumn_YieldsNoRows()
    {
        // quality zero-row-file edge: a nested column in a file with no rows must decode to zero rows without
        // error (no batch, or an empty batch — both acceptable).
        byte[] bytes = await WriteAsync(new List<ListRow>());
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        using var stream = new MemoryStream(bytes, writable: false);
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(0, batches.Sum(b => b.RowCount));
        foreach (ColumnBatch batch in batches)
        {
            Assert.Equal(0, Assert.IsType<ListColumnVector>(batch.Column("Arr")).Length);
        }
    }

    [Fact]
    public async Task AllNullListColumn_DecodesAllNull()
    {
        // quality all-null-column edge: every row's list is null. The column decodes to all-null with zero
        // elements — distinct from all-empty (asserted elsewhere).
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = null },
            new() { Id = 2, Arr = null },
            new() { Id = 3, Arr = null },
        };
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        Assert.Equal(3, arr.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(arr.IsNull(i));
            Assert.Equal(0, arr.ElementLength(i));
        }

        Assert.Equal(0, arr.ElementsAt(0).Length); // no elements materialized at all
    }

    [Fact]
    public async Task Struct_DecodesDateTimestampDecimalLeaves()
    {
        // A7: nested-leaf coverage for the highest-value untested conversions — DATE (epoch-day int lane),
        // TIMESTAMP (epoch-micros long lane), and DECIMAL (unscaled reconstruction) — plus a null struct row.
        var date = new DateOnly(2024, 1, 2);
        var timestamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var rows = new List<DateRow>
        {
            new() { Id = 1, S = new DateInner { D = date, Ts = timestamp, Dec = 12.3400m } },
            new() { Id = 2, S = null },
        };
        byte[] bytes = await WriteAsync(rows);

        DecimalType decimalType = DataTypes.CreateDecimalType(18, 4);
        StructType st = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("D", DataTypes.DateType, nullable: false),
            DataTypes.CreateStructField("Ts", DataTypes.TimestampType, nullable: false),
            DataTypes.CreateStructField("Dec", decimalType, nullable: false),
        });
        var requested = new StructType(new[] { new StructField("S", st, nullable: true) });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));

        int expectedEpochDay = date.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        Assert.Equal(expectedEpochDay, s.Child("D").GetValue<int>(0));

        long expectedMicros = (timestamp.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
        Assert.Equal(expectedMicros, s.Child("Ts").GetValue<long>(0));

        Assert.Equal(12.3400m, ParquetTypeMapping.ReadDecimal(s.Child("Dec"), decimalType, 0));

        Assert.True(s.IsNull(1)); // null struct → all leaves null
        Assert.True(s.Child("D").IsNull(1));
        Assert.True(s.Child("Ts").IsNull(1));
        Assert.True(s.Child("Dec").IsNull(1));
    }

    [Fact]
    public async Task NestedWithinNested_PresentColumn_FailsClosed_DecodePathGuard()
    {
        // A8: request a PRESENT array<int> against a file column that is actually array<struct>. The requested
        // scalar-element array clears the front-line EnsureReadSupported, so the rejection comes from the
        // decode-path shape guard ("the file column is itself nested") — proving that guard is exercised, not
        // shadowed by the front line (as the absent-column ArrayOfStruct_FailsClosed case is).
        var rows = new List<NestedListRow>
        {
            new() { Id = 1, Items = new List<Inner> { new() { A = 1, B = "x" } } },
        };
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Items", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("file column is itself nested", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedColumnResolution_UsesRawName_NotSanitizedName()
    {
        // Kills: byName.TryGetValue(DiagnosticText.Sanitize(name), ...) at the nested-column arm in
        // ParquetFileReader.ResolveFileFields. If the lookup were sanitized, the RAW name lookup would
        // fail to find the column (DiagnosticText.Sanitize("s\u200b") != "s\u200b"), throwing
        // ColumnNotPresentInFile. With the correct RAW lookup the read succeeds.
        //
        // U+200B (ZERO WIDTH SPACE, Unicode category Cf/Format) is sanitized to U+FFFD by
        // DiagnosticText.Sanitize, so Sanitize("s\u200b") == "s\uFFFD" — an injectable/format category
        // character. The test asserts the read succeeds with the raw name, proving the nested-column arm
        // uses the unmodified field name for its byName lookup, not the sanitized diagnostic form.
        const string rawName = "s\u200b";

        // Write a struct column with a U+200B in its top-level name using the low-level Parquet.Net API
        // (ParquetSerializer only accepts CLR property names, which can't carry U+200B).
        var structField = new global::Parquet.Schema.StructField(
            rawName,
            new global::Parquet.Schema.DataField<int>("A"));
        var parquetSchema = new global::Parquet.Schema.ParquetSchema(
            new global::Parquet.Schema.DataField<int>("Id"), structField);
        global::Parquet.Schema.DataField[] leaves = parquetSchema.GetDataFields(); // [Id, s\u200b.A]

        using var writeStream = new MemoryStream();
        await using (global::Parquet.ParquetWriter writer =
            await global::Parquet.ParquetWriter.CreateAsync(parquetSchema, writeStream))
        {
            using global::Parquet.ParquetRowGroupWriter rg = writer.CreateRowGroup();
            // Row 1: Id=1, s\u200b.A=10; Row 2: Id=2, s\u200b.A=20
            await rg.WriteAsync<int>(leaves[0], new ReadOnlyMemory<int?>(new int?[] { 1, 2 }), null, null, CancellationToken.None);
            await rg.WriteAsync<int>(leaves[1], new ReadOnlyMemory<int?>(new int?[] { 10, 20 }), null, null, CancellationToken.None);
        }

        byte[] bytes = writeStream.ToArray();

        // Request the struct column by its RAW name (containing U+200B).
        StructType innerType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
        });
        var requested = new StructType(new[]
        {
            new StructField("Id", DataTypes.IntegerType, nullable: false),
            new StructField(rawName, innerType, nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        Assert.Equal(2, batch.RowCount);

        ColumnVector id = batch.Column("Id");
        Assert.Equal(1, id.GetValue<int>(0));
        Assert.Equal(2, id.GetValue<int>(1));

        var s = Assert.IsType<StructColumnVector>(batch.Column(rawName));
        Assert.False(s.IsNull(0));
        Assert.False(s.IsNull(1));
        Assert.Equal(10, s.Child("A").GetValue<int>(0));
        Assert.Equal(20, s.Child("A").GetValue<int>(1));
    }

    // ---- §3.1 585a round-trip cells: arbitrary-depth nested DECODE (value + null-structure identity) --------
    //
    // Harness (stated per the design): each cell WRITES a REAL depth-2+ Parquet file via ParquetSerializer
    // (the production serializer, which emits correct rep/def level streams for the nested-within-nested
    // shape), then READS it back through the production ParquetFileReader.ReadAsync -> NestedParquetColumnReader
    // decode (a REAL decode: ColumnVector reconstruction, not a footer/schema-only probe). Describe() renders
    // each reconstructed cell's EXACT value AND null-structure across arbitrary depth, so the assert pins the
    // full grouping (inner-list boundaries, map entries, struct fields, three-way null/empty/present).

    [Fact]
    public async Task ArrayOfStruct_Depth2_RoundTrips()
    {
        // §3.1 cell 1 (array-of-struct): null outer list, empty outer list, and a populated list of structs
        // (one with a null string leaf) all reconstruct with per-element struct grouping intact.
        string[] got = await RoundTripDescribe(
            new List<NestedListRow>
            {
                new() { Id = 0, Items = null },
                new() { Id = 1, Items = new List<Inner>() },
                new() { Id = 2, Items = new List<Inner> { new() { A = 1, B = "x" }, new() { A = 2, B = null } } },
            },
            "Items",
            DataTypes.CreateArrayType(
                DataTypes.CreateStructType(new[]
                {
                    DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
                    DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
                }),
                containsNull: true));

        Assert.Equal(new[] { "null", "[]", "[(A=1,B=x),(A=2,B=null)]" }, got);
    }

    [Fact]
    public async Task StructOfArray_Depth2_RoundTrips()
    {
        // §3.1 cell 2 (struct-of-array): the struct is transparent to repetition, so its inner list field
        // reconstructs the three-way null-list / empty-list / present-list distinction PER present struct,
        // and a null struct yields a null cell (no phantom inner list).
        string[] got = await RoundTripDescribe(
            new List<SoARow>
            {
                new() { Id = 0, S = null },
                new() { Id = 1, S = new SoA { Xs = null } },
                new() { Id = 2, S = new SoA { Xs = new List<int?>() } },
                new() { Id = 3, S = new SoA { Xs = new List<int?> { 5, 6 } } },
            },
            "S",
            DataTypes.CreateStructType(new[]
            {
                DataTypes.CreateStructField(
                    "Xs", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
            }));

        Assert.Equal(new[] { "null", "(Xs=null)", "(Xs=[])", "(Xs=[5,6])" }, got);
    }

    [Fact]
    public async Task StructOfStruct_Depth2_RoundTrips()
    {
        // §3.1 cell 3 (struct-of-struct): a null outer struct, a present outer struct wrapping a null inner
        // struct, and a fully-present nested struct all reconstruct distinctly (the null-struct-of-null-struct
        // adjacency must not collapse).
        string[] got = await RoundTripDescribe(
            new List<SoSRow>
            {
                new() { Id = 0, M = null },
                new() { Id = 1, M = new Mid { Deep = null } },
                new() { Id = 2, M = new Mid { Deep = new Inner { A = 7, B = "z" } } },
            },
            "M",
            DataTypes.CreateStructType(new[]
            {
                DataTypes.CreateStructField(
                    "Deep",
                    DataTypes.CreateStructType(new[]
                    {
                        DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
                        DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
                    }),
                    nullable: true),
            }));

        Assert.Equal(new[] { "null", "(Deep=null)", "(Deep=(A=7,B=z))" }, got);
    }

    [Fact]
    public async Task MapOfStruct_Depth2_RoundTrips()
    {
        // §3.1 cell 4 (map-of-struct): null map, empty map, and a single-entry map whose value is a struct.
        string[] got = await RoundTripDescribe(
            new List<MapOfStructRow>
            {
                new() { Id = 0, M = null },
                new() { Id = 1, M = new Dictionary<string, Inner?>(StringComparer.Ordinal) },
                new()
                {
                    Id = 2,
                    M = new Dictionary<string, Inner?>(StringComparer.Ordinal) { ["k"] = new Inner { A = 3, B = "y" } },
                },
            },
            "M",
            DataTypes.CreateMapType(
                DataTypes.StringType,
                DataTypes.CreateStructType(new[]
                {
                    DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
                    DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
                }),
                valueContainsNull: true));

        Assert.Equal(new[] { "null", "{}", "{k=(A=3,B=y)}" }, got);
    }

    [Fact]
    public async Task ArrayOfArray_Depth2_OuterOffsetsCountInnerLists_NotLeaves()
    {
        // §3.1 cell 5 (array-of-array) — THE bug the BuildRepeatedStructure rewrite fixes. The OUTER offsets
        // must count inner-LIST occurrences, NOT flattened leaf values. For rows [null, [], [[],[]], [[10,20],
        // [30]]] the design §2.4 trace requires OUTER offsets [0,0,0,2,4] (inner-list counts) and INNER offsets
        // [0,0,0,2,3] (leaf counts). The pre-fix top-level-hardwired reader produced OUTER [0,0,0,2,5] (leaf
        // counts) — flattening the two-level grouping. This cell asserts the reconstructed grouping directly.
        var rows = new List<ArrayOfArrayRow>
        {
            new() { Id = 0, Outer = null },
            new() { Id = 1, Outer = new List<List<int?>?>() },
            new() { Id = 2, Outer = new List<List<int?>?> { new(), new() } },
            new() { Id = 3, Outer = new List<List<int?>?> { new() { 10, 20 }, new() { 30 } } },
        };

        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Outer",
                DataTypes.CreateArrayType(
                    DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), containsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var outer = Assert.IsType<ListColumnVector>(batch.Column("Outer"));

        // Reconstruct the OUTER offset array [0,0,0,2,4] from per-row inner-list counts (RawElementSpan length).
        var outerOffsets = new int[batch.RowCount + 1];
        for (int r = 0; r < batch.RowCount; r++)
        {
            (int _, int len) = outer.RawElementSpan(r);
            outerOffsets[r + 1] = outerOffsets[r] + len;
        }

        Assert.Equal(new[] { 0, 0, 0, 2, 4 }, outerOffsets);

        // And the INNER offset array [0,0,0,2,3] over those 4 inner lists (leaf counts).
        var inner = Assert.IsType<ListColumnVector>(outer.Elements);
        var innerOffsets = new int[5];
        for (int e = 0; e < 4; e++)
        {
            (int _, int len) = inner.RawElementSpan(e);
            innerOffsets[e + 1] = innerOffsets[e] + len;
        }

        Assert.Equal(new[] { 0, 0, 0, 2, 3 }, innerOffsets);

        // Full value+structure identity: null | [] | [[],[]] | [[10,20],[30]] (NOT a flattened [10,20,30]).
        Assert.Equal("null", Describe(outer, 0));
        Assert.Equal("[]", Describe(outer, 1));
        Assert.Equal("[[],[]]", Describe(outer, 2));
        Assert.Equal("[[10,20],[30]]", Describe(outer, 3));
    }

    [Fact]
    public async Task MapOfMap_Depth2_RoundTrips()
    {
        // §3.1 cell 6 (map-of-map): null map, empty map, and a single outer entry whose value is itself a map.
        string[] got = await RoundTripDescribe(
            new List<MapOfMapRow>
            {
                new() { Id = 0, M = null },
                new() { Id = 1, M = new Dictionary<string, Dictionary<string, int?>?>(StringComparer.Ordinal) },
                new()
                {
                    Id = 2,
                    M = new Dictionary<string, Dictionary<string, int?>?>(StringComparer.Ordinal)
                    {
                        ["a"] = new Dictionary<string, int?>(StringComparer.Ordinal) { ["p"] = 1 },
                    },
                },
            },
            "M",
            DataTypes.CreateMapType(
                DataTypes.StringType,
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                valueContainsNull: true));

        Assert.Equal(new[] { "null", "{}", "{a={p=1}}" }, got);
    }

    [Fact]
    public async Task ArrayOfMap_Depth2_RoundTrips()
    {
        // §3.1 cell 7 (array-of-map): a list whose elements are maps (one populated, one empty).
        string[] got = await RoundTripDescribe(
            new List<ArrayOfMapRow>
            {
                new() { Id = 0, Arr = null },
                new() { Id = 1, Arr = new List<Dictionary<string, int?>?>() },
                new()
                {
                    Id = 2,
                    Arr = new List<Dictionary<string, int?>?>
                    {
                        new(StringComparer.Ordinal) { ["p"] = 1 },
                        new(StringComparer.Ordinal),
                    },
                },
            },
            "Arr",
            DataTypes.CreateArrayType(
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                containsNull: true));

        Assert.Equal(new[] { "null", "[]", "[{p=1},{}]" }, got);
    }

    [Fact]
    public async Task MapOfArray_Depth2_RoundTrips()
    {
        // §3.1 cell 8 (map-of-array): a map whose values are lists.
        string[] got = await RoundTripDescribe(
            new List<MapOfArrayRow>
            {
                new() { Id = 0, M = null },
                new() { Id = 1, M = new Dictionary<string, List<int?>?>(StringComparer.Ordinal) },
                new()
                {
                    Id = 2,
                    M = new Dictionary<string, List<int?>?>(StringComparer.Ordinal) { ["k"] = new List<int?> { 9, 8 } },
                },
            },
            "M",
            DataTypes.CreateMapType(
                DataTypes.StringType,
                DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true),
                valueContainsNull: true));

        Assert.Equal(new[] { "null", "{}", "{k=[9,8]}" }, got);
    }

    [Fact]
    public async Task ArrayOfStructOfArray_Depth3_RoundTrips()
    {
        // §3.1 cell 9 (depth-3, array<struct<array>>): three repeated/optional levels compose — the outer list
        // groups structs, each struct is transparent to repetition, and each struct's inner list reconstructs
        // its own leaf grouping (one populated, one empty).
        string[] got = await RoundTripDescribe(
            new List<ArrayOfStructOfArrayRow>
            {
                new() { Id = 0, Arr = null },
                new()
                {
                    Id = 1,
                    Arr = new List<StructWithArray>
                    {
                        new() { Xs = new List<int?> { 1, 2 } },
                        new() { Xs = new List<int?>() },
                    },
                },
            },
            "Arr",
            DataTypes.CreateArrayType(
                DataTypes.CreateStructType(new[]
                {
                    DataTypes.CreateStructField(
                        "Xs", DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: true), nullable: true),
                }),
                containsNull: true));

        Assert.Equal(new[] { "null", "[(Xs=[1,2]),(Xs=[])]" }, got);
    }

    [Fact]
    public async Task MapOfStructOfMap_Depth3_RoundTrips()
    {
        // §3.1 cell 10 (depth-3, map<struct<map>>): the outer map's value is a struct whose field is itself a
        // map — three nested container levels reconstructed end-to-end.
        string[] got = await RoundTripDescribe(
            new List<MapOfStructOfMapRow>
            {
                new() { Id = 0, M = null },
                new()
                {
                    Id = 1,
                    M = new Dictionary<string, StructWithMap?>(StringComparer.Ordinal)
                    {
                        ["k"] = new StructWithMap
                        {
                            M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["p"] = 4 },
                        },
                    },
                },
            },
            "M",
            DataTypes.CreateMapType(
                DataTypes.StringType,
                DataTypes.CreateStructType(new[]
                {
                    DataTypes.CreateStructField(
                        "M",
                        DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: true),
                        nullable: true),
                }),
                valueContainsNull: true));

        Assert.Equal(new[] { "null", "{k=(M={p=4})}" }, got);
    }

    [Fact]
    public async Task AllScalarLeavesAtDepth_ArrayOfWideStruct_RoundTrips()
    {
        // §3.1 all-scalar-leaves-at-depth: an array<struct<long,double,bool>> exercises three DISTINCT scalar
        // physical leaf types at depth 2 under a repeated ancestor (each leaf carries the ancestor's placeholder
        // slots; each must decode with the correct per-element value).
        var rows = new List<ArrayOfWideRow>
        {
            new() { Id = 0, Arr = null },
            new()
            {
                Id = 1,
                Arr = new List<Wide>
                {
                    new() { L = 100L, D = 1.5, Flag = true },
                    new() { L = -7L, D = 2.5, Flag = false },
                },
            },
        };

        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[]
        {
            new StructField(
                "Arr",
                DataTypes.CreateArrayType(
                    DataTypes.CreateStructType(new[]
                    {
                        DataTypes.CreateStructField("L", DataTypes.LongType, nullable: false),
                        DataTypes.CreateStructField("D", DataTypes.DoubleType, nullable: false),
                        DataTypes.CreateStructField("Flag", DataTypes.BooleanType, nullable: false),
                    }),
                    containsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        Assert.True(arr.IsNull(0));
        Assert.Equal("[(L=100,D=1.5,Flag=true),(L=-7,D=2.5,Flag=false)]", Describe(arr, 1));
    }

    // -------------------------------------------------------------------------------------------------
    // §3.1 cells 14/15/16 — the recursive map guards (EnsureCanonicalMapChildNames / EnsureRequiredMapKey)
    // must fire at the INNER (depth-2) map, not only at the top-level map. Parquet.Net binds a map's
    // key_value children POSITIONALLY (MapField.Assign: first → Key, second → Value), so a mis-named or
    // key/value-transposed INNER map<T,T> silently transposes past the type/level guards — a silent
    // data-corruption class. 585a runs both map guards on EVERY map node the shape validator visits
    // (NestedParquetColumnReader.ValidateNode :183-184); these cells pin the DEPTH-2 application. No released
    // writer can author these footers (the MapField ctor forbids a nullable key and always emits canonical
    // names), so — mirroring the depth-1 sibling in NestedColumnMappingGuardCoverageTests and the internal
    // crafted-stream cells here — the malformed file field tree is hand-built and driven straight through the
    // shape-resolution door (ValidateShape), which fails closed BEFORE any data page is read.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void NonCanonicalMapChildNames_AtInnerMap_FailsClosed()
    {
        // §3.1-14: a depth-2 shape array<map<string,long>> whose INNER map's key_value children are named
        // 'k'/'v' (not the canonical 'key'/'value'). The OUTER container is a well-formed list (name-agnostic —
        // single-child lists have no transposition hazard, so the list arm runs no name check), so the ONLY
        // guard that can fire is the recursive EnsureCanonicalMapChildNames applied at the INNER map node. A
        // reader that stopped recursing the map guard into inner maps would read this transposition-prone shape
        // silently. Expect a typed SchemaMismatch.
        var fileField = new global::Parquet.Schema.ListField(
            "Arr",
            new global::Parquet.Schema.MapField(
                "element",
                new global::Parquet.Schema.DataField<string>("k", false),
                new global::Parquet.Schema.DataField<long>("v")));
        var requested = DataTypes.CreateArrayType(
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true),
            containsNull: true);

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(fileField, requested, "Arr", allowTypeWideningPromotion: false));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("not named the canonical", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapWithNullableKey_AtDepth2_FailsClosed()
    {
        // §3.1-15: a depth-2 shape array<map<string,long>> whose INNER map has a NULLABLE key. Parquet.Net's
        // MapField ctor itself forbids a nullable key ("map's key cannot be nullable"), so no released writer —
        // and no ctor path — can author this footer; the malformed state is synthesized by lifting the inner
        // key leaf's definition level one above its map's own level (exactly the shape a nullable key would
        // have). The inner map's children ARE canonically named, so EnsureCanonicalMapChildNames passes and the
        // ONLY guard that can fire is the recursive EnsureRequiredMapKey applied at the INNER map node. Expect a
        // typed SchemaMismatch.
        var innerMap = new global::Parquet.Schema.MapField(
            "element",
            new global::Parquet.Schema.DataField<string>("key", false),
            new global::Parquet.Schema.DataField<long>("value"));
        var fileField = new global::Parquet.Schema.ListField("Arr", innerMap);

        // Force the inner map's key to LOOK nullable: a required map key has MaxDefinitionLevel == the map's own
        // MaxDefinitionLevel; a nullable key is one higher. The setter is internal to Parquet.Net, so reflect.
        System.Reflection.PropertyInfo maxDef = typeof(global::Parquet.Schema.Field).GetProperty(
            "MaxDefinitionLevel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
        maxDef.GetSetMethod(nonPublic: true)!.Invoke(innerMap.Key, new object[] { innerMap.MaxDefinitionLevel + 1 });
        Assert.NotEqual(innerMap.MaxDefinitionLevel, innerMap.Key.MaxDefinitionLevel); // fixture precondition

        var requested = DataTypes.CreateArrayType(
            DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true),
            containsNull: true);

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(fileField, requested, "Arr", allowTypeWideningPromotion: false));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("map key is nullable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InnerMapKeyValueTransposed_WitnessDisjoint_FailsClosed()
    {
        // §3.1-16 (the crux — silent-data-corruption cell): a depth-2 shape map<string, map<long,long>> whose
        // INNER map has a REQUIRED value and its key/value children SWAPPED. Both inner children are `long` and
        // both REQUIRED, so the type guard, the leaf structural-level guard, and EnsureRequiredMapKey are all a
        // WITNESS-DISJOINT no-op here — none can separate the two children. ONLY the recursive
        // EnsureCanonicalMapChildNames, applied at the INNER map, catches the transposition. The OUTER map is
        // fully well-formed (canonical 'key'/'value', required string key), so this cell fires SPECIFICALLY
        // because the guard RECURSES into the inner map: a regression that guarded only the top-level map would
        // ship this silent key/value transposition of an inner map<long,long>. This mirrors the depth-1
        // map<long,long> transposition witness (design §3 ncm transposition cell) lifted to DEPTH 2. Expect a
        // typed SchemaMismatch.
        var innerTransposed = new global::Parquet.Schema.MapField(
            "value",
            new global::Parquet.Schema.DataField<long>("value"),  // positionally the KEY, mis-named 'value'
            new global::Parquet.Schema.DataField<long>("key"));   // positionally the VALUE, mis-named 'key'
        var fileField = new global::Parquet.Schema.MapField(
            "M",
            new global::Parquet.Schema.DataField<string>("key", false),
            innerTransposed);
        var requested = DataTypes.CreateMapType(
            DataTypes.StringType,
            DataTypes.CreateMapType(DataTypes.LongType, DataTypes.LongType, valueContainsNull: false),
            valueContainsNull: true);

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(fileField, requested, "M", allowTypeWideningPromotion: false));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("not named the canonical", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalPrecision29_AtDepth_FailsClosed_UnsupportedFeature()
    {
        // §3.1 fail-closed: a decimal leaf with precision 29 ( > 28, outside decimal's unscaled range) at depth
        // must fail closed at shape resolution, echoing the sanitized nested PATH — fail-closed parity holds at
        // any depth (design §2.6). The file content is irrelevant: EnsureReadSupported rejects the requested
        // type BEFORE any column read.
        var requested = new StructField(
            "Arr",
            DataTypes.CreateArrayType(
                DataTypes.CreateStructType(new[]
                {
                    DataTypes.CreateStructField("Dec", DataTypes.CreateDecimalType(29, 4), nullable: true),
                }),
                containsNull: true),
            nullable: true);

        DeltaStorageException error =
            Assert.Throws<DeltaStorageException>(() => ParquetTypeMapping.EnsureReadSupported(requested));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public async Task NestedLeafWidening_AtDepth_StaysFailClosed_SchemaMismatch()
    {
        // §3.1 fail-closed (585b stays out of scope): requesting a nested int leaf as LONG is a WIDENING. 585a
        // does NOT widen nested leaves (blocked on #546), so a physical mismatch at depth must fail closed with
        // an EXACT-physical-type reject — not a silent int->long promotion. File is array<struct<A:int>>.
        var rows = new List<NestedListRow>
        {
            new() { Id = 1, Items = new List<Inner> { new() { A = 1, B = "x" } } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Items",
                DataTypes.CreateArrayType(
                    DataTypes.CreateStructType(new[]
                    {
                        DataTypes.CreateStructField("A", DataTypes.LongType, nullable: false), // WIDENING int->long
                        DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
                    }),
                    containsNull: true),
                nullable: true),
        });

        DeltaStorageException error =
            await Assert.ThrowsAsync<DeltaStorageException>(() => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public void OverMaxNestedReadDepth_FailsClosed_UnsupportedFeature()
    {
        // §3.1 fail-closed (DoS bound, design §2.6): a requested schema nesting deeper than MaxNestedReadDepth
        // (64) is rejected DETERMINISTICALLY at shape resolution — BEFORE any allocation/descent — as a typed
        // UnsupportedFeature, never a StackOverflowException. 65 array levels put the leaf at depth 65 > 64.
        DataType overDeep = DataTypes.IntegerType;
        for (int i = 0; i < NestedParquetColumnReader.MaxNestedReadDepth + 1; i++)
        {
            overDeep = DataTypes.CreateArrayType(overDeep, containsNull: true);
        }

        var field = new StructField("Deep", overDeep, nullable: true);
        DeltaStorageException error =
            Assert.Throws<DeltaStorageException>(() => ParquetTypeMapping.EnsureReadSupported(field));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("nests deeper than the supported limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtMaxNestedReadDepth_ValidationAccepts()
    {
        // §3.1: a requested schema nesting EXACTLY to MaxNestedReadDepth (64) is accepted by shape resolution
        // (the bound is inclusive). A real depth-64 round-trip is infeasible via the serializer, so this pins
        // the at-bound acceptance at the validation door (the complement of the over-bound reject above).
        DataType atBound = DataTypes.IntegerType;
        for (int i = 0; i < NestedParquetColumnReader.MaxNestedReadDepth; i++)
        {
            atBound = DataTypes.CreateArrayType(atBound, containsNull: true);
        }

        // Must NOT throw.
        ParquetTypeMapping.EnsureReadSupported(new StructField("Deep", atBound, nullable: true));
    }

    [Fact]
    public void BuildRepeatedStructure_AtNestedInnerLevel_RejectsCorruption_CorruptData()
    {
        // §3.1 fail-closed (crafted def/rep corruption at depth 2): the rewritten BuildRepeatedStructure runs
        // at a DEEP repeated level (parentMaxRep > 0). These crafted inner-list level streams — which no
        // conforming writer can emit — must fail closed rather than reconstruct a phantom inner list. Levels
        // model the INNER list of array<array<int>>: thisMaxRep=2, thisMaxDef=4, parentMaxRep=1, parentMaxDef=2
        // (emptyContainerDef = thisMaxDef-1 = 3).

        // (a) A leading THIS-LEVEL continuation (rep=2, > parentMaxRep) with no owner yet opened.
        DeltaStorageException leading = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 4 }, rep: new[] { 2 }, numValues: 1, thisMaxDef: 4, thisMaxRep: 2,
                parentMaxDef: 2, parentMaxRep: 1, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col.inner"));
        Assert.Equal(StorageErrorKind.CorruptData, leading.Kind);

        // (b) A NULL inner container (def=2 < emptyContainerDef 3) opened at the parent boundary, then a
        // this-level continuation (rep=2) — a null/empty inner container has no element and cannot be continued.
        DeltaStorageException continueAfterNull = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 2, 4 }, rep: new[] { 0, 2 }, numValues: 2, thisMaxDef: 4, thisMaxRep: 2,
                parentMaxDef: 2, parentMaxRep: 1, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col.inner"));
        Assert.Equal(StorageErrorKind.CorruptData, continueAfterNull.Kind);

        // (c) More parent-boundary openings (rep=0, def>=parentMaxDef) than the owner count the parent decoded.
        DeltaStorageException tooManyOwners = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 4, 4 }, rep: new[] { 0, 0 }, numValues: 2, thisMaxDef: 4, thisMaxRep: 2,
                parentMaxDef: 2, parentMaxRep: 1, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col.inner"));
        Assert.Equal(StorageErrorKind.CorruptData, tooManyOwners.Kind);
    }

    [Fact]
    public void BuildRepeatedStructure_AtDepth3InnerLevel_RejectsLeadingContinuation_CorruptData()
    {
        // §3.1 fail-closed (crafted corruption at depth 3): the innermost list of array<array<array<int>>> has
        // thisMaxRep=3, thisMaxDef=6, parentMaxRep=2, parentMaxDef=4. A leading this-level continuation (rep=3)
        // with no owner opened is a structural contradiction and must fail closed at any depth.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() =>
            NestedParquetColumnReader.BuildRepeatedStructure(
                def: new[] { 6 }, rep: new[] { 3 }, numValues: 1, thisMaxDef: 6, thisMaxRep: 3,
                parentMaxDef: 4, parentMaxRep: 2, ownerCells: 1,
                offsets: new int[2], nulls: new bool[1], columnName: "col.inner3"));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
    }

    [Fact]
    public void BuildRepeatedStructure_TopLevel_IsBytePreserved_NoRegression()
    {
        // §3.1 regression: the TOP repeated level (parentMaxRep=0, parentMaxDef=0, single repeated level)
        // reduces byte-identically to the pre-585a `rep==0` owner boundary + ungated `d >= thisMaxDef` count
        // — the rep<=thisMaxRep gate is vacuously true for a single repeated level (no #571 regression). Verify
        // the exact §2.4 OUTER decode of [null, [], [[],[]], [[10,20],[30]]] at the outer level:
        // outer offsets [0,0,0,2,4], nulls [true,false,false,false] (only the null OUTER list is null).
        // Outer level: thisMaxRep=1, thisMaxDef=2 (null=0, empty=1, present-inner=2). The inner-list openers
        // for row 3 are the two rep=1 continuations of the outer list; the third leaf (rep=2) belongs to the
        // INNER level and is EXCLUDED here.
        var offsets = new int[5];
        var nulls = new bool[4];
        int total = NestedParquetColumnReader.BuildRepeatedStructure(
            def: new[] { 0, 1, 2, 2, 4, 4, 4 },
            rep: new[] { 0, 0, 0, 1, 0, 1, 2 },
            numValues: 7, thisMaxDef: 2, thisMaxRep: 1, parentMaxDef: 0, parentMaxRep: 0, ownerCells: 4,
            offsets, nulls, "outer");

        Assert.Equal(new[] { 0, 0, 0, 2, 4 }, offsets);
        Assert.Equal(new[] { true, false, false, false }, nulls);
        Assert.Equal(4, total);
    }

    // Round-trips a nested column through the production serializer + reader and renders each reconstructed
    // cell with Describe (value + null-structure identity across arbitrary depth).
    private static async Task<string[]> RoundTripDescribe<T>(IReadOnlyList<T> rows, string col, DataType colType)
        where T : class, new()
    {
        byte[] bytes = await WriteAsync(rows);
        var requested = new StructType(new[] { new StructField(col, colType, nullable: true) });
        ColumnBatch batch = await ReadSingleAsync(bytes, requested);
        ColumnVector v = batch.Column(col);
        var result = new string[batch.RowCount];
        for (int r = 0; r < batch.RowCount; r++)
        {
            result[r] = Describe(v, r);
        }

        return result;
    }

    // ===== #546: per-leaf type-widening promotion on the nested read path =====================

    [Fact]
    public async Task Array_ElementWidening_IntToLong_WhenEnabled_Promotes_AndPreservesNulls()
    {
        // A pre-widening array<int> file read under a widened array<long> schema: each present element is
        // promoted INT32 → INT64, and the null element / null list structure is preserved intact.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 10, 20 } },
            new() { Id = 2, Arr = null },
            new() { Id = 3, Arr = new List<int?> { int.MaxValue, null, int.MinValue } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.LongType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));

        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(DataTypes.LongType, e0.Type);
        Assert.Equal(10L, e0.GetValue<long>(0));
        Assert.Equal(20L, e0.GetValue<long>(1));

        Assert.True(arr.IsNull(1)); // null list preserved through promotion

        ColumnVector e2 = arr.ElementsAt(2);
        Assert.Equal(int.MaxValue, e2.GetValue<long>(0));
        Assert.True(e2.IsNull(1)); // null element preserved
        Assert.Equal(int.MinValue, e2.GetValue<long>(2));
    }

    [Fact]
    public async Task Array_ElementWidening_IntToDouble_CrossFamily_WhenEnabled_Promotes()
    {
        // #535 cross-family int→double at a nested element position, gated identically to the scalar path.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 5, null, -3 } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.DoubleType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(DataTypes.DoubleType, e0.Type);
        Assert.Equal(5.0, e0.GetValue<double>(0));
        Assert.True(e0.IsNull(1));
        Assert.Equal(-3.0, e0.GetValue<double>(2));
    }

    [Fact]
    public async Task Array_ElementWidening_FloatToDouble_WhenEnabled_Promotes()
    {
        var rows = new List<FloatListRow>
        {
            new() { Id = 1, Arr = new List<float?> { 1.5f, null, -2.25f } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.DoubleType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(1.5, e0.GetValue<double>(0));
        Assert.True(e0.IsNull(1));
        Assert.Equal(-2.25, e0.GetValue<double>(2));
    }

    [Fact]
    public async Task Array_ElementWidening_IntToDecimal_Fits_WhenEnabled_Promotes()
    {
        // #535 cross-family int→decimal(12,2): p−s = 10 ≥ 10 int-digit capacity, so the decimal holds the full
        // INT32 range — the decimal-fit boundary the read path enforces via TypeWidening.IsSanctionedWidening.
        var rows = new List<ListRow>
        {
            new() { Id = 1, Arr = new List<int?> { 5, null, -3 } },
        };
        byte[] bytes = await WriteAsync(rows);

        var wide = DataTypes.CreateDecimalType(12, 2);
        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(wide, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(5.00m, ParquetTypeMapping.ReadDecimal(e0, wide, 0));
        Assert.True(e0.IsNull(1));
        Assert.Equal(-3.00m, ParquetTypeMapping.ReadDecimal(e0, wide, 2));
    }

    [Fact]
    public async Task Array_ElementWidening_IntToDecimal_DoesNotFit_FailsClosed()
    {
        // Decimal-fit boundary: decimal(9,2) has p−s = 7 < 10 int digits, so it CANNOT hold the full INT32
        // range — not a Delta-sanctioned widening, so the read fails closed even with the gate open (never a
        // truncating promotion).
        var rows = new List<ListRow> { new() { Id = 1, Arr = new List<int?> { 5 } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.CreateDecimalType(9, 2), containsNull: true),
                nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSinglePromotedAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Array_ElementWidening_WhenGateClosed_FailsClosed_NotSilentlyPromoted()
    {
        // Fail-closed parity: with the promotion gate CLOSED, a narrow array<int> file requested as
        // array<long> is a physical mismatch — the same fail-closed behavior the scalar path has (#495).
        var rows = new List<ListRow> { new() { Id = 1, Arr = new List<int?> { 10 } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.LongType), nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Array_ElementNarrowing_WhenEnabled_FailsClosed()
    {
        // Narrowing (long→int) is not a sanctioned widening: even with the gate open the read fails closed.
        var rows = new List<LongListRow> { new() { Id = 1, Arr = new List<long?> { 10L } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSinglePromotedAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Array_ElementWidening_LongToDouble_IsLossy_FailsClosedEvenWithGate()
    {
        // long→double is LOSSY (not a Delta-sanctioned widening), so it is never promoted — fail-closed even
        // with the gate open, matching the scalar path's exclusion of long→double.
        var rows = new List<LongListRow> { new() { Id = 1, Arr = new List<long?> { 10L } } };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField("Arr", DataTypes.CreateArrayType(DataTypes.DoubleType), nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSinglePromotedAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Map_ValueWidening_IntToLong_WhenEnabled_Promotes_AndPreservesNulls()
    {
        var rows = new List<MapRow>
        {
            new() { Id = 1, M = new Dictionary<string, int?>(StringComparer.Ordinal) { ["k1"] = 100, ["k2"] = null } },
            new() { Id = 2, M = null },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));
        Assert.Equal(2, m.EntryLength(0));
        Assert.True(m.IsNull(1)); // null map preserved

        ColumnVector keys = m.KeysAt(0);
        ColumnVector vals = m.ValuesAt(0);
        Assert.Equal(DataTypes.LongType, vals.Type);
        var read = new Dictionary<string, long?>(StringComparer.Ordinal);
        for (int i = 0; i < 2; i++)
        {
            read[Utf8(keys, i)] = vals.IsNull(i) ? null : vals.GetValue<long>(i);
        }

        Assert.Equal(100L, read["k1"]);
        Assert.Null(read["k2"]); // null value preserved through promotion
    }

    [Fact]
    public async Task Map_KeyWidening_IntToLong_WhenEnabled_Promotes()
    {
        var rows = new List<IntKeyMapRow>
        {
            new() { Id = 1, M = new Dictionary<int, string?> { [1] = "a", [2] = "b" } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "M",
                DataTypes.CreateMapType(DataTypes.LongType, DataTypes.StringType, valueContainsNull: true),
                nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var m = Assert.IsType<MapColumnVector>(batch.Column("M"));
        ColumnVector keys = m.KeysAt(0);
        ColumnVector vals = m.ValuesAt(0);
        Assert.Equal(DataTypes.LongType, keys.Type);
        var read = new Dictionary<long, string?>();
        for (int i = 0; i < m.EntryLength(0); i++)
        {
            read[keys.GetValue<long>(i)] = Utf8(vals, i);
        }

        Assert.Equal("a", read[1L]);
        Assert.Equal("b", read[2L]);
    }

    [Fact]
    public async Task Struct_FieldWidening_IntToLong_WhenEnabled_Promotes_AndPreservesNulls()
    {
        var rows = new List<StructRow>
        {
            new() { Id = 1, S = new Inner { A = 10, B = "x" } },
            new() { Id = 2, S = null },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType structType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("Id", DataTypes.IntegerType, nullable: false),
            new StructField("S", structType, nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var s = Assert.IsType<StructColumnVector>(batch.Column("S"));
        ColumnVector a = s.Child("A");
        Assert.Equal(DataTypes.LongType, a.Type);
        Assert.Equal(10L, a.GetValue<long>(0));
        Assert.True(a.IsNull(1)); // null struct materializes null children, preserved through promotion
    }

    [Fact]
    public async Task Struct_FieldWidening_WhenGateClosed_FailsClosed()
    {
        // Fail-closed parity for a struct field: with the gate closed, a narrow int field requested as long
        // is a physical mismatch.
        var rows = new List<StructRow> { new() { Id = 1, S = new Inner { A = 10, B = "x" } } };
        byte[] bytes = await WriteAsync(rows);

        StructType structType = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("S", structType, nullable: true),
        });

        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSingleAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    // ----- #546 O3 (design §3.3): the depth-composed gate is NOT a plain bool -----

    [Fact]
    public async Task Array_OfStruct_InnerFieldWidening_AtDepth2_WhenEnabled_FailsClosed()
    {
        // O3 (design §3.3, required-for-merge): 585a can DECODE array<struct<a:int>>, but #546 only widens a
        // scalar leaf at container depth ≤ 1. A leaf inside a nested-within-nested shape (array<struct<a:int>>,
        // the struct field 'A' sits at depth 2) requested as array<struct<a:long>> must NOT be promoted even
        // with the gate OPEN — it stays fail-closed (SchemaMismatch), proving `promoteLeaf` is composed with
        // 585a's container depth, not a plain depth-agnostic bool. (Pairs with the write-side AC7.)
        var rows = new List<NestedListRow>
        {
            new() { Id = 1, Items = new List<Inner> { new() { A = 10, B = "x" } } },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType innerLong = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.LongType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("Items", DataTypes.CreateArrayType(innerLong, containsNull: true), nullable: true),
        });

        // Gate OPEN: the depth-2 leaf is still exact-match, so INT32 ≠ requested INT64 → SchemaMismatch.
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ReadSinglePromotedAsync(bytes, requested));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    [Fact]
    public async Task Array_OfStruct_ExactShape_WhenGateOpen_DecodesUnchanged()
    {
        // O3 companion (design §3.3, depth ≤ 1 unaffected): the 585a recursive decode of array<struct<…>> must
        // still read byte-identically when the gate is OPEN but no leaf needs promotion (exact physical match),
        // proving #546 adds no decode regression at depth ≥ 2.
        var rows = new List<NestedListRow>
        {
            new() { Id = 1, Items = new List<Inner> { new() { A = 10, B = "x" }, new() { A = 20, B = "y" } } },
        };
        byte[] bytes = await WriteAsync(rows);

        StructType innerInt = DataTypes.CreateStructType(new[]
        {
            DataTypes.CreateStructField("A", DataTypes.IntegerType, nullable: false),
            DataTypes.CreateStructField("B", DataTypes.StringType, nullable: true),
        });
        var requested = new StructType(new[]
        {
            new StructField("Items", DataTypes.CreateArrayType(innerInt, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Items"));
        var s0 = Assert.IsType<StructColumnVector>(arr.ElementsAt(0));
        ColumnVector a = s0.Child("A");
        Assert.Equal(DataTypes.IntegerType, a.Type); // exact — never promoted
        Assert.Equal(10, a.GetValue<int>(0));
        Assert.Equal(20, a.GetValue<int>(1));
    }

    // ----- #546 O2 (design §3.3 / AC13): nested date→timestamp_ntz promote + micros/LTZ identity -----

    [Fact]
    public async Task Array_ElementWidening_DateToTimestampNtz_WhenEnabled_Promotes()
    {
        // O2/AC13: a nested DATE element promoted to timestamp_ntz (#533) — each epoch-day widens to
        // epoch-micros at midnight of the date (days × 86_400_000_000), timezone-less, nulls preserved.
        var d1 = new DateOnly(2021, 3, 15);
        var d2 = new DateOnly(1970, 1, 1);
        var rows = new List<DateListRow>
        {
            new() { Id = 1, Arr = new List<DateOnly?> { d1, null, d2 } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.TimestampNtzType, containsNull: true), nullable: true),
        });

        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(DataTypes.TimestampNtzType, e0.Type);

        const long MicrosPerDay = 86_400L * 1_000_000L;
        long epochDay1 = d1.DayNumber - new DateOnly(1970, 1, 1).DayNumber;
        Assert.Equal(epochDay1 * MicrosPerDay, e0.GetValue<long>(0));
        Assert.True(e0.IsNull(1)); // null element preserved through promotion
        Assert.Equal(0L, e0.GetValue<long>(2)); // 1970-01-01 → 0 micros
    }

    [Fact]
    public async Task Array_ElementTimestampNtz_NativeMicros_WhenEnabled_TakesIdentityRead_NotPromotion()
    {
        // O2/AC13 companion: a native micros/LTZ timestamp element requested as timestamp_ntz is NOT a
        // sanctioned widening (IsSanctionedWidening(timestamp, timestamp_ntz) is false), so it takes the
        // IDENTITY micros read, never a date→ntz promotion — matching the flat #533 behavior at a nested leaf.
        var t1 = new DateTime(2021, 3, 15, 12, 30, 45, DateTimeKind.Utc);
        var rows = new List<TimestampListRow>
        {
            new() { Id = 1, Arr = new List<DateTime?> { t1, null } },
        };
        byte[] bytes = await WriteAsync(rows);

        var requested = new StructType(new[]
        {
            new StructField(
                "Arr", DataTypes.CreateArrayType(DataTypes.TimestampNtzType, containsNull: true), nullable: true),
        });

        // Gate open, but the physical timestamp lane already satisfies timestamp_ntz — identity micros read.
        ColumnBatch batch = await ReadSinglePromotedAsync(bytes, requested);
        var arr = Assert.IsType<ListColumnVector>(batch.Column("Arr"));
        ColumnVector e0 = arr.ElementsAt(0);
        Assert.Equal(DataTypes.TimestampNtzType, e0.Type);
        long expectedMicros = (t1.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
        Assert.Equal(expectedMicros, e0.GetValue<long>(0));
        Assert.True(e0.IsNull(1));
    }

    private static async Task<byte[]> WriteAsync<T>(IReadOnlyList<T> rows)
        where T : class, new()
    {
        using var stream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task EnumerateAsync(ParquetFileReader reader, byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch _ in reader.ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
        }
    }

    private static async Task<ColumnBatch> ReadSingleAsync(byte[] bytes, StructType requested) =>
        await ReadSingleAsync(new ParquetFileReader(), bytes, requested);

    // Reads with the type-widening promotion gate OPEN (#546) — the read-side counterpart of a table whose
    // protocol declares the `typeWidening` feature. A pre-widening (narrow) nested leaf is promoted per value
    // into the requested wide lane.
    private static async Task<ColumnBatch> ReadSinglePromotedAsync(byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: true,
            CancellationToken.None))
        {
            Assert.Null(only);
            only = batch;
        }

        Assert.NotNull(only);
        return only!;
    }

    private static async Task<ColumnBatch> ReadSingleAsync(
        ParquetFileReader reader, byte[] bytes, StructType requested)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch batch in reader.ReadAsync(
            stream, requested, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            Assert.Null(only); // the serializer writes a single row group for these small inputs
            only = batch;
        }

        Assert.NotNull(only);
        return only!;
    }

    private static Dictionary<string, int?> ReadIntMap(MapColumnVector map, int row)
    {
        ColumnVector keys = map.KeysAt(row);
        ColumnVector values = map.ValuesAt(row);
        var result = new Dictionary<string, int?>(StringComparer.Ordinal);
        for (int i = 0; i < map.EntryLength(row); i++)
        {
            result[Utf8(keys, i)] = values.IsNull(i) ? null : values.GetValue<int>(i);
        }

        return result;
    }

    // Reads the value for <paramref name="key"/> in an int→int map row (null when the value cell is null),
    // asserting the key is present. Map entry ordering is not part of the contract, so the entry is located
    // by key rather than by position.
    private static int? ReadIntMapEntry(MapColumnVector map, int row, int key)
    {
        ColumnVector keys = map.KeysAt(row);
        ColumnVector values = map.ValuesAt(row);
        for (int i = 0; i < map.EntryLength(row); i++)
        {
            if (keys.GetValue<int>(i) == key)
            {
                return values.IsNull(i) ? null : values.GetValue<int>(i);
            }
        }

        throw new KeyNotFoundException($"key {key} not found in map row {row}");
    }

    private static string Utf8(ColumnVector vector, int index) => Encoding.UTF8.GetString(vector.GetBytes(index));
}
