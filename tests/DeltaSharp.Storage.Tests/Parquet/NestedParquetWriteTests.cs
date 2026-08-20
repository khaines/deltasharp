using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Write-path tests for the three single-level nested Parquet shapes (#841): <c>struct&lt;scalars&gt;</c>,
/// <c>array&lt;scalar&gt;</c>, and <c>map(scalar → scalar)</c>.
/// </summary>
/// <remarks>
/// The primary oracle is a <b>round trip against the real #571 read path</b>: DeltaSharp writes the nested
/// column, then <see cref="ParquetFileReader"/> — which independently validates the Dremel level streams and
/// rejects anything structurally invalid — decodes it back, and the decoded vector is compared against the
/// model the write was built from at EVERY level (null container vs empty container vs null child). A defect
/// in the shredder therefore surfaces either as a read-side rejection or as a model mismatch; it cannot pass
/// silently. A second, independent oracle asserts the LITERAL definition/repetition arrays the design's §2.3
/// tables prescribe, so a self-consistent-but-wrong encoding (one the reader would happily accept) is caught
/// too.
/// </remarks>
public sealed class NestedParquetWriteTests
{
    private static readonly StructType InnerType = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.IntegerType, nullable: true),
        new StructField("b", DataTypes.StringType, nullable: true),
    });

    private static readonly ArrayType IntArrayType = DataTypes.CreateArrayType(DataTypes.IntegerType);

    private static readonly MapType StringIntMapType =
        DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType);

    // ----- round-trip oracle: struct -----

    [Fact]
    public async Task Struct_RoundTrips_EveryDremelBranch()
    {
        // present / null struct / present-with-null-field / both fields null / present again — the four
        // struct Dremel branches plus a repeat, so a level defect cannot be masked by a single-row file.
        var rows = new (int? A, string? B)?[]
        {
            (1, "one"),
            null,
            (null, "two"),
            (3, null),
            (4, "four"),
        };

        var schema = DataTypes.CreateStructType(new[] { new StructField("s", InnerType, nullable: true) });
        StructColumnVector vector = NestedVectors.IntStringStruct(InnerType, rows);
        ColumnBatch decoded = await WriteThenReadAsync(schema, vector);

        NestedVectors.AssertStructsEqual(
            rows, NestedVectors.ReadIntStringStruct((StructColumnVector)decoded.Column(0)));
    }

    [Fact]
    public async Task Struct_RoundTrips_AllNullRows()
    {
        var rows = new (int? A, string? B)?[] { null, null, null };
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", InnerType, nullable: true) });

        ColumnBatch decoded = await WriteThenReadAsync(schema, NestedVectors.IntStringStruct(InnerType, rows));

        NestedVectors.AssertStructsEqual(
            rows, NestedVectors.ReadIntStringStruct((StructColumnVector)decoded.Column(0)));
    }

    // ----- round-trip oracle: array -----

    [Fact]
    public async Task Array_RoundTrips_EveryDremelBranch()
    {
        // present multi-element / null list / EMPTY list / list of a single null element / single element:
        // the empty-vs-null-vs-null-element distinction is the whole point of the 3-level list encoding, and
        // all three collapse into "no value" if the definition table is off by one.
        var rows = new int?[]?[]
        {
            new int?[] { 10, 20 },
            null,
            Array.Empty<int?>(),
            new int?[] { null },
            new int?[] { 30 },
        };

        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, NestedVectors.IntList(IntArrayType, rows));

        NestedVectors.AssertListsEqual(rows, NestedVectors.ReadIntList((ListColumnVector)decoded.Column(0)));
    }

    [Fact]
    public async Task Array_RoundTrips_MixedNullAndPresentElementsInOneRow()
    {
        var rows = new int?[]?[]
        {
            new int?[] { null, 1, null, 2, null },
            new int?[] { 3 },
            Array.Empty<int?>(),
        };

        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, NestedVectors.IntList(IntArrayType, rows));

        NestedVectors.AssertListsEqual(rows, NestedVectors.ReadIntList((ListColumnVector)decoded.Column(0)));
    }

    // ----- round-trip oracle: map -----

    [Fact]
    public async Task Map_RoundTrips_EveryDremelBranch()
    {
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("a", (int?)1), ("b", null) },
            null,
            Array.Empty<(string, int?)>(),
            new[] { ("c", (int?)3) },
        };

        var schema = DataTypes.CreateStructType(
            new[] { new StructField("m", StringIntMapType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, NestedVectors.StringIntMap(StringIntMapType, rows));

        NestedVectors.AssertMapsEqual(
            rows, NestedVectors.ReadStringIntMap((MapColumnVector)decoded.Column(0)));
    }

    [Fact]
    public async Task Map_RoundTrips_MultiEntryRows()
    {
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("k1", (int?)1), ("k2", 2), ("k3", 3), ("k4", null) },
            new[] { ("z", (int?)9) },
        };

        var schema = DataTypes.CreateStructType(
            new[] { new StructField("m", StringIntMapType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, NestedVectors.StringIntMap(StringIntMapType, rows));

        NestedVectors.AssertMapsEqual(
            rows, NestedVectors.ReadStringIntMap((MapColumnVector)decoded.Column(0)));
    }

    // ----- mixed scalar + nested, and repeated leaf names across columns -----

    [Fact]
    public async Task MixedScalarAndNestedColumns_RoundTrip_WithDuplicateLeafNamesAcrossColumns()
    {
        // Two struct columns whose CHILDREN share names ("a"/"b"), interleaved with scalar columns: proves
        // leaves are attributed by their full schema path, not by leaf-local name (#497/#713 footer shape).
        var left = new (int? A, string? B)?[] { (1, "L1"), null, (3, "L3") };
        var right = new (int? A, string? B)?[] { null, (2, "R2"), (3, null) };

        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("left", InnerType, nullable: true),
            new StructField("right", InnerType, nullable: true),
            new StructField("tag", DataTypes.StringType, nullable: true),
        });

        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 3);
        MutableColumnVector tag = ColumnVectors.Create(DataTypes.StringType, 3);
        for (int i = 0; i < 3; i++)
        {
            id.AppendValue((long)i);
            tag.AppendBytes(Encoding.UTF8.GetBytes($"t{i}"));
        }

        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                id,
                NestedVectors.IntStringStruct(InnerType, left),
                NestedVectors.IntStringStruct(InnerType, right),
                tag,
            },
            rowCount: 3);

        ColumnBatch decoded = await WriteThenReadBatchAsync(schema, batch);

        NestedVectors.AssertStructsEqual(
            left, NestedVectors.ReadIntStringStruct((StructColumnVector)decoded.Column(1)));
        NestedVectors.AssertStructsEqual(
            right, NestedVectors.ReadIntStringStruct((StructColumnVector)decoded.Column(2)));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((long)i, decoded.Column(0).GetValue<long>(i));
            Assert.Equal($"t{i}", Encoding.UTF8.GetString(decoded.Column(3).GetBytes(i)));
        }
    }

    [Fact]
    public async Task DuplicateLeafNamesAcrossTheFullColumnList_AreAttributedPerColumn()
    {
        // §3.1's full duplicate-leaf-name column list. Parquet names an array's leaf `element`, a map's
        // `key`/`value`, and a struct child by its own name — so this schema contains FOUR leaves named
        // `element`, four named `key`/`value`, two named `zip`, and a top-level scalar that also spells
        // `zip`. Every one is distinguished only by its full schema PATH; a shredder or a #497 comparator
        // that keyed on leaf-local names would cross-wire them silently.
        var longArray = DataTypes.CreateArrayType(DataTypes.LongType);
        var stringLongMap = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType);
        var addressType = DataTypes.CreateStructType(new[]
        {
            new StructField("zip", DataTypes.StringType, nullable: true),
            new StructField("n", DataTypes.IntegerType, nullable: true),
        });

        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("a", longArray, nullable: true),
            new StructField("b", longArray, nullable: true),
            new StructField("m1", stringLongMap, nullable: true),
            new StructField("m2", stringLongMap, nullable: true),
            new StructField("home", addressType, nullable: true),
            new StructField("work", addressType, nullable: true),
            new StructField("zip", DataTypes.StringType, nullable: true),
        });

        var aRows = new long[]?[] { new long[] { 1, 2 }, null, Array.Empty<long>() };
        var bRows = new long[]?[] { null, new long[] { 30 }, new long[] { 40, 50 } };
        var m1Rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("k", (int?)1) }, null, Array.Empty<(string, int?)>(),
        };
        var m2Rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            Array.Empty<(string, int?)>(), new[] { ("q", (int?)null), ("r", 9) }, null,
        };
        var homeRows = new (int? A, string? B)?[] { (1, "H1"), null, (3, null) };
        var workRows = new (int? A, string? B)?[] { null, (2, "W2"), (3, "W3") };

        MutableColumnVector zip = ColumnVectors.Create(DataTypes.StringType, 3);
        for (int i = 0; i < 3; i++)
        {
            zip.AppendBytes(Encoding.UTF8.GetBytes($"top{i}"));
        }

        // The struct children are (zip:string, n:int) but the shared model builder appends (int, string), so
        // build the address vectors directly with the child order the schema declares.
        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                NestedVectors.LongList(longArray, aRows),
                NestedVectors.LongList(longArray, bRows),
                NestedVectors.StringIntMap(stringLongMap, m1Rows),
                NestedVectors.StringIntMap(stringLongMap, m2Rows),
                Address(addressType, homeRows),
                Address(addressType, workRows),
                zip,
            },
            rowCount: 3);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        ColumnBatch? decoded = null;
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            decoded = group;
        }

        Assert.NotNull(decoded);
        Assert.Equal(aRows, NestedVectors.ReadLongList((ListColumnVector)decoded!.Column(0)));
        Assert.Equal(bRows, NestedVectors.ReadLongList((ListColumnVector)decoded.Column(1)));
        NestedVectors.AssertMapsEqual(
            m1Rows, NestedVectors.ReadStringIntMap((MapColumnVector)decoded.Column(2)));
        NestedVectors.AssertMapsEqual(
            m2Rows, NestedVectors.ReadStringIntMap((MapColumnVector)decoded.Column(3)));
        AssertAddresses(homeRows, (StructColumnVector)decoded.Column(4));
        AssertAddresses(workRows, (StructColumnVector)decoded.Column(5));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal($"top{i}", Encoding.UTF8.GetString(decoded.Column(6).GetBytes(i)));
        }

        // #497: the write door's structural comparator must accept THIS schema against THIS footer — every
        // leaf resolved by path — and must still reject a schema that only permutes the duplicate names.
        using var stream = new MemoryStream(bytes, writable: false);
        StructType footer = await new ParquetFileReader().ReadDataSchemaAsync(stream, CancellationToken.None);
        Assert.True(DeltaTableWriter.DataColumnsMatch(schema, footer));

        var swapped = DataTypes.CreateStructType(new[]
        {
            schema.Fields[0], schema.Fields[1], schema.Fields[2], schema.Fields[3],
            new StructField("home", DataTypes.CreateStructType(new[]
            {
                new StructField("n", DataTypes.IntegerType, nullable: true),
                new StructField("zip", DataTypes.StringType, nullable: true),
            }), nullable: true),
            schema.Fields[5], schema.Fields[6],
        });
        Assert.False(DeltaTableWriter.DataColumnsMatch(swapped, footer));
    }

    private static StructColumnVector Address(StructType type, IReadOnlyList<(int? A, string? B)?> rows)
    {
        MutableColumnVector zip = ColumnVectors.Create(DataTypes.StringType, rows.Count);
        MutableColumnVector n = ColumnVectors.Create(DataTypes.IntegerType, rows.Count);
        var nulls = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            (int? A, string? B)? row = rows[i];
            nulls[i] = row is null;
            if (row?.B is null)
            {
                zip.AppendNull();
            }
            else
            {
                zip.AppendBytes(Encoding.UTF8.GetBytes(row.Value.B!));
            }

            if (row?.A is null)
            {
                n.AppendNull();
            }
            else
            {
                n.AppendValue(row.Value.A!.Value);
            }
        }

        return new StructColumnVector(type, new ColumnVector[] { zip, n }, nulls);
    }

    private static void AssertAddresses(
        IReadOnlyList<(int? A, string? B)?> expected, StructColumnVector actual)
    {
        ColumnVector zip = actual.Child(0);
        ColumnVector n = actual.Child(1);
        Assert.Equal(expected.Count, actual.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] is null)
            {
                Assert.True(actual.IsNull(i));
                continue;
            }

            Assert.False(actual.IsNull(i));
            Assert.Equal(expected[i]!.Value.B, zip.IsNull(i) ? null : Encoding.UTF8.GetString(zip.GetBytes(i)));
            Assert.Equal(expected[i]!.Value.A, n.IsNull(i) ? null : n.GetValue<int>(i));
        }
    }

    // ----- multi-batch / multi-row-group -----

    [Fact]
    public async Task Array_RoundTrips_AcrossRowGroupBoundaries()
    {
        // A row-group limit of 2 forces the shredder's segment walk to straddle batch boundaries and to be
        // re-entered per row group, which is where a per-column cursor bug would show up as shifted rows.
        var rows = new int?[]?[]
        {
            new int?[] { 1 },
            null,
            Array.Empty<int?>(),
            new int?[] { 2, 3 },
            new int?[] { null },
        };

        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 2);
        List<int?[]?> decoded = new();
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            decoded.AddRange(NestedVectors.ReadIntList((ListColumnVector)group.Column(0)));
        }

        NestedVectors.AssertListsEqual(rows, decoded);
    }

    [Fact]
    public async Task Map_RoundTrips_AcrossRowGroupBoundaries()
    {
        // The row-group SPLIT is the mainline path for wide nested columns (§2.9.2), so its VALUE fidelity —
        // not just its row count — has to be pinned. Every key and value here is derived from the GLOBAL
        // logical row index, so an entry span mis-based by the segment start (a defect that only appears once
        // a row group starts mid-batch) shows up as a wrong key/value rather than a right count. Neither the
        // §2.4b footer reconciliation (counts agree) nor the §2.3c level guards (levels stay well-formed) can
        // see that class of corruption.
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("k0a", (int?)0), ("k0b", (int?)1) },
            null,
            Array.Empty<(string, int?)>(),
            new[] { ("k3a", (int?)30), ("k3b", (int?)31) },
            new[] { ("k4a", (int?)40), ("k4b", (int?)null) },
            new[] { ("k5a", (int?)50), ("k5b", (int?)51) },
            new[] { ("k6a", (int?)60), ("k6b", (int?)61) },
            new[] { ("k7a", (int?)70), ("k7b", (int?)71) },
        };

        var schema = DataTypes.CreateStructType(
            new[] { new StructField("m", StringIntMapType, nullable: true) });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { NestedVectors.StringIntMap(StringIntMapType, rows) }, rows.Length);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 2);
        var decoded = new List<List<(string Key, int? Value)>?>();
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            decoded.AddRange(NestedVectors.ReadStringIntMap((MapColumnVector)group.Column(0)));
        }

        NestedVectors.AssertMapsEqual(rows, decoded);

        // Explicitly keyed to the global logical row index, so a shift/duplication across the split cannot be
        // absorbed by an equally shifted expectation.
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] is null)
            {
                Assert.Null(decoded[i]);
                continue;
            }

            Assert.Equal(rows[i]!.Select(e => e.Key), decoded[i]!.Select(e => e.Key));
            Assert.Equal(rows[i]!.Select(e => e.Value), decoded[i]!.Select(e => e.Value));
        }
    }

    [Fact]
    public async Task Struct_RoundTrips_AcrossRowGroupBoundaries()
    {
        // The struct sibling of the cell above: every child payload is keyed to the global logical row index,
        // so a child span mis-based by the segment start fails on VALUE.
        var rows = new (int? A, string? B)?[]
        {
            (0, "s0"),
            null,
            (20, "s2"),
            (30, "s3"),
            (40, null),
            (null, "s5"),
            (60, "s6"),
            (70, "s7"),
        };

        var schema = DataTypes.CreateStructType(new[] { new StructField("s", InnerType, nullable: true) });
        var batch = new ManagedColumnBatch(
            schema, new ColumnVector[] { NestedVectors.IntStringStruct(InnerType, rows) }, rows.Length);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, rowGroupRowLimit: 2);
        var decoded = new List<(int? A, string? B)?>();
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            decoded.AddRange(NestedVectors.ReadIntStringStruct((StructColumnVector)group.Column(0)));
        }

        NestedVectors.AssertStructsEqual(rows, decoded);

        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(rows[i]?.A, decoded[i]?.A);
            Assert.Equal(rows[i]?.B, decoded[i]?.B);
        }
    }

    [Fact]
    public async Task Map_RoundTrips_AcrossMultipleInputBatches()
    {
        var first = new IReadOnlyList<(string Key, int? Value)>?[] { new[] { ("a", (int?)1) }, null };
        var second = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            Array.Empty<(string, int?)>(),
            new[] { ("b", (int?)2), ("c", null) },
        };

        var schema = DataTypes.CreateStructType(
            new[] { new StructField("m", StringIntMapType, nullable: true) });
        var batches = new[]
        {
            new ManagedColumnBatch(
                schema, new ColumnVector[] { NestedVectors.StringIntMap(StringIntMapType, first) }, 2),
            new ManagedColumnBatch(
                schema, new ColumnVector[] { NestedVectors.StringIntMap(StringIntMapType, second) }, 2),
        };

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, batches);
        var decoded = new List<List<(string Key, int? Value)>?>();
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            decoded.AddRange(NestedVectors.ReadStringIntMap((MapColumnVector)group.Column(0)));
        }

        NestedVectors.AssertMapsEqual(first.Concat(second).ToArray(), decoded);
    }

    // ----- zero rows -----

    [Fact]
    public async Task NestedColumns_ZeroRows_WriteAndReadBackEmpty()
    {
        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("s", InnerType, nullable: true),
            new StructField("a", IntArrayType, nullable: true),
            new StructField("m", StringIntMapType, nullable: true),
        });

        var batch = new ManagedColumnBatch(
            schema,
            new ColumnVector[]
            {
                NestedVectors.IntStringStruct(InnerType, Array.Empty<(int? A, string? B)?>()),
                NestedVectors.IntList(IntArrayType, Array.Empty<int?[]?>()),
                NestedVectors.StringIntMap(StringIntMapType, Array.Empty<IReadOnlyList<(string, int?)>?>()),
            },
            rowCount: 0);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        // The zero-row write emits no row group at all (L2), so the footer must still round-trip the schema
        // and report zero rows.
        using var stream = new MemoryStream(bytes, writable: false);
        Assert.Equal(0L, await new ParquetFileReader().GetRowCountAsync(stream, CancellationToken.None));
    }

    // ----- sliced / offset vectors -----

    [Fact]
    public async Task Array_RoundTrips_FromASlicedVector_WithNonZeroElementBase()
    {
        // A sliced list vector has offset > 0 AND an element base > 0, so the shredder's RawElementSpan
        // rebasing is load-bearing: without it every element index is shifted by the dropped prefix.
        var all = new int?[]?[]
        {
            new int?[] { 100, 200 },
            new int?[] { 1 },
            null,
            Array.Empty<int?>(),
            new int?[] { 2, 3 },
        };

        ListColumnVector sliced = (ListColumnVector)NestedVectors.IntList(IntArrayType, all).Slice(1, 4);
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, sliced);

        NestedVectors.AssertListsEqual(
            all.Skip(1).ToArray(), NestedVectors.ReadIntList((ListColumnVector)decoded.Column(0)));
    }

    [Fact]
    public async Task Struct_RoundTrips_FromASlicedVector()
    {
        var all = new (int? A, string? B)?[] { (0, "drop"), (1, "one"), null, (3, null) };
        StructColumnVector sliced = (StructColumnVector)NestedVectors.IntStringStruct(InnerType, all).Slice(1, 3);

        var schema = DataTypes.CreateStructType(new[] { new StructField("s", InnerType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, sliced);

        NestedVectors.AssertStructsEqual(
            all.Skip(1).ToArray(), NestedVectors.ReadIntStringStruct((StructColumnVector)decoded.Column(0)));
    }

    [Fact]
    public async Task Map_RoundTrips_FromASlicedVector_WithNonZeroEntryBase()
    {
        var all = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("drop1", (int?)9), ("drop2", 8) },
            new[] { ("a", (int?)1) },
            null,
            new[] { ("b", (int?)null), ("c", 3) },
        };

        MapColumnVector sliced = (MapColumnVector)NestedVectors.StringIntMap(StringIntMapType, all).Slice(1, 3);
        var schema = DataTypes.CreateStructType(
            new[] { new StructField("m", StringIntMapType, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, sliced);

        NestedVectors.AssertMapsEqual(
            all.Skip(1).ToArray(), NestedVectors.ReadStringIntMap((MapColumnVector)decoded.Column(0)));
    }

    // ----- determinism -----

    public static TheoryData<string> DeterminismShapes() => new()
    {
        "array<int>", "array<string>", "map<string,long>", "struct<int,string>",
    };

    [Theory]
    [MemberData(nameof(DeterminismShapes))]
    public async Task NestedWrite_IsByteIdentical_AcrossRepeatedWrites(string shape)
    {
        // §3.5. The lanes that MATTER here are the pooled-scratch ones: `array<int>` alone rents no
        // variable-width scratch at all, so parameterizing over the string / map / struct shapes is what
        // actually exercises the "never grown, exactly sized, cleared on return" contract. The large write
        // between the two small ones leaves the shared ArrayPool DIRTY, so any reliance on a rented buffer
        // being zero, or any stale-tail read past the exact slice, changes the bytes.
        (StructType schema, Func<int, ColumnBatch> build) = DeterminismCase(shape);

        byte[] first = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { build(0) });
        await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { build(1) });
        byte[] second = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { build(0) });

        Assert.Equal(first, second);
    }

    // `size` 0 is the small model the byte identity is asserted over; 1 is the large dirtying write.
    private static (StructType Schema, Func<int, ColumnBatch> Build) DeterminismCase(string shape)
    {
        switch (shape)
        {
            case "array<int>":
                {
                    var schema = DataTypes.CreateStructType(
                        new[] { new StructField("a", IntArrayType, nullable: true) });
                    return (schema, size =>
                    {
                        int?[]?[] rows = size == 0
                            ? new int?[]?[] { new int?[] { 1, null, 3 }, null, Array.Empty<int?>(), new int?[] { 4 } }
                            : Enumerable.Range(0, 5_000).Select(i => (int?[]?)new int?[] { i, null }).ToArray();
                        return new ManagedColumnBatch(
                            schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length);
                    }
                    );
                }

            case "array<string>":
                {
                    var type = DataTypes.CreateArrayType(DataTypes.StringType);
                    var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
                    return (schema, size =>
                    {
                        string?[]?[] rows = size == 0
                            ? new string?[]?[] { new string?[] { "x", null, string.Empty }, null, new[] { "\u00e9\u4e2d" } }
                            : Enumerable.Range(0, 3_000)
                                .Select(i => (string?[]?)new string?[] { new string('z', 40), null })
                                .ToArray();
                        return new ManagedColumnBatch(
                            schema, new ColumnVector[] { StringList(type, rows) }, rows.Length);
                    }
                    );
                }

            case "map<string,long>":
                {
                    var type = DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType);
                    var schema = DataTypes.CreateStructType(new[] { new StructField("m", type, nullable: true) });
                    return (schema, size =>
                    {
                        IReadOnlyList<(string Key, int? Value)>?[] rows = size == 0
                            ? new IReadOnlyList<(string Key, int? Value)>?[]
                            {
                            new[] { ("a", (int?)1), ("bb", null) }, null, Array.Empty<(string, int?)>(),
                            }
                            : Enumerable.Range(0, 3_000)
                                .Select(i => (IReadOnlyList<(string Key, int? Value)>?)new[]
                                {
                                (new string('k', 30), (int?)i),
                                })
                                .ToArray();
                        return new ManagedColumnBatch(
                            schema, new ColumnVector[] { NestedVectors.StringIntMap(type, rows) }, rows.Length);
                    }
                    );
                }

            default:
                {
                    var schema = DataTypes.CreateStructType(
                        new[] { new StructField("s", InnerType, nullable: true) });
                    return (schema, size =>
                    {
                        (int? A, string? B)?[] rows = size == 0
                            ? new (int? A, string? B)?[] { (1, "one"), null, (null, "two"), (3, null) }
                            : Enumerable.Range(0, 3_000)
                                .Select(i => ((int? A, string? B)?)(i, new string('s', 25)))
                                .ToArray();
                        return new ManagedColumnBatch(
                            schema,
                            new ColumnVector[] { NestedVectors.IntStringStruct(InnerType, rows) },
                            rows.Length);
                    }
                    );
                }
        }
    }

    private static ListColumnVector StringList(ArrayType type, IReadOnlyList<string?[]?> rows)
    {
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.StringType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            string?[]? row = rows[i];
            nulls[i] = row is null;
            if (row is not null)
            {
                foreach (string? value in row)
                {
                    if (value is null)
                    {
                        elements.AppendNull();
                    }
                    else
                    {
                        elements.AppendBytes(Encoding.UTF8.GetBytes(value));
                    }

                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        return new ListColumnVector(type, elements, offsets, nulls);
    }

    [Fact]
    public async Task WideStruct_RoundTrips_WithoutHoldingOneLevelBufferPerChild()
    {
        // D2. Struct width is unbounded (CreateField rejects only the ZERO-field struct), and the lane used to
        // rent one int[rowGroupRows] per child and hold them all live so the children could be compared to each
        // other — O(width x row-group rows), ~512 MiB for a 1000-field struct on a full row group. The lane now
        // captures the struct's null mask ONCE as a bitmap and validates each child incrementally, so it holds
        // exactly one level buffer. This pins that a wide struct still round-trips through every branch of the
        // level table (present / null-struct / null-field).
        const int width = 256;
        var fields = new StructField[width];
        for (int i = 0; i < width; i++)
        {
            fields[i] = new StructField($"f{i}", DataTypes.IntegerType, nullable: true);
        }

        var inner = DataTypes.CreateStructType(fields);
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", inner, nullable: true) });

        const int rows = 3;
        var children = new ColumnVector[width];
        for (int i = 0; i < width; i++)
        {
            MutableColumnVector child = ColumnVectors.Create(DataTypes.IntegerType, rows);
            child.AppendValue(i);          // present struct, present field
            child.AppendNull();            // null struct (the child's own cell is irrelevant)
            child.AppendNull();            // present struct, null field
            children[i] = child;
        }

        var vector = new StructColumnVector(inner, children, new[] { false, true, false });
        ColumnBatch decoded = await WriteThenReadAsync(schema, vector);
        var actual = (StructColumnVector)decoded.Column(0);

        Assert.Equal(rows, actual.Length);
        Assert.False(actual.IsNull(0));
        Assert.True(actual.IsNull(1));
        Assert.False(actual.IsNull(2));
        for (int i = 0; i < width; i++)
        {
            ColumnVector child = actual.Child(i);
            Assert.Equal(i, child.GetValue<int>(0));
            Assert.True(child.IsNull(2));
        }
    }

    // ----- H1-e: unsliced vectors whose raw offsets do NOT start at 0 / do not end at Elements.Length -----

    [Fact]
    public async Task Array_RoundTrips_FromAnUnslicedVectorWithANonZeroBaseAndADanglingTail()
    {
        // NOT a Slice: an UNSLICED ListColumnVector whose offsets[0] > 0 (a leading run of elements no row
        // references) and whose offsets[^1] < Elements.Length (a dangling uncommitted tail). This is exactly
        // why element counts must come from the RAW offsets and never from Elements.Length: the tail would
        // be written as extra elements and the base would shift every row.
        var type = DataTypes.CreateArrayType(DataTypes.IntegerType);
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.IntegerType, 16);
        foreach (int value in new[] { -1, -2, 10, 20, 30, -3, -4, -5 })
        {
            elements.AppendValue(value);
        }

        // Rows: [10,20] / null / [] / [30]. Offsets start at 2 and stop at 5, so index 0-1 and 5-7 dangle.
        int[] offsets = [2, 4, 4, 4, 5];
        bool[] nulls = [false, true, false, false];
        var vector = new ListColumnVector(type, elements, offsets, nulls);

        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
        ColumnBatch decoded = await WriteThenReadAsync(schema, vector);

        NestedVectors.AssertListsEqual(
            new int?[]?[] { new int?[] { 10, 20 }, null, Array.Empty<int?>(), new int?[] { 30 } },
            NestedVectors.ReadIntList((ListColumnVector)decoded.Column(0)));
    }

    [Fact]
    public async Task Array_NullRowRetainingElements_WritesNoneOfThem()
    {
        // A NULL list row that physically RETAINS a non-empty element span (only offset monotonicity is
        // enforced, so a masked row may keep its bytes). Those retained values must never reach disk: the
        // row occupies exactly ONE level slot at def 0 and contributes zero values. If the shredder walked
        // the span instead of the null mask, the file would carry the masked payload AND shift every
        // subsequent row.
        var type = DataTypes.CreateArrayType(DataTypes.IntegerType);
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.IntegerType, 8);
        foreach (int value in new[] { 1, 777, 888, 2 })
        {
            elements.AppendValue(value);
        }

        int[] offsets = [0, 1, 3, 4];
        bool[] nulls = [false, true, false];
        var vector = new ListColumnVector(type, elements, offsets, nulls);

        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(
            schema,
            new[] { new ManagedColumnBatch(schema, new ColumnVector[] { vector }, 3) });

        ColumnBatch? decoded = null;
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            decoded = group;
        }

        Assert.NotNull(decoded);
        List<int?[]?> actual = NestedVectors.ReadIntList((ListColumnVector)decoded!.Column(0));
        NestedVectors.AssertListsEqual(
            new int?[]?[] { new int?[] { 1 }, null, new int?[] { 2 } }, actual);

        // Belt and braces: the retained payload must not appear ANYWHERE in the file's bytes.
        Assert.DoesNotContain(BitConverter.GetBytes(777), Search(bytes));
        Assert.DoesNotContain(BitConverter.GetBytes(888), Search(bytes));

        static IEnumerable<byte[]> Search(byte[] bytes)
        {
            for (int i = 0; i + 4 <= bytes.Length; i++)
            {
                yield return bytes[i..(i + 4)];
            }
        }
    }

    // ----- helpers -----

    private static async Task<ColumnBatch> WriteThenReadAsync(StructType schema, ColumnVector column)
    {
        var batch = new ManagedColumnBatch(schema, new[] { column }, column.Length);
        return await WriteThenReadBatchAsync(schema, batch);
    }

    private static async Task<ColumnBatch> WriteThenReadBatchAsync(StructType schema, ColumnBatch batch)
    {
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        ColumnBatch? only = null;
        await foreach (ColumnBatch decoded in ReadAsync(bytes, schema))
        {
            Assert.Null(only);
            only = decoded;
        }

        Assert.NotNull(only);
        return only!;
    }

    private static async IAsyncEnumerable<ColumnBatch> ReadAsync(byte[] bytes, StructType schema)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            yield return batch;
        }
    }
}
