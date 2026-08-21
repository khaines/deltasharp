using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// §2.9.2 row-group planning: a nested column's transient Dremel level buffers are bounded by a BYTE budget,
/// and a column whose per-row fan-out is large makes the row group SMALLER — it never makes the column
/// unwritable.
/// </summary>
/// <remarks>
/// <para>§2.6 admits <c>array&lt;scalar&gt;</c> and <c>map&lt;scalar,scalar&gt;</c> UNCONDITIONALLY, so no
/// resource control may narrow that acceptance set. A prior revision capped the per-row fan-out at 256
/// elements, which failed a mainstream 1536-dimension embedding column closed with
/// <c>UnsupportedFeature</c> and no user-reachable workaround — a semantic regression wearing a resource
/// control's clothes. These cells pin both halves: legal wide data round-trips end to end, and the byte
/// budget does resource work by SPLITTING.</para>
/// </remarks>
public sealed class NestedParquetRowGroupPlanningTests
{
    // A writer whose level-buffer budget is small enough that the SPLIT and the single-row reject can be
    // driven without authoring hundreds of megabytes. Lowering the budget only moves row-group boundaries,
    // which is always legal — it cannot produce an invalid file.
    private sealed class BudgetedParquetFileWriter : ParquetFileWriter
    {
        private readonly long _budget;

        internal BudgetedParquetFileWriter(long budget) => _budget = budget;

        protected override long NestedLevelBufferBudgetBytes => _budget;
    }

    [Fact]
    public async Task WideEmbeddingColumn_IsAcceptedAndRoundTripsEndToEnd()
    {
        // The exact shape the fan-out ceiling used to reject: a mainstream 1536-dimension embedding column,
        // written and read back through the real #571 nested reader. 25,000 x 1536 = 38.4M level slots, which
        // is BOTH past the old semantic ceiling (33,554,432 — the column was simply unwritable, with no
        // user-reachable workaround) and past what one row group's level buffers may rent. Under the byte
        // budget the writer answers by emitting more, smaller row groups instead of failing.
        const int rows = 25_000;
        const int dims = 1_536;

        var type = DataTypes.CreateArrayType(DataTypes.FloatType);
        var schema = DataTypes.CreateStructType(new[] { new StructField("embedding", type, nullable: true) });

        MutableColumnVector elements = ColumnVectors.Create(DataTypes.FloatType, rows * dims);
        var offsets = new int[rows + 1];
        for (int r = 0; r < rows; r++)
        {
            offsets[r] = r * dims;
            for (int d = 0; d < dims; d++)
            {
                elements.AppendValue((float)((r * 31 + d) % 997));
            }
        }

        offsets[rows] = rows * dims;
        var vector = new ListColumnVector(type, elements, offsets, new bool[rows]);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { vector }, rows);

        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });

        using (var footer = new MemoryStream(bytes))
        await using (global::Parquet.ParquetReader reader =
            await global::Parquet.ParquetReader.CreateAsync(footer, null, true, CancellationToken.None))
        {
            // The budget SPLIT this column rather than rejecting it — the whole point of A1.
            Assert.True(reader.RowGroupCount >= 2, $"expected a split, got {reader.RowGroupCount} row group(s)");
        }

        long observed = 0;
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            var list = (ListColumnVector)group.Column(0);
            for (int i = 0; i < list.Length; i++)
            {
                ColumnVector row = list.ElementsAt(i);
                Assert.Equal(dims, row.Length);

                // Spot-check the ends of every row: a mis-planned split would shift the element base.
                long logical = observed + i;
                Assert.Equal((float)((logical * 31) % 997), row.GetValue<float>(0));
                Assert.Equal((float)((logical * 31 + dims - 1) % 997), row.GetValue<float>(dims - 1));
            }

            observed += list.Length;
        }

        Assert.Equal(rows, observed);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public async Task AWideColumn_SplitsIntoMoreRowGroups_RatherThanFailingClosed(string shape)
    {
        // The folded per-slot cost (level streams + leaf value width, #845 item 2) drives the split: a row
        // group holds floor(maxSlots / fanOut) rows, so the value-heavier MAP lane (a 32-byte string key on top
        // of four level ints ⇒ 52 bytes/slot) splits into MORE, smaller row groups than the ARRAY lane
        // (12 bytes/slot) at the same budget. Deriving the expectation from the PRODUCTION per-slot cost keeps
        // this cell honest across a cost change while still asserting the END-TO-END split really happens.
        const int rows = 6;
        const int fanOut = 4;
        const long budget = 256;
        (StructType schema, ColumnBatch batch) = shape == "array"
            ? (ArraySchema, WideArrayBatch(rows, fanOut))
            : (MapSchema, WideMapBatch(rows, fanOut));

        int perSlot = NestedColumnShredder.RowGroupTransientBytesPerSlot(schema[0].DataType);
        long maxSlots = Math.Max(budget / perSlot, 1);
        int rowsPerGroup = Math.Max((int)(maxSlots / fanOut), 1);
        int expectedGroups = (rows + rowsPerGroup - 1) / rowsPerGroup;

        using var output = new MemoryStream();
        await new BudgetedParquetFileWriter(budget)
            .WriteAsync(output, schema, new[] { batch }, CancellationToken.None);

        output.Position = 0;
        await using (global::Parquet.ParquetReader reader =
            await global::Parquet.ParquetReader.CreateAsync(output, null, true, CancellationToken.None))
        {
            Assert.Equal(expectedGroups, reader.RowGroupCount);
            Assert.True(expectedGroups > 1, "the budget must split this column for the cell to be non-vacuous");
        }

        // Non-vacuity: the same data at the DEFAULT budget is one row group, so the split is the budget's
        // doing and not a property of the input.
        byte[] unsplit = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        using var single = new MemoryStream(unsplit);
        await using (global::Parquet.ParquetReader reader =
            await global::Parquet.ParquetReader.CreateAsync(single, null, true, CancellationToken.None))
        {
            Assert.Equal(1, reader.RowGroupCount);
        }

        // And the split file still reads back every row, in order, WITH ITS VALUES: a row-count oracle would
        // accept a split that mis-bases an element/entry span, which is precisely the defect the split path
        // introduces. Keys and values are derived from the global row index, so any shift mismatches.
        output.Position = 0;
        int observed = 0;
        await foreach (ColumnBatch group in ReadAsync(output.ToArray(), schema))
        {
            if (shape == "array")
            {
                var list = (ListColumnVector)group.Column(0);
                for (int i = 0; i < list.Length; i++)
                {
                    ColumnVector row = list.ElementsAt(i);
                    Assert.Equal(4, row.Length);
                    for (int e = 0; e < 4; e++)
                    {
                        Assert.Equal(((observed + i) * 4) + e, row.GetValue<int>(e));
                    }
                }

                observed += list.Length;
                continue;
            }

            List<List<(string Key, int? Value)>?> entries =
                NestedVectors.ReadStringIntMap((MapColumnVector)group.Column(0));
            for (int i = 0; i < entries.Count; i++)
            {
                List<(string Key, int? Value)> row = Assert.IsType<List<(string Key, int? Value)>>(entries[i]);
                Assert.Equal(4, row.Count);
                for (int e = 0; e < 4; e++)
                {
                    Assert.Equal($"k{observed + i}_{e}", row[e].Key);
                    Assert.Equal(((observed + i) * 4) + e, row[e].Value);
                }
            }

            observed += entries.Count;
        }

        Assert.Equal(rows, observed);
    }

    [Fact]
    public async Task AValueHeavyArrayColumn_SplitsTighterThanAFixedWidthOne_UnderTheFoldedBudget()
    {
        // #845 item 2: folding the leaf value width into the per-slot cost makes ONE budget govern the whole
        // per-column transient. A wide array<string> stages a 32-byte value proxy per element on top of the two
        // level ints, so it costs far more per slot than array<int> — and at the SAME budget/fanOut/rows the
        // writer answers by emitting MORE, smaller row groups for the value-heavy column. That boundary shift is
        // exactly the tightening the fold buys; this cell pins it end to end and binds it to the production cost.
        const int rows = 8;
        const int fanOut = 4;
        const long budget = 256;

        StructType stringSchema = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.CreateArrayType(DataTypes.StringType), nullable: true),
        });

        // The value fold is real: array<string> costs strictly more per SPLIT slot than array<int>.
        Assert.True(
            NestedColumnShredder.RowGroupTransientBytesPerSlot(stringSchema[0].DataType)
                > NestedColumnShredder.RowGroupTransientBytesPerSlot(ArraySchema[0].DataType),
            "the folded per-slot cost must charge the string leaf's value width");

        ColumnBatch stringBatch = WideStringArrayBatch(stringSchema, rows, fanOut);
        ColumnBatch intBatch = WideArrayBatch(rows, fanOut);

        int stringGroups = await CountRowGroupsAsync(budget, stringSchema, stringBatch);
        int intGroups = await CountRowGroupsAsync(budget, ArraySchema, intBatch);

        Assert.True(
            stringGroups > intGroups,
            $"array<string> ({stringGroups} row groups) must split tighter than array<int> ({intGroups}) at "
            + $"the same budget once the value width is folded in");

        // And the value-heavy column still round-trips every element across the tighter split.
        byte[] bytes;
        using (var output = new MemoryStream())
        {
            await new BudgetedParquetFileWriter(budget)
                .WriteAsync(output, stringSchema, new[] { stringBatch }, CancellationToken.None);
            bytes = output.ToArray();
        }

        int observed = 0;
        await foreach (ColumnBatch group in ReadAsync(bytes, stringSchema))
        {
            var list = (ListColumnVector)group.Column(0);
            for (int i = 0; i < list.Length; i++)
            {
                ColumnVector row = list.ElementsAt(i);
                Assert.Equal(fanOut, row.Length);
                for (int e = 0; e < fanOut; e++)
                {
                    Assert.Equal(
                        $"s{observed + i}_{e}",
                        Encoding.UTF8.GetString(row.GetBytes(e)));
                }
            }

            observed += list.Length;
        }

        Assert.Equal(rows, observed);
    }

    private static async Task<int> CountRowGroupsAsync(long budget, StructType schema, ColumnBatch batch)
    {
        // DeltaSharp's reader yields one ColumnBatch per row group, so the yielded count is the emitted
        // row-group count — the boundary the fold moves.
        byte[] bytes;
        using (var output = new MemoryStream())
        {
            await new BudgetedParquetFileWriter(budget)
                .WriteAsync(output, schema, new[] { batch }, CancellationToken.None);
            bytes = output.ToArray();
        }

        int groups = 0;
        await foreach (ColumnBatch _ in ReadAsync(bytes, schema))
        {
            groups++;
        }

        return groups;
    }

    private static ColumnBatch WideStringArrayBatch(StructType schema, int rows, int fanOut)
    {
        var type = (ArrayType)schema[0].DataType;
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.StringType, rows * fanOut);
        var offsets = new int[rows + 1];
        for (int r = 0; r < rows; r++)
        {
            offsets[r] = r * fanOut;
            for (int e = 0; e < fanOut; e++)
            {
                elements.AppendBytes(Encoding.UTF8.GetBytes($"s{r}_{e}"));
            }
        }

        offsets[rows] = rows * fanOut;
        var vector = new ListColumnVector(type, elements, offsets, new bool[rows]);
        return new ManagedColumnBatch(schema, new ColumnVector[] { vector }, rows);
    }

    [Fact]
    public async Task EveryNestedColumn_IsBudgetBounded()
    {
        // Q2: the planner must probe EVERY nested column. A narrow column that plans down to a single row must
        // not short-circuit the probe of a later, far wider one — an unprobed column skips both its budget
        // clamp and its single-row reject and falls through to the coarser addressability backstop.
        var schema = DataTypes.CreateStructType(new[]
        {
            new StructField("narrow", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true),
            new StructField("wide", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
        });

        const int rows = 4;
        MapColumnVector narrow = ((ManagedColumnBatch)WideMapBatch(rows, fanOut: 4)).Column(0) is MapColumnVector m
            ? m
            : throw new InvalidOperationException("expected a map vector");
        ColumnVector wide = WideArrayBatch(rows, fanOut: 64).Column(0);
        var batch = new ManagedColumnBatch(schema, new[] { (ColumnVector)narrow, wide }, rows);

        // 256 bytes admits ONE map row (52 bytes/slot x 4 entries = 208 <= 256; 2 rows = 416 > 256), so the map
        // drives the plan to a single row first. The array's 64 elements/row cost 768 bytes (12 bytes/slot x 64)
        // even at one row, so it must still be seen — and at one row it is unsplittable, which is the only
        // genuine reject.
        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new BudgetedParquetFileWriter(256)
                .WriteAsync(output, schema, new[] { batch }, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("a single logical row contributes 64 Dremel level slot(s)", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);

        // Non-vacuity: with a budget that admits the wide column, the same two-column write succeeds and both
        // columns round-trip — so the reject above is the budget probing the SECOND column, not a broken write.
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
        int observed = 0;
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            observed += group.LogicalRowCount;
        }

        Assert.Equal(rows, observed);
    }

    [Fact]
    public async Task ASingleRowThatCannotFitAnyRowGroup_FailsClosedWithAnAccurateDiagnosis()
    {
        // The ONLY genuine reject: there is nothing left to split. The message must diagnose exactly that —
        // "implausibly wide" is a wrong diagnosis for legal data and was reserved away from this arm.
        using var output = new MemoryStream();
        DeltaStorageException error = await Assert.ThrowsAsync<DeltaStorageException>(
            () => new BudgetedParquetFileWriter(8)
                .WriteAsync(output, ArraySchema, new[] { WideArrayBatch(2, fanOut: 4) }, CancellationToken.None));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, error.Kind);
        Assert.Contains("a single logical row contributes 4 Dremel level slot(s)", error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be split any further", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("implausibly", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);   // §2.9 N9: rejected before a single byte
    }

    [Fact]
    public void PlanRowCount_ChargesLevelStreamsPlusLeafValueWidthPerSlot()
    {
        // The SPLIT cost is the number of int level streams the lane rents CONCURRENTLY (bound to the rents the
        // lanes ACTUALLY issue by the source invariant in
        // DeltaSharp.Core.Tests.WriteDoor.NestedShredderGuardWiringTests, so the level portion is not
        // self-referential) PLUS the physical width of the leaf value(s) each slot stages (#845 item 2). This
        // folded cost drives SPLITTING only (RowGroupTransientBytesPerSlot); the ACCEPTANCE/reject surface uses
        // the level-only LevelBufferBytesPerSlot, so the fold can never narrow the §2.6 acceptance set (pinned
        // by TheValueFold_NeverNarrowsTheAcceptanceSet_OnlySplitsSmaller). OMITTING the value portion here lets
        // array<string>/array<decimal> rent ~2-3x the named budget before splitting, which the fold closes so
        // ONE budget governs the whole per-column transient. The string leaf's 32-byte per-slot proxy is
        // NestedColumnShredder.VariableLeafValueBytesPerSlot (a 16-byte ReadOnlyMemory descriptor + a 16-byte
        // payload estimate).
        Assert.Equal(
            (4 * sizeof(int)) + 32 + sizeof(int),   // key/value def+rep + string key (32) + int value (4)
            NestedColumnShredder.RowGroupTransientBytesPerSlot(MapSchema[0].DataType));
        Assert.Equal(
            (2 * sizeof(int)) + sizeof(int),        // element def+rep + int element (4)
            NestedColumnShredder.RowGroupTransientBytesPerSlot(ArraySchema[0].DataType));
        Assert.Equal(
            sizeof(int) + sizeof(int),              // one def stream (reused across children) + int child (4)
            NestedColumnShredder.RowGroupTransientBytesPerSlot(
                DataTypes.CreateStructType(new[] { new StructField("x", DataTypes.IntegerType, nullable: true) })));
        Assert.Equal(0, NestedColumnShredder.RowGroupTransientBytesPerSlot(DataTypes.IntegerType));
    }

    [Fact]
    public void PlanRowCount_IsUnboundedForAnAmpleBudget()
    {
        // Non-vacuity for the planner: at the production budget a 1536-dimension embedding plans the WHOLE
        // row group, so the default path never splits mainstream data.
        ColumnBatch batch = WideArrayBatch(rows: 64, fanOut: 1_536);
        var segments = new[] { new NestedColumnShredder.ColumnSegment(batch.Column(0), 0, 64) };

        Assert.Equal(
            64,
            NestedColumnShredder.PlanRowCount(
                ArraySchema[0], segments, 64, ParquetFileWriter.DefaultNestedLevelBufferBudgetBytes));
    }

    [Theory]
    [InlineData("array", 100)]
    [InlineData("map", 50)]
    public async Task TheValueFold_NeverNarrowsTheAcceptanceSet_OnlySplitsSmaller(string shape, int fanOut)
    {
        // §2.6 non-narrowing invariant (RFL #850 / #845 item 2). array<scalar>/map<scalar,scalar> are admitted
        // UNCONDITIONALLY, so the leaf value-width fold (RowGroupTransientBytesPerSlot) may only make row groups
        // SMALLER (splitting) — it must NEVER decide whether a write is REJECTED. The decorrelated red-team's
        // exact case: a SINGLE row of a high null/empty fan-out, whose TRUE per-value transient is 0, must be
        // ACCEPTED whenever it fits the LEVEL budget, regardless of the (much larger) FOLDED budget. Before the
        // fix the 32-byte string/binary value proxy was folded into the reject surface and rejected this legal
        // row (rowSlots > budget/foldedCost), narrowing the acceptance set below the level-only baseline.
        const long budget = 1024;
        (StructType schema, ColumnBatch batch) = shape == "array"
            ? (NullElementStringArraySchema, NullElementStringArrayBatch(fanOut))
            : (NullValueStringMapSchema, NullValueStringMapBatch(fanOut));

        DataType type = schema[0].DataType;
        long maxLevelSlots = Math.Max(budget / NestedColumnShredder.LevelBufferBytesPerSlot(type), 1);
        long maxFoldedSlots = Math.Max(budget / NestedColumnShredder.RowGroupTransientBytesPerSlot(type), 1);

        // Non-vacuity: this fan-out FITS the level budget (so it is legal, unconditionally admitted) but EXCEEDS
        // the folded budget — so if the reject were (wrongly) wired to the folded cost it WOULD throw. That gap
        // is exactly the acceptance-set narrowing this cell pins closed.
        Assert.True(fanOut <= maxLevelSlots, "the fan-out must fit the LEVEL budget");
        Assert.True(fanOut > maxFoldedSlots, "the fan-out must exceed the FOLDED budget, so a folded reject would narrow acceptance");

        // PlanRowCount must ACCEPT the single row (plans >= 1), identically to a level-only plan — never throw.
        var segments = new[] { new NestedColumnShredder.ColumnSegment(batch.Column(0), 0, 1) };
        Assert.Equal(1, NestedColumnShredder.PlanRowCount(schema[0], segments, 1, budget));

        // And end to end: the row writes and reads back, so acceptance is real, not just a planning artifact.
        byte[] bytes;
        using (var output = new MemoryStream())
        {
            await new BudgetedParquetFileWriter(budget)
                .WriteAsync(output, schema, new[] { batch }, CancellationToken.None);
            bytes = output.ToArray();
        }

        int observed = 0;
        await foreach (ColumnBatch group in ReadAsync(bytes, schema))
        {
            observed += group.LogicalRowCount;
        }

        Assert.Equal(1, observed);
    }

    private static readonly StructType ArraySchema = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
    });

    private static readonly StructType MapSchema = DataTypes.CreateStructType(new[]
    {
        new StructField("m", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true),
    });

    private static readonly StructType NullElementStringArraySchema = DataTypes.CreateStructType(new[]
    {
        new StructField("a", DataTypes.CreateArrayType(DataTypes.StringType), nullable: true),
    });

    private static readonly StructType NullValueStringMapSchema = DataTypes.CreateStructType(new[]
    {
        new StructField("m", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.StringType), nullable: true),
    });

    // A single row of NULL string elements: the true per-value transient is 0, but the 32-byte string proxy
    // over-charges each slot. It fits the LEVEL budget yet exceeds the FOLDED budget — the §2.6 case the fold
    // must SPLIT (or here, pack as one row), never REJECT.
    private static ColumnBatch NullElementStringArrayBatch(int fanOut)
    {
        var type = (ArrayType)NullElementStringArraySchema[0].DataType;
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.StringType, fanOut);
        for (int e = 0; e < fanOut; e++)
        {
            elements.AppendNull();
        }

        var offsets = new[] { 0, fanOut };
        var vector = new ListColumnVector(type, elements, offsets, new bool[1]);
        return new ManagedColumnBatch(NullElementStringArraySchema, new ColumnVector[] { vector }, 1);
    }

    // A single map row whose (required) keys are tiny and whose string VALUES are all null: again a 0-byte true
    // value transient over-charged by two 32-byte proxies (key + value) folded per slot.
    private static ColumnBatch NullValueStringMapBatch(int fanOut)
    {
        var type = (MapType)NullValueStringMapSchema[0].DataType;
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, fanOut);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.StringType, fanOut);
        for (int e = 0; e < fanOut; e++)
        {
            keys.AppendBytes(Encoding.UTF8.GetBytes($"k{e}"));
            values.AppendNull();
        }

        var offsets = new[] { 0, fanOut };
        var vector = new MapColumnVector(type, keys, values, offsets, new bool[1]);
        return new ManagedColumnBatch(NullValueStringMapSchema, new ColumnVector[] { vector }, 1);
    }

    private static ColumnBatch WideArrayBatch(int rows, int fanOut)
    {
        var type = (ArrayType)ArraySchema[0].DataType;
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.IntegerType, rows * fanOut);
        var offsets = new int[rows + 1];
        for (int r = 0; r < rows; r++)
        {
            offsets[r] = r * fanOut;
            for (int e = 0; e < fanOut; e++)
            {
                elements.AppendValue(r * fanOut + e);
            }
        }

        offsets[rows] = rows * fanOut;
        var vector = new ListColumnVector(type, elements, offsets, new bool[rows]);
        return new ManagedColumnBatch(ArraySchema, new ColumnVector[] { vector }, rows);
    }

    private static ColumnBatch WideMapBatch(int rows, int fanOut)
    {
        var type = (MapType)MapSchema[0].DataType;
        var entries = new List<(string Key, int? Value)>[rows];
        for (int r = 0; r < rows; r++)
        {
            var row = new List<(string, int?)>(fanOut);
            for (int e = 0; e < fanOut; e++)
            {
                row.Add(($"k{r}_{e}", (int?)(r * fanOut + e)));
            }

            entries[r] = row;
        }

        MapColumnVector vector = NestedVectors.StringIntMap(
            type, entries.Select(e => (IReadOnlyList<(string Key, int? Value)>?)e).ToArray());
        return new ManagedColumnBatch(MapSchema, new ColumnVector[] { vector }, rows);
    }

    private static async IAsyncEnumerable<ColumnBatch> ReadAsync(byte[] bytes, StructType schema)
    {
        using var stream = new MemoryStream(bytes);
        await foreach (ColumnBatch batch in new ParquetFileReader().ReadAsync(
            stream, schema, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, CancellationToken.None))
        {
            yield return batch;
        }
    }
}
