using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
/// (design D4: follow Spark where the reference engines diverge).</item>
/// <item><b>ref-&gt;DS:</b> DeltaSharp reads a real Spark-written and a real delta-rs-written table and
/// returns the exact rows and partition values, for both a Spark-shaped (literal space) and a
/// delta-rs-shaped (escaped space) on-disk layout. This closes the partition-VALUE read path only: every
/// fixture fixes the column to <c>region</c>, so the partition-NAME read half of #708 stays open (#907).</item>
/// </list>
/// <para><b>Fixture text never reaches the CI log raw.</b> Every failure message renders fixture-derived
/// text through <see cref="Escape"/>, and the assertions that print their operands (<c>Assert.Equal</c>,
/// <c>Assert.Contains</c>, …) are not used here at all — the <c>AssertFixture*</c> helpers replace them.
/// <see cref="FixtureTextGuard_HoldsOverThisFile"/> enforces both rules over this file's own syntax tree.</para>
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
    // footer. `created_by` is not settable that way, and Parquet.Net stamps its own value — so the
    // engine-PREFIX assertion below is what rejects a foreign or DeltaSharp-authored file. (The separate
    // "not Parquet.Net" check is for diagnosis; see its comment for the narrow case where it also bites.)
    private const string SparkCreatedByPrefix = "parquet-mr version 1.13.";
    private const string DeltaRsCreatedByPrefix = "delta-rs version py-1.6.3";

    // Each fixture's own provenance note, pinned so it cannot be rewritten to claim DeltaSharp origin.
    private const string SparkNote =
        "Reference (dir, add.path) partition-encoding golden. Emitted by real Spark; never from DeltaSharp.";
    private const string DeltaRsNote =
        "delta-rs reference (dir, add.path). It escapes space/non-ASCII (and some sub-delims like '&') on "
        + "disk (diverges from Spark). DeltaSharp follows Spark; this fixture backs the read-compat + "
        + "documented-residual tests.";

    // Parquet.Net stamps this into `created_by`; no reference-engine golden may carry it. Checked for
    // diagnosis, not for strength — the engine-prefix assertion is what rejects a foreign file.
    private const string DeltaSharpWriterCreatedBy = "Parquet.Net";

    // Fixture directory names (the on-disk engine folders under Fixtures/PartitionEncodingGoldens/).
    private const string SparkDir = "spark";
    private const string DeltaRsDir = "delta-rs";

    // Dictionary stand-in for the null partition value (a Dictionary key may not be null).
    private const string NullValueKey = "<null>";

    private sealed record GoldenRow(string? Value, string OnDiskDir, string AddPathSegment);

    /// <summary>Renders text for a failure message so that nothing in it can act on a terminal or reorder
    /// a log line. A null renders as the bare token <c>&lt;null&gt;</c>; any string renders quoted, with
    /// <c>\</c> and <c>"</c> backslash-escaped and every control (Cc), format (Cf — bidi overrides, zero-width
    /// and tag characters), line/paragraph-separator and unpaired-surrogate code point written as
    /// <c>\uXXXX</c> / <c>\UXXXXXXXX</c>. A well-formed surrogate pair is an ordinary character (the emoji
    /// matrix rows) unless its code point is itself a format character.</summary>
    private static string Escape(string? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsSurrogatePair(value, i))
            {
                int codePoint = char.ConvertToUtf32(value, i);
                if (IsUnsafeForLog(CharUnicodeInfo.GetUnicodeCategory(codePoint)))
                {
                    sb.Append("\\U").Append(codePoint.ToString("X8", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c).Append(value[i + 1]);
                }

                i++;
            }
            else if (c is '\\' or '"')
            {
                sb.Append('\\').Append(c);
            }
            else if (IsUnsafeForLog(char.GetUnicodeCategory(c)))
            {
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }

    private static bool IsUnsafeForLog(UnicodeCategory category) => category
        is UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.Surrogate;

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

    /// <summary>The actions of a committed newline-delimited <c>_delta_log</c> JSON file, in order. Each
    /// element is cloned, so it outlives the per-line document.</summary>
    private static IEnumerable<JsonElement> ReadLogActions(string logPath)
    {
        foreach (string line in File.ReadAllLines(logPath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument action = JsonDocument.Parse(line);
            yield return action.RootElement.Clone();
        }
    }

    private static string? PartitionValue(JsonElement add)
    {
        JsonElement pv = add.GetProperty("partitionValues").GetProperty("region");
        return pv.ValueKind == JsonValueKind.Null ? null : pv.GetString();
    }

    private static string?[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()).ToArray();

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
        AssertFixtureTextEquals(onDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", value), "layer-1 directory");

        // Layer 2 — the committed add.path first segment (Java-URI/RFC-2396 quoting), byte-for-byte Spark.
        string physical = onDiskDir + "/part-x.parquet";
        AssertFixtureTextEquals(addPathSegment + "/part-x.parquet", DeltaWriteEncoding.ToAddPath(physical), "layer-2 add.path");

        // The production read-side decoder is the exact inverse — DeltaSharp reads its own (and Spark's)
        // add.path. Bind to PartitionPathResolver.DecodePhysicalKey (not Uri.UnescapeDataString) so a future
        // resolver change cannot break the write/read round trip while this suite stays green.
        AssertFixtureTextEquals(
            physical, PartitionPathResolver.DecodePhysicalKey(DeltaWriteEncoding.ToAddPath(physical)), "decode(add.path)");
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
        AssertFixtureSequenceEquals(
            new[] { "region=__HIVE_DEFAULT_PARTITION__", "region=keep" },
            Strings(spark.GetProperty("on_disk_dirs")),
            "spark empty_string.on_disk_dirs");

        // delta-rs DIVERGES on this axis: "" gets its OWN directory (region=) and its own add-action with a
        // partitionValues of "". It agrees with Spark on null. DeltaSharp follows Spark at the directory layer
        // (design D5).
        JsonElement deltaRs = EmptyStringBlock(DeltaRsDir);
        Assert.True(deltaRs.GetProperty("distinct_add_action").GetBoolean());
        Assert.False(deltaRs.GetProperty("folds_onto_sentinel_with_null").GetBoolean());
        AssertFixtureTextEquals("region=", deltaRs.GetProperty("add_path_segment").GetString(), "delta-rs empty_string.add_path_segment");

        // The directory listing is the direct fixture evidence for "delta-rs gives \"\" its own region= dir";
        // without this the claim rests on add_path_segment alone.
        AssertFixtureSequenceEquals(
            new[] { "region=", "region=__HIVE_DEFAULT_PARTITION__", "region=keep" },
            Strings(deltaRs.GetProperty("on_disk_dirs")),
            "delta-rs empty_string.on_disk_dirs");

        // Both engines report NULL for the empty string here — but note the mechanism, which an earlier draft
        // of this file stated incorrectly: for Spark this is a WRITE-time fold (it never commits "" at all, so
        // this measures Spark->Spark after the fold), and for delta-rs it commits "" and returns null on read
        // (measured; the fixture pins the returned VALUE, not the mechanism by which either engine produces
        // it). delta-rs's null is the Delta protocol's own rule — PROTOCOL.md "Partition Value
        // Serialization": an empty string for any type translates to a null partition value.
        //
        // What this therefore does NOT establish is what Spark does with a COMMITTED partitionValues of "" —
        // no committed fixture covers that shape, and no test asserts it. See design §3.2, which states the
        // provenance of every cell; #905 owns landing it as an integration-tier test.
        Assert.True(spark.GetProperty("read_back_is_null").GetBoolean());
        Assert.True(deltaRs.GetProperty("read_back_is_null").GetBoolean());

        // Cross-check the harvested value against its own boolean: otherwise a fixture could carry
        // read_back_value="zzz" alongside read_back_is_null=true and no test would notice the contradiction.
        AssertValueEquals(JsonValueKind.Null, spark.GetProperty("read_back_value").ValueKind, "spark read_back_value kind");
        AssertValueEquals(JsonValueKind.Null, deltaRs.GetProperty("read_back_value").ValueKind, "delta-rs read_back_value kind");

        // Ground the block in the engine's OWN log for the empty-string table. Scope, precisely: this binds
        // distinct_add_action and add_partition_values to a second engine-emitted artifact, and
        // Goldens_CarryReferenceEngineProvenanceMarkers holds that log to the same engineInfo/protocol/
        // partitionColumns bar as the other two. The read_back_* cells are READ results and cannot be
        // log-grounded by construction — no log records what a reader returned.
        foreach ((string dir, JsonElement block) in new[] { (SparkDir, spark), (DeltaRsDir, deltaRs) })
        {
            string?[] logValues = ReadLogActions(Path.Combine(GoldensDir, dir, "empty-string-log.json"))
                .Where(a => a.TryGetProperty("add", out _))
                .Select(a => PartitionValue(a.GetProperty("add")))
                .ToArray();

            Assert.NotEmpty(logValues);

            // distinct_add_action is true iff the engine actually committed an add whose partition value is "".
            AssertValueEquals(
                logValues.Contains(string.Empty),
                block.GetProperty("distinct_add_action").GetBoolean(),
                "distinct_add_action vs empty-string-log.json");
            AssertFixtureSequenceEquals(
                Strings(block.GetProperty("add_partition_values")).OrderBy(v => v, StringComparer.Ordinal),
                logValues.Distinct().OrderBy(v => v, StringComparer.Ordinal),
                "empty_string.add_partition_values vs empty-string-log.json");
        }

        // Row presence, asserted rather than inferred: "row 1 read back as null" and "row 1 was never
        // returned" would otherwise harvest to identical blocks, so read_back_is_null could pin a value that
        // was never measured.
        foreach (JsonElement block in new[] { spark, deltaRs })
        {
            AssertValueSequenceEquals(
                new[] { 0, 1, 2 },
                block.GetProperty("read_back_ids").EnumerateArray().Select(e => e.GetInt32()),
                "empty_string.read_back_ids");
        }

        // The engines AGREE on null (they diverge only on ""): both fold it onto the sentinel directory.
        // (Asserted here because an earlier refactor of this class silently dropped it while §3.2 still
        // claimed it.)
        AssertFixtureTextEquals(
            LoadMatrix(SparkDir).Single(r => r.Value is null).OnDiskDir,
            LoadMatrix(DeltaRsDir).Single(r => r.Value is null).OnDiskDir,
            "null-row directory, spark vs delta-rs");

        // Harvested but previously unasserted — measured data that nothing read is dead data.
        AssertFixtureSequenceEquals(
            new[] { null, "keep" }, Strings(spark.GetProperty("add_partition_values")), "spark empty_string.add_partition_values");
        AssertFixtureSequenceEquals(
            new[] { string.Empty, null, "keep" },
            Strings(deltaRs.GetProperty("add_partition_values")),
            "delta-rs empty_string.add_partition_values");
    }

    [Fact]
    public async Task EmptyStringPartitionValue_DirectoryMatchesSpark_ButDeltaSharpPreservesTheValue()
    {
        // LAYER 1 (directory) — DeltaSharp matches Spark: null and "" both fold onto the sentinel. This is why
        // HivePartitionSegment tests string.IsNullOrEmpty (Spark's ExternalCatalogUtils.getPartitionValueString).
        GoldenRow nullRow = LoadMatrix(SparkDir).Single(r => r.Value is null);
        AssertFixtureTextEquals("region=__HIVE_DEFAULT_PARTITION__", nullRow.OnDiskDir, "spark null-row directory");
        AssertFixtureTextEquals(nullRow.OnDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", null), "DeltaSharp null directory");
        AssertFixtureTextEquals(nullRow.OnDiskDir, DeltaWriteEncoding.HivePartitionSegment("region", string.Empty), "DeltaSharp empty-string directory");

        // LAYER 2 (recovered value) — DeltaSharp diverges from both engines' WRITERS, deliberately (design D5).
        // Spark folds "" -> null at write time (it never commits ""); delta-rs commits "" but normalizes it to
        // null on read, as PROTOCOL.md specifies. DeltaSharp treats add.partitionValues as authoritative and
        // writes the value verbatim, so "" round-trips as "" — a deliberate departure from the protocol's
        // empty-string-is-null rule. On the foreign-reader side, real Spark 3.5.3 was observed to return ""
        // for this shape and real delta-rs 1.6.3 to return null — but that was measured OUT OF BAND during
        // review: it is NOT backed by any committed fixture and is NOT asserted by this or any other test.
        // #905 owns turning it into an integration-tier test. See design §3.2 for the per-cell provenance.
        // It distinguishes null from "", which the engines cannot, but it is NOT byte-parity and it does not
        // survive a Spark rewrite of the table (Spark's writer folds "" to null) — asserted here so the
        // divergence is pinned rather than discovered later.
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
            AssertFixtureSequenceEquals(
                new[] { "region=__HIVE_DEFAULT_PARTITION__" },
                Directory.GetDirectories(root).Select(Path.GetFileName)
                    .Where(n => n!.StartsWith("region=", StringComparison.Ordinal))
                    .OrderBy(n => n, StringComparer.Ordinal),
                "partition directories written");

            // ... and the load-bearing artifact is the COMMITTED add.partitionValues: assert it directly, so a
            // future writer that folded "" -> null in the log (with the reader compensating elsewhere) cannot
            // keep this test green.
            var committed = new List<string?>();
            foreach (JsonElement action in ReadLogActions(Path.Combine(root, "_delta_log", "00000000000000000000.json")))
            {
                if (!action.TryGetProperty("add", out JsonElement add))
                {
                    continue;
                }

                AssertFixtureTextStartsWith("region=__HIVE_DEFAULT_PARTITION__/", add.GetProperty("path").GetString(), "committed add.path");
                committed.Add(PartitionValue(add));
            }

            AssertFixtureSequenceEquals(
                new string?[] { null, string.Empty }, committed.OrderBy(v => v, StringComparer.Ordinal), "committed partitionValues");

            // ... so DeltaSharp recovers "" as "" and null as null. Measured foreign-reader behaviour on this
            // exact shape was observed OUT OF BAND during review — not repo-backed, not asserted here;
            // #905 owns the integration-tier test. Real Spark 3.5.3 returned "" (it honours the committed
            // value), real delta-rs 1.6.3 returns null.
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

            AssertFixtureSequenceEquals(
                new[] { RenderRow(1L, string.Empty), RenderRow(2L, (string?)null) },
                recovered.OrderBy(t => t.Id).Select(t => RenderRow(t.Id, t.Region)),
                "recovered (id, region) rows");
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

    // ---- DS-&gt;ref: the delta-rs on-disk residual (documented divergence, design D4/§2.2) --------

    [Fact]
    public void DeltaSharpEncoding_FollowsSpark_NotDeltaRs_OnDiskResidual()
    {
        IReadOnlyList<GoldenRow> spark = LoadMatrix(SparkDir);
        var deltaRsByValue = new Dictionary<string, GoldenRow>(StringComparer.Ordinal);
        foreach (GoldenRow r in LoadMatrix(DeltaRsDir))
        {
            // Not ToDictionary: its duplicate-key ArgumentException embeds the raw key, and fixture values
            // carry control characters.
            Assert.True(
                deltaRsByValue.TryAdd(r.Value ?? NullValueKey, r),
                $"duplicate delta-rs matrix row for {Escape(r.Value ?? NullValueKey)}");
        }

        var spaceOrNonAsciiDiverged = new HashSet<string>(StringComparer.Ordinal);
        foreach (GoldenRow s in spark)
        {
            string dsOnDisk = DeltaWriteEncoding.HivePartitionSegment("region", s.Value);
            // Core claim: DeltaSharp's on-disk directory equals Apache Spark byte-for-byte, always.
            AssertFixtureTextEquals(s.OnDiskDir, dsOnDisk, "DeltaSharp directory vs spark");

            // Residual: delta-rs percent-escapes a BROADER on-disk set than Spark — space and non-ASCII, and
            // (measured) a number of ASCII sub-delims such as '&'. DeltaSharp follows Spark (design D4), so
            // wherever delta-rs escapes and Spark does not, DeltaSharp's on-disk dir differs from delta-rs.
            // We do NOT assert equality elsewhere: delta-rs is free to escape more, and the contract is only
            // that DeltaSharp == Spark and that a delta-rs table stays read-compatible.
            if (!deltaRsByValue.TryGetValue(s.Value ?? NullValueKey, out GoldenRow? dr))
            {
                Assert.Fail($"delta-rs matrix has no row for {Escape(s.Value ?? NullValueKey)}");
                return;
            }

            if (s.Value is not null && s.Value.Any(c => c == ' ' || c > 0x7F))
            {
                AssertFixtureTextNotEquals(dr.OnDiskDir, dsOnDisk, "space/non-ASCII residual"); // the documented residual
                spaceOrNonAsciiDiverged.Add(s.Value);
            }
        }

        // The residual is real and exercised across space AND non-ASCII (Latin, CJK, emoji).
        AssertFixtureSetContains("na me", spaceOrNonAsciiDiverged, "space/non-ASCII residual values");
        AssertFixtureSetContains("région", spaceOrNonAsciiDiverged, "space/non-ASCII residual values");
        Assert.True(spaceOrNonAsciiDiverged.Count >= 4,
            $"expected the space/non-ASCII residual to be broadly exercised; diverged={spaceOrNonAsciiDiverged.Count}");

        // delta-rs also escapes some ASCII sub-delims on disk that Spark/DeltaSharp keep literal — pin the
        // measured '&' case explicitly (the documented broader-escaping residual): DeltaSharp writes
        // `region=amp&r` (like Spark), delta-rs writes `region=amp%26r`.
        GoldenRow ampSpark = spark.Single(r => r.Value == "amp&r");
        AssertFixtureTextEquals("region=amp&r", DeltaWriteEncoding.HivePartitionSegment("region", "amp&r"), "DeltaSharp '&' directory");
        AssertFixtureTextEquals("region=amp&r", ampSpark.OnDiskDir, "spark '&' directory");
        AssertFixtureTextEquals("region=amp%26r", deltaRsByValue["amp&r"].OnDiskDir, "delta-rs '&' directory");

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

            if (!deltaRsByValue.TryGetValue(row.Value, out GoldenRow? dr))
            {
                Assert.Fail($"delta-rs matrix has no row for {Escape(row.Value)}");
                return;
            }

            foreach (char c in row.Value.Where(c => !char.IsLetterOrDigit(c)))
            {
                // A character is a delta-rs-only residual iff SPARK keeps it literal and delta-rs escapes it.
                // Deriving per VALUE instead would wrongly sweep in characters Spark escapes too: `brace{}`
                // gives Spark `region=brace%7B}` and delta-rs `region=brace%7B%7D`, so only `}` is a residual
                // while `{` is escaped by both.
                // Compare the VALUE part only — the whole `region=…` segment would misclassify the
                // structural '=' separator. And decide "escaped" by the presence of the %XX triple rather
                // than by the literal absence of `c`: for c == '%' the escape output ("%25") itself contains
                // '%', so a literal-presence test would call '%' kept-literal by both engines.
                string sparkValuePart = row.OnDiskDir[(row.OnDiskDir.IndexOf('=', StringComparison.Ordinal) + 1)..];
                string deltaRsValuePart = dr.OnDiskDir[(dr.OnDiskDir.IndexOf('=', StringComparison.Ordinal) + 1)..];
                string escaped = "%" + ((int)c).ToString("X2", CultureInfo.InvariantCulture);
                bool sparkKeepsLiteral = !sparkValuePart.Contains(escaped, StringComparison.Ordinal);
                bool deltaRsKeepsLiteral = !deltaRsValuePart.Contains(escaped, StringComparison.Ordinal);
                if (sparkKeepsLiteral && !deltaRsKeepsLiteral)
                {
                    residualAscii.Add(c);
                }
            }
        }

        // The predicate above is only sound because BOTH engines escape '%'; pin that premise directly so a
        // future engine keeping '%' literal breaks here rather than silently skewing the derived set.
        AssertFixtureTextEquals("region=p%25p", DeltaWriteEncoding.HivePartitionSegment("region", "p%p"), "DeltaSharp '%' directory");
        AssertFixtureTextEquals("region=p%25p", spark.Single(r => r.Value == "p%p").OnDiskDir, "spark '%' directory");

        // Must stay SET-EQUAL to the list in the class summary, the fixture README and design §3.2 (those
        // render the same characters space-separated, so they are not byte-identical to this literal).
        AssertFixtureTextEquals("!$&()+,;<>@`|}", new string(residualAscii.ToArray()), "derived delta-rs ASCII residual set");
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
            AssertFixtureTextEquals(r.OnDiskDir, PartitionPathResolver.DecodePhysicalKey(r.AddPathSegment), "delta-rs decode(add.path) vs on_disk_dir");
        }
    }

    // ---- Provenance: intrinsic engine markers + the checked-in SHA256SUMS (design §3.2 / R7) -----

    [Theory]
    [InlineData(SparkDir, SparkEngineName, SparkVersion, SparkEngineInfo, SparkCreatedByPrefix,
        "delta", "3.2.0", SparkNote)]
    [InlineData(DeltaRsDir, DeltaRsEngineName, DeltaRsVersion, DeltaRsEngineInfo, DeltaRsCreatedByPrefix,
        "pyarrow", "25.0.1", DeltaRsNote)]
    public async Task Goldens_CarryReferenceEngineProvenanceMarkers(
        string engineDirName, string expectedEngine, string expectedVersion, string expectedEngineInfo,
        string expectedCreatedByPrefix, string libraryVersionKey, string expectedLibraryVersion,
        string expectedNote)
    {
        // The checksum manifest below proves the bytes have not DRIFTED, but it is minted by the same script
        // that writes the fixtures, so on its own it cannot prove the bytes came from a reference engine at
        // all (C2: never trust a self-settable signal).
        //
        // These markers are NOT equally strong, and it matters which is which:
        //   * the Parquet footer's structured `created_by` (below) is stamped by the writer LIBRARY and is not
        //     settable through Parquet.Net's CustomMetadata, so an accidentally-substituted or
        //     DeltaSharp-authored data file is caught — by the engine-PREFIX assertion (the
        //     "not Parquet.Net" check is usually not the deciding one, and is there for the diagnosis; it is
        //     not strictly redundant, see its comment). It is NOT
        //     proof against a determined forger who crafts a footer byte-by-byte — no in-repo artifact can be;
        //   * `_delta_log` commitInfo.engineInfo is written by the engine itself — strong;
        //   * matrix.json `version` (and delta-rs's `pyarrow`) is read from the installed library at generation
        //     time — moderate;
        //   * matrix.json `engine`, and Spark's `delta` as committed, were constants in the generator — they pin
        //     DRIFT only, not origin. Spark's Delta version is anchored by engineInfo ("Delta-Lake/3.2.0").
        string engineDir = Path.Combine(GoldensDir, engineDirName);
        using (JsonDocument matrix = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(engineDir, "matrix.json"))))
        {
            AssertFixtureTextEquals(expectedEngine, matrix.RootElement.GetProperty("engine").GetString(), $"{Escape(engineDirName)}/matrix.json engine");
            AssertFixtureTextEquals(expectedVersion, matrix.RootElement.GetProperty("version").GetString(), $"{Escape(engineDirName)}/matrix.json version");

            // Every test in this class hard-codes the partition column; bind it here so a regenerated fixture
            // with a different column fails at the declaration rather than obscurely downstream.
            AssertFixtureTextEquals("region", matrix.RootElement.GetProperty("column").GetString(), $"{Escape(engineDirName)}/matrix.json column");

            // The remaining harvested header fields, so none is dead data. `note` matters most: it is the
            // fixture's own provenance statement, and unpinned it could be rewritten to claim the golden was
            // "generated from DeltaSharp output" with the suite still green. Pinned exactly, since the
            // generator owns the text.
            AssertFixtureTextEquals(
                expectedLibraryVersion,
                matrix.RootElement.GetProperty(libraryVersionKey).GetString(),
                $"{Escape(engineDirName)}/matrix.json {Escape(libraryVersionKey)}");
            AssertFixtureTextEquals(expectedNote, matrix.RootElement.GetProperty("note").GetString(), $"{Escape(engineDirName)}/matrix.json note");
        }

        // ALL THREE committed logs must carry the engine's commit shape: the read-table log, matrix-log.json
        // (which grounds every matrix row) and empty-string-log.json (which grounds the empty_string block).
        // Leaving any of them unasserted lets a forger replace it with BARE hand-authored `add` lines —
        // measured: that is exactly what happened to empty-string-log.json when it was committed without being
        // added here. (A forge that keeps the commit shape still passes; see the honest-scope note on
        // GoldenMatrix_EveryRow_MatchesTheEngineWrittenLog.)
        foreach (string logPath in new[]
        {
            Path.Combine(engineDir, "read-table", "_delta_log", "00000000000000000000.json"),
            Path.Combine(engineDir, "matrix-log.json"),
            Path.Combine(engineDir, "empty-string-log.json"),
        })
        {
            string? engineInfo = null;
            bool sawProtocol = false;
            string?[] partitionColumns = Array.Empty<string?>();

            foreach (JsonElement action in ReadLogActions(logPath))
            {
                if (action.TryGetProperty("commitInfo", out JsonElement commitInfo)
                    && commitInfo.TryGetProperty("engineInfo", out JsonElement info))
                {
                    engineInfo = info.GetString();
                }

                sawProtocol |= action.TryGetProperty("protocol", out _);

                if (action.TryGetProperty("metaData", out JsonElement metaData)
                    && metaData.TryGetProperty("partitionColumns", out JsonElement cols))
                {
                    partitionColumns = Strings(cols);
                }
            }

            string which = Path.GetFileName(logPath);
            AssertFixtureTextEquals(expectedEngineInfo, engineInfo, $"{Escape(engineDirName)}/{Escape(which)} commitInfo.engineInfo");
            Assert.True(sawProtocol, $"{Escape(engineDirName)}/{Escape(which)}: no protocol action — not an engine-written log");
            AssertFixtureSequenceEquals(new[] { "region" }, partitionColumns, $"{Escape(engineDirName)}/{Escape(which)} metaData.partitionColumns");
        }

        // Every committed data file's STRUCTURED created_by must be its engine's, and must not be
        // Parquet.Net's. The PREFIX assertion is what catches a cross-engine substitution or a
        // DeltaSharp-authored Parquet — both of which a substring scan of the whole file would miss, since
        // CustomMetadata can plant any string in the footer's key/value section.
        string[] dataFiles = Directory.GetFiles(
            Path.Combine(engineDir, "read-table"), "*.parquet", SearchOption.AllDirectories);
        Assert.NotEmpty(dataFiles);
        foreach (string dataFile in dataFiles)
        {
            await using FileStream fs = File.OpenRead(dataFile);
            await using ParquetReader reader = await ParquetReader.CreateAsync(fs);
            string createdBy = reader.Metadata?.CreatedBy ?? string.Empty;

            // Check the DeltaSharp-authored case FIRST, for the DIAGNOSIS: "a golden was produced by the
            // system under test" is a different problem from "this file came from the wrong engine", and an
            // operator should be told which. For genuine Parquet.Net output the prefix assertion below would
            // also reject it, so this is usually not the deciding check — but it is not strictly redundant:
            // a crafted created_by such as "parquet-mr version 1.13.x Parquet.Net" passes the prefix and is
            // caught only here.
            Assert.False(
                createdBy.Contains(DeltaSharpWriterCreatedBy, StringComparison.OrdinalIgnoreCase),
                $"{Escape(engineDirName)}: {Escape(Path.GetFileName(dataFile))} was written by DeltaSharp's own "
                + $"Parquet writer (created_by {Escape(createdBy)}) — a golden must never be produced by the "
                + "system under test.");

            // This is the assertion that actually bites: it rejects any file whose writer library is not the
            // pinned engine, including a cross-engine substitution.
            Assert.True(
                createdBy.StartsWith(expectedCreatedByPrefix, StringComparison.Ordinal),
                $"{Escape(engineDirName)}: {Escape(Path.GetFileName(dataFile))} has created_by {Escape(createdBy)}, "
                + $"expected it to start with {Escape(expectedCreatedByPrefix)} — it was not written by the "
                + "pinned reference engine.");
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
        foreach (JsonElement action in ReadLogActions(Path.Combine(engineDir, "matrix-log.json")))
        {
            if (!action.TryGetProperty("add", out JsonElement add))
            {
                continue;
            }

            string firstSegment = add.GetProperty("path").GetString()!.Split('/')[0];
            string? value = PartitionValue(add);
            if (value is null)
            {
                nullAddPath = firstSegment;
            }
            else
            {
                engineAddPaths[value] = firstSegment;
            }
        }

        IReadOnlyList<GoldenRow> matrix = LoadMatrix(engineDirName);
        Assert.NotEmpty(matrix);
        AssertValueEquals(matrix.Count, engineAddPaths.Count + (nullAddPath is null ? 0 : 1), "matrix rows vs matrix-log.json adds");

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
                    $"{Escape(engineDirName)}: matrix.json row {Escape(row.Value)} has no matching add in matrix-log.json");
                return;
            }

            // The committed add.path segment is engine bytes — this is what kills a fabricated row.
            AssertFixtureTextEquals(engineSegment, row.AddPathSegment, $"{Escape(engineDirName)} matrix row add_path_segment vs engine log");

            // The on-disk directory is the URI decode of that segment. NOTE the honest limit: this checks
            // that matrix.json's two columns are decode-consistent with the ENGINE's add.path, which pins
            // on_disk_dir for any row whose add_path_segment is genuine. It is NOT an independent check of
            // the encoder, because DeltaWriteEncoding maintains UnescapeDataString(ToAddPath(p)) == p by
            // construction — so a forger who edits BOTH columns and the log keeps this consistent. Committed
            // engine evidence for the on-disk layer exists only for the read-table rows, which
            // GoldenMatrix_RowsCoveredByReadTable_MatchRealOnDiskDirectories checks against real directories.
            AssertFixtureTextEquals(Uri.UnescapeDataString(engineSegment), row.OnDiskDir, $"{Escape(engineDirName)} matrix row on_disk_dir");
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
        foreach (JsonElement action in ReadLogActions(Path.Combine(readTable, "_delta_log", "00000000000000000000.json")))
        {
            if (action.TryGetProperty("add", out JsonElement add) && PartitionValue(add) is string value)
            {
                covered[value] = add.GetProperty("path").GetString()!.Split('/')[0];
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

            AssertFixtureTextEquals(realAddSegment, row.AddPathSegment, $"{Escape(engineDirName)} matrix row add_path_segment");
            AssertFixtureSetContains(row.OnDiskDir, onDiskDirs, $"{Escape(engineDirName)} read-table directories");
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
        //
        // SCOPE: this runs over the fixture tree as COPIED beside the test binary. The SDK's default item
        // excludes do not copy dot-directories, and MSBuild copies a symlink's TARGET as a regular file, so
        // an unlisted file under a dot-directory, or a tracked symlink, is invisible here. The generators
        // refuse both when minting the manifest, and the fixture README states the residual.
        string engineDir = Path.Combine(GoldensDir, engineDirName);
        string sumsPath = Path.Combine(engineDir, "SHA256SUMS");
        Assert.True(File.Exists(sumsPath), $"missing {Escape(engineDirName)}/SHA256SUMS");

        var manifest = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(sumsPath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // Format: "<hex-sha256>  <relative/path>" (two spaces, sha256sum/shasum -c convention).
            int sep = line.IndexOf("  ", StringComparison.Ordinal);
            Assert.True(sep > 0, $"malformed SHA256SUMS line: {Escape(line)}");
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
                $"SHA256SUMS entry must be a plain relative path inside the fixture tree: {Escape(relative)}");

            string filePath = Path.Combine(engineDir, relative.Replace('/', Path.DirectorySeparatorChar));

            // Containment is the check that cannot be out-thought by separator trivia (on Windows '\\' is a
            // separator too, so a segment scan alone is not enough).
            string rootFull = Path.GetFullPath(engineDir) + Path.DirectorySeparatorChar;
            AssertFixtureTextStartsWith(rootFull, Path.GetFullPath(filePath), "SHA256SUMS entry containment");

            Assert.True(File.Exists(filePath), $"SHA256SUMS references a missing file: {Escape(relative)}");
            Assert.True(
                (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) == 0,
                $"SHA256SUMS references a symbolic link, which hashes its target: {Escape(relative)}");
            string actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(filePath)));
            Assert.True(
                string.Equals(expectedHash, actualHash, StringComparison.Ordinal),
                $"{Escape(engineDirName)}: {Escape(relative)} hashes to {Escape(actualHash)}, manifest says "
                + $"{Escape(expectedHash)}");
            Assert.True(manifest.Add(relative), $"duplicate SHA256SUMS entry: {Escape(relative)}");
        }

        // Reverse direction: every file ON DISK must be listed. Without this an ADDED, unvetted golden — the
        // very way an unmeasured fixture enters the tree — passes unnoticed. Set equality also subsumes the
        // old ">= 6" floor, which silently stopped meaning anything the moment a fixture was added.
        var onDisk = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(engineDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(engineDir, entry).Replace(Path.DirectorySeparatorChar, '/');
            Assert.True(
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0,
                $"{Escape(engineDirName)}: the fixture tree contains a symbolic link: {Escape(relative)}");
            if (Directory.Exists(entry) || relative == "SHA256SUMS")
            {
                continue;
            }

            onDisk.Add(relative);
        }

        // Name the offending paths: xUnit truncates both sets identically, so a bare set comparison tells an
        // operator that CI is red without telling them WHICH golden was added or dropped.
        Assert.True(
            manifest.SetEquals(onDisk),
            $"{Escape(engineDirName)}: SHA256SUMS does not match the fixture tree. "
            + $"On disk but unlisted: [{string.Join(", ", onDisk.Except(manifest).Select(Escape))}]; "
            + $"listed but absent: [{string.Join(", ", manifest.Except(onDisk).Select(Escape))}]. "
            + "(Fixtures are copied with PreserveNewest, so if a golden was REMOVED from source a stale copy "
            + "can linger in the output tree: rm -rf bin obj and rebuild before treating this as a "
            + "provenance violation.)");
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

        var actual = new List<(long Id, string Name, string Region)>();
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

        AssertFixtureSequenceEquals(
            expected.OrderBy(e => e.Id).Select(e => RenderRow(e.Id, e.Name, e.Region)),
            actual.OrderBy(a => a.Id).Select(a => RenderRow(a.Id, a.Name, a.Region)),
            $"{Escape(engineDirName)} read-table rows");
    }

    // ---- Fixture-safe assertions -------------------------------------------------------------------
    //
    // xUnit's value-printing assertions (Equal, Contains, StartsWith, …) put both operands on the CI output
    // stream verbatim on failure, and fixture text is attacker-chosen (a crafted `note`, a forged
    // `on_disk_dir`) — so a hostile fixture could paint the very log this suite exists to make trustworthy.
    // Every comparison in this file goes through one of these instead, and each renders through Escape. The
    // `what` argument is a description built by the caller; the guard below holds it to the same rule as
    // any other failure-message text.

    private static void AssertFixtureTextEquals(string? expected, string? actual, string what)
    {
        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"{Escape(what)}: expected {Escape(expected)}, fixture has {Escape(actual)}");
    }

    private static void AssertFixtureTextNotEquals(string? unexpected, string? actual, string what)
    {
        Assert.False(
            string.Equals(unexpected, actual, StringComparison.Ordinal),
            $"{Escape(what)}: expected a value other than {Escape(unexpected)}");
    }

    private static void AssertFixtureTextStartsWith(string prefix, string? actual, string what)
    {
        Assert.True(
            actual is not null && actual.StartsWith(prefix, StringComparison.Ordinal),
            $"{Escape(what)}: expected a value starting with {Escape(prefix)}, got {Escape(actual)}");
    }

    private static void AssertFixtureSetContains(string? item, IReadOnlySet<string> set, string what)
    {
        Assert.True(
            item is not null && set.Contains(item),
            $"{Escape(what)}: {Escape(item)} is not among [{string.Join(", ", set.Order(StringComparer.Ordinal).Select(Escape))}]");
    }

    private static void AssertFixtureSequenceEquals(IEnumerable<string?> expected, IEnumerable<string?> actual, string what)
    {
        string?[] e = expected.ToArray();
        string?[] a = actual.ToArray();
        Assert.True(
            e.SequenceEqual(a, StringComparer.Ordinal),
            $"{Escape(what)}: expected [{string.Join(", ", e.Select(Escape))}], got [{string.Join(", ", a.Select(Escape))}]");
    }

    // Values that cannot carry text (numbers, booleans, enums) — rendered invariantly, then escaped anyway so
    // the message rule has no exception.
    private static void AssertValueEquals<T>(T expected, T actual, string what)
        where T : unmanaged
    {
        Assert.True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{Escape(what)}: expected {Escape(Convert.ToString(expected, CultureInfo.InvariantCulture))}, "
            + $"got {Escape(Convert.ToString(actual, CultureInfo.InvariantCulture))}");
    }

    private static void AssertValueSequenceEquals<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
        where T : unmanaged
    {
        T[] e = expected.ToArray();
        T[] a = actual.ToArray();
        Assert.True(
            e.SequenceEqual(a),
            $"{Escape(what)}: expected {Escape(string.Join(", ", e))}, got {Escape(string.Join(", ", a))}");
    }

    // One row of a read table as a single comparable string; each text field is escaped, so a null and a
    // string spelled "<null>" render differently.
    private static string RenderRow(long id, params string?[] fields) =>
        string.Join("|", fields.Select(Escape).Prepend(id.ToString(CultureInfo.InvariantCulture)));

    // ---- Guard: fixture text never reaches the CI log unescaped ------------------------------------

    private const string EmbeddedSourceName = "Delta/PartitionEncodingGoldenDifferentialTests.cs";

    // The xUnit assertions this file may call: none of them prints an operand on failure (True/False/Fail
    // print only the message, NotEmpty and NotNull print nothing value-bearing). Every other Assert member
    // — Equal, Contains, StartsWith, Single, … — prints its operands and is a finding.
    private static readonly HashSet<string> NonPrintingAssertions = new(StringComparer.Ordinal)
    {
        "True", "False", "Fail", "NotEmpty", "NotNull",
    };

    [Fact]
    public void FixtureTextGuard_HoldsOverThisFile()
    {
        // This defect class recurred across several review rounds: a failure message carrying fixture text
        // (partition values carry 0x01/0x09; SHA256SUMS lines and matrix.json's `note` are attacker-authored)
        // put raw control/ANSI bytes on the CI output stream — from inside the anti-forgery control, i.e. a
        // hostile fixture could paint the log this suite exists to make trustworthy. Its first two guards
        // were a line filter plus a regex over `$"` lines with a NAME allowlist, and review measured them
        // missing `$@"…"`, raw strings, brace-adjacent holes, `Escape(a) + raw`, string concatenation and a
        // local re-bound to an allowlisted name. So this one parses the file with the real C# parser and
        // holds every message to a STRUCTURAL rule instead (see FindFixtureTextFindings).
        //
        // THE SOURCE IS READ FROM THE ASSEMBLY, NOT FROM DISK. A [CallerFilePath] path is green on a developer
        // machine and red on every pipeline run — deterministic builds rewrite source paths to `/_/tests/...`,
        // so the file is not beside the build. Embedding makes both environments identical and keeps a missing
        // resource a hard failure. The same remedy, for the same reason, is documented on
        // PathDisclosureHygieneTests.
        using Stream? stream = typeof(PartitionEncodingGoldenDifferentialTests).Assembly
            .GetManifestResourceStream(EmbeddedSourceName);
        Assert.True(stream is not null, $"embedded source {Escape(EmbeddedSourceName)} is missing");

        using var reader = new StreamReader(stream!);
        GuardResult result = FindFixtureTextFindings(reader.ReadToEnd());

        Assert.True(
            result.Findings.Count == 0,
            "fixture text can reach a failure message unescaped, or a value-printing assertion is used:\n  "
            + string.Join("\n  ", result.Findings.Select(Escape)));

        // The guard must not pass by seeing nothing: a parse that stopped finding the shapes it was built for
        // would read as verified. These floors sit well below this file's real usage.
        int messagesChecked = result.MessagesChecked;
        int holesChecked = result.HolesChecked;
        int escapeMethods = result.EscapeMethods;
        Assert.True(messagesChecked >= 30, $"guard checked only {messagesChecked} failure messages");
        Assert.True(holesChecked >= 30, $"guard checked only {holesChecked} interpolation holes");
        Assert.True(escapeMethods == 1, $"expected exactly one Escape method, found {escapeMethods}");
    }

    // Every evasion review found against the earlier guards, plus the obvious ones, each wrapped in a method
    // the guard must flag. Kept beside the guard so a weakened rule fails here, not in review.
    [Theory]
    [InlineData("Assert.True(ok, $\"{row.Value}\");")]
    [InlineData("Assert.True(ok, $@\"{row.Value}\");")]
    [InlineData("Assert.True(ok, @$\"{row.Value}\");")]
    [InlineData("Assert.True(ok, $\"{{{row.Value}}}\");")]
    [InlineData("Assert.True(ok, $\"{row.Value}}}\");")]
    [InlineData("Assert.True(ok, $\"\"\"{row.Value}\"\"\");")]
    [InlineData("Assert.True(ok, $$\"\"\"{{row.Value}}\"\"\");")]
    [InlineData("Assert.True(ok, $\"{Escape(\"\") + row.Value}\");")]
    [InlineData("Assert.True(ok, $\"{row.Value + string.Join(\",\", names.Select(Escape))}\");")]
    [InlineData("Assert.True(ok, $\"{string.Join(\",\", names)}\");")]
    [InlineData("string which = row.OnDiskDir; Assert.True(ok, $\"{which}\");")]
    [InlineData("Assert.True(ok, \"x\" + row.Value);")]
    [InlineData("string message = \"x\"; Assert.True(ok, message);")]
    [InlineData("Assert.True(ok, string.Format(\"{0}\", row.Value));")]
    [InlineData("Assert.Fail(row.Value);")]
    [InlineData("throw new InvalidOperationException(row.Value);")]
    [InlineData("_ = $\"{row.Value}\";")]
    [InlineData("Assert.Equal(\"a\", row.Value);")]
    [InlineData("Assert.Contains(row.Value, names);")]
    [InlineData("Assert.StartsWith(\"a\", row.Value);")]
    [InlineData("AssertFixtureTextEquals(\"a\", row.Value, row.OnDiskDir);")]
    [InlineData("AssertFixtureTextEquals(\"a\", row.Value, $\"{row.OnDiskDir} row\");")]
    public void FixtureTextGuard_FlagsEveryKnownEvasion(string body)
    {
        GuardResult result = FindFixtureTextFindings(GuardSnippet(body));
        Assert.True(result.Findings.Count > 0, $"the guard accepted {Escape(body)}");
    }

    // ... and accepts the shapes this file actually relies on, so the rule is not simply "reject everything".
    [Theory]
    [InlineData("Assert.True(ok, $\"{Escape(row.Value)} {count} {names.Length} {Label}\");")]
    [InlineData("Assert.True(ok, $\"[{string.Join(\", \", names.Select(Escape))}]\");")]
    [InlineData("Assert.True(ok, \"literal \" + Escape(row.Value));")]
    [InlineData("Assert.Fail($\"{count + 1} of {names.Length}\");")]
    [InlineData("AssertFixtureTextEquals(\"a\", row.Value, $\"{Label} row\");")]
    [InlineData("Assert.True(ok, $\"{(ok ? Escape(row.Value) : Label)}\");")]
    [InlineData("throw new InvalidOperationException(\"constant text\");")]
    [InlineData("Assert.NotEmpty(names);")]
    public void FixtureTextGuard_AcceptsTheSafeShapes(string body)
    {
        GuardResult result = FindFixtureTextFindings(GuardSnippet(body));
        Assert.True(
            result.Findings.Count == 0,
            $"the guard rejected {Escape(body)}: {string.Join("; ", result.Findings.Select(Escape))}");
    }

    private static string GuardSnippet(string body) =>
        "class C { const string Label = \"k\"; static string Escape(string? s) => s ?? \"\"; "
        + "void M(GoldenRow row, string[] names, int count, bool ok) { " + body + " } }";

    private sealed record GuardResult(List<string> Findings, int MessagesChecked, int HolesChecked, int EscapeMethods);

    /// <summary>
    /// The structural rule, over a C# syntax tree. A failure message — the message argument of
    /// <c>Assert.True/False/Fail</c>, any argument to an exception constructor, and the <c>what</c> argument of
    /// an <c>AssertFixture*</c>/<c>AssertValue*</c> helper — and every interpolation hole anywhere in the file
    /// must be built only from: literals; <c>const</c> names; <c>nameof</c>; numeric shapes (an <c>int</c>/
    /// <c>long</c>-declared name, a <c>.Count</c>/<c>.Length</c>); <c>Escape(x)</c> as the WHOLE expression;
    /// <c>string.Join(literal, xs.Select(Escape))</c>; and interpolations, concatenations, conditionals and
    /// parentheses of those. Anything else — a string local, a parameter, a call, a member read — is a finding,
    /// so a new fixture-derived value fails closed instead of depending on someone adding it to a list. Separately,
    /// every <c>Assert.*</c> member outside <see cref="NonPrintingAssertions"/> is a finding.
    /// </summary>
    private static GuardResult FindFixtureTextFindings(string source)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        var declarations = DeclaredTypes(root);
        var findings = new List<string>();
        int messages = 0;
        int holes = 0;

        void Check(ExpressionSyntax expression, string role)
        {
            if (!IsSafe(expression, declarations))
            {
                int lineNumber = expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add($"{EmbeddedSourceName}:{lineNumber}: {Escape(role)}: {Escape(expression.ToString())}");
            }
        }

        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
            if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Assert" } } member)
            {
                string name = member.Name.Identifier.Text;
                if (!NonPrintingAssertions.Contains(name))
                {
                    int lineNumber = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    findings.Add($"{EmbeddedSourceName}:{lineNumber}: value-printing assertion Assert.{Escape(name)}");
                    continue;
                }

                int messageIndex = name == "Fail" ? 0 : name is "True" or "False" ? 1 : -1;
                if (messageIndex >= 0 && args.Count > messageIndex)
                {
                    messages++;
                    Check(args[messageIndex].Expression, "assertion message");
                }
            }
            else if (call.Expression is IdentifierNameSyntax helper
                && (helper.Identifier.Text.StartsWith("AssertFixture", StringComparison.Ordinal)
                    || helper.Identifier.Text.StartsWith("AssertValue", StringComparison.Ordinal))
                && args.Count > 0)
            {
                messages++;
                Check(args[^1].Expression, "assertion description");
            }
        }

        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (creation is ObjectCreationExpressionSyntax { Type: var type }
                && type.ToString().EndsWith("Exception", StringComparison.Ordinal)
                && creation.ArgumentList is { } list)
            {
                foreach (ArgumentSyntax arg in list.Arguments)
                {
                    messages++;
                    Check(arg.Expression, "exception message");
                }
            }
        }

        foreach (InterpolationSyntax hole in root.DescendantNodes().OfType<InterpolationSyntax>())
        {
            // A hole inside Escape(...)'s argument is escaped by the enclosing call.
            if (hole.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsEscapeCall))
            {
                continue;
            }

            holes++;
            Check(hole.Expression, "interpolation hole");
        }

        int escapeMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Count(m => m.Identifier.Text == nameof(Escape));
        return new GuardResult(findings, messages, holes, escapeMethods);
    }

    private static bool IsEscapeCall(InvocationExpressionSyntax call) =>
        call.Expression is IdentifierNameSyntax { Identifier.Text: nameof(Escape) } && call.ArgumentList.Arguments.Count == 1;

    private static bool IsSafe(ExpressionSyntax expression, Dictionary<string, List<DeclaredName>> declarations)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax:
                return true;
            case InterpolatedStringExpressionSyntax interpolated:
                return interpolated.Contents.OfType<InterpolationSyntax>().All(h => IsSafe(h.Expression, declarations));
            case ParenthesizedExpressionSyntax parenthesized:
                return IsSafe(parenthesized.Expression, declarations);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
                || binary.IsKind(SyntaxKind.SubtractExpression) || binary.IsKind(SyntaxKind.MultiplyExpression):
                return IsSafe(binary.Left, declarations) && IsSafe(binary.Right, declarations);
            case ConditionalExpressionSyntax conditional:
                return IsSafe(conditional.WhenTrue, declarations) && IsSafe(conditional.WhenFalse, declarations);
            case MemberAccessExpressionSyntax { Name.Identifier.Text: "Count" or "Length" }:
                return true;
            case IdentifierNameSyntax identifier:
                return declarations.TryGetValue(identifier.Identifier.Text, out List<DeclaredName>? decls)
                    && (decls.All(d => d.IsConst) || decls.All(d => d.Type is "int" or "long"));
            case InvocationExpressionSyntax call when IsEscapeCall(call):
                return true;
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } }:
                return true;
            case InvocationExpressionSyntax call:
                return IsJoinOfEscaped(call, declarations);
            default:
                return false;
        }
    }

    // string.Join(<safe separator>, <anything>.Select(Escape)) — every element is escaped.
    private static bool IsJoinOfEscaped(InvocationExpressionSyntax call, Dictionary<string, List<DeclaredName>> declarations) =>
        call.Expression is MemberAccessExpressionSyntax
        {
            Expression: PredefinedTypeSyntax { Keyword.Text: "string" }, Name.Identifier.Text: "Join",
        }
        && call.ArgumentList.Arguments.Count == 2
        && IsSafe(call.ArgumentList.Arguments[0].Expression, declarations)
        && call.ArgumentList.Arguments[1].Expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Select" },
            ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax { Identifier.Text: nameof(Escape) } }],
        };

    private sealed record DeclaredName(string? Type, bool IsConst);

    // Every declaration of every name in the tree, with its declared type text (null when the parser cannot
    // say, e.g. a deconstruction designation) and whether it is a const. A name is treated as safe only if
    // EVERY declaration of it is const, or EVERY one is int/long, so re-using a safe name for a string fails.
    private static Dictionary<string, List<DeclaredName>> DeclaredTypes(SyntaxNode root)
    {
        var map = new Dictionary<string, List<DeclaredName>>(StringComparer.Ordinal);
        void Add(string name, string? type, bool isConst)
        {
            if (!map.TryGetValue(name, out List<DeclaredName>? list))
            {
                map[name] = list = new List<DeclaredName>();
            }

            list.Add(new DeclaredName(type, isConst));
        }

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator:
                    bool isConst = declaration.Parent switch
                    {
                        FieldDeclarationSyntax field => field.Modifiers.Any(SyntaxKind.ConstKeyword),
                        LocalDeclarationStatementSyntax local => local.Modifiers.Any(SyntaxKind.ConstKeyword),
                        _ => false,
                    };
                    Add(declarator.Identifier.Text, declaration.Type.ToString(), isConst);
                    break;
                case ParameterSyntax parameter:
                    Add(parameter.Identifier.Text, parameter.Type?.ToString(), isConst: false);
                    break;
                case ForEachStatementSyntax forEach:
                    Add(forEach.Identifier.Text, forEach.Type.ToString(), isConst: false);
                    break;
                case SingleVariableDesignationSyntax designation:
                    string? designated = designation.Parent switch
                    {
                        DeclarationExpressionSyntax declarationExpression => declarationExpression.Type.ToString(),
                        DeclarationPatternSyntax pattern => pattern.Type.ToString(),
                        _ => null,
                    };
                    Add(designation.Identifier.Text, designated, isConst: false);
                    break;
            }
        }

        return map;
    }
}
