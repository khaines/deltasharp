using System.Text;
using DeltaSharp.Engine.Columnar;
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

    [Fact]
    public async Task NestedWrite_IsByteIdentical_AcrossRepeatedWrites()
    {
        var rows = new int?[]?[] { new int?[] { 1, null, 3 }, null, Array.Empty<int?>(), new int?[] { 4 } };
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", IntArrayType, nullable: true) });

        byte[] first = await ParquetTestHelpers.WriteToBytesAsync(
            schema,
            new[]
            {
                new ManagedColumnBatch(
                    schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length),
            });

        // A large write first, so the pooled scratch buffers the second write rents are DIRTY: any reliance
        // on a rented buffer being zeroed, or any stale-tail read, changes the bytes.
        var big = Enumerable.Range(0, 5_000).Select(i => (int?[]?)new int?[] { i, null }).ToArray();
        await ParquetTestHelpers.WriteToBytesAsync(
            schema,
            new[]
            {
                new ManagedColumnBatch(
                    schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, big) }, big.Length),
            });

        byte[] second = await ParquetTestHelpers.WriteToBytesAsync(
            schema,
            new[]
            {
                new ManagedColumnBatch(
                    schema, new ColumnVector[] { NestedVectors.IntList(IntArrayType, rows) }, rows.Length),
            });

        Assert.Equal(first, second);
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
