using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #806 Inc-C — the differential parity oracle ("measured, not assumed"). These tests measure
/// DeltaSharp's partition-path encoding against goldens emitted by REAL Apache Spark 3.5 and delta-rs
/// 1.6 (see <c>Fixtures/PartitionEncodingGoldens/README.md</c> for the provenance guarantee — the
/// fixtures are never regenerated from DeltaSharp output). Two directions per the design §3.2 gate:
/// <list type="bullet">
/// <item><b>DS→ref:</b> DeltaSharp's <c>(EscapePathName, ToAddPath)</c> equals the Spark reference
/// bytes for every value in the matrix; it diverges from delta-rs exactly and only on the documented
/// on-disk residual (space + non-ASCII), which DeltaSharp intentionally does not follow (design D1).</item>
/// <item><b>ref→DS:</b> DeltaSharp reads a real Spark-written and a real delta-rs-written table and
/// returns the exact rows and partition values — closing the #708 read-half gap for both a
/// Spark-shaped (literal space) and a delta-rs-shaped (escaped space) on-disk layout.</item>
/// </list>
/// </summary>
public sealed class PartitionEncodingGoldenDifferentialTests
{
    private static readonly string GoldensDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "PartitionEncodingGoldens");

    private sealed record GoldenRow(string Value, string OnDiskDir, string AddPathSegment);

    private static IReadOnlyList<GoldenRow> LoadMatrix(string engine)
    {
        string path = Path.Combine(GoldensDir, engine, "matrix.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var rows = new List<GoldenRow>();
        foreach (JsonElement e in doc.RootElement.GetProperty("matrix").EnumerateArray())
        {
            rows.Add(new GoldenRow(
                e.GetProperty("value").GetString()!,
                e.GetProperty("on_disk_dir").GetString()!,
                e.GetProperty("add_path_segment").GetString()!));
        }

        return rows;
    }

    public static TheoryData<string, string, string> SparkMatrix()
    {
        var data = new TheoryData<string, string, string>();
        foreach (GoldenRow r in LoadMatrix("spark"))
        {
            data.Add(r.Value, r.OnDiskDir, r.AddPathSegment);
        }

        return data;
    }

    // ---- DS→ref: byte-parity against the Apache Spark reference (the #806 core oracle) -----------

    [Theory]
    [MemberData(nameof(SparkMatrix))]
    public void DeltaSharpEncoding_MatchesSpark_ByteForByte(string value, string onDiskDir, string addPathSegment)
    {
        // Layer 1 — the on-disk directory name (escapePathName), byte-for-byte Spark.
        Assert.Equal(onDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", value));

        // Layer 2 — the committed add.path first segment (Java-URI/RFC-2396 quoting), byte-for-byte Spark.
        string physical = onDiskDir + "/part-x.parquet";
        Assert.Equal(addPathSegment + "/part-x.parquet", DeltaWriteEncoding.ToAddPath(physical));

        // The resolver decode is the exact inverse — DeltaSharp reads its own (and Spark's) add.path.
        Assert.Equal(physical, Uri.UnescapeDataString(DeltaWriteEncoding.ToAddPath(physical)));
    }

    // ---- DS→ref: the delta-rs on-disk residual (documented divergence, design D1/§2.2) ----------

    [Fact]
    public void DeltaSharpEncoding_FollowsSpark_NotDeltaRs_OnDiskResidual()
    {
        IReadOnlyList<GoldenRow> spark = LoadMatrix("spark");
        var deltaRsByValue = LoadMatrix("delta-rs").ToDictionary(r => r.Value);

        var spaceOrNonAsciiDiverged = new List<string>();
        foreach (GoldenRow s in spark)
        {
            string dsOnDisk = DeltaWriteEncoding.HivePartitionSegment("region", s.Value);
            // Core claim: DeltaSharp's on-disk directory equals Apache Spark byte-for-byte, always.
            Assert.Equal(s.OnDiskDir, dsOnDisk);

            // Residual: delta-rs percent-escapes a BROADER on-disk set than Spark — at minimum space and
            // non-ASCII (measured: it also escapes some sub-delims such as '&'). DeltaSharp follows Spark
            // (design D1), so wherever delta-rs escapes and Spark does not, DeltaSharp's on-disk dir differs
            // from delta-rs. We do NOT assert equality elsewhere: delta-rs is free to escape more, and the
            // contract is only that DeltaSharp == Spark and that a delta-rs table stays read-compatible.
            GoldenRow dr = deltaRsByValue[s.Value];
            if (s.Value.Any(c => c == ' ' || c > 0x7F))
            {
                Assert.NotEqual(dr.OnDiskDir, dsOnDisk); // the documented space/non-ASCII residual
                spaceOrNonAsciiDiverged.Add(s.Value);
            }
        }

        // The residual is real and exercised across space AND non-ASCII (Latin, CJK, emoji).
        Assert.Contains("na me", spaceOrNonAsciiDiverged);
        Assert.Contains("région", spaceOrNonAsciiDiverged);
        Assert.True(spaceOrNonAsciiDiverged.Count >= 4,
            $"expected the space/non-ASCII residual to be broadly exercised; diverged={spaceOrNonAsciiDiverged.Count}");
    }

    // ---- ref→DS: DeltaSharp reads a real Spark-written table (literal-space on-disk layout) ------

    [Fact]
    public async Task DeltaSharp_Reads_RealSparkWrittenTable()
    {
        await AssertReadsForeignTableAsync(
            engine: "spark",
            expected: new (long Id, string Name, string Region)[]
            {
                (1, "a1", "US"), (2, "b2", "a=b"), (3, "c3", "na me"), (4, "d4", "o'brien"), (5, "e5", "US"),
            });
    }

    // ---- ref→DS: DeltaSharp reads a real delta-rs-written table (escaped-space on-disk layout) ---

    [Fact]
    public async Task DeltaSharp_Reads_RealDeltaRsWrittenTable()
    {
        await AssertReadsForeignTableAsync(
            engine: "delta-rs",
            expected: new (long Id, string Name, string Region)[]
            {
                (1, "a1", "US"), (2, "b2", "a=b"), (3, "c3", "na me"), (4, "d4", "o'brien"), (5, "e5", "US"),
            });
    }

    private static async Task AssertReadsForeignTableAsync(string engine, (long Id, string Name, string Region)[] expected)
    {
        // The fixture read-table is a real reference-engine _delta_log + Parquet tree copied to the test
        // output. Read it read-only through the same door as any Delta table; partition truth comes from
        // add.partitionValues (never inferred from the path), which is what makes the two on-disk layouts
        // (Spark's literal `region=na me` vs delta-rs's escaped `region=na%20me`) both resolve.
        string table = Path.Combine(GoldensDir, engine, "read-table");
        using DeltaReadSource source = DeltaReadSource.ForLocalPath(table);
        DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
        int idIdx = info.Schema.IndexOf("id");
        int nameIdx = info.Schema.IndexOf("name");
        int regionIdx = info.Schema.IndexOf("region");

        var actual = new List<(long, string, string)>();
        foreach (ColumnBatch batch in await source.ReadBatchesAsync(info.Version))
        {
            ColumnVector id = batch.Column(idIdx);
            ColumnVector name = batch.Column(nameIdx);
            ColumnVector region = batch.Column(regionIdx);
            for (int r = 0; r < batch.RowCount; r++)
            {
                actual.Add((
                    id.GetValue<long>(r),
                    Encoding.UTF8.GetString(name.GetBytes(r)),
                    Encoding.UTF8.GetString(region.GetBytes(r))));
            }
        }

        Assert.Equal(
            expected.Select(e => (e.Id, e.Name, e.Region)).OrderBy(t => t.Id).ToArray(),
            actual.OrderBy(t => t.Item1).ToArray());
    }
}
