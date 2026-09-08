using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
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
/// <item><b>DS-&gt;ref:</b> DeltaSharp's <c>(EscapePathName, ToAddPath)</c> equals the Spark reference
/// bytes for every value in the matrix. It diverges from delta-rs on the documented broader-escaping
/// on-disk residual — space, non-ASCII, <b>and</b> a number of ASCII sub-delims / URI-illegal chars
/// (measured: <c>&amp; + , ; ! $ ( ) @ &lt; &gt; |</c>) — which DeltaSharp intentionally does not
/// follow (design D1).</item>
/// <item><b>ref-&gt;DS:</b> DeltaSharp reads a real Spark-written and a real delta-rs-written table and
/// returns the exact rows and partition values — closing the #708 read-half gap for both a
/// Spark-shaped (literal space) and a delta-rs-shaped (escaped space) on-disk layout.</item>
/// </list>
/// </summary>
public sealed class PartitionEncodingGoldenDifferentialTests
{
    private static readonly string GoldensDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "PartitionEncodingGoldens");

    // The pinned reference engines. These are asserted against the fixtures' own intrinsic, engine-written
    // provenance markers (matrix.json engine/version + the _delta_log commitInfo.engineInfo the engine
    // stamps), so "emitted by a real reference engine" is enforced by a test rather than only by the README.
    private const string SparkEngineName = "apache-spark";
    private const string SparkVersion = "3.5.3";
    private const string SparkEngineInfo = "Apache-Spark/3.5.3 Delta-Lake/3.2.0";
    private const string DeltaRsEngineName = "delta-rs";
    private const string DeltaRsVersion = "1.6.3";
    private const string DeltaRsEngineInfo = "delta-rs:py-1.6.3";

    // Fixture directory names (the on-disk engine folders under Fixtures/PartitionEncodingGoldens/).
    private const string SparkDir = "spark";
    private const string DeltaRsDir = "delta-rs";

    // Dictionary stand-in for the null partition value (a Dictionary key may not be null).
    private const string NullValueKey = "<null>";

    private sealed record GoldenRow(string? Value, string OnDiskDir, string AddPathSegment);

    private static IReadOnlyList<GoldenRow> LoadMatrix(string engineDirName)
    {
        string path = Path.Combine(GoldensDir, engineDirName, "matrix.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var rows = new List<GoldenRow>();
        foreach (JsonElement e in doc.RootElement.GetProperty("matrix").EnumerateArray())
        {
            JsonElement value = e.GetProperty("value");
            rows.Add(new GoldenRow(
                value.ValueKind == JsonValueKind.Null ? null : value.GetString()!,
                e.GetProperty("on_disk_dir").GetString()!,
                e.GetProperty("add_path_segment").GetString()!));
        }

        return rows;
    }

    public static TheoryData<string?, string, string> SparkMatrix()
    {
        var data = new TheoryData<string?, string, string>();
        foreach (GoldenRow r in LoadMatrix(SparkDir))
        {
            data.Add(r.Value, r.OnDiskDir, r.AddPathSegment);
        }

        return data;
    }

    // ---- DS-&gt;ref: byte-parity against the Apache Spark reference (the #806 core oracle) ---------

    [Theory]
    [MemberData(nameof(SparkMatrix))]
    public void DeltaSharpEncoding_MatchesSpark_ByteForByte(string? value, string onDiskDir, string addPathSegment)
    {
        // Layer 1 — the on-disk directory name (escapePathName), byte-for-byte Spark.
        Assert.Equal(onDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", value));

        // Layer 2 — the committed add.path first segment (Java-URI/RFC-2396 quoting), byte-for-byte Spark.
        string physical = onDiskDir + "/part-x.parquet";
        Assert.Equal(addPathSegment + "/part-x.parquet", DeltaWriteEncoding.ToAddPath(physical));

        // The production read-side decoder is the exact inverse — DeltaSharp reads its own (and Spark's)
        // add.path. Bind to PartitionPathResolver.DecodePhysicalKey (not Uri.UnescapeDataString) so a future
        // resolver change cannot break the write/read round trip while this suite stays green.
        Assert.Equal(physical, PartitionPathResolver.DecodePhysicalKey(DeltaWriteEncoding.ToAddPath(physical)));
    }

    // ---- DS-&gt;ref: null / empty-string both map to the sentinel (measured Spark rule, #899) ------

    [Fact]
    public void NullAndEmptyPartitionValue_BothMapToSentinel_SparkParity()
    {
        // MEASURED against real Spark 3.5.3 (not assumed): a null partition value and an EMPTY-STRING
        // partition value both land in region=__HIVE_DEFAULT_PARTITION__, and Spark reads BOTH back as
        // null — the empty string is not round-trippable through Hive-style partitioning. That is why
        // HivePartitionSegment folds string.IsNullOrEmpty onto the sentinel (Spark's
        // ExternalCatalogUtils.getPartitionValueString). The null row is pinned by the golden matrix;
        // "" has no distinct reference encoding to pin (Spark emits no separate directory or add-action
        // for it), so its DS-side parity is asserted here.
        GoldenRow nullRow = LoadMatrix(SparkDir).Single(r => r.Value is null);
        Assert.Equal("region=__HIVE_DEFAULT_PARTITION__", nullRow.OnDiskDir);
        Assert.Equal(nullRow.OnDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", null));
        Assert.Equal(nullRow.OnDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", string.Empty));

        // delta-rs DIVERGES for the empty string: it writes an empty directory (region=) rather than folding
        // it onto the sentinel. It agrees with Spark on null. DeltaSharp follows Spark (design D1); the
        // disambiguating truth in every case is add.partitionValues, never the path.
        Assert.Equal("region=__HIVE_DEFAULT_PARTITION__", LoadMatrix(DeltaRsDir).Single(r => r.Value is null).OnDiskDir);
    }

    // ---- DS-&gt;ref: the delta-rs on-disk residual (documented divergence, design D1/§2.2) --------

    [Fact]
    public void DeltaSharpEncoding_FollowsSpark_NotDeltaRs_OnDiskResidual()
    {
        IReadOnlyList<GoldenRow> spark = LoadMatrix(SparkDir);
        var deltaRsByValue = LoadMatrix(DeltaRsDir).ToDictionary(r => r.Value ?? NullValueKey);

        var spaceOrNonAsciiDiverged = new List<string>();
        foreach (GoldenRow s in spark)
        {
            string dsOnDisk = DeltaWriteEncoding.HivePartitionSegment("region", s.Value);
            // Core claim: DeltaSharp's on-disk directory equals Apache Spark byte-for-byte, always.
            Assert.Equal(s.OnDiskDir, dsOnDisk);

            // Residual: delta-rs percent-escapes a BROADER on-disk set than Spark — space and non-ASCII, and
            // (measured) a number of ASCII sub-delims such as '&'. DeltaSharp follows Spark (design D1), so
            // wherever delta-rs escapes and Spark does not, DeltaSharp's on-disk dir differs from delta-rs.
            // We do NOT assert equality elsewhere: delta-rs is free to escape more, and the contract is only
            // that DeltaSharp == Spark and that a delta-rs table stays read-compatible.
            GoldenRow dr = deltaRsByValue[s.Value ?? NullValueKey];
            if (s.Value is not null && s.Value.Any(c => c == ' ' || c > 0x7F))
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

        // delta-rs also escapes some ASCII sub-delims on disk that Spark/DeltaSharp keep literal — pin the
        // measured '&' case explicitly (the documented broader-escaping residual): DeltaSharp writes
        // `region=amp&r` (like Spark), delta-rs writes `region=amp%26r`.
        GoldenRow ampSpark = spark.Single(r => r.Value == "amp&r");
        Assert.Equal("region=amp&r", DeltaWriteEncoding.HivePartitionSegment("region", "amp&r"));
        Assert.Equal("region=amp&r", ampSpark.OnDiskDir);
        Assert.Equal("region=amp%26r", deltaRsByValue["amp&r"].OnDiskDir);
    }

    // ---- The delta-rs golden's add.path column is self-consistent (backs the read-compat premise) ----

    [Fact]
    public void DeltaRsGoldens_AddPathSegment_DecodesToItsOwnOnDiskDir()
    {
        // The ref/DS read test relies on the premise that a delta-rs add.path decodes (decoded-first) onto
        // the directory delta-rs actually created. That premise is sampled by the small read-table; assert it
        // across the whole measured matrix so the delta-rs half of the golden is not inert data.
        IReadOnlyList<GoldenRow> deltaRs = LoadMatrix(DeltaRsDir);
        Assert.NotEmpty(deltaRs);
        foreach (GoldenRow r in deltaRs)
        {
            Assert.Equal(r.OnDiskDir, PartitionPathResolver.DecodePhysicalKey(r.AddPathSegment));
        }
    }

    // ---- Provenance: intrinsic engine markers + the checked-in SHA256SUMS (design §3.2 / R7) -----

    [Theory]
    [InlineData(SparkDir, SparkEngineName, SparkVersion, SparkEngineInfo)]
    [InlineData(DeltaRsDir, DeltaRsEngineName, DeltaRsVersion, DeltaRsEngineInfo)]
    public void Goldens_CarryReferenceEngineProvenanceMarkers(
        string engineDirName, string expectedEngine, string expectedVersion, string expectedEngineInfo)
    {
        // The checksum manifest below proves the bytes have not DRIFTED, but it is minted by the same script
        // that writes the fixtures, so on its own it cannot prove the bytes came from a reference engine at
        // all (C2: never trust a self-settable signal). These markers are written by the reference engines
        // themselves — a golden regenerated from DeltaSharp output would have to forge them deliberately.
        string engineDir = Path.Combine(GoldensDir, engineDirName);
        using (JsonDocument matrix = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(engineDir, "matrix.json"))))
        {
            Assert.Equal(expectedEngine, matrix.RootElement.GetProperty("engine").GetString());
            Assert.Equal(expectedVersion, matrix.RootElement.GetProperty("version").GetString());
        }

        string logPath = Path.Combine(engineDir, "read-table", "_delta_log", "00000000000000000000.json");
        string? engineInfo = null;
        foreach (string line in File.ReadAllLines(logPath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument action = JsonDocument.Parse(line);
            if (action.RootElement.TryGetProperty("commitInfo", out JsonElement commitInfo)
                && commitInfo.TryGetProperty("engineInfo", out JsonElement info))
            {
                engineInfo = info.GetString();
            }
        }

        Assert.Equal(expectedEngineInfo, engineInfo);
    }

    [Theory]
    [InlineData(SparkDir)]
    [InlineData(DeltaRsDir)]
    public void Goldens_MatchCheckedInChecksums(string engineDirName)
    {
        // Self-enforcing provenance tripwire: a golden silently regenerated or hand-edited to match a buggy
        // encoder (the exact failure mode design R7 warns about) drifts from SHA256SUMS and fails here,
        // forcing a deliberate checksum update rather than passing unnoticed.
        string engineDir = Path.Combine(GoldensDir, engineDirName);
        string sumsPath = Path.Combine(engineDir, "SHA256SUMS");
        Assert.True(File.Exists(sumsPath), $"missing {engineDirName}/SHA256SUMS");

        var manifest = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(sumsPath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // Format: "<hex-sha256>  <relative/path>" (two spaces, sha256sum/shasum -c convention).
            int sep = line.IndexOf("  ", StringComparison.Ordinal);
            Assert.True(sep > 0, $"malformed SHA256SUMS line: {line}");
            string expectedHash = line[..sep].Trim();
            string relative = line[(sep + 2)..].Trim();
            if (relative.StartsWith("./", StringComparison.Ordinal))
            {
                relative = relative[2..];
            }

            string filePath = Path.Combine(engineDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(filePath), $"SHA256SUMS references a missing file: {relative}");
            string actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(filePath)));
            Assert.Equal(expectedHash, actualHash);
            Assert.True(manifest.Add(relative), $"duplicate SHA256SUMS entry: {relative}");
        }

        // Reverse direction: every file ON DISK must be listed. Without this an ADDED, unvetted golden — the
        // very way an unmeasured fixture enters the tree — passes unnoticed. Set equality also subsumes the
        // old ">= 6" floor, which silently stopped meaning anything the moment a fixture was added.
        var onDisk = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(engineDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(engineDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == "SHA256SUMS")
            {
                continue;
            }

            onDisk.Add(relative);
        }

        Assert.Equal(manifest, onDisk);
    }

    // ---- ref-&gt;DS: DeltaSharp reads real reference-engine-written tables ------------------------

    // The committed read-table rows (both engines write the same logical rows; only the on-disk partition
    // directory encoding differs). Includes the `p%p` cell so the double-decode path (on-disk region=p%25p,
    // add.path region=p%2525p) is exercised end-to-end through a real foreign table, not just at string level.
    private static readonly (long Id, string Name, string Region)[] ExpectedReadRows =
    {
        (1, "a1", "US"), (2, "b2", "a=b"), (3, "c3", "na me"), (4, "d4", "o'brien"), (5, "e5", "US"),
        (6, "f6", "p%p"),
    };

    [Fact]
    public async Task DeltaSharp_Reads_RealSparkWrittenTable()
    {
        // Spark-shaped on-disk layout: literal space (region=na me).
        await AssertReadsForeignTableAsync(SparkDir, ExpectedReadRows);
    }

    [Fact]
    public async Task DeltaSharp_Reads_RealDeltaRsWrittenTable()
    {
        // delta-rs-shaped on-disk layout: escaped space (region=na%20me).
        await AssertReadsForeignTableAsync(DeltaRsDir, ExpectedReadRows);
    }

    private static async Task AssertReadsForeignTableAsync(string engineDirName, (long Id, string Name, string Region)[] expected)
    {
        // The fixture read-table is a real reference-engine _delta_log + Parquet tree copied to the test
        // output. Read it read-only through the same door as any Delta table; partition truth comes from
        // add.partitionValues (never inferred from the path), which is what makes the two on-disk layouts
        // (Spark's literal `region=na me` vs delta-rs's escaped `region=na%20me`) both resolve.
        string table = Path.Combine(GoldensDir, engineDirName, "read-table");
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
