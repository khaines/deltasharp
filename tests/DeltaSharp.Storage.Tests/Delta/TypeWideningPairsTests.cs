using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end curated coverage for the wider Delta <b>type-widening</b> pairs the CDF/snapshot oracle did not
/// yet exercise: <c>short → int</c>, <c>float → double</c>, and grow-only <c>decimal(p,s) → decimal(p',s')</c>
/// (the model oracle already covers <c>int → long</c>). Each pair is driven through the REAL production doors
/// in BOTH column-mapping modes it is reachable in (<c>none</c> and <c>name</c>): create a table with the
/// NARROW column, append narrow rows, enable <c>typeWidening</c>, then a <c>mergeSchema</c> append writes the
/// WIDE type and APPLIES the same-family widening (<see cref="TypeWidening.IsSchemaEvolutionWidening"/>).
/// </summary>
/// <remarks>
/// <para><b>Load-bearing.</b> Each narrow batch carries at least one value that would be WRONG under a
/// truncation / bit-reinterpretation misread (a NEGATIVE <c>short</c> whose unsigned misread differs; a
/// <c>float</c> whose exact double promotion pins the mantissa; a <c>decimal</c> whose rescale must preserve
/// the value), and each wide append carries a value only representable in the WIDE type (an <c>int</c> beyond
/// <c>short</c> range; a <c>double</c> not representable as <c>float</c>; a decimal needing the new scale). So
/// a regression that misread the promoted narrow file, or failed to evolve the reconciled schema to the wide
/// type, goes RED on the value map or the end-schema type. The NEGATIVE oracle proves the promotion is GATED:
/// reading the narrow file under the wide schema with the promotion gate CLOSED fails closed as
/// <see cref="StorageErrorKind.SchemaMismatch"/> — never a silent misread.</para>
/// <para>Cross-family pairs (e.g. <c>int → double</c>) are deliberately NOT exercised on the append path: they
/// are read-promotable / ALTER-applicable but NOT schema-evolution-eligible
/// (<see cref="TypeWidening.IsSchemaEvolutionWidening"/> excludes them), so a <c>mergeSchema</c> append of a
/// cross-family type is rejected fail-closed — covered by the schema-evolution writer tests.</para>
/// </remarks>
[Collection(ColumnMappingTestCollection.Name)]
public sealed class TypeWideningPairsTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Widen_ShortToInt_PromotesNarrowRows_AndEvolvesSchema(bool nameMapped)
    {
        ColumnMappingMode mode = nameMapped ? ColumnMappingMode.Name : ColumnMappingMode.None;
        // A NEGATIVE short (-5) is the truncation/sign-extension witness (an unsigned misread yields 65531);
        // 30000 is the narrow upper sample. The wide append writes 100000 / -200000 — GENUINE ints beyond the
        // short range, proving the reconciled column is truly `int` (not a re-widened short).
        (StructType schema, Dictionary<long, int> map) = await RunWideningAsync(
            mode, DataTypes.ShortType, DataTypes.IntegerType,
            narrowIds: [1, 2], fillNarrow: v => { v.AppendValue((short)-5); v.AppendValue((short)30000); },
            wideIds: [3, 4], fillWide: v => { v.AppendValue(100000); v.AppendValue(-200000); },
            decode: (v, r) => v.GetValue<int>(r));

        Assert.Equal(DataTypes.IntegerType, schema["v"].DataType);
        Assert.Equal(
            new Dictionary<long, int> { [1] = -5, [2] = 30000, [3] = 100000, [4] = -200000 }, map);

        await AssertFailsClosedWithoutPromotionAsync(
            DataTypes.ShortType, DataTypes.IntegerType, v => { v.AppendValue((short)-5); v.AppendValue((short)30000); }, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Widen_FloatToDouble_PromotesNarrowRows_AndEvolvesSchema(bool nameMapped)
    {
        ColumnMappingMode mode = nameMapped ? ColumnMappingMode.Name : ColumnMappingMode.None;
        // 1.5f / -0.25f are EXACT in both float and double, so a correct promotion reproduces them bit-for-bit
        // (a bit-reinterpretation misread would not). The wide append writes 0.1d — NOT representable as a
        // float — proving the reconciled column is truly `double`.
        (StructType schema, Dictionary<long, double> map) = await RunWideningAsync(
            mode, DataTypes.FloatType, DataTypes.DoubleType,
            narrowIds: [1, 2], fillNarrow: v => { v.AppendValue(1.5f); v.AppendValue(-0.25f); },
            wideIds: [3], fillWide: v => v.AppendValue(0.1d),
            decode: (v, r) => v.GetValue<double>(r));

        Assert.Equal(DataTypes.DoubleType, schema["v"].DataType);
        Assert.Equal(new Dictionary<long, double> { [1] = 1.5d, [2] = -0.25d, [3] = 0.1d }, map);

        await AssertFailsClosedWithoutPromotionAsync(
            DataTypes.FloatType, DataTypes.DoubleType, v => { v.AppendValue(1.5f); v.AppendValue(-0.25f); }, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Widen_DecimalGrow_RescalesNarrowRows_AndEvolvesSchema(bool nameMapped)
    {
        ColumnMappingMode mode = nameMapped ? ColumnMappingMode.Name : ColumnMappingMode.None;
        // decimal(6,2) → decimal(10,4): both the integer-digit range (p−s: 4→6) and the scale (2→4) grow, so
        // every value is preserved by an EXACT rescale (12.34 → 12.3400). The wide append writes 1.2345 —
        // needing the NEW scale of 4 — proving the reconciled column is truly decimal(10,4).
        DecimalType narrow = DataTypes.CreateDecimalType(6, 2);
        DecimalType wide = DataTypes.CreateDecimalType(10, 4);
        (StructType schema, Dictionary<long, decimal> map) = await RunWideningAsync(
            mode, narrow, wide,
            narrowIds: [1, 2],
            fillNarrow: v => { ParquetTypeMapping.AppendDecimal(v, narrow, 12.34m); ParquetTypeMapping.AppendDecimal(v, narrow, -0.05m); },
            wideIds: [3], fillWide: v => ParquetTypeMapping.AppendDecimal(v, wide, 1.2345m),
            decode: (v, r) => ParquetTypeMapping.ReadDecimal(v, wide, r));

        Assert.Equal(wide, schema["v"].DataType);
        Assert.Equal(new Dictionary<long, decimal> { [1] = 12.3400m, [2] = -0.0500m, [3] = 1.2345m }, map);

        await AssertFailsClosedWithoutPromotionAsync(
            narrow, wide,
            v => { ParquetTypeMapping.AppendDecimal(v, narrow, 12.34m); ParquetTypeMapping.AppendDecimal(v, narrow, -0.05m); }, 2);
    }

    // ------------------------------------------------------------------ end-to-end widening driver

    // Creates a (`none` or `name`-mapped) table with a NARROW `v` column + narrow rows, enables typeWidening,
    // then a mergeSchema append writes the WIDE `v` column + wide rows (applying the same-family widening).
    // Reads the final snapshot back through the production door (which opens the promotion gate because the
    // table declares the typeWidening feature) and returns the reconciled end schema + an id→decoded-value map
    // (narrow rows PROMOTED into the wide lane, wide rows native).
    private async Task<(StructType Schema, Dictionary<long, T> Map)> RunWideningAsync<T>(
        ColumnMappingMode mode, DataType narrow, DataType wide,
        long[] narrowIds, Action<MutableColumnVector> fillNarrow,
        long[] wideIds, Action<MutableColumnVector> fillWide,
        Func<ColumnVector, int, T> decode)
    {
        string root = NewRoot();
        var narrowSchema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("v", narrow, nullable: true),
        });
        var wideSchema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("v", wide, nullable: true),
        });

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(root))
        {
            ColumnBatch narrowBatch = BuildBatch(narrowSchema, narrowIds, fillNarrow);
            if (mode == ColumnMappingMode.Name)
            {
                await target.CreateNameMappedTableAsync(
                    narrowSchema, Array.Empty<string>(), new[] { narrowBatch }, new SeededPhysicalNameSource("tw-pairs"));
            }
            else
            {
                await target.AppendAsync(narrowSchema, Array.Empty<string>(), new[] { narrowBatch });
            }
        }

        await new DeltaTableWriter(new LocalFileSystemBackend(root)).EnableTypeWideningAsync();

        using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(root))
        {
            ColumnBatch wideBatch = BuildBatch(wideSchema, wideIds, fillWide);
            await target.AppendAsync(wideSchema, Array.Empty<string>(), new[] { wideBatch }, mergeSchema: true);
        }

        using DeltaReadSource source = DeltaReadSource.ForLocalPath(root);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        int idIdx = info.Schema.IndexOf("id");
        int vIdx = info.Schema.IndexOf("v");
        var map = new Dictionary<long, T>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector idColumn = batch.SelectedColumn(idIdx);
            ColumnVector vColumn = batch.SelectedColumn(vIdx);
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                map[idColumn.GetValue<long>(r)] = decode(vColumn, r);
            }
        }

        return (info.Schema, map);
    }

    // The negative oracle: the narrow file read under the WIDE schema with the promotion gate CLOSED (the
    // state of a reader that has NOT observed the typeWidening feature — e.g. a table whose widen was reverted)
    // must fail closed as SchemaMismatch, never silently promote.
    private static async Task AssertFailsClosedWithoutPromotionAsync(
        DataType narrow, DataType wide, Action<MutableColumnVector> fill, int rowCount)
    {
        var narrowSchema = new StructType(new[] { new StructField("v", narrow, nullable: true) });
        MutableColumnVector column = ColumnVectors.Create(narrow, rowCount);
        fill(column);
        var batch = new ManagedColumnBatch(narrowSchema, new ColumnVector[] { column }, rowCount);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(narrowSchema, new[] { batch });

        var wideSchema = new StructType(new[] { new StructField("v", wide, nullable: true) });
        DeltaStorageException ex = await Assert.ThrowsAsync<DeltaStorageException>(
            () => ParquetTestHelpers.ReadAllAsync(bytes, wideSchema, keepRowGroup: null, allowTypeWideningPromotion: false));
        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
    }

    private static ManagedColumnBatch BuildBatch(StructType schema, long[] ids, Action<MutableColumnVector> fillV)
    {
        MutableColumnVector idColumn = ColumnVectors.Create(DataTypes.LongType, ids.Length);
        foreach (long id in ids)
        {
            idColumn.AppendValue(id);
        }

        MutableColumnVector vColumn = ColumnVectors.Create(schema[1].DataType, ids.Length);
        fillV(vColumn);
        return new ManagedColumnBatch(schema, new ColumnVector[] { idColumn, vColumn }, ids.Length);
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ds-tw-pairs-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }
}
