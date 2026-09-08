using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Parquet;
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
/// (measured, and pinned by <c>DeltaSharpEncoding_FollowsSpark_NotDeltaRs_OnDiskResidual</c> so it cannot
/// rot: <c>&amp; + , ; ! $ ( ) @ &lt; &gt; | } `</c>) — which DeltaSharp intentionally does not follow
/// (design D1).</item>
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

    // The Parquet footer's STRUCTURED `created_by` field, set by the writer library itself.
    //
    // This must be read as a structured field, NOT scanned for as a substring: Parquet.Net exposes
    // `ParquetWriter.CustomMetadata` (DeltaSharp itself uses it, ParquetFileWriter.cs:197), so arbitrary
    // key/value strings — including "parquet-mr version 1.13.1" — can be planted in a DeltaSharp-written
    // footer. `created_by` is not settable that way, and Parquet.Net stamps its own value, which is why the
    // negative assertion below (no golden may be Parquet.Net-authored) is the part that actually bites.
    private const string SparkCreatedByPrefix = "parquet-mr version 1.13.";
    private const string DeltaRsCreatedByPrefix = "delta-rs version py-1.6.3";

    // Parquet.Net stamps this into `created_by`; no reference-engine golden may carry it.
    private const string DeltaSharpWriterCreatedBy = "Parquet.Net";

    // Fixture directory names (the on-disk engine folders under Fixtures/PartitionEncodingGoldens/).
    private const string SparkDir = "spark";
    private const string DeltaRsDir = "delta-rs";

    // Dictionary stand-in for the null partition value (a Dictionary key may not be null).
    private const string NullValueKey = "<null>";

    private sealed record GoldenRow(string? Value, string OnDiskDir, string AddPathSegment);

    /// <summary>Renders a fixture value safely: partition values may contain control characters, which must
    /// never reach a log or CI output stream verbatim.</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            _ = char.IsControl(c) ? sb.Append("\\u").Append(((int)c).ToString("X4")) : sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

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

    // ---- The empty-string axis: measured engine behaviour + DeltaSharp's value-layer divergence (#899) ----

    private static JsonElement EmptyStringBlock(string engineDirName)
    {
        string path = Path.Combine(GoldensDir, engineDirName, "matrix.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetProperty("empty_string").Clone();
    }

    [Fact]
    public void EmptyStringPartitionValue_EngineBehaviour_IsHarvestedNotAssumed()
    {
        // The empty string cannot be an ordinary matrix row: Spark folds it onto the SAME
        // __HIVE_DEFAULT_PARTITION__ partition as null and emits no distinct directory or add-action for it,
        // so there is no separate (dir, add.path) pair to record. Rather than assert that in prose, both
        // generators HARVEST the behaviour from a real run into matrix.json's `empty_string` block, and this
        // test pins it — so the claim is a checked-in measurement (design R7), not a comment.
        JsonElement spark = EmptyStringBlock(SparkDir);
        Assert.True(spark.GetProperty("folds_onto_sentinel_with_null").GetBoolean());
        Assert.False(spark.GetProperty("distinct_add_action").GetBoolean());
        Assert.Equal(
            new[] { "region=__HIVE_DEFAULT_PARTITION__", "region=keep" },
            spark.GetProperty("on_disk_dirs").EnumerateArray().Select(e => e.GetString()).ToArray());

        // delta-rs DIVERGES on this axis: "" gets its OWN directory (region=) and its own add-action with a
        // partitionValues of "". It agrees with Spark on null. DeltaSharp follows Spark (design D1).
        JsonElement deltaRs = EmptyStringBlock(DeltaRsDir);
        Assert.True(deltaRs.GetProperty("distinct_add_action").GetBoolean());
        Assert.Equal("region=", deltaRs.GetProperty("add_path_segment").GetString());

        // BOTH reference engines read an empty-string partition value back as NULL — they re-derive the value
        // from the sentinel/empty directory rather than trusting the committed partitionValues.
        Assert.True(spark.GetProperty("read_back_is_null").GetBoolean());
        Assert.True(deltaRs.GetProperty("read_back_is_null").GetBoolean());

        // The engines AGREE on null (they diverge only on ""): both fold it onto the sentinel directory.
        // (Asserted here because an earlier refactor of this class silently dropped it while §3.2 still
        // claimed it.)
        Assert.Equal(
            LoadMatrix(SparkDir).Single(r => r.Value is null).OnDiskDir,
            LoadMatrix(DeltaRsDir).Single(r => r.Value is null).OnDiskDir);

        // Harvested but previously unasserted — measured data that nothing read is dead data.
        Assert.Equal(
            new[] { null, "keep" },
            spark.GetProperty("add_partition_values").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new[] { string.Empty, null, "keep" },
            deltaRs.GetProperty("add_partition_values").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task EmptyStringPartitionValue_DirectoryMatchesSpark_ButDeltaSharpPreservesTheValue()
    {
        // LAYER 1 (directory) — DeltaSharp matches Spark: null and "" both fold onto the sentinel. This is why
        // HivePartitionSegment tests string.IsNullOrEmpty (Spark's ExternalCatalogUtils.getPartitionValueString).
        GoldenRow nullRow = LoadMatrix(SparkDir).Single(r => r.Value is null);
        Assert.Equal("region=__HIVE_DEFAULT_PARTITION__", nullRow.OnDiskDir);
        Assert.Equal(nullRow.OnDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", null));
        Assert.Equal(nullRow.OnDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", string.Empty));

        // LAYER 2 (recovered value) — DeltaSharp DIVERGES from BOTH reference engines, deliberately.
        // Spark and delta-rs re-derive the partition value from the directory, so an empty string comes back
        // as null (see EmptyStringPartitionValue_EngineBehaviour_IsHarvestedNotAssumed). DeltaSharp treats
        // add.partitionValues as authoritative and writes the value verbatim, so "" round-trips as "".
        // That is LOSSLESS (it distinguishes null from "", which the engines cannot) but it is NOT byte-parity,
        // and it is asserted here so the divergence is pinned rather than discovered later. Design §3.2.
        Assert.True(EmptyStringBlock(SparkDir).GetProperty("read_back_is_null").GetBoolean());

        string root = Path.Combine(Path.GetTempPath(), "ds806-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var schema = new StructType(new[]
            {
                new StructField("region", DataTypes.StringType, nullable: true),
                new StructField("id", DataTypes.LongType, nullable: false),
            });

            MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 2);
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 2);
            region.AppendBytes(Encoding.UTF8.GetBytes(string.Empty)); // row 1: the empty string
            id.AppendValue(1L);
            region.AppendNull();                                     // row 2: a genuine null
            id.AppendValue(2L);
            ColumnBatch batch = new ManagedColumnBatch(schema, new ColumnVector[] { region, id }, 2);

            using (DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(root))
            {
                await target.AppendAsync(schema, new[] { "region" }, new[] { batch });
            }

            // Both rows share the sentinel directory on disk (Spark-parity layer 1) ...
            Assert.Equal(
                new[] { "region=__HIVE_DEFAULT_PARTITION__" },
                Directory.GetDirectories(root).Select(Path.GetFileName)
                    .Where(n => n!.StartsWith("region=", StringComparison.Ordinal))
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray());

            // ... and the load-bearing artifact is the COMMITTED add.partitionValues: assert it directly, so a
            // future writer that folded "" -> null in the log (with the reader compensating elsewhere) cannot
            // keep this test green.
            var committed = new List<string?>();
            foreach (string line in File.ReadAllLines(
                Path.Combine(root, "_delta_log", "00000000000000000000.json")))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using JsonDocument action = JsonDocument.Parse(line);
                if (!action.RootElement.TryGetProperty("add", out JsonElement add))
                {
                    continue;
                }

                Assert.StartsWith(
                    "region=__HIVE_DEFAULT_PARTITION__/",
                    add.GetProperty("path").GetString(),
                    StringComparison.Ordinal);
                JsonElement pv = add.GetProperty("partitionValues").GetProperty("region");
                committed.Add(pv.ValueKind == JsonValueKind.Null ? null : pv.GetString());
            }

            Assert.Equal(new string?[] { null, string.Empty }, committed.OrderBy(v => v, StringComparer.Ordinal).ToArray());

            // ... so DeltaSharp recovers "" as "" and null as null. Measured foreign-reader behaviour on this
            // exact shape (integration-tier, #905): real Spark 3.5.3 returns "" (it honours the committed
            // value), real delta-rs 1.6.3 normalizes it to null.
            using DeltaReadSource source = DeltaReadSource.ForLocalPath(root);
            DeltaSnapshotInfo info = await source.LoadSnapshotAsync(null, null);
            int regionIdx = info.Schema.IndexOf("region");
            int idIdx = info.Schema.IndexOf("id");
            var recovered = new List<(long Id, string? Region)>();
            foreach (ColumnBatch b in await source.ReadBatchesAsync(info.Version))
            {
                for (int r = 0; r < b.RowCount; r++)
                {
                    ColumnVector reg = b.Column(regionIdx);
                    recovered.Add((
                        b.Column(idIdx).GetValue<long>(r),
                        reg.IsNull(r) ? null : Encoding.UTF8.GetString(reg.GetBytes(r))));
                }
            }

            Assert.Equal(
                new (long, string?)[] { (1L, string.Empty), (2L, null) },
                recovered.OrderBy(t => t.Id).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
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

        // PIN the exact ASCII residual set rather than describing it in prose. The enumerated list is repeated
        // in the class summary, the README and design §3.2; deriving it here means a regeneration that shifts
        // the set fails loudly instead of letting those three descriptions silently rot.
        var residualAscii = new SortedSet<char>();
        foreach (GoldenRow row in spark)
        {
            if (row.Value is null || row.Value.Any(c => c == ' ' || c > 0x7F))
            {
                continue; // space / non-ASCII residual is asserted above; this pins the ASCII-only extras.
            }

            GoldenRow dr = deltaRsByValue[row.Value];
            foreach (char c in row.Value.Where(c => !char.IsLetterOrDigit(c)))
            {
                // A character is a delta-rs-only residual iff SPARK keeps it literal and delta-rs escapes it.
                // Deriving per VALUE instead would wrongly sweep in characters Spark escapes too: `brace{}`
                // gives Spark `region=brace%7B}` and delta-rs `region=brace%7B%7D`, so only `}` is a residual
                // while `{` is escaped by both.
                bool sparkKeepsLiteral = row.OnDiskDir.Contains(c, StringComparison.Ordinal);
                bool deltaRsKeepsLiteral = dr.OnDiskDir.Contains(c, StringComparison.Ordinal);
                if (sparkKeepsLiteral && !deltaRsKeepsLiteral)
                {
                    residualAscii.Add(c);
                }
            }
        }

        // Must stay byte-identical to the list in the class summary, the fixture README and design §3.2.
        Assert.Equal("!$&()+,;<>@`|}", new string(residualAscii.ToArray()));
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
    [InlineData(SparkDir, SparkEngineName, SparkVersion, SparkEngineInfo, SparkCreatedByPrefix)]
    [InlineData(DeltaRsDir, DeltaRsEngineName, DeltaRsVersion, DeltaRsEngineInfo, DeltaRsCreatedByPrefix)]
    public async Task Goldens_CarryReferenceEngineProvenanceMarkers(
        string engineDirName, string expectedEngine, string expectedVersion, string expectedEngineInfo,
        string expectedCreatedByPrefix)
    {
        // The checksum manifest below proves the bytes have not DRIFTED, but it is minted by the same script
        // that writes the fixtures, so on its own it cannot prove the bytes came from a reference engine at
        // all (C2: never trust a self-settable signal).
        //
        // These markers are NOT equally strong, and it matters which is which:
        //   * the Parquet footer's structured `created_by` (below) is stamped by the writer LIBRARY and is not
        //     settable through Parquet.Net's CustomMetadata, so an accidentally-substituted or
        //     DeltaSharp-authored data file is caught. It is NOT proof against a determined forger who
        //     crafts a footer byte-by-byte — no in-repo artifact can be;
        //   * `_delta_log` commitInfo.engineInfo is written by the engine itself — strong;
        //   * matrix.json `version` is read from the installed library at generation time — moderate;
        //   * matrix.json `engine` is a constant typed into the generator — it pins DRIFT only, not origin.
        string engineDir = Path.Combine(GoldensDir, engineDirName);
        using (JsonDocument matrix = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(engineDir, "matrix.json"))))
        {
            Assert.Equal(expectedEngine, matrix.RootElement.GetProperty("engine").GetString());
            Assert.Equal(expectedVersion, matrix.RootElement.GetProperty("version").GetString());
        }

        // BOTH committed logs must carry the engine's commit shape — the read-table log and matrix-log.json.
        // matrix-log.json is what grounds all 33 matrix rows, so leaving it unasserted would let a forger
        // replace it with bare hand-authored `add` lines.
        foreach (string logPath in new[]
        {
            Path.Combine(engineDir, "read-table", "_delta_log", "00000000000000000000.json"),
            Path.Combine(engineDir, "matrix-log.json"),
        })
        {
            string? engineInfo = null;
            bool sawProtocol = false;
            string[] partitionColumns = Array.Empty<string>();

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

                sawProtocol |= action.RootElement.TryGetProperty("protocol", out _);

                if (action.RootElement.TryGetProperty("metaData", out JsonElement metaData)
                    && metaData.TryGetProperty("partitionColumns", out JsonElement cols))
                {
                    partitionColumns = cols.EnumerateArray().Select(c => c.GetString()!).ToArray();
                }
            }

            string which = Path.GetFileName(logPath);
            Assert.Equal(expectedEngineInfo, engineInfo);
            Assert.True(sawProtocol, $"{engineDirName}/{which}: no protocol action — not an engine-written log");
            Assert.Equal(new[] { "region" }, partitionColumns);
        }

        // Every committed data file's STRUCTURED created_by must be its engine's, and must not be
        // Parquet.Net's — that negative is what catches a cross-engine substitution or a DeltaSharp-authored
        // Parquet, both of which a substring scan of the whole file would miss (CustomMetadata can plant any
        // string in the footer's key/value section).
        string[] dataFiles = Directory.GetFiles(
            Path.Combine(engineDir, "read-table"), "*.parquet", SearchOption.AllDirectories);
        Assert.NotEmpty(dataFiles);
        foreach (string dataFile in dataFiles)
        {
            await using FileStream fs = File.OpenRead(dataFile);
            await using ParquetReader reader = await ParquetReader.CreateAsync(fs);
            string createdBy = reader.Metadata?.CreatedBy ?? string.Empty;

            Assert.True(
                createdBy.StartsWith(expectedCreatedByPrefix, StringComparison.Ordinal),
                $"{engineDirName}: {Path.GetFileName(dataFile)} has created_by '{createdBy}', expected it to "
                + $"start with '{expectedCreatedByPrefix}' — it was not written by the pinned reference engine.");

            Assert.False(
                createdBy.Contains(DeltaSharpWriterCreatedBy, StringComparison.OrdinalIgnoreCase),
                $"{engineDirName}: {Path.GetFileName(dataFile)} was written by DeltaSharp's own Parquet writer "
                + $"(created_by '{createdBy}') — a golden must never be produced by the system under test.");
        }
    }

    // ---- Provenance: EVERY matrix row is grounded in the committed engine bytes ------------------

    [Theory]
    [InlineData(SparkDir)]
    [InlineData(DeltaRsDir)]
    public void GoldenMatrix_EveryRow_MatchesTheEngineWrittenLog(string engineDirName)
    {
        // matrix.json is a DERIVED artifact: the generator harvests it, so on its own nothing stops a
        // hand-edited row from blessing a buggy encoder and passing every other test — precisely design
        // risk R7, and a mutant that survived the previous review round.
        //
        // matrix-log.json is the reference engine's OWN _delta_log for the full matrix table, committed
        // verbatim, and Goldens_CarryReferenceEngineProvenanceMarkers asserts it carries the engine's commit
        // shape (engineInfo + protocol + metaData.partitionColumns).
        //
        // HONEST SCOPE — this does NOT make the goldens unforgeable, and must not be described as closing
        // design risk R7. Every committed artifact can be edited by whoever edits the encoder: a determined
        // forge that patches matrix.json AND matrix-log.json AND re-mints SHA256SUMS still passes (measured).
        // What this control does buy is that a fabricated row is no longer a ONE-file edit, and that an
        // ACCIDENT — a regeneration against the wrong engine, a hand-tweaked row, a stale fixture — is caught.
        // Note also that the on_disk_dir check below is deliberately NOT claimed as engine evidence: see the
        // comment there. Genuine closure requires re-measurement against the live engines, not an in-repo
        // artifact; that is tracked as a scheduled-regeneration CI job (#908).
        //
        // Only the log is committed, never the matrix table's files — so the non-ASCII and control-bearing
        // values live solely as text inside this JSON, never as filesystem paths (design R6).
        string engineDir = Path.Combine(GoldensDir, engineDirName);

        var engineAddPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        string? nullAddPath = null;
        foreach (string line in File.ReadAllLines(Path.Combine(engineDir, "matrix-log.json")))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument action = JsonDocument.Parse(line);
            if (!action.RootElement.TryGetProperty("add", out JsonElement add))
            {
                continue;
            }

            string firstSegment = add.GetProperty("path").GetString()!.Split('/')[0];
            JsonElement pv = add.GetProperty("partitionValues").GetProperty("region");
            if (pv.ValueKind == JsonValueKind.Null)
            {
                nullAddPath = firstSegment;
            }
            else
            {
                engineAddPaths[pv.GetString()!] = firstSegment;
            }
        }

        IReadOnlyList<GoldenRow> matrix = LoadMatrix(engineDirName);
        Assert.NotEmpty(matrix);
        Assert.Equal(matrix.Count, engineAddPaths.Count + (nullAddPath is null ? 0 : 1));

        foreach (GoldenRow row in matrix)
        {
            string engineSegment;
            if (row.Value is null)
            {
                engineSegment = nullAddPath
                    ?? throw new InvalidOperationException("matrix-log.json has no null-partition add");
            }
            else if (!engineAddPaths.TryGetValue(row.Value, out engineSegment!))
            {
                // Escape before reporting: matrix values legitimately contain control characters (0x01, 0x09),
                // and a bare indexer's KeyNotFoundException would put those raw bytes on the CI output stream.
                Assert.Fail(
                    $"{engineDirName}: matrix.json row {Escape(row.Value)} has no matching add in matrix-log.json");
                return;
            }

            // The committed add.path segment is engine bytes — this is what kills a fabricated row.
            Assert.Equal(engineSegment, row.AddPathSegment);

            // The on-disk directory is the URI decode of that segment. NOTE the honest limit: this checks
            // that matrix.json's two columns are decode-consistent with the ENGINE's add.path, which pins
            // on_disk_dir for any row whose add_path_segment is genuine. It is NOT an independent check of
            // the encoder, because DeltaWriteEncoding maintains UnescapeDataString(ToAddPath(p)) == p by
            // construction — so a forger who edits BOTH columns and the log keeps this consistent. Committed
            // engine evidence for the on-disk layer exists only for the read-table rows, which
            // GoldenMatrix_RowsCoveredByReadTable_MatchRealOnDiskDirectories checks against real directories.
            Assert.Equal(Uri.UnescapeDataString(engineSegment), row.OnDiskDir);
        }
    }

    // ---- The read-table rows additionally match real on-disk directory names --------------------

    [Theory]
    [InlineData(SparkDir)]
    [InlineData(DeltaRsDir)]
    public void GoldenMatrix_RowsCoveredByReadTable_MatchRealOnDiskDirectories(string engineDirName)
    {
        // The matrix log proves the add.path bytes; this proves the DIRECTORY names actually materialised on
        // disk for the subset the committed read-table covers (US, a=b, na me, o'brien, p%p).
        string engineDir = Path.Combine(GoldensDir, engineDirName);
        string readTable = Path.Combine(engineDir, "read-table");

        var onDiskDirs = new HashSet<string>(
            Directory.GetDirectories(readTable).Select(Path.GetFileName)!, StringComparer.Ordinal);

        var covered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(
            Path.Combine(readTable, "_delta_log", "00000000000000000000.json")))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument action = JsonDocument.Parse(line);
            if (!action.RootElement.TryGetProperty("add", out JsonElement add))
            {
                continue;
            }

            JsonElement pv = add.GetProperty("partitionValues").GetProperty("region");
            if (pv.ValueKind != JsonValueKind.Null)
            {
                covered[pv.GetString()!] = add.GetProperty("path").GetString()!.Split('/')[0];
            }
        }

        Assert.NotEmpty(covered);
        int crossChecked = 0;
        foreach (GoldenRow row in LoadMatrix(engineDirName))
        {
            if (row.Value is null || !covered.TryGetValue(row.Value, out string? realAddSegment))
            {
                continue;
            }

            Assert.Equal(realAddSegment, row.AddPathSegment);
            Assert.Contains(row.OnDiskDir, onDiskDirs);
            crossChecked++;
        }

        Assert.True(crossChecked >= 5, $"expected >=5 matrix rows grounded on disk; got {crossChecked}");
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

            // The manifest drives a filesystem read inside an anti-forgery control, so refuse anything that
            // could reach outside the fixture tree even though the manifest is repo-controlled today.
            Assert.False(
                Path.IsPathRooted(relative)
                    || relative.Contains('\\', StringComparison.Ordinal)
                    || relative.Split('/').Any(seg => seg == ".." || seg == "."),
                $"SHA256SUMS entry must be a plain relative path inside the fixture tree: {relative}");

            string filePath = Path.Combine(engineDir, relative.Replace('/', Path.DirectorySeparatorChar));

            // Containment is the check that cannot be out-thought by separator trivia (on Windows '\\' is a
            // separator too, so a segment scan alone is not enough).
            string rootFull = Path.GetFullPath(engineDir) + Path.DirectorySeparatorChar;
            Assert.StartsWith(rootFull, Path.GetFullPath(filePath), StringComparison.Ordinal);

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

        // Name the offending paths: xUnit truncates both sets identically, so a bare set comparison tells an
        // operator that CI is red without telling them WHICH golden was added or dropped.
        Assert.True(
            manifest.SetEquals(onDisk),
            $"{engineDirName}: SHA256SUMS does not match the fixture tree. "
            + $"On disk but unlisted: [{string.Join(", ", onDisk.Except(manifest))}]; "
            + $"listed but absent: [{string.Join(", ", manifest.Except(onDisk))}].");
    }

    // ---- ref-&gt;DS: DeltaSharp reads real reference-engine-written tables ------------------------

    // The committed read-table rows (both engines write the same logical rows; only the on-disk partition
    // directory encoding differs). Includes the `p%p` cell: it is the only read row whose ONCE-decoded key
    // still contains a literal '%' (add.path region=p%2525p -> on-disk region=p%25p), so it distinguishes
    // decode-once from decode-to-fixpoint AND from the legacy literal-% fallback, end-to-end through a real
    // foreign table rather than only at string level.
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
