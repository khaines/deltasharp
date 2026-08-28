using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using static DeltaSharp.Storage.Tests.Parquet.NestedValueModel;
using LeafCase = DeltaSharp.Storage.Tests.Parquet.NestedParquetLeafTypeTests.LeafCase;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #873 §3.3 composition + per-leaf-type cells: value/temporal/decimal leaves at depth (11), the 585b widen
/// compose (24), the footer row-count reconcile for a nested-within-nested column (25), and the CI-gated
/// cross-engine interop gate (26).
/// </summary>
public sealed class NestedWithinNestedComposeTests
{
    // 11 — a depth-2 array<struct<v:T>> carrying each scalar leaf type is written and read back EXACTLY
    // (CreateScalarField reused per leaf at depth). Reuses the single-level per-leaf-type corpus.
    [Theory]
    [MemberData(nameof(NestedParquetLeafTypeTests.LeafCases), MemberType = typeof(NestedParquetLeafTypeTests))]
    public async Task Write_NestedLeaves_AllScalarTypes_RoundTrip(LeafCase leaf)
    {
        var inner = DataTypes.CreateStructType(new[] { new StructField("v", leaf.Type, nullable: true) });
        var arrayType = DataTypes.CreateArrayType(inner);
        var schema = DataTypes.CreateStructType(new[] { new StructField("c", arrayType, nullable: true) });

        // The struct child v carries the leaf value; wrap ValueCount structs into ONE outer list row.
        MutableColumnVector v = ColumnVectors.Create(leaf.Type, leaf.ValueCount);
        for (int i = 0; i < leaf.ValueCount; i++)
        {
            leaf.Append(v, i);
        }

        var structVector = new StructColumnVector(inner, new ColumnVector[] { v }, new bool[leaf.ValueCount]);
        var outer = new ListColumnVector(
            arrayType, structVector, new[] { 0, leaf.ValueCount }, new[] { false });
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { outer }, 1);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        ColumnBatch decoded = await ReadOneAsync(bytes, schema);

        var dl = (ListColumnVector)decoded.Column(0);
        Assert.False(dl.IsNull(0));
        (int start, int length) = dl.RawElementSpan(0);
        Assert.Equal(leaf.ValueCount, length);
        var ds = (StructColumnVector)dl.Elements;
        ColumnVector dv = ds.Child(0);
        for (int e = 0; e < leaf.ValueCount; e++)
        {
            leaf.AssertValue(leaf.Read(v, e), leaf.Read(dv, start + e));
        }
    }

    // 24 — write array<array<int>> via #873, then read it back requesting array<array<long>> with promotion
    // (585b): values promote int→long and the null structure is preserved across the widen — the three
    // increments compose (write narrow via 585a-inverse, read wide via 585b).
    [Fact]
    public async Task Write_ThenWidenAppend_585bReadPromotes()
    {
        var narrow = DataTypes.CreateArrayType(DataTypes.CreateArrayType(DataTypes.IntegerType));
        var narrowSchema = DataTypes.CreateStructType(new[] { new StructField("c", narrow, nullable: true) });
        var rows = new object?[]
        {
            null,
            Arr(),
            Arr(null, Arr()),
            Arr(Arr(7, null), Arr(9)),
        };
        ColumnVector column = Build(narrow, rows);
        var batch = new ManagedColumnBatch(narrowSchema, new[] { column }, rows.Length);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(narrowSchema, new[] { batch });

        // Read back requesting the WIDENED type with promotion enabled.
        var wide = DataTypes.CreateArrayType(DataTypes.CreateArrayType(DataTypes.LongType));
        var wideSchema = DataTypes.CreateStructType(new[] { new StructField("c", wide, nullable: true) });
        var expected = new object?[]
        {
            null,
            Arr(),
            Arr(null, Arr()),
            Arr(Arr(7L, null), Arr(9L)),
        };

        using var stream = new MemoryStream(bytes, writable: false);
        int row = 0;
        await foreach (ColumnBatch decoded in new ParquetFileReader().ReadAsync(
            stream, wideSchema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: true,
            CancellationToken.None))
        {
            for (int i = 0; i < decoded.LogicalRowCount; i++, row++)
            {
                AssertEqual(expected[row], ReadValue(decoded.Column(0), i, wide));
            }
        }

        Assert.Equal(expected.Length, row);
    }

    // 25 — the §2.4b post-write footer NumRows reconciliation holds for a nested-within-nested column: a
    // dropped deep slot is caught by the row-count reconcile (not just the type door), and a clean write
    // reconciles.
    [Fact]
    public async Task Write_NestedWithinNested_FooterRowCountReconciles()
    {
        var type = DataTypes.CreateArrayType(DataTypes.CreateArrayType(DataTypes.IntegerType));
        var schema = DataTypes.CreateStructType(new[] { new StructField("c", type, nullable: true) });
        var rows = new object?[]
        {
            Arr(Arr(1, 2), Arr(3)),
            null,
            Arr(Arr(4)),
            Arr(Arr(5, 6), Arr(7, 8)),
        };
        ColumnVector column = Build(type, rows);
        var batch = new ManagedColumnBatch(schema, new[] { column }, rows.Length);

        // Clean write reconciles.
        using (var ok = new MemoryStream())
        {
            await new ParquetFileWriter().WriteAsync(ok, schema, new[] { batch }, CancellationToken.None);
            Assert.True(ok.Length > 0);
        }

        // A row-dropping segmentation fault is caught by the footer reconcile.
        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new DroppingWriter().WriteAsync(output, schema, new[] { batch }, CancellationToken.None));
        Assert.Equal(StorageErrorKind.CorruptData, error.Kind);
        Assert.Contains("structurally inconsistent", error.Message, StringComparison.Ordinal);
    }

    // 26 — cross-engine interop: a #873-written file must be readable by Apache Spark AND delta-rs to the
    // original values, for >= 1 array, map, and struct shape. This is the ONLY proof the canonical physical
    // names/annotations are external-contract correct (our own reader cannot prove it). It is CI-gated: it
    // needs external tooling (Spark / delta-rs) not present in the unit-test sandbox, so it is SKIPPED locally
    // (a clear skip reason) rather than blocking the build. A CI job provisioning Spark/delta-rs removes the
    // Skip and supplies the harness for array<struct<a:int,b:string>>, map<string,array<long>>, and
    // struct<xs:array<int>,name:string>.
    [Fact(Skip = "Cross-engine interop (Apache Spark + delta-rs) requires external tooling not available in "
        + "the unit-test sandbox; run in a CI job that provisions Spark/delta-rs.")]
    public void Write_NestedWithinNested_ReadableBySparkAndDeltaRs() =>
        Assert.Fail("interop harness must be provided by the CI job");

    private static async Task<ColumnBatch> ReadOneAsync(byte[] bytes, StructType schema)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            only = batch;
        }

        Assert.NotNull(only);
        return only!;
    }

    // A test-only writer that drops one row per row group at the segmentation seam — the row-LOSING defect the
    // §2.4b footer reconcile must catch for a nested-within-nested column.
    private sealed class DroppingWriter : ParquetFileWriter
    {
        protected override int OnRowGroupSegmentsCollected(List<Segment> segments, int size)
        {
            Segment last = segments[^1];
            if (last.Length <= 1)
            {
                return size;
            }

            segments[^1] = new Segment(last.Batch, last.Start, last.Length - 1);
            return size - 1;
        }
    }
}
