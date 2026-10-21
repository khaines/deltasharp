using System.Collections.Immutable;
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
using Microsoft.CodeAnalysis.Operations;
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
/// <para><b>Fixture text reaches the CI log only through <see cref="Escape"/>.</b> Every failure message,
/// assertion description and exception message this file builds renders fixture-derived text through Escape;
/// the assertions that print their operands (<c>Assert.Equal</c>, <c>Assert.Contains</c>, …) are not used here
/// at all — the <c>AssertFixture*</c> helpers replace them; and fixture-driven theory data is row indices (other
/// theory data is compile-time constants), so no fixture text becomes a test's display name.
/// <see cref="FixtureTextGuard_HoldsOverThisFile"/> checks the channels this file's own code can use, on a
/// compiled, symbol-bound view of it (the list is on <c>FindFixtureTextFindings</c>). Its stated residual is
/// text a library composes from its own input when it fails — the Parse family, file, encoding and regex APIs,
/// System.Text.Json, Parquet.Net — and reflection (#917).</para>
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

    // The theory is keyed by row INDEX, never by the fixture text itself: xUnit renders theory arguments into
    // the test's display name and escapes only C0 controls there, so a fixture value carrying a bidi override,
    // a C1 control or a line separator would otherwise reach the CI log raw before any assertion runs.
    public static TheoryData<int> SparkMatrixRows()
    {
        var data = new TheoryData<int>();
        for (int row = 0; row < LoadMatrix(SparkDir).Count; row++)
        {
            data.Add(row);
        }

        return data;
    }

    // ---- DS-&gt;ref: byte-parity against the Apache Spark reference (the #806 core oracle) ---------

    [Theory]
    [MemberData(nameof(SparkMatrixRows))]
    public void DeltaSharpEncoding_MatchesSpark_ByteForByte(int row)
    {
        GoldenRow golden = LoadMatrix(SparkDir)[row];

        // Layer 1 — the on-disk directory name (escapePathName), byte-for-byte Spark.
        AssertFixtureTextEquals(
            golden.OnDiskDir,
            DeltaWriteEncoding.HivePartitionSegment("region", golden.Value),
            $"row {row} {Escape(golden.Value)}: layer-1 directory");

        // Layer 2 — the committed add.path first segment (Java-URI/RFC-2396 quoting), byte-for-byte Spark.
        string physical = golden.OnDiskDir + "/part-x.parquet";
        AssertFixtureTextEquals(
            golden.AddPathSegment + "/part-x.parquet",
            DeltaWriteEncoding.ToAddPath(physical),
            $"row {row} {Escape(golden.Value)}: layer-2 add.path");

        // The production read-side decoder is the exact inverse — DeltaSharp reads its own (and Spark's)
        // add.path. Bind to PartitionPathResolver.DecodePhysicalKey (not Uri.UnescapeDataString) so a future
        // resolver change cannot break the write/read round trip while this suite stays green.
        AssertFixtureTextEquals(
            physical,
            PartitionPathResolver.DecodePhysicalKey(DeltaWriteEncoding.ToAddPath(physical)),
            $"row {row} {Escape(golden.Value)}: decode(add.path)");
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
                (File.GetAttributes(filePath) & global::System.IO.FileAttributes.ReparsePoint) == 0,
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
                (File.GetAttributes(entry) & global::System.IO.FileAttributes.ReparsePoint) == 0,
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
            $"{what}: expected {Escape(expected)}, got {Escape(actual)}");
    }

    private static void AssertFixtureTextNotEquals(string? unexpected, string? actual, string what)
    {
        Assert.False(
            string.Equals(unexpected, actual, StringComparison.Ordinal),
            $"{what}: expected a value other than {Escape(unexpected)}");
    }

    private static void AssertFixtureTextStartsWith(string prefix, string? actual, string what)
    {
        Assert.True(
            actual is not null && actual.StartsWith(prefix, StringComparison.Ordinal),
            $"{what}: expected a value starting with {Escape(prefix)}, got {Escape(actual)}");
    }

    private static void AssertFixtureSetContains(string? item, IReadOnlySet<string> set, string what)
    {
        Assert.True(
            item is not null && set.Contains(item),
            $"{what}: {Escape(item)} is not among [{string.Join(", ", set.Order(StringComparer.Ordinal).Select(Escape))}]");
    }

    private static void AssertFixtureSequenceEquals(IEnumerable<string?> expected, IEnumerable<string?> actual, string what)
    {
        string?[] e = expected.ToArray();
        string?[] a = actual.ToArray();
        Assert.True(
            e.SequenceEqual(a, StringComparer.Ordinal),
            $"{what}: expected [{string.Join(", ", e.Select(Escape))}], got [{string.Join(", ", a.Select(Escape))}]");
    }

    // Values that cannot carry text (numbers, booleans, enums) — rendered invariantly, then escaped anyway so
    // the message rule has no exception.
    private static void AssertValueEquals<T>(T expected, T actual, string what)
        where T : unmanaged
    {
        Assert.True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{what}: expected {Escape(Convert.ToString(expected, CultureInfo.InvariantCulture))}, "
            + $"got {Escape(Convert.ToString(actual, CultureInfo.InvariantCulture))}");
    }

    private static void AssertValueSequenceEquals<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
        where T : unmanaged
    {
        T[] e = expected.ToArray();
        T[] a = actual.ToArray();
        Assert.True(
            e.SequenceEqual(a),
            $"{what}: expected {Escape(string.Join(", ", e))}, got {Escape(string.Join(", ", a))}");
    }

    // One row of a read table as a single comparable string; each text field is escaped, so a null and a
    // string spelled "<null>" render differently.
    private static string RenderRow(long id, params string?[] fields) =>
        string.Join("|", fields.Select(Escape).Prepend(id.ToString(CultureInfo.InvariantCulture)));

    // ---- Guard: fixture text never reaches a failure message, or a test name, unescaped -------------

    private const string EmbeddedSourceName = "Delta/PartitionEncodingGoldenDifferentialTests.cs";

    // The Xunit.Assert members this file may call: none prints an operand on failure (True/False/Fail print
    // only the message; NotEmpty and NotNull print nothing value-bearing). Every other member — Equal,
    // Contains, StartsWith, Single, … — prints its operands and is a finding, however it is spelled or reached
    // (qualified, aliased, `using static`, or as a method group).
    private static readonly HashSet<string> NonPrintingAssertions = new(StringComparer.Ordinal)
    {
        "True", "False", "Fail", "NotEmpty", "NotNull",
    };

    // Types whose members write straight to an output stream the CI log may capture.
    private static readonly HashSet<string> OutputWriterTypes = new(StringComparer.Ordinal)
    {
        "System.Console", "System.Diagnostics.Debug", "System.Diagnostics.Trace", "Xunit.Abstractions.ITestOutputHelper",
    };

    [Fact]
    public void FixtureTextGuard_HoldsOverThisFile()
    {
        // This defect class recurred across several review rounds: a failure message carrying fixture text
        // (partition values carry 0x01/0x09; SHA256SUMS lines and matrix.json's `note` are attacker-authored)
        // put raw control, ANSI or bidi bytes on the CI output stream — from inside the anti-forgery control,
        // i.e. a hostile fixture could paint the log this suite exists to make trustworthy. Two earlier guards
        // matched SPELLINGS (a regex over `$"` lines, then a syntax walk keyed on the identifiers `Assert` and
        // `Escape`), and review measured both being walked around: `$@"…"`, raw strings, `Xunit.Assert.Equal`,
        // `using static`, named arguments, a local function shadowing Escape, target-typed `new(…)`, a
        // dictionary indexer whose KeyNotFoundException echoes the key, and theory arguments rendered into test
        // names. So this guard COMPILES the file and decides on symbols and types (FindFixtureTextFindings).
        //
        // THE SOURCE IS READ FROM THE ASSEMBLY, NOT FROM DISK. A [CallerFilePath] path is green on a developer
        // machine and red on every pipeline run — deterministic builds rewrite source paths to `/_/tests/...`,
        // so the file is not beside the build. Embedding makes both environments identical and keeps a missing
        // resource a hard failure. The same remedy, for the same reason, is documented on
        // PathDisclosureHygieneTests.
        //
        // SCOPE, stated rather than implied: the guard checks the channels this file's OWN code can use —
        // assertions, exceptions, dictionary key echoes, output writers, theory display names — listed on
        // FindFixtureTextFindings. It does not see text a library composes from its own input when it fails (the
        // Parse family, file/encoding/regex APIs, System.Text.Json, Parquet.Net) or reflection, nor the own-code
        // shapes listed as its residual there; that residual is tracked in #917, and this file feeds fixture text to
        // none of those library APIs on a failure path. Three facts check that what the guard compiled is what runs:
        // FixtureTextGuard_ChecksTheClassThatRuns (no other source adds members, by name and arity; `partial` is a
        // finding here too), FixtureTextGuard_CallsNothingElseInTheTestAssembly (no other source rebinds a call made
        // here to code in the test assembly) and FixtureTextGuard_NothingShadowsANameThisFileBinds (no type elsewhere
        // in the assembly shadows a name this file binds, so an inlined constant cannot change unseen).
        using Stream? stream = typeof(PartitionEncodingGoldenDifferentialTests).Assembly
            .GetManifestResourceStream(EmbeddedSourceName);
        global::Xunit.Assert.True(stream is not null, $"embedded source {EmbeddedSourceName} is missing");

        using var reader = new StreamReader(stream!);
        GuardResult result = FindFixtureTextFindings(reader.ReadToEnd(), "DeltaSharp.Storage.Tests");

        // A file that does not compile here binds nothing, and every rule below would pass vacuously.
        global::Xunit.Assert.True(
            result.CompileErrors.Count == 0,
            $"{EmbeddedSourceName} does not compile inside the guard, so its symbols cannot be checked. It must compile on "
            + "its own, without the rest of the test assembly (copy a shared helper into the class):\n  "
            + string.Join("\n  ", result.CompileErrors.Select(Escape)));
        global::Xunit.Assert.True(
            result.Findings.Count == 0,
            $"{EmbeddedSourceName}: fixture text can reach the CI log unescaped. Render it with Escape(…), compare "
            + "through the AssertFixture*/AssertValue* helpers, read dictionaries with TryGetValue; the rules are on "
            + "FindFixtureTextFindings:\n  "
            + string.Join("\n  ", result.Findings.Select(Escape)));

        // Nor may it pass by seeing nothing: the floors catch a wholesale loss, and the per-member coverage oracle
        // inside the guard catches a single skipped member.
        global::Xunit.Assert.True(result.SinksChecked >= 80, $"guard checked only {result.SinksChecked} sinks");
        global::Xunit.Assert.True(result.EscapeCalls >= 55, $"guard saw only {result.EscapeCalls} calls to Escape");
    }

    // This fact, FixtureTextGuard_CallsNothingElseInTheTestAssembly, FixtureTextGuard_NothingShadowsANameThisFileBinds
    // and FixtureTextGuard_HoldsOverThisFile report through `global::Xunit.Assert`: a type named Assert in an enclosing
    // namespace, or an alias, in another file could otherwise swallow the very verdict that reports it.
    //
    // The guard's unit is the embedded FILE; the claim's unit is the CLASS that runs. They are the same only if no
    // other source contributes to the class (`partial` is a finding for that reason), the embedded copy is the one
    // compiled, and its names bind as they do in the guard's compilation. This fact checks the first two by member
    // name and arity: the members the guard compiled must be exactly the members the test host loaded. The third is
    // FixtureTextGuard_CallsNothingElseInTheTestAssembly, for bindings into the test assembly, and
    // FixtureTextGuard_NothingShadowsANameThisFileBinds, for inlined constants.
    [Fact]
    public void FixtureTextGuard_ChecksTheClassThatRuns()
    {
        using Stream? stream = typeof(PartitionEncodingGoldenDifferentialTests).Assembly
            .GetManifestResourceStream(EmbeddedSourceName);
        global::Xunit.Assert.True(stream is not null, $"embedded source {EmbeddedSourceName} is missing");

        using var reader = new StreamReader(stream!);
        GuardResult result = FindFixtureTextFindings(reader.ReadToEnd(), "DeltaSharp.Storage.Tests");
        INamedTypeSymbol? compiled = result.Compilation.GetTypeByMetadataName(typeof(PartitionEncodingGoldenDifferentialTests).FullName!);
        global::Xunit.Assert.True(compiled is not null, $"{EmbeddedSourceName} does not declare the class under test");

        ShapeDifference difference = CompareShapes(compiled!, typeof(PartitionEncodingGoldenDifferentialTests));
        global::Xunit.Assert.True(difference.Loaded.Contains($"{nameof(FixtureTextGuard_ChecksTheClassThatRuns)}/0"), "the loaded shape does not list this test");
        global::Xunit.Assert.True(difference.Loaded.Any(m => m.StartsWith($"{nameof(GoldenRow)}.", StringComparison.Ordinal)), "the loaded shape lists no nested type");
        global::Xunit.Assert.True(
            difference.Same,
            $"the class that runs is not the class the guard checked. Only loaded: "
            + $"[{string.Join(", ", difference.OnlyLoaded.Select(Escape))}]; only in {EmbeddedSourceName}: "
            + $"[{string.Join(", ", difference.OnlyInSource.Select(Escape))}]");
    }

    private sealed record ShapeDifference(List<string> Loaded, List<string> OnlyLoaded, List<string> OnlyInSource)
    {
        public bool Same => OnlyLoaded.Count == 0 && OnlyInSource.Count == 0;
    }

    // Multiset difference of the two shapes, so a duplicated entry (two members collapsing to one name) counts.
    private static ShapeDifference CompareShapes(INamedTypeSymbol source, Type loaded)
    {
        List<string> fromSource = Comparable(SourceShape(source, string.Empty));
        List<string> fromLoaded = Comparable(LoadedShape(loaded, string.Empty));
        List<string> onlyLoaded = fromLoaded.ToList();
        List<string> onlyInSource = new();
        foreach (string member in fromSource)
        {
            if (!onlyLoaded.Remove(member))
            {
                onlyInSource.Add(member);
            }
        }

        return new ShapeDifference(fromLoaded, onlyLoaded, onlyInSource);
    }

    // The guard compiles this file WITHOUT the rest of the test assembly, while the real build compiles them
    // together — so another file could rebind a call made here: an extension method or type in an enclosing
    // namespace, a global using alias, an interceptor, a base class added by another part. The guard's compilation
    // has no reference to this assembly and no errors, so the loaded class must reach nothing in the assembly
    // outside itself; this reads every member token in its IL, its catch, local variable and member signature types,
    // its types' bases and interfaces, and every attribute (with its typeof arguments) on its types, members,
    // parameters and return values. Only compiler-synthesized helpers are exempt (IsCompilerGenerated) — never a
    // `file`-local type, because an interceptor never needs a name.
    [Fact]
    public void FixtureTextGuard_CallsNothingElseInTheTestAssembly()
    {
        ReferenceWalk walk = ReferencesOutside(typeof(PartitionEncodingGoldenDifferentialTests));
        global::Xunit.Assert.True(walk.References >= 1000, $"the IL walk saw only {walk.References} member references");
        global::Xunit.Assert.True(
            walk.Clean,
            "code elsewhere in the test assembly is bound into the class the guard checked. This file must stand alone: "
            + "copy a shared helper into the class rather than calling it (coverlet's IncludeTestAssembly also instruments "
            + $"it — run without that option): [{string.Join(", ", walk.Outside.Select(Escape))}]");
    }

    // The walk is pinned on a compiled snippet: one class reaching another type of its own assembly by every route
    // the walk reads, and one reaching none. Each entry of the expected set is one route; dropping it from the walk
    // drops that entry.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixtureTextGuard_CallsNothingElseSeesEveryRoute(bool reaches)
    {
        string source =
            "public class B { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class Attr : System.Attribute { public Attr(System.Type? t) { } public System.Type? Named { get; set; } public System.Type[]? Many { get; set; } public object[]? Deeper { get; set; } public EnumArg Kind { get; set; } }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class FieldAttr : System.Attribute { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class ParamAttr : System.Attribute { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class RetAttr : System.Attribute { }\n"
            + "public sealed class Tagged { }\npublic sealed class NamedArg { }\npublic sealed class ArrArg { }\npublic sealed class ArrArg2 { }\npublic sealed class FpDeep { }\npublic sealed class FpRet { }\npublic sealed class FpArg { }\npublic sealed class Deep { }\npublic sealed class Nest2 { }\npublic sealed class Nest3 { }\npublic enum EnumArg { A }\npublic enum EnumInArr { A }\npublic sealed class Other { }\npublic sealed class Gen { }\npublic sealed class Elem { }\n"
            + "public interface IMark { }\npublic sealed class Caught : System.Exception { }\npublic sealed class LocalOnly { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class MethodAttr : System.Attribute { }\n"
            + "public static class StaticInit { public static int Y = 2; }\npublic static class PrivOnly { public static int Z = 3; }\n"
            + "public sealed class SigOnly { }\npublic sealed class Tested { }\npublic sealed class FieldSig { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class PropAttr : System.Attribute { }\n"
            + "[System.AttributeUsage(System.AttributeTargets.All)] public sealed class EventAttr : System.Attribute { }\n"
            + "public static class Ext { public static string Shout(this string s) => s; }\n"
            + "public static class Holder { public static int X = 1; }\n"
            + "file static class Hidden { public static int Twice(int x) => x * 2; }\n"
            + (reaches
                ? "[Attr(typeof(Tagged), Named = typeof(NamedArg), Many = new[] { typeof(ArrArg), typeof(ArrArg2) }, Deeper = new object[] { new object[] { typeof(Nest2), new object[] { typeof(Nest3) } }, EnumInArr.A }, Kind = EnumArg.A)]\npublic sealed class C : B, IMark\n{\n"
                    + "    [FieldAttr] private int _f;\n"
                    + "    private static readonly int Seed = StaticInit.Y;\n"
                    + "    internal static FieldSig? Unused;\n"
                    + "    [PropAttr] public int P => 0;\n"
                    + "    private static unsafe void Fp(delegate*<FpArg, List<FpDeep>, FpRet> f) { }\n"
                    + "    [EventAttr] public event Action? E;\n"
                    + "    private static int Private() => PrivOnly.Z + Seed;\n"
                    + "    private static bool Takes(SigOnly? x, object o) => o is Tested;\n"
                    + "    [MethodAttr]\n"
                    + "    [return: RetAttr]\n"
                    + "    public int Run([ParamAttr] string s)\n    {\n"
                    + "        var list = new List<Other>(); Type t = typeof(Elem[]);\n"
                    + "        Func<int, int> f = x => Hidden.Twice(x);\n"
                    + "        var nested = new List<List<Deep>>(); double scale = 1.5;\n"
                    + "        LocalOnly? kept = null; try { s = s.Trim(); } catch (Caught) { s = string.Empty; } System.GC.KeepAlive(kept);\n"
                    + "        return s.Shout().Length + Holder.X + list.Count + Enumerable.Empty<Gen>().Count() + f(_f) + t.Name.Length + Private()\n"
                    + "            + (Takes(null, s) ? 1 : 0) + nested.Count + (int)scale;\n    }\n}\n"
                : "public sealed class C\n{\n    private int _f;\n"
                    + "    public int Run(string s)\n    {\n"
                    + "        var list = new List<int>(); Func<int, int> f = x => x * 2;\n"
                    + "        return s.Length + list.Count + f(_f);\n    }\n}\n");
        GuardResult result = FindFixtureTextFindings(source, "RouteSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));

        // A file-local type needs a file path to be emitted.
        SyntaxTree snippet = result.Compilation.SyntaxTrees.First();
        CSharpCompilation compilation = result.Compilation.ReplaceSyntaxTree(snippet, snippet.WithFilePath("Routes.cs"));
        using var image = new MemoryStream();
        Assert.True(compilation.Emit(image).Success, "the route snippet did not emit");
        image.Position = 0;
        var context = new System.Runtime.Loader.AssemblyLoadContext(nameof(FixtureTextGuard_CallsNothingElseSeesEveryRoute), isCollectible: true);
        try
        {
            ReferenceWalk walk = ReferencesOutside(context.LoadFromStream(image).GetType("C", throwOnError: true)!);
            List<string> reached = walk.Outside
                .Select(entry => entry[(entry.IndexOf(" -> ", StringComparison.Ordinal) + 4)..])
                .Select(target => target.Contains("__", StringComparison.Ordinal) ? target[(target.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : target)
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
            string[] expected = reaches
                ? new[] { "ArrArg", "ArrArg2", "Attr", "B", "Caught", "Deep", "Elem", "EnumArg", "EnumInArr", "EventAttr", "Ext", "FieldAttr", "FieldSig", "FpArg", "FpDeep", "FpRet", "Gen", "Hidden", "Holder", "IMark", "LocalOnly", "MethodAttr", "NamedArg", "Nest2", "Nest3", "Other", "ParamAttr", "PrivOnly", "PropAttr", "RetAttr", "SigOnly", "StaticInit", "Tagged", "Tested" }
                : Array.Empty<string>();
            Assert.True(
                walk.Clean == !reaches && reached.SequenceEqual(expected),
                $"the walk reached [{string.Join(", ", reached.Select(Escape))}]");
        }
        finally
        {
            context.Unload();
        }
    }

    // A constant (an enum member or const field) is inlined, so no IL fact can see which type supplied it — and a
    // type in this namespace, an enclosing one or the global namespace, declared in another file, outranks the
    // usings this file's names bind through. So no type in the test assembly may sit there under a name the guard's
    // compilation binds to a type. (A global using ALIAS has no runtime trace: it is the stated residual, and the
    // guard's own constants are pinned by self-tests that run this same compiled code.)
    [Fact]
    public void FixtureTextGuard_NothingShadowsANameThisFileBinds()
    {
        using Stream? stream = typeof(PartitionEncodingGoldenDifferentialTests).Assembly
            .GetManifestResourceStream(EmbeddedSourceName);
        global::Xunit.Assert.True(stream is not null, $"embedded source {EmbeddedSourceName} is missing");

        using var reader = new StreamReader(stream!);
        GuardResult result = FindFixtureTextFindings(reader.ReadToEnd(), "DeltaSharp.Storage.Tests");
        HashSet<string> bound = BoundTypeNames(result.Compilation);
        global::Xunit.Assert.True(bound.Contains("TypeKind") && bound.Contains("List`1") && bound.Contains("Convert") && !bound.Contains(nameof(GoldenRow)), "the guard bound no outside type names, or bound its own");

        List<string> shadows = ShadowingTypes(typeof(PartitionEncodingGoldenDifferentialTests), bound);
        global::Xunit.Assert.True(
            shadows.Count == 0,
            "a type elsewhere in the test assembly sits where it outranks this file's usings: it shares a name this file "
            + "binds (a constant from it would be inlined unseen), or it declares extension methods (which outrank "
            + "`using System.Linq` whatever its name). Move it into a child namespace (e.g. DeltaSharp.Storage.Tests.Delta.Helpers), "
            + $"or rename a same-named type: [{string.Join(", ", shadows.Select(Escape))}]");
    }

    // The checks above run in the same compilation as any file that might try to subvert them, so the code they
    // rest on — the IL walk and the shadow enumeration — must not bind through anything such a file could redefine:
    // no extension method (an extension in an enclosing namespace outranks `using System.Linq`), no interpolated
    // string (its handler type is looked up by name), and no type or namespace name except `global::`-rooted ones
    // (an alias or an enclosing type would win), no member bound by a language pattern (a query, a deconstruction, an
    // await, an extension GetEnumerator) and no tuple syntax (its ValueTuple type is looked up by name). Interceptors, which can redirect even a `global::` call, are
    // switched off for this project in its csproj. This fact keeps that core as written.
    private static readonly string[] TrustedCore =
    {
        nameof(ShadowingTypes), nameof(AttributeValues), nameof(ReferenceWalk), nameof(ReferencesOutside),
        nameof(DeclaredMembers), nameof(TypeAndNested), nameof(Expand), nameof(IsWithin), nameof(FileLocalTypeName),
        nameof(IsCompilerGenerated), nameof(BuildOpCodeTable), nameof(MemberTokens),
    };

    [Fact]
    public void FixtureTextGuard_TrustedCoreBindsNothingRebindable()
    {
        using Stream? stream = typeof(PartitionEncodingGoldenDifferentialTests).Assembly
            .GetManifestResourceStream(EmbeddedSourceName);
        global::Xunit.Assert.True(stream is not null, $"embedded source {EmbeddedSourceName} is missing");

        using var reader = new StreamReader(stream!);
        GuardResult result = FindFixtureTextFindings(reader.ReadToEnd(), "DeltaSharp.Storage.Tests");
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int typeNames = 0;
        SyntaxTree tree = result.Compilation.SyntaxTrees.First();
        SemanticModel model = result.Compilation.GetSemanticModel(tree);
        foreach (MemberDeclarationSyntax member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            string? name = member switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text,
                FieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier.Text,
                RecordDeclarationSyntax record => record.Identifier.Text,
                _ => null,
            };
            if (name is null || Array.IndexOf(TrustedCore, name) < 0)
            {
                continue;
            }

            seen.Add(name);
            findings.AddRange(TrustedCoreFindings(model, member, ref typeNames));
        }

        global::Xunit.Assert.True(
            seen.SetEquals(TrustedCore) && typeNames >= 100,
            $"the trusted-core check saw [{string.Join(", ", seen.Order(StringComparer.Ordinal).Select(Escape))}] and {typeNames} type names");
        global::Xunit.Assert.True(
            findings.Count == 0,
            "the trusted core binds through something another file could redefine; use `global::`-qualified framework "
            + $"types, plain loops and string concatenation there: [{string.Join(", ", findings.Select(Escape))}]");
    }

    // The csproj's NoInterceptorsForTheFixtureTextGuard target records in this assembly the interceptor settings the
    // compiler was handed. An interceptor can redirect even a `global::` call in the trusted core, so none may be set:
    // a deleted line in that target, or the target itself, fails here rather than quietly switching them back on.
    [Fact]
    public void FixtureTextGuard_InterceptorsAreOffForThisAssembly()
    {
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (global::System.Reflection.AssemblyMetadataAttribute attribute in
            global::System.Reflection.CustomAttributeExtensions.GetCustomAttributes<global::System.Reflection.AssemblyMetadataAttribute>(
                typeof(PartitionEncodingGoldenDifferentialTests).Assembly))
        {
            if (attribute.Key.StartsWith("FixtureTextGuard.", StringComparison.Ordinal))
            {
                recorded[attribute.Key] = attribute.Value ?? string.Empty;
            }
        }

        global::Xunit.Assert.True(
            recorded.Count == 3,
            "the csproj target NoInterceptorsForTheFixtureTextGuard did not record the compiler's interceptor settings; "
            + $"restore it (found [{string.Join(", ", recorded.Keys.Select(Escape))}])");
        global::Xunit.Assert.True(
            recorded["FixtureTextGuard.InterceptorsNamespaces"].Length == 0
            && recorded["FixtureTextGuard.InterceptorsPreviewNamespaces"].Length == 0,
            "interceptors are enabled for this assembly: InterceptorsNamespaces="
            + Escape(recorded["FixtureTextGuard.InterceptorsNamespaces"]) + ", InterceptorsPreviewNamespaces="
            + Escape(recorded["FixtureTextGuard.InterceptorsPreviewNamespaces"]));
        string[] active = recorded["FixtureTextGuard.Features"]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(feature => feature.StartsWith("InterceptorsNamespaces=", StringComparison.OrdinalIgnoreCase)
                || feature.StartsWith("InterceptorsPreviewNamespaces=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        global::Xunit.Assert.True(
            active.Length == 0,
            $"interceptors are enabled through compiler features: [{string.Join(", ", active.Select(Escape))}]");
    }

    // ... and it does find each kind of rebindable binding.
    [Theory]
    [InlineData("int[] a = { 1 }; return a.Length + a.Count();")]
    [InlineData("return $\"{1}\".Length;")]
    [InlineData("List<int> a = new(); return a.Count;")]
    [InlineData("System.Collections.Generic.List<int> a = new(); return a.Count;")]
    [InlineData("return (int)StringComparison.Ordinal;")]
    [InlineData("int[] a = { 1 }; foreach (int x in from y in a select y) { return x; } return 0;")]
    [InlineData("int[] a = { 1 }; foreach (int x in from y in a where y > 0 select y + 1) { return x; } return 0;")]
    [InlineData("(int a, int b) = global::System.Tuple.Create(1, 2); return a + b;")]
    [InlineData("global::System.Func<int> f = new int[0].Count; return f();")]
    [InlineData("(int A, int B) t = (1, 2); return t.A;")]
    [InlineData("int[] a = { 1, 2 }; return a[^1];")]
    [InlineData("int[] a = [1]; return a.Length;")]
    [InlineData("return Escape(null).Length;")]
    [InlineData("var a = new int[1]; return a.Length;")]
    public void FixtureTextGuard_TrustedCoreCheckFindsRebindableBindings(string body)
    {
        GuardResult result = FindFixtureTextFindings(
            "public static class C\n{\n    private static string Escape(string? s) => s ?? \"<null>\";\n"
            + "    public static int M()\n    {\n        " + body + "\n    }\n}\n",
            "TrustedSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));
        SyntaxTree tree = result.Compilation.SyntaxTrees.First();
        MethodDeclarationSyntax method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "M");
        int typeNames = 0;
        List<string> findings = TrustedCoreFindings(result.Compilation.GetSemanticModel(tree), method, ref typeNames);
        Assert.True(findings.Count > 0, $"the trusted-core check accepted {Escape(body)}");
    }

    private static List<string> TrustedCoreFindings(SemanticModel model, SyntaxNode member, ref int typeNames)
    {
        var findings = new List<string>();
        var extensionCalls = new HashSet<IOperation>();
        INamedTypeSymbol? owner = model.GetDeclaredSymbol(member.Ancestors().OfType<TypeDeclarationSyntax>().First());
        foreach (SyntaxNode node in member.DescendantNodesAndSelf())
        {
            string where = $"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

            // Every extension call under this node, including the implicit ones a language pattern binds (a query
            // clause, a collection-initializer Add), not only the calls written as such.
            foreach (IOperation inner in model.GetOperation(node)?.DescendantsAndSelf() ?? Enumerable.Empty<IOperation>())
            {
                ISymbol? used = inner switch
                {
                    IInvocationOperation call => call.TargetMethod,
                    IMethodReferenceOperation group => group.Method,
                    IPropertyReferenceOperation property => property.Property,
                    IFieldReferenceOperation field => field.Field,
                    IBinaryOperation { OperatorMethod: { } method } => method,
                    IUnaryOperation { OperatorMethod: { } method } => method,
                    _ => null,
                };
                string at = $"line {inner.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                if (used is IMethodSymbol { IsExtensionMethod: true } || used?.ContainingType is { IsExtension: true })
                {
                    if (extensionCalls.Add(inner))
                    {
                        findings.Add($"{at}: extension member {used!.Name}");
                    }
                }
                else if (used is not null && owner is not null && SymbolEqualityComparer.Default.Equals(used.ContainingType, owner)
                    && used is not IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }
                    && Array.IndexOf(TrustedCore, used.Name) < 0 && extensionCalls.Add(inner))
                {
                    // The core is closed under calls: a member of this class it reaches is core too.
                    findings.Add($"{at}: reaches {used.Name}, which is not in the trusted core");
                }
            }

            if (node is InterpolatedStringExpressionSyntax)
            {
                findings.Add($"{where}: interpolated string");
            }
            else if (node is QueryExpressionSyntax or AwaitExpressionSyntax or ForEachVariableStatementSyntax
                    or TupleTypeSyntax or TupleExpressionSyntax or WithExpressionSyntax or RangeExpressionSyntax
                    or CollectionExpressionSyntax or PositionalPatternClauseSyntax or AttributeListSyntax
                || node.IsKind(SyntaxKind.IndexExpression)
                || node is AssignmentExpressionSyntax { Left: TupleExpressionSyntax or DeclarationExpressionSyntax }
                || (node is ForEachStatementSyntax loop && model.GetForEachStatementInfo(loop).GetEnumeratorMethod is { IsExtensionMethod: true }))
            {
                // Each binds a method by pattern (Select/Where, GetAwaiter, Deconstruct, GetEnumerator, a collection
                // builder) that an extension in an enclosing namespace could supply, a well-known type (ValueTuple,
                // Index, Range) a source declaration could replace, or an attribute type by unrooted name.
                findings.Add($"{where}: pattern-bound member ({node.Kind()})");
            }
            else if (node is SimpleNameSyntax name
                && model.GetSymbolInfo(name).Symbol is INamespaceOrTypeSymbol symbol
                && !(symbol is INamedTypeSymbol own && own.Locations.Any(location => location.IsInSource)))
            {
                typeNames++;
                bool rooted = name.Parent switch
                {
                    AliasQualifiedNameSyntax alias => alias.Name == name || alias.Alias.Identifier.IsKind(SyntaxKind.GlobalKeyword),
                    QualifiedNameSyntax qualified => qualified.Right == name,
                    MemberAccessExpressionSyntax access => access.Name == name,
                    _ => false,
                };
                if (!rooted && !name.Identifier.IsKind(SyntaxKind.GlobalKeyword))
                {
                    findings.Add($"{where}: unrooted type or namespace name {name.Identifier.Text}");
                }
            }
        }

        return findings;
    }

    // The shadow check is pinned on a loaded snippet: only top-level types in this namespace, an enclosing one or the
    // global namespace count — named ones, and any that declares extension methods — the class itself does not, and a
    // type elsewhere does not.
    [Fact]
    public void FixtureTextGuard_ShadowCheckSeesEnclosingNamespacesOnly()
    {
        GuardResult result = FindFixtureTextFindings(
            "namespace N.M { public sealed class C { public sealed class Nested { } } }\n"
            + "namespace N { internal static class Shadow { } }\nnamespace N.M { public static class Sibling { } }\n"
            + "namespace N.M.Deeper { public static class Below { } }\nnamespace Other { public static class Away { } }\n"
            + "internal enum Glob { A }\n"
            + "namespace N { internal static class Helpers { public static int Twice(this int x) => x * 2; } }\n"
            + "namespace Other { public static class FarExt { public static int Thrice(this int x) => x * 3; } }\n",
            "ShadowSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));

        using var image = new MemoryStream();
        Assert.True(result.Compilation.Emit(image).Success, "the shadow snippet did not emit");
        image.Position = 0;
        var context = new System.Runtime.Loader.AssemblyLoadContext(nameof(FixtureTextGuard_ShadowCheckSeesEnclosingNamespacesOnly), isCollectible: true);
        try
        {
            Type self = context.LoadFromStream(image).GetType("N.M.C", throwOnError: true)!;
            List<string> shadows = ShadowingTypes(self, new HashSet<string>(StringComparer.Ordinal) { "C", "Nested", "Shadow", "Sibling", "Below", "Away", "Glob" });
            Assert.True(
                shadows.SequenceEqual(new[] { "Glob", "N.Helpers", "N.M.Sibling", "N.Shadow" }),
                $"the shadow check reported [{string.Join(", ", shadows.Select(Escape))}]");
        }
        finally
        {
            context.Unload();
        }
    }

    // Every type name (metadata name, so generic arity included) the compilation binds anywhere in its first tree to
    // a type it does NOT declare: a type declared in this file is nested here (or file-local), and a nested type
    // outranks any namespace type, so nothing can shadow it.
    private static HashSet<string> BoundTypeNames(CSharpCompilation compilation)
    {
        SyntaxTree tree = compilation.SyntaxTrees.First();
        SemanticModel model = compilation.GetSemanticModel(tree);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (model.GetSymbolInfo(name).Symbol is INamedTypeSymbol type && !type.Locations.Any(location => location.IsInSource))
            {
                names.Add(type.MetadataName);
            }
        }

        return names;
    }

    // Top-level types of self's assembly, other than self, declared in self's namespace, an enclosing one, or the
    // global namespace — the places that outrank a using directive — that are named in `names` or declare extension
    // methods (an extension there outranks `using System.Linq`, whatever its name). TRUSTED CORE: see
    // FixtureTextGuard_TrustedCoreBindsNothingRebindable.
    private static global::System.Collections.Generic.List<string> ShadowingTypes(
        global::System.Type self, global::System.Collections.Generic.HashSet<string> names)
    {
        global::System.Collections.Generic.HashSet<string> enclosing = new(global::System.StringComparer.Ordinal);
        enclosing.Add(string.Empty);
        string prefix = string.Empty;
        foreach (string part in (self.Namespace ?? string.Empty).Split('.'))
        {
            if (part.Length > 0)
            {
                prefix = prefix.Length == 0 ? part : prefix + "." + part;
                enclosing.Add(prefix);
            }
        }

        global::System.Collections.Generic.List<string> found = new();
        foreach (global::System.Type type in self.Assembly.GetTypes())
        {
            if (type.DeclaringType is null && type != self && enclosing.Contains(type.Namespace ?? string.Empty)
                && (names.Contains(type.Name)
                    || type.IsDefined(typeof(global::System.Runtime.CompilerServices.ExtensionAttribute), false)))
            {
                found.Add(type.Namespace is null ? type.Name : type.Namespace + "." + type.Name);
            }
        }

        found.Sort(global::System.StringComparer.Ordinal);
        return found;
    }

    // A typeof (or enum) inside an array argument arrives as a collection of typed arguments, nested to any depth;
    // each element's own argument type is reported with its value. TRUSTED CORE.
    private static void AttributeValues(
        global::System.Reflection.CustomAttributeTypedArgument argument,
        global::System.Collections.Generic.List<global::System.Type?> into)
    {
        into.Add(argument.ArgumentType);
        if (argument.Value is global::System.Collections.Generic.IReadOnlyCollection<global::System.Reflection.CustomAttributeTypedArgument> items)
        {
            foreach (global::System.Reflection.CustomAttributeTypedArgument item in items)
            {
                AttributeValues(item, into);
            }
        }
        else
        {
            into.Add(argument.Value as global::System.Type);
        }
    }

    // TRUSTED CORE.
    private sealed record ReferenceWalk(global::System.Collections.Generic.SortedSet<string> Outside, int References)
    {
        public bool Clean => Outside.Count == 0;
    }

    // Everything the loaded class (and its nested and compiler-generated types) reaches: IL tokens, catch types,
    // local variable types, member signature types, base types, interfaces, and attributes — with their typeof
    // arguments — on types, members, parameters and return values. TRUSTED CORE: written with `global::`-qualified
    // framework types, plain loops and no extension methods or interpolation, so nothing another file declares can
    // rebind the walk that is meant to catch such a rebinding (FixtureTextGuard_TrustedCoreBindsNothingRebindable).
    private static ReferenceWalk ReferencesOutside(global::System.Type self)
    {
        global::System.Collections.Generic.SortedSet<string> outside = new(global::System.StringComparer.Ordinal);
        global::System.Reflection.Emit.OpCode[][] opCodes = BuildOpCodeTable();
        int references = 0;

        // Compiler-generated helpers outside the class (`<PrivateImplementationDetails>` span and inline-array
        // helpers) that the class calls: their bodies are walked too, because they call well-known framework members
        // (MemoryMarshal, Unsafe) that a source declaration in another file would silently replace.
        global::System.Collections.Generic.List<global::System.Reflection.MethodBase> helpers = new();
        global::System.Collections.Generic.HashSet<global::System.Reflection.MethodBase> helpersSeen = new();

        void Check(string from, global::System.Type? touched)
        {
            foreach (global::System.Type type in Expand(touched))
            {
                if (type.Assembly == self.Assembly && !IsWithin(type, self) && !IsCompilerGenerated(type))
                {
                    outside.Add(from + " -> " + type.FullName);
                }
            }
        }

        void CheckAttributes(string from, global::System.Collections.Generic.IList<global::System.Reflection.CustomAttributeData> attributes)
        {
            foreach (global::System.Reflection.CustomAttributeData attribute in attributes)
            {
                Check(from, attribute.AttributeType);
                global::System.Collections.Generic.List<global::System.Type?> values = new();
                foreach (global::System.Reflection.CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
                {
                    AttributeValues(argument, values);
                }

                foreach (global::System.Reflection.CustomAttributeNamedArgument named in attribute.NamedArguments)
                {
                    AttributeValues(named.TypedValue, values);
                }

                foreach (global::System.Type? value in values)
                {
                    Check(from, value);
                }
            }
        }

        void CheckParameter(string from, global::System.Reflection.ParameterInfo parameter)
        {
            CheckAttributes(from, parameter.GetCustomAttributesData());
            Check(from, parameter.ParameterType);
        }

        void CheckMethod(global::System.Type type, global::System.Reflection.MethodBase method)
        {
            string from = type.Name + "." + method.Name;
            CheckAttributes(from, method.GetCustomAttributesData());
            foreach (global::System.Reflection.ParameterInfo parameter in method.GetParameters())
            {
                CheckParameter(from, parameter);
            }

            if (method is global::System.Reflection.MethodInfo info)
            {
                CheckParameter(from, info.ReturnParameter);
            }

            global::System.Reflection.MethodBody? body = method.GetMethodBody();
            if (body is null)
            {
                return;
            }

            foreach (global::System.Reflection.ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                Check(from, clause.Flags == global::System.Reflection.ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null);
            }

            foreach (global::System.Reflection.LocalVariableInfo local in body.LocalVariables)
            {
                Check(from, local.LocalType);
            }

            byte[]? il = body.GetILAsByteArray();
            if (il is null)
            {
                return;
            }

            foreach (int token in MemberTokens(il, opCodes))
            {
                references++;
                global::System.Reflection.MemberInfo target = method.Module.ResolveMember(
                    token,
                    type.IsGenericType ? type.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null)!;
                if (target is global::System.Type touched)
                {
                    Check(from, touched);
                }
                else if (target is global::System.Reflection.MethodBase called)
                {
                    Check(from, called.DeclaringType);
                    if (called.DeclaringType is { } helperType && helperType.Assembly == self.Assembly
                        && !IsWithin(helperType, self) && IsCompilerGenerated(helperType) && helpersSeen.Add(called))
                    {
                        helpers.Add(called);
                    }

                    if (called.IsGenericMethod)
                    {
                        foreach (global::System.Type argument in called.GetGenericArguments())
                        {
                            Check(from, argument);
                        }
                    }
                }
                else
                {
                    Check(from, target.DeclaringType);
                }
            }
        }

        foreach (global::System.Type type in TypeAndNested(self))
        {
            Check(type.Name, type.BaseType);
            foreach (global::System.Type contract in type.GetInterfaces())
            {
                Check(type.Name, contract);
            }

            CheckAttributes(type.Name, type.GetCustomAttributesData());
            foreach (global::System.Reflection.FieldInfo field in type.GetFields(DeclaredMembers))
            {
                CheckAttributes(type.Name + "." + field.Name, field.GetCustomAttributesData());
                Check(type.Name + "." + field.Name, field.FieldType);
            }

            foreach (global::System.Reflection.PropertyInfo property in type.GetProperties(DeclaredMembers))
            {
                CheckAttributes(type.Name + "." + property.Name, property.GetCustomAttributesData());
                Check(type.Name + "." + property.Name, property.PropertyType);
            }

            foreach (global::System.Reflection.EventInfo @event in type.GetEvents(DeclaredMembers))
            {
                CheckAttributes(type.Name + "." + @event.Name, @event.GetCustomAttributesData());
                Check(type.Name + "." + @event.Name, @event.EventHandlerType);
            }

            foreach (global::System.Reflection.MethodInfo method in type.GetMethods(DeclaredMembers))
            {
                CheckMethod(type, method);
            }

            foreach (global::System.Reflection.ConstructorInfo constructor in type.GetConstructors(DeclaredMembers))
            {
                CheckMethod(type, constructor);
            }
        }

        for (int i = 0; i < helpers.Count; i++)
        {
            CheckMethod(helpers[i].DeclaringType!, helpers[i]);
        }

        return new ReferenceWalk(outside, references);
    }

    // TRUSTED CORE.
    private const global::System.Reflection.BindingFlags DeclaredMembers = global::System.Reflection.BindingFlags.DeclaredOnly
        | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic
        | global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Static;

    // TRUSTED CORE.
    private static global::System.Collections.Generic.IEnumerable<global::System.Type> TypeAndNested(global::System.Type type)
    {
        yield return type;
        foreach (global::System.Type nested in type.GetNestedTypes(DeclaredMembers))
        {
            foreach (global::System.Type inner in TypeAndNested(nested))
            {
                yield return inner;
            }
        }
    }

    // A type and every type it is built from: generic arguments, element types, declaring types, function-pointer
    // signatures. An array, pointer, by-ref or function-pointer type is declared nowhere itself (`T[]` reports the
    // assembly of its type parameter), so only its parts are. TRUSTED CORE.
    private static global::System.Collections.Generic.IEnumerable<global::System.Type> Expand(global::System.Type? type)
    {
        if (type is null || type.IsGenericParameter)
        {
            yield break;
        }

        global::System.Collections.Generic.List<global::System.Type> parts = new();
        if (type.IsFunctionPointer)
        {
            parts.AddRange(type.GetFunctionPointerParameterTypes());
            parts.Add(type.GetFunctionPointerReturnType());
        }
        else
        {
            if (!type.HasElementType)
            {
                yield return type;
            }
            else
            {
                parts.Add(type.GetElementType()!);
            }

            if (type.IsGenericType)
            {
                parts.AddRange(type.GetGenericArguments());
            }

            if (type.DeclaringType is { } declaring)
            {
                parts.Add(declaring);
            }
        }

        foreach (global::System.Type part in parts)
        {
            foreach (global::System.Type expanded in Expand(part))
            {
                yield return expanded;
            }
        }
    }

    // TRUSTED CORE.
    private static bool IsWithin(global::System.Type type, global::System.Type container)
    {
        for (global::System.Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (current == container)
            {
                return true;
            }
        }

        return false;
    }

    // Only compiler-synthesized helpers: `<>`-prefixed closures, anonymous types and inline arrays, and
    // `<PrivateImplementationDetails>`. Never a `file`-local type or anything nested in one, whatever its file is
    // named: it is emitted as `<FileName>F{sha256}__Name` (`<>F…` for a file named `.cs`), and is exactly the shape an
    // interceptor generator writes. TRUSTED CORE.
    private static readonly global::System.Text.RegularExpressions.Regex FileLocalTypeName =
        new("^<[^>]*>F[0-9A-F]{64}__", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsCompilerGenerated(global::System.Type type)
    {
        for (global::System.Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (FileLocalTypeName.IsMatch(current.Name))
            {
                return false;
            }
        }

        for (global::System.Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (current.Name.StartsWith("<>", global::System.StringComparison.Ordinal)
                || current.Name.StartsWith("<PrivateImplementationDetails>", global::System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // TRUSTED CORE: one-byte opcodes in [0], 0xFE-prefixed ones in [1] — two arrays, not a tuple (whose ValueTuple
    // type a source declaration could replace), built afresh for each walk rather than held in a static that code
    // elsewhere could rewrite by reflection.
    private static global::System.Reflection.Emit.OpCode[][] BuildOpCodeTable()
    {
        global::System.Reflection.Emit.OpCode[] oneByte = new global::System.Reflection.Emit.OpCode[256];
        global::System.Reflection.Emit.OpCode[] twoByte = new global::System.Reflection.Emit.OpCode[256];
        foreach (global::System.Reflection.FieldInfo field in typeof(global::System.Reflection.Emit.OpCodes).GetFields(
            global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.Static))
        {
            global::System.Reflection.Emit.OpCode opCode = (global::System.Reflection.Emit.OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)opCode.Value);
            if (opCode.Size == 1)
            {
                oneByte[value & 0xFF] = opCode;
            }
            else
            {
                twoByte[value & 0xFF] = opCode;
            }
        }

        return new[] { oneByte, twoByte };
    }

    // The decoder's operand sizes, pinned on hand-built IL: each body puts one operand of a given width before a
    // `call 0x0A000001`, with bytes that decode to a different token stream if that width is wrong.
    private static readonly byte[][] OperandWidthBodies =
    {
        new byte[] { 0x23, 0, 0, 0, 0, 0x28, 0x02, 0, 0, 0x28, 0x01, 0, 0, 0x0A, 0x2A },         // ldc.r8 (8)
        new byte[] { 0x21, 0, 0, 0, 0, 0x28, 0x02, 0, 0, 0x28, 0x01, 0, 0, 0x0A, 0x2A },         // ldc.i8 (8)
        new byte[] { 0x22, 0, 0, 0x28, 0x02, 0x28, 0x01, 0, 0, 0x0A, 0x2A },                     // ldc.r4 (4)
        new byte[] { 0xFE, 0x0C, 0x00, 0x28, 0x28, 0x01, 0, 0, 0x0A, 0x2A },                    // ldloc (2)
        new byte[] { 0x11, 0x28, 0x28, 0x01, 0, 0, 0x0A, 0x2A },                                 // ldloc.s (1)
        new byte[] { 0x45, 0x01, 0, 0, 0, 0x28, 0x02, 0, 0, 0x28, 0x01, 0, 0, 0x0A, 0x2A },      // switch (4 + 4n)
        new byte[] { 0x72, 0x28, 0x02, 0, 0x70, 0x28, 0x01, 0, 0, 0x0A, 0x2A },                  // ldstr (4, not a member)
    };

    public static TheoryData<int> OperandWidthIndices()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < OperandWidthBodies.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OperandWidthIndices))]
    public void FixtureTextGuard_DecodesEveryOperandWidth(int index)
    {
        int[] tokens = MemberTokens(OperandWidthBodies[index], BuildOpCodeTable()).ToArray();
        Assert.True(tokens.SequenceEqual(new[] { 0x0A000001 }), $"body {index} decoded to [{string.Join(", ", tokens.Select(t => t.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Select(Escape))}]");
    }

    // The metadata tokens an IL body references (call, newobj, ldfld, ldtoken, castclass, …). TRUSTED CORE.
    private static global::System.Collections.Generic.IEnumerable<int> MemberTokens(byte[] il, global::System.Reflection.Emit.OpCode[][] table)
    {
        for (int i = 0; i < il.Length;)
        {
            global::System.Reflection.Emit.OpCode opCode = il[i] == 0xFE ? table[1][il[i + 1]] : table[0][il[i]];
            if (opCode.Size == 0)
            {
                throw new global::System.InvalidOperationException("unknown IL opcode at offset " + i);
            }

            i += opCode.Size;
            switch (opCode.OperandType)
            {
                case global::System.Reflection.Emit.OperandType.InlineMethod or global::System.Reflection.Emit.OperandType.InlineField
                    or global::System.Reflection.Emit.OperandType.InlineType or global::System.Reflection.Emit.OperandType.InlineTok:
                    yield return global::System.BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case global::System.Reflection.Emit.OperandType.InlineNone:
                    break;
                case global::System.Reflection.Emit.OperandType.ShortInlineBrTarget or global::System.Reflection.Emit.OperandType.ShortInlineI
                    or global::System.Reflection.Emit.OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case global::System.Reflection.Emit.OperandType.InlineVar:
                    i += 2;
                    break;
                case global::System.Reflection.Emit.OperandType.InlineI8 or global::System.Reflection.Emit.OperandType.InlineR:
                    i += 8;
                    break;
                case global::System.Reflection.Emit.OperandType.InlineSwitch:
                    i += 4 + (4 * global::System.BitConverter.ToInt32(il, i));
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }

    // The two shapes agree for the member kinds this file could gain in an ordinary edit, so a failure above means a
    // real difference, not a Roslyn/reflection mismatch; and the comparison reports exactly what another part could
    // add — an overload, a nested member, a field, an interface — or rename at the same member count. Compiled from
    // source, emitted, loaded, compared. Variant 0 loads the same source, 1 adds members, 2 renames one.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FixtureTextGuard_ShapeComparisonAcceptsOrdinaryMembers(int variant)
    {
        static string Snippet(bool more, bool renamed = false) =>
            "public sealed class C" + (more ? " : System.ICloneable" : string.Empty) + "\n{\n"
            + "    private enum Engine { Spark, DeltaRs }\n"
            + "    private readonly record struct Pair(int A);\n"
            + "    private sealed record Row(string? Value)" + (more ? " { public int Extra => 1; }" : ";") + "\n"
            + "    private struct Cell { public int X;" + (more ? " public int Y;" : string.Empty) + " }\n"
            + "    private sealed class Box<T> { public T? Item; }\n"
            + "    public event EventHandler? Changed;\n"
            + "    private static readonly object? Nothing = null;\n"
            + "    private static readonly int Zero = 0;\n"
            + "    private static readonly string[] Names = { \"a\" };\n"
            + "    private const int Limit = 3;\n"
            + "    public int Size { get; init; }\n"
            + "    private IEnumerable<int> Count() { yield return Limit; }\n"
            + "    private async Task<int> Later() { await Task.Yield(); return Zero; }\n"
            + "    private int Twice() { int Double(int x) => x * 2; Func<int, int> f = x => x + Names.Length; return Double(f(1)); }\n"
            + "    public void " + (renamed ? "Fire" : "Raise") + "() => Changed?.Invoke(Nothing, EventArgs.Empty);\n"
            + (more ? "    public void Raise(int n) { }\n    public void Raise(string s) { }\n    public object Clone() => this;\n    public C(int n) { }\n    public C() { }\n" : string.Empty)
            + "}\n";

        GuardResult source = FindFixtureTextFindings(Snippet(false), "ShapeSnippet");
        GuardResult emitted = variant == 0 ? source : FindFixtureTextFindings(Snippet(variant == 1, variant == 2), "ShapeSnippet");
        Assert.True(source.CompileErrors.Count == 0 && emitted.CompileErrors.Count == 0, string.Join("; ", emitted.CompileErrors.Select(Escape)));

        using var image = new MemoryStream();
        Assert.True(emitted.Compilation.Emit(image).Success, "the shape snippet did not emit");
        image.Position = 0;
        var context = new System.Runtime.Loader.AssemblyLoadContext(nameof(FixtureTextGuard_ShapeComparisonAcceptsOrdinaryMembers), isCollectible: true);
        try
        {
            ShapeDifference difference = CompareShapes(
                source.Compilation.GetTypeByMetadataName("C")!, context.LoadFromStream(image).GetType("C", throwOnError: true)!);
            Assert.True(difference.Loaded.Any(m => m.StartsWith("Engine.", StringComparison.Ordinal)), "the loaded shape lists no nested type");
            (string[] onlyLoaded, string[] onlyInSource) = variant switch
            {
                1 => (new[] { ".ctor/1", ":System.ICloneable", "Cell.Y", "Clone/0", "Raise/1", "Raise/1", "Row.get_Extra/0" }, Array.Empty<string>()),
                2 => (new[] { "Fire/0" }, new[] { "Raise/0" }),
                _ => (Array.Empty<string>(), Array.Empty<string>()),
            };
            Assert.True(
                difference.Same == (variant == 0)
                    && difference.OnlyLoaded.SequenceEqual(onlyLoaded) && difference.OnlyInSource.SequenceEqual(onlyInSource),
                $"only loaded: [{string.Join(", ", difference.OnlyLoaded.Select(Escape))}]; "
                + $"only in source: [{string.Join(", ", difference.OnlyInSource.Select(Escape))}]");
        }
        finally
        {
            context.Unload();
        }
    }

    // The exemption above must not cover a `file`-local type, whose emitted name also contains `<` — and starts with
    // `<>` (or `<PrivateImplementationDetails>`) when the source file is named `.cs` (or
    // `PrivateImplementationDetails.cs`). Compiled, emitted and loaded under each file name: a
    // file-local class and a closure nested in it are checked, and a lambda's closure class elsewhere is exempt.
    [Theory]
    [InlineData("Exemption.cs")]
    [InlineData(".cs")]
    [InlineData("PrivateImplementationDetails.cs")]
    public void FixtureTextGuard_ExemptsOnlyCompilerGeneratedTypes(string fileName)
    {
        GuardResult result = FindFixtureTextFindings(
            "file static class Hidden { public static int Twice(int x) => x * 2; public static Func<int, int> Scale(int k) => x => x * k; }\n"
            + "public static class C { public static Func<int, int> F(int k) => x => Hidden.Twice(x + k); }\n",
            "ExemptionSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));

        // A file-local type needs a file path to be emitted.
        SyntaxTree snippet = result.Compilation.SyntaxTrees.First();
        CSharpCompilation compilation = result.Compilation.ReplaceSyntaxTree(snippet, snippet.WithFilePath(fileName));
        using var image = new MemoryStream();
        Assert.True(compilation.Emit(image).Success, "the exemption snippet did not emit");
        image.Position = 0;
        var context = new System.Runtime.Loader.AssemblyLoadContext(nameof(FixtureTextGuard_ExemptsOnlyCompilerGeneratedTypes), isCollectible: true);
        try
        {
            Type[] types = context.LoadFromStream(image).GetTypes();
            Type fileLocal = types.Single(t => t.Name.EndsWith("__Hidden", StringComparison.Ordinal));
            Type inFileLocal = types.Single(t => t.DeclaringType == fileLocal && t.Name.StartsWith("<>", StringComparison.Ordinal));
            Type closure = types.Single(t => t.DeclaringType?.Name == "C" && t.Name.StartsWith("<>", StringComparison.Ordinal));
            Assert.True(!IsCompilerGenerated(fileLocal), $"a file-local type is exempt: {Escape(fileLocal.Name)}");
            Assert.True(!IsCompilerGenerated(inFileLocal), $"a closure in a file-local type is exempt: {Escape(inFileLocal.Name)}");
            Assert.True(IsCompilerGenerated(closure), $"a closure class is not exempt: {Escape(closure.Name)}");
        }
        finally
        {
            context.Unload();
        }
    }

    // Compiler-generated names are dropped, and so are parameterless and static constructors: the compiler emits
    // or elides them by rules reflection and Roslyn do not share (an implicit struct constructor is never emitted,
    // an empty static one is dropped). The `partial` rule, not this shape, keeps a constructor from another file.
    private static List<string> Comparable(IEnumerable<string> shape) => shape
        .Where(m => !m.Contains('<') && !m.EndsWith("..ctor/0", StringComparison.Ordinal) && !m.EndsWith("..cctor/0", StringComparison.Ordinal)
            && m != ".ctor/0" && m != ".cctor/0")
        .Order(StringComparer.Ordinal)
        .ToList();

    // Member shape as name/arity, so an added overload changes it; compiler-generated members are dropped by caller.
    private static IEnumerable<string> SourceShape(INamedTypeSymbol type, string path)
    {
        foreach (INamedTypeSymbol contract in type.AllInterfaces.Concat(type.BaseType is { } baseType ? new[] { baseType } : Array.Empty<INamedTypeSymbol>()))
        {
            yield return $"{path}:{(contract.ContainingNamespace.IsGlobalNamespace ? string.Empty : contract.ContainingNamespace + ".")}{contract.MetadataName}";
        }

        foreach (ISymbol member in type.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method:
                    yield return $"{path}{method.MetadataName}/{method.Parameters.Length}";
                    break;
                case IFieldSymbol field:
                    yield return path + field.MetadataName;
                    break;
                case INamedTypeSymbol nested:
                    foreach (string shape in SourceShape(nested, $"{path}{nested.MetadataName}."))
                    {
                        yield return shape;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<string> LoadedShape(Type type, string path)
    {
        foreach (Type contract in type.GetInterfaces().Concat(type.BaseType is { } baseType ? new[] { baseType } : Type.EmptyTypes))
        {
            yield return $"{path}:{(string.IsNullOrEmpty(contract.Namespace) ? string.Empty : contract.Namespace + ".")}{contract.Name}";
        }

        foreach (System.Reflection.MethodBase method in type.GetMethods(DeclaredMembers).Concat<System.Reflection.MethodBase>(type.GetConstructors(DeclaredMembers)))
        {
            yield return $"{path}{method.Name}/{method.GetParameters().Length}";
        }

        // An enum's `value__` and a field-like event's backing field have no symbol on the source side.
        foreach (System.Reflection.FieldInfo field in type.GetFields(DeclaredMembers)
            .Where(f => !f.IsSpecialName && type.GetEvent(f.Name, DeclaredMembers) is null))
        {
            yield return path + field.Name;
        }

        foreach (Type nested in type.GetNestedTypes(DeclaredMembers))
        {
            foreach (string shape in LoadedShape(nested, $"{path}{nested.Name}."))
            {
                yield return shape;
            }
        }
    }

    // Every evasion review found against the earlier guards, each in a method the guard must flag. Kept beside
    // the guard so a weakened rule fails here, not in review. `usings` lets a case add file-level directives.
    [Theory]
    [InlineData("", "Assert.True(ok, $\"{row.Value}\");")]
    [InlineData("", "Assert.True(ok, $@\"{row.Value}\");")]
    [InlineData("", "Assert.True(ok, @$\"{row.Value}\");")]
    [InlineData("", "Assert.True(ok, $\"{{{row.Value}}}\");")]
    [InlineData("", "Assert.True(ok, $\"{row.Value}}}\");")]
    [InlineData("", "Assert.True(ok, $\"\"\"{row.Value}\"\"\");")]
    [InlineData("", "Assert.True(ok, $$\"\"\"{{row.Value}}\"\"\");")]
    [InlineData("", "Assert.True(ok, $\"{Escape(\"\") + row.Value}\");")]
    [InlineData("", "Assert.True(ok, $\"{row.Value + string.Join(\",\", names.Select(Escape))}\");")]
    [InlineData("", "Assert.True(ok, $\"{string.Join(\",\", names)}\");")]
    [InlineData("", "string which = row.OnDiskDir; Assert.True(ok, $\"{which}\");")]
    [InlineData("", "Assert.True(ok, \"x\" + row.Value);")]
    [InlineData("", "string message = $\"{row.Value}\"; Assert.True(ok, message);")]
    [InlineData("", "Assert.True(ok, string.Format(\"{0}\", row.Value));")]
    [InlineData("", "Assert.Fail(row.Value);")]
    [InlineData("", "Assert.True(userMessage: row.Value, condition: ok);")]
    [InlineData("", "Assert.True(ok, new { Count = row.Value }.Count);")]
    [InlineData("", "Assert.Equal(\"a\", row.Value);")]
    [InlineData("", "Assert.Contains(row.Value, names);")]
    [InlineData("", "Xunit.Assert.Equal(\"a\", row.Value);")]
    [InlineData("", "global::Xunit.Assert.Equal(\"a\", row.Value);")]
    [InlineData("using static Xunit.Assert;", "Equal(\"a\", row.Value);")]
    [InlineData("using XA = Xunit.Assert;", "XA.Equal(\"a\", row.Value);")]
    [InlineData("", "Action<string> fail = Assert.Fail; fail(row.Value);")]
    [InlineData("", "throw new InvalidOperationException(row.Value);")]
    [InlineData("", "Exception e = new(row.Value); throw e;")]
    [InlineData("using Boom = System.InvalidOperationException;", "throw new Boom(row.Value);")]
    [InlineData("", "try { } catch (Exception problem) { Assert.Fail($\"{problem}\"); }")]
    [InlineData("", "Assert.True(ok, (from v in names select v).First());")]
    [InlineData("", "{ string Label = row.OnDiskDir; Assert.True(ok, $\"{Label}\"); }")]
    [InlineData("", "Assert.True(ok, $\"{(ok ? Label : row.Value)}\");")]
    [InlineData("", "Assert.True(ok, $\"{(ok ? row.Value : Label)}\");")]
    [InlineData("", "_ = new Dictionary<string, int>()[row.Value];")]
    [InlineData("", "_ = names.ToDictionary(n => n);")]
    [InlineData("", "Console.WriteLine(row.Value);")]
    [InlineData("", "string Escape(string s) => s; Assert.True(ok, Escape(row.Value));")]
    [InlineData("", "Func<string, string> Escape = s => s; Assert.True(ok, Escape(row.Value));")]
    [InlineData("", "AssertFixtureTextEquals(\"a\", row.Value, row.OnDiskDir);")]
    [InlineData("", "AssertFixtureTextEquals(what: row.OnDiskDir, expected: \"a\", actual: row.Value);")]
    [InlineData("", "AssertFixtureTextEquals(\"a\", row.Value, $\"{row.OnDiskDir} row\");")]
    [InlineData("", "Console.Out.WriteLine(row.Value);")]
    [InlineData("", "Console.Error.Write(row.Value);")]
    [InlineData("", "TextWriter w = Console.Out; w.WriteLine(row.Value);")]
    [InlineData("", "Environment.FailFast(row.Value);")]
    [InlineData("", "throw new ArgumentOutOfRangeException(\"p\", row.Value, \"m\");")]
    [InlineData("", "ArgumentOutOfRangeException.ThrowIfEqual(row.Value, \"x\");")]
    [InlineData("", "throw Xunit.Sdk.FailException.ForFailure(row.Value);")]
    [InlineData("sealed class Boom : System.Exception { public Boom(GoldenRow r) : base(r.Value) { } }", "throw new Boom(row);")]
    [InlineData("", "dynamic d = row.Value; Assert.True(ok, d);")]
    [InlineData("", "var d = new Dictionary<string, int>(); d.Add(row.Value, 1);")]
    [InlineData("", "_ = new Dictionary<string, int> { { row.Value, 1 } };")]
    [InlineData("", "_ = new Dictionary<string, string>(names.Select(n => new KeyValuePair<string, string>(n, n)));")]
    [InlineData("using System.Collections.Frozen;", "_ = names.ToFrozenDictionary(n => n);")]
    [InlineData("", "_ = new SortedList<string, int> { { row.Value, 1 } };")]
    [InlineData("", "_ = new Dictionary<object, int>()[row.Value];")]
    [InlineData("", "Action<string?, string?, string> f = AssertFixtureTextEquals; f(\"a\", row.Value, \"w\");")]
    [InlineData("static class Ext { public static System.Collections.Generic.IEnumerable<string> Select(this string[] s, System.Func<string?, string> f) => s; }", "Assert.True(ok, string.Join(\",\", names.Select(Escape)));")]
    [InlineData("", "\n#if NET\nAssert.Equal(\"a\", row.Value);\n#endif\n")]
    [InlineData("", "Assert.True(ok, System.Text.RegularExpressions.Regex.Escape(row.Value!));")]
    [InlineData("", "Assert.Contains(\"a\", names);")]
    [InlineData("", "Assert.True(ok, string.Join(\",\", names.Select(Path.GetFileName)));")]
    [InlineData("", "void Local(string text) => Assert.True(ok, text); Local(row.OnDiskDir);")]
    [InlineData("", "string Escape(string s) => s;")]
    [InlineData("", "var h = new System.Collections.Hashtable(); h.Add(row.Value!, 1);")]
    [InlineData("", "_ = new Dictionary<(string, int), int>()[(row.OnDiskDir, 1)];")]
    [InlineData("", "throw Xunit.Sdk.EqualException.ForMismatchedValues(\"a\", row.Value);")]
    [InlineData("sealed class Boom : System.Exception { public string? Detail { get; set; } public override string Message => Detail ?? string.Empty; }", "throw new Boom { Detail = row.Value };")]
    [InlineData("sealed class Boom : System.Exception { public string? D { get; set; } }", "Assert.True(ok, \"c\"); var b = new Boom(); b.D = row.Value; throw b;")]
    [InlineData("", "var d = new Dictionary<string, int>(); d[row.Value!] += 1;")]
    [InlineData("", "Assert.True(ok, Escape(row.Value)); throw new InvalidOperationException { Source = row.Value };")]
    [InlineData("", "Assert.True(ok, Escape(row.Value)); dynamic d = row.Value!; throw new InvalidOperationException(d);")]
    [InlineData("", "var d = new Dictionary<string, string?>(); d[row.Value!] ??= \"x\";")]
    [InlineData("sealed class H { public Dictionary<string, int> D { get; } = new(); }", "_ = new H { D = { { row.Value!, 1 } } };")]
    [InlineData("", "_ = new Dictionary<string, Dictionary<string, int>> { [\"a\"] = { { row.Value!, 1 } } };")]
    [InlineData("sealed class Leak { public static string Stash = \"\"; public static implicit operator string(Leak? l) => Stash; }", "Leak.Stash = row.Value!; Assert.True(ok, (Leak?)null);")]
    [InlineData("", "_ = System.Collections.Immutable.ImmutableDictionary.CreateRange(names.Select(n => new KeyValuePair<string, int>(n, 1)));")]
    [InlineData("", "Assert.True(ok, string.Join(row.Value, names.Select(Escape)));")]
    [InlineData("", "Action<string?> w = Console.WriteLine; w(row.Value);")]
    [InlineData("static class H { public static void AssertValueX(string what, string v) { what = v; Xunit.Assert.True(true, what); } }", "H.AssertValueX(\"k\", row.Value!);")]
    [InlineData("static class Factory { public static Exception Make(string m) => new InvalidOperationException(\"c\"); }", "throw Factory.Make(row.Value!);")]
    [InlineData("", "_ = Xunit.Sdk.ArgumentFormatter.Format(row.Value);")]
    [InlineData("", "System.Diagnostics.Debug.WriteLine(row.Value);")]
    [InlineData("", "TextWriter w = new StreamWriter(Stream.Null); w.Write(row.Value);")]
    [InlineData("static class W { public static void Put<T>(T w, string s) where T : TextWriter { w.Write(s); } }", "W.Put<TextWriter>(null!, row.Value!);")]
    [InlineData("static class O { public static void Put<T>(T o, string s) where T : Xunit.Abstractions.ITestOutputHelper => o.WriteLine(s); }", "O.Put<Xunit.Abstractions.ITestOutputHelper>(null!, row.Value!);")]
    [InlineData("sealed class Rows : TheoryData<int> { public Rows(string s) { } }", "_ = new Rows(row.Value!);")]
    [InlineData("", "ICollection<KeyValuePair<string, int>> c = new Dictionary<string, int>(); c.Add(new KeyValuePair<string, int>(row.Value!, 1));")]
    [InlineData("sealed class Leak { public static string S = \"\"; public static string operator +(Leak? l, int n) => S; }", "Leak.S = row.Value!; Assert.True(ok, (Leak?)null + 1);")]
    [InlineData("", "Console.Title = row.Value!;")]
    [InlineData("", "_ = new StreamWriter(Stream.Null);")]
    [InlineData("", "_ = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.AddRange(names.Select(n => new KeyValuePair<string, int>(n, 1)));")]
    [InlineData("", "Assert.True(ok, $\"{row.Value![0]}\");")]
    [InlineData("", "System.Diagnostics.Trace.WriteLine(row.Value);")]
    [InlineData("", "dynamic d = row.Value!; _ = new Dictionary<string, int>()[d];")]
    [InlineData("partial class P { }", "")]
    [InlineData("", "Assert.True(ok, $\"{(char)count}\");")]
    [InlineData("", "ArgumentOutOfRangeException.ThrowIfEqual(count, 1, $\"{row.Value}\");")]
    [InlineData("", "Assert.True(ok, $\"{(char?)row.Value![0]}\");")]
    [InlineData("static class G { public static void H<T>(T v) => Xunit.Assert.True(false, $\"{v}\"); }", "G.H(row.Value);")]
    [InlineData("using System.Collections.Immutable;", "_ = names.ToImmutableDictionary(n => n);")]
    [InlineData("using System.Collections.Immutable;", "_ = names.ToImmutableSortedDictionary(n => n, n => 1);")]
    [InlineData("static class Tg { public static int Tag<T>(IEnumerable<T> s) => 0; public static string Tag<T>(ReadOnlySpan<T> s) => \"\"; }", "Assert.True(ok, $\"{Tg.Tag(new[] { row.Value })}\");")]
    public void FixtureTextGuard_FlagsEveryKnownEvasion(string usings, string body)
    {
        GuardResult result = FindFixtureTextFindings(GuardSnippet(usings, body), "GuardSnippet");
        Assert.True(
            result.CompileErrors.Count == 0,
            $"the case {Escape(body)} does not compile, so it proves nothing: {string.Join("; ", result.CompileErrors.Select(Escape))}");
        // A REAL rule must fire: the coverage oracle alone ("checked no sink") would let a deleted rule stay green.
        Assert.True(
            result.Findings.Any(f => !f.Contains("the guard checked no sink", StringComparison.Ordinal)),
            $"the guard accepted {Escape(usings)} {Escape(body)}: [{string.Join("; ", result.Findings.Select(Escape))}]");
    }

    // The non-printing allow-list is exactly the members whose overloads take no value to print beside the message:
    // adding any other name (Equal, Single, Matches, …) would let its value-printing overloads through.
    [Fact]
    public void FixtureTextGuard_NonPrintingAssertionsAreExactlyTheMessageOnlyOnes()
    {
        Assert.True(
            NonPrintingAssertions.SetEquals(new[] { "True", "False", "Fail", "NotEmpty", "NotNull" }),
            $"the non-printing allow-list changed: [{string.Join(", ", NonPrintingAssertions.Order(StringComparer.Ordinal).Select(Escape))}]");
    }

    // ... and accepts the shapes this file actually relies on, so the rule is not simply "reject everything".
    [Theory]
    [InlineData("Assert.True(ok, $\"{Escape(row.Value)} {count} {names.Length} {Label}\");")]
    [InlineData("Assert.True(ok, $\"{(StringComparison)count} {ok} {(byte)count} {(sbyte)count} {(short)count} {(ushort)count}\");")]
    [InlineData("Assert.True(ok, $\"{(uint)count} {(long)count} {(ulong)count} {(float)count} {(double)count} {(decimal)count}\");")]
    [InlineData("Assert.True(ok, $\"[{string.Join(\", \", names.Select(Escape))}]\");")]
    [InlineData("Assert.True(ok, \"literal \" + Escape(row.Value));")]
    [InlineData("Assert.Fail($\"{count + 1} of {names.Length}\");")]
    [InlineData("long total = 3; Assert.True(ok, $\"{total}\");")]
    [InlineData("AssertFixtureTextEquals(\"a\", row.Value, $\"{Label} row\");")]
    [InlineData("Assert.True(ok, $\"{(ok ? Escape(row.Value) : Label)}\");")]
    [InlineData("Assert.True(ok, $\"{nameof(GoldenRow)}\");")]
    [InlineData("throw new InvalidOperationException(\"constant text\");")]
    [InlineData("_ = new Dictionary<string, int>()[\"literal\"];")]
    [InlineData("var d = new Dictionary<string, int>(); d[row.Value] = 1;")]
    [InlineData("Assert.NotEmpty(names);")]
    [InlineData("var d = new Dictionary<string, int>(StringComparer.Ordinal); d.Add(\"literal\", 1); _ = d[\"literal\"];")]
    [InlineData("_ = new Dictionary<int, int>()[count];")]
    [InlineData("throw new ArgumentOutOfRangeException(\"p\", count, \"constant\");")]
    [InlineData("if (!ok) throw new InvalidOperationException();")]
    [InlineData("_ = Record.Exception(() => { });")]
    [InlineData("using var w = new StringWriter(); w.Write(count);")]
    public void FixtureTextGuard_AcceptsTheSafeShapes(string body)
    {
        GuardResult result = FindFixtureTextFindings(GuardSnippet(string.Empty, body), "GuardSnippet");
        Assert.True(
            result.CompileErrors.Count == 0,
            $"the case {Escape(body)} does not compile: {string.Join("; ", result.CompileErrors.Select(Escape))}");
        Assert.True(
            result.Findings.Count == 0,
            $"the guard rejected {Escape(body)}: {string.Join("; ", result.Findings.Select(Escape))}");
    }

    // Escape is the sanitiser everything above relies on, so its OUTPUT is pinned, case by case. A [Fact] over
    // a table rather than a [Theory]: as theory data these inputs would land in the test's display name, which
    // is the very channel this file keeps fixture-shaped text out of.
    [Fact]
    public void Escape_RendersEveryUnsafeCodePointAsAnEscape()
    {
        (string? Input, string Expected)[] cases =
        {
            (null, "<null>"),
            ("<null>", "\"<null>\""),
            ("plain", "\"plain\""),
            ("a\u001B[31mb", "\"a\\u001B[31mb\""),
            ("\u0001\t", "\"\\u0001\\u0009\""),
            ("\u007F", "\"\\u007F\""),
            ("\u009B", "\"\\u009B\""),
            ("x\u202Ey", "\"x\\u202Ey\""),
            ("\u200B", "\"\\u200B\""),
            ("\u2028\u2029", "\"\\u2028\\u2029\""),
            ("\uD800", "\"\\uD800\""),
            ("\uDB40\uDC01", "\"\\U000E0001\""),
            ("back\\slash \"quote\"", "\"back\\\\slash \\\"quote\\\"\""),
            ("e\U0001F3AFmoji région 名前", "\"e\U0001F3AFmoji région 名前\""),
        };

        for (int i = 0; i < cases.Length; i++)
        {
            AssertFixtureTextEquals(cases[i].Expected, Escape(cases[i].Input), $"Escape case {i}");
        }
    }

    // Theory data in display-name position: each case is a theory the guard must refuse — text-typed
    // MemberData, MemberData resolved on another type, and a custom data attribute. Indexed rather than passed
    // as text, so this theory's own display names stay text-free.
    private static readonly string[] TheoryDataEvasions =
    {
        "    public static TheoryData<string> Rows() => new() { \"a\" };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows))]\n    public void T(string value) => Assert.True(value.Length > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows), MemberType = typeof(Other))]\n    public void T(string value) => Assert.True(value.Length > 0);\n",
        "    private sealed class Rows : Xunit.Sdk.DataAttribute\n    {\n"
        + "        public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo m) => new[] { new object[] { \"a\" } };\n    }\n"
        + "    [Theory]\n    [Rows]\n    public void T(string value) => Assert.True(value.Length > 0);\n",
        "    [Theory]\n    [ClassData(typeof(Other))]\n    public void T(string value) => Assert.True(value.Length > 0);\n",
        "    public sealed class Rows<TData> : IEnumerable<object[]>\n    {\n"
        + "        public IEnumerator<object[]> GetEnumerator() { yield return new object[] { \"a\" }; }\n"
        + "        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();\n    }\n"
        + "    public static Rows<int> Data() => new();\n"
        + "    [Theory]\n    [MemberData(nameof(Data))]\n    public void T(int value) => Assert.True(value > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    public static TheoryData<string> Rows(int n) => new() { \"a\" };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows), 1)]\n    public void T(string value) => Assert.True(value.Length > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    public static TheoryData<string> Rows(int n) => new() { \"a\" };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows), 1)]\n    public void T(int value) => Assert.True(value > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    public static IEnumerable<object[]> Rows(int n) { yield return new object[] { \"a\" }; }\n"
        + "    [Theory]\n    [MemberData(nameof(Rows), 1)]\n    public void T(int value) => Assert.True(value > 0);\n",
        "    public static TheoryData<string> Rows() => new() { \"a\" };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows))]\n    public void T(int value) => Assert.True(value > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows), MemberType = typeof(Other))]\n    public void T(int value) => Assert.True(value > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows))]\n    public void T(string value) => Assert.True(value.Length > 0);\n",
        "    [Theory]\n    [MemberData(nameof(Other.Rows))]\n    public void T(int value) => Assert.True(value > 0);\n",
        "    public static TheoryData<int> Rows() => new() { 1 };\n"
        + "    [Theory]\n    [MemberData(nameof(Rows), @MemberType = typeof(Other))]\n    public void T(int value) => Assert.True(value > 0);\n",
    };

    public static TheoryData<int> TheoryDataEvasionIndices()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < TheoryDataEvasions.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TheoryDataEvasionIndices))]
    public void FixtureTextGuard_FlagsTextBearingTheoryData(int index)
    {
        // The class declares Escape, so the only finding available is the theory-data rule itself.
        string source = "using Xunit;\n"
            + "public static class Other { public static TheoryData<string> Rows() => new() { \"a\" }; }\n"
            + "public sealed class C\n{\n    private static string Escape(string? s) => s ?? \"<null>\";\n"
            + TheoryDataEvasions[index] + "}\n";
        GuardResult result = FindFixtureTextFindings(source, "GuardSnippet");
        Assert.True(
            result.CompileErrors.Count == 0,
            $"theory-data case {index} does not compile: {string.Join("; ", result.CompileErrors.Select(Escape))}");
        Assert.True(
            result.Findings.Count > 0 && result.Findings.All(f => f.Contains("MemberData", StringComparison.Ordinal)
                || f.Contains("theory data", StringComparison.Ordinal)),
            $"theory-data case {index}: expected only theory-data findings, got [{string.Join("; ", result.Findings.Select(Escape))}]");
    }

    // A second declaration named Escape is rejected on its own, even where nothing calls either of them — so the
    // rule does not depend on a call site turning unsafe as a side effect.
    [Fact]
    public void FixtureTextGuard_RejectsASecondEscapeDeclaration()
    {
        GuardResult result = FindFixtureTextFindings(
            "public sealed class C\n{\n    private static string Escape(string? s) => s ?? \"<null>\";\n"
            + "    private static void M() { static string Escape(string s) => s; }\n}\n",
            "GuardSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));
        Assert.True(
            result.Findings.Any(f => f.Contains("declaration named Escape", StringComparison.Ordinal)),
            $"a second Escape declaration was not reported: [{string.Join("; ", result.Findings.Select(Escape))}]");
    }

    // MemberData resolves through base types: an inherited member — overload or property — that yields text must
    // be refused too. Shape 2 hides the base property behind a text-free method, so the lookup is not empty and only
    // the property's own type can decide.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FixtureTextGuard_FlagsTextBearingTheoryDataInheritedMember(int shape)
    {
        string inherited = shape == 0
            ? "public class B { public static TheoryData<string> Rows(int n) => new() { \"a\" }; }\n"
            : "public class B { public static IEnumerable<object[]> Rows => new[] { new object[] { \"a\" } }; }\n";
        string Source =
            "using Xunit;\n" + inherited
            + "public sealed class C : B\n{\n    private static string Escape(string? s) => s ?? \"<null>\";\n"
            + (shape != 1 ? "    public static new TheoryData<int> Rows() => new() { 1 };\n" : string.Empty)
            + "    [Theory]\n    [MemberData(nameof(Rows)" + (shape == 0 ? ", 1" : string.Empty) + ")]\n"
            + "    public void T(int value) => Assert.True(value > 0);\n}\n";
        GuardResult result = FindFixtureTextFindings(Source, "GuardSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));
        Assert.True(
            result.Findings.Any(f => f.Contains("MemberData", StringComparison.Ordinal)),
            $"an inherited text-yielding overload was not refused: [{string.Join("; ", result.Findings.Select(Escape))}]");
    }

    // ... and text-free data is accepted however it is declared — field, property, or inherited method — so the
    // member lookup is not simply "refuse anything that is not a method on this class".
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FixtureTextGuard_AcceptsTextFreeTheoryData(int shape)
    {
        string data = shape switch
        {
            0 => "    public static readonly TheoryData<int> Rows = new() { 1 };\n",
            1 => "    public static TheoryData<int> Rows => new() { 1 };\n",
            _ => string.Empty,
        };
        string source = "using Xunit;\n"
            + "public class B { public static TheoryData<int> Rows() => new() { 1 }; }\n"
            + "public sealed class C" + (shape == 2 ? " : B" : string.Empty)
            + "\n{\n    private static string Escape(string? s) => s ?? \"<null>\";\n" + data
            + "    [Theory]\n    [MemberData(nameof(Rows))]\n    public void T(int value) => Assert.True(value > 0);\n}\n";
        GuardResult result = FindFixtureTextFindings(source, "GuardSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));
        Assert.True(
            result.Findings.Count == 0,
            $"text-free theory data shape {shape} was refused: [{string.Join("; ", result.Findings.Select(Escape))}]");
    }

    // The per-member coverage oracle fires on its own: an assertion-shaped call the guard does not recognise as a
    // sink (here a look-alike Assert type) leaves its member with sites but no checked sink.
    [Fact]
    public void FixtureTextGuard_CoverageOracleReportsAMemberWithNoCheckedSink()
    {
        GuardResult result = FindFixtureTextFindings(
            "public static class MyAssert { public static void True(bool c, string m) { } }\n"
            + "public sealed class C\n{\n    private static string Escape(string? s) => s ?? \"<null>\";\n"
            + "    private static void M(string v) => MyAssert.True(true, v);\n}\n",
            "GuardSnippet");
        Assert.True(result.CompileErrors.Count == 0, string.Join("; ", result.CompileErrors.Select(Escape)));
        Assert.True(
            result.Findings.Any(f => f.Contains("the guard checked no sink in M", StringComparison.Ordinal)),
            $"the coverage oracle did not report M: [{string.Join("; ", result.Findings.Select(Escape))}]");
    }

    // The compile-error gate is what keeps every rule above from passing vacuously on a file that binds nothing.
    [Fact]
    public void FixtureTextGuard_ReportsCompileErrors()
    {
        GuardResult result = FindFixtureTextFindings("public sealed class C { void M() { undefined(); } }", "GuardSnippet");
        Assert.True(result.CompileErrors.Count > 0, "a source that does not compile reported no compile errors");
    }

    private static string GuardSnippet(string usings, string body) =>
        "using Xunit;\n" + usings + "\n"
        + "public sealed record GoldenRow(string? Value, string OnDiskDir, string AddPathSegment);\n"
        + "public sealed class C\n{\n"
        + "    private const string Label = \"k\";\n"
        + "    private static string Escape(string? s) => s ?? \"<null>\";\n"
        + "    private static void AssertFixtureTextEquals(string? expected, string? actual, string what) =>\n"
        + "        Assert.True(expected == actual, $\"{what}: {Escape(expected)} {Escape(actual)}\");\n"
        + "    public void M(GoldenRow row, string[] names, int count, bool ok)\n    {\n        " + body + "\n    }\n}\n";

    private sealed record GuardResult(
        List<string> Findings, List<string> CompileErrors, int SinksChecked, int EscapeCalls, CSharpCompilation Compilation);

    private const string ImplicitUsings =
        "global using System;\nglobal using System.Collections.Generic;\nglobal using System.IO;\nglobal using System.Linq;\n"
        + "global using System.Net.Http;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n";

    // Every assembly the test host can load, except this test assembly itself (the file under check is
    // compiled from source, and its own compiled copy would collide with it).
    private static readonly Lazy<List<MetadataReference>> GuardReferences = new(() =>
    {
        string self = typeof(PartitionEncodingGoldenDifferentialTests).Assembly.Location;
        string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        return tpa.Split(Path.PathSeparator)
            .Where(path => path.Length > 0 && File.Exists(path) && !string.Equals(path, self, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    });

    /// <summary>
    /// The rule, decided on SYMBOLS and TYPES over a compilation of the source, so that spelling cannot route
    /// around it. These are the channels it checks — the ways this file's OWN code can put text on the CI log —
    /// and what each must receive:
    /// <list type="bullet">
    /// <item>every string argument of an <c>Xunit.Assert</c> member (any spelling); any member other than
    /// True/False/Fail/NotEmpty/NotNull is itself a finding, and so is an <c>Xunit.Assert</c> method group;</item>
    /// <item>the <c>what</c> argument of every <c>AssertFixture*</c>/<c>AssertValue*</c> helper, matched by
    /// parameter, not position; a helper used as a method group, or a vetted parameter reassigned by direct
    /// assignment, is a finding;</item>
    /// <item>every argument and object-initializer value of an exception construction (explicit, target-typed or
    /// aliased), every argument of a constructor initializer on an exception type, and of any method declared on,
    /// or returning, an exception type (the <c>ThrowIf*</c> helpers, factories) — except a lambda or method-group
    /// argument (<c>Record.Exception(() =&gt; …)</c>), which is code rather than text and is checked where it runs;
    /// a method group of such a member is a finding;</item>
    /// <item>the key of a dictionary READ or <c>Add</c> (including a collection initializer and
    /// <c>ICollection&lt;KeyValuePair&gt;.Add</c>) — both echo it on failure; dictionary constructors from a
    /// collection, <c>CreateRange</c>/<c>AddRange</c> on dictionary types and the <c>To…Dictionary</c>
    /// conversions are findings outright, since a duplicate key is echoed;</item>
    /// <item>any member of Console, Debug, Trace, ITestOutputHelper or a TextWriter, Environment.FailFast, any
    /// <c>Xunit.Sdk</c> member and any <c>dynamic</c> operation is a finding outright (the output writers are an
    /// enumerated list, not every API that can reach a stream — see the residual; creating an in-memory
    /// StringWriter, or calling on a receiver typed exactly as one, is exempt);</item>
    /// <item>theory data becomes the display name: <c>[MemberData]</c> must yield only text-free values and may
    /// not name a <c>MemberType</c>; any other data attribute than InlineData/MemberData, and any TheoryData
    /// subclass declared here, is a finding;</item>
    /// <item>a conditional-compilation directive is a finding: code it disables here could be enabled by the
    /// real build's symbols and would ship unchecked;</item>
    /// <item>an exception type declared in this file is a finding: its Message could read a property or static
    /// field set after construction, which no argument sink sees;</item>
    /// <item>a <c>partial</c> type is a finding: its other parts compile into the class from files the guard does
    /// not see (FixtureTextGuard_ChecksTheClassThatRuns separately compares the compiled and loaded members, base
    /// type and interfaces, and FixtureTextGuard_CallsNothingElseInTheTestAssembly refuses a base type, attribute
    /// or call bound to code in the test assembly outside the class; a part adding only a constructor body or a
    /// framework attribute changes neither, so this rule is the one that stops it).</item>
    /// </list>
    /// A sink value is safe only if it is a compile-time constant; a number, boolean or enum (no text can ride
    /// on it under an unmutated culture); a call to THE <see cref="Escape"/> method (by symbol — and no other declaration may be named
    /// Escape); <c>string.Join(safe, xs.Select(Escape))</c> with LINQ's Select; a helper's own vetted
    /// <c>what</c> parameter; or an interpolation, concatenation, conditional or parenthesis built only from
    /// those. Anything else — a string local, a parameter, a property, a call — is a finding.
    /// <para>STATED RESIDUAL: text a library composes from its own input when it fails — the Parse family, file,
    /// encoding and regex APIs, System.Text.Json, Parquet.Net — and reflection. Those cannot be enumerated
    /// completely; this file does not feed fixture text to them in a failure path. Also outside the rule, as
    /// own-code shapes judged contrived here: reassigning `what` by ref, out or deconstruction; duplicate keys
    /// through KeyedCollection.Add, OrderedDictionary.Insert/SetAt or a dictionary's GetAlternateLookup indexer;
    /// culture-dependent number text (a mutated NumberFormat.NegativeSign makes a number print text); reaching a
    /// stream through Unsafe.As or a file handle; per-site (rather than per-member) coverage; an embedded copy that
    /// is not the compiled file yet declares the same member names and arities (a project-file edit; bodies,
    /// signature types and attributes could differ); a call here rebound by another file, through a global using,
    /// to a member OUTSIDE the test assembly (the framework or a project reference); and a constant (enum member or
    /// const field) rebound by a global using ALIAS in another file, which is inlined and leaves no runtime trace.
    /// All of it is tracked in #917.</para>
    /// <para>What bounds that residual: rebinding to code INSIDE the test assembly is refused by
    /// FixtureTextGuard_CallsNothingElseInTheTestAssembly, and a shadowing type or extension class by
    /// FixtureTextGuard_NothingShadowsANameThisFileBinds; both rest on a trusted core that binds nothing another file
    /// can redefine (FixtureTextGuard_TrustedCoreBindsNothingRebindable), interceptors are switched off for this
    /// project, and the guard's facts report through `global::Xunit.Assert`. The compiler options are
    /// `global::`-qualified, and the load-bearing rule constants (TypeKind, SpecialType, the `#if` and `partial`
    /// SyntaxKinds, DiagnosticSeverity, BinaryOperatorKind) are pinned by self-tests running this same compiled code;
    /// `#elif`/`#else` cannot appear without `#if`, and `#define`/`#undef` act the same in the guard and the build. A StringComparison alias can
    /// only loosen a name match (case or culture), which widens what is checked rather than exempting anything. A
    /// same-named type in an IMPORTED namespace that this file names needs a configuration edit (CS0436 disabled);
    /// a well-known type the compiler binds without a name (MemoryMarshal, ValueTuple, Index…) is a type in the test
    /// assembly as soon as it is redeclared there, so any use of it — from this class or from a compiler-generated
    /// helper it calls, whose body is walked too — is refused by the IL fact; and an interceptor needs interceptors
    /// re-enabled, like the project-file case. Code in the test assembly that runs unprompted (a module initializer)
    /// and rewrites this class's private state by reflection is runtime tampering, not a rebinding, and is out of
    /// scope; the guard keeps no shared mutable state for it to rewrite.</para>
    /// </summary>
    private static GuardResult FindFixtureTextFindings(string source, string assemblyName)
    {
        // The compiler options are written `global::`-qualified: a global using alias or a same-named type in another
        // file cannot redefine them (an older language version binds some calls to different overloads).
        var parseOptions = new CSharpParseOptions(global::Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree, CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions) },
            GuardReferences.Value,
            new CSharpCompilationOptions(
                global::Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: global::Microsoft.CodeAnalysis.NullableContextOptions.Enable, allowUnsafe: true));
        List<string> compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        SemanticModel model = compilation.GetSemanticModel(tree);
        SyntaxNode root = tree.GetRoot();
        var findings = new List<string>();
        int sinks = 0;
        int escapeCalls = 0;

        void Finding(SyntaxNode node, string what) =>
            findings.Add($"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {what}: {node}");

        // The guard parses with no preprocessor symbols, while the real build defines NET, RELEASE, TRACE, … —
        // so a block it sees as disabled text could ship enabled. No such block may exist here.
        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.IfDirectiveTrivia) || trivia.IsKind(SyntaxKind.ElifDirectiveTrivia)
                || trivia.IsKind(SyntaxKind.ElseDirectiveTrivia) || trivia.IsKind(SyntaxKind.DefineDirectiveTrivia)
                || trivia.IsKind(SyntaxKind.UndefDirectiveTrivia))
            {
                findings.Add($"line {trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: conditional compilation hides code from the guard");
            }
        }

        // The guard compiles one file; a partial type could put code in another file that joins the same class.
        foreach (BaseTypeDeclarationSyntax type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (type.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                findings.Add($"line {type.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: partial type "
                    + $"{type.Identifier.Text}: its other parts are outside the guard");
            }
        }

        // THE escaping function: the one declaration named Escape, which must be a method. A second declaration
        // of the name — a local function, a delegate, a parameter — could stand in for it at a call site.
        List<SyntaxNode> escapeDeclarations = root.DescendantNodes().Where(n => n switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text == nameof(Escape),
            LocalFunctionStatementSyntax f => f.Identifier.Text == nameof(Escape),
            VariableDeclaratorSyntax v => v.Identifier.Text == nameof(Escape),
            ParameterSyntax p => p.Identifier.Text == nameof(Escape),
            PropertyDeclarationSyntax p => p.Identifier.Text == nameof(Escape),
            SingleVariableDesignationSyntax d => d.Identifier.Text == nameof(Escape),
            _ => false,
        }).ToList();
        IMethodSymbol? escape = escapeDeclarations is [MethodDeclarationSyntax only] ? model.GetDeclaredSymbol(only) : null;
        if (escape is null)
        {
            findings.Add($"expected exactly one declaration named {nameof(Escape)}, a method; found {escapeDeclarations.Count}");
        }

        // The `what` parameter of each helper. Every call site's argument for it is checked as a sink, so
        // inside the helper the parameter itself is safe to print.
        var vettedParameters = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.Text.StartsWith("AssertFixture", StringComparison.Ordinal)
                || method.Identifier.Text.StartsWith("AssertValue", StringComparison.Ordinal))
            {
                foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
                {
                    if (parameter.Identifier.Text == "what" && model.GetDeclaredSymbol(parameter) is { } symbol)
                    {
                        vettedParameters.Add(symbol);
                    }
                }
            }
        }

        bool IsEscape(IMethodSymbol method) =>
            escape is not null && SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, escape);

        bool IsVettedHelper(IMethodSymbol method) =>
            method.OriginalDefinition.Parameters.Any(p => vettedParameters.Contains(p));

        bool IsSafe(IOperation operation)
        {
            if (operation.ConstantValue.HasValue || IsTextFree(operation.Type))
            {
                return true;
            }

            return operation switch
            {
                IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion => IsSafe(conversion.Operand),
                IParenthesizedOperation parenthesized => IsSafe(parenthesized.Operand),
                IInterpolatedStringOperation interpolated => interpolated.Parts.All(part => part switch
                {
                    IInterpolatedStringTextOperation => true,
                    IInterpolationOperation hole => IsSafe(hole.Expression), // alignment and format are constants by C# rule
                    _ => false,
                }),
                // A user-defined operator is a call, not a concatenation: it can return any text.
                IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, OperatorMethod: null } binary =>
                    IsSafe(binary.LeftOperand) && IsSafe(binary.RightOperand),
                IConditionalOperation { WhenFalse: { } whenFalse } conditional => IsSafe(conditional.WhenTrue) && IsSafe(whenFalse),
                IInvocationOperation call when IsEscape(call.TargetMethod) => true,
                IInvocationOperation call => IsJoinOfEscaped(call),
                IParameterReferenceOperation parameter => vettedParameters.Contains(parameter.Parameter.OriginalDefinition),
                _ => false,
            };
        }

        // string.Join(<safe separator>, <anything>.Select(Escape)) — every element passes through Escape, and
        // Select is LINQ's (a user-defined Select could ignore its selector).
        bool IsJoinOfEscaped(IInvocationOperation call) =>
            call.TargetMethod is { Name: "Join", ContainingType.SpecialType: SpecialType.System_String }
            && call.Arguments.Length == 2
            && IsSafe(call.Arguments[0].Value)
            && Unwrap(call.Arguments[1].Value) is IInvocationOperation { TargetMethod.Name: "Select" } select
            && select.TargetMethod.ContainingType?.ToDisplayString() == "System.Linq.Enumerable"
            && select.Arguments.Length == 2
            && Unwrap(select.Arguments[1].Value) is IDelegateCreationOperation { Target: IMethodReferenceOperation reference }
            && IsEscape(reference.Method);

        var sinksByMember = new Dictionary<SyntaxNode, int>();
        var initializerAddsChecked = new HashSet<IOperation>();

        void Sink(IOperation value, string what)
        {
            sinks++;
            if (value.Syntax.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault() is { } owner)
            {
                sinksByMember[owner] = sinksByMember.GetValueOrDefault(owner) + 1;
            }

            if (!IsSafe(value))
            {
                Finding(value.Syntax, what);
            }
        }

        void SinkAll(ImmutableArray<IArgumentOperation> arguments, string what)
        {
            foreach (IArgumentOperation argument in arguments)
            {
                Sink(argument.Value, what);
            }
        }

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            IOperation? operation = model.GetOperation(node);
            if (operation is null || operation.Syntax != node)
            {
                continue;
            }

            switch (operation)
            {
                case IDynamicInvocationOperation or IDynamicMemberReferenceOperation
                    or IDynamicObjectCreationOperation or IDynamicIndexerAccessOperation:
                    Finding(node, "dynamic operation (unchecked binding)");
                    break;
                case IInvocationOperation call:
                    CheckInvocation(call);
                    break;
                case IObjectCreationOperation creation when IsException(creation.Type):
                    SinkAll(creation.Arguments, "exception argument");
                    foreach (IOperation initializer in creation.Initializer?.Initializers ?? ImmutableArray<IOperation>.Empty)
                    {
                        Sink(initializer is IAssignmentOperation assigned ? assigned.Value : initializer, "exception initializer");
                    }

                    break;
                case IObjectCreationOperation creation when IsDictionary(creation.Type)
                    && creation.Arguments.Any(a => a.Parameter?.Type is INamedTypeSymbol { Name: "IEnumerable" or "IDictionary" or "IReadOnlyDictionary" }):
                    Finding(node, "dictionary built from a collection (a duplicate key is echoed)");
                    break;
                case IObjectCreationOperation creation when IsChecked(creation.Type) && creation.Type?.ToDisplayString() != "System.IO.StringWriter":
                    Finding(node, "use of an output writer or Xunit.Sdk type");
                    break;
                case IMemberReferenceOperation member when member is not IMethodReferenceOperation && IsChecked(member.Member.ContainingType):
                    Finding(node, "use of an output writer or Xunit.Sdk member");
                    break;
                case IMethodReferenceOperation reference:
                    CheckMethodGroup(node, reference.Method);
                    break;
                case IAssignmentOperation { Target: IParameterReferenceOperation target }
                    when vettedParameters.Contains(target.Parameter.OriginalDefinition):
                    Finding(node, "a vetted `what` parameter is reassigned");
                    break;
            }

            // A collection initializer's Add calls are implicit and do not surface on their own syntax node —
            // including those nested in a member or index initializer (`new H { D = { { k, 1 } } }`).
            if (operation is IObjectCreationOperation { Initializer: { } collection })
            {
                foreach (IInvocationOperation add in collection.Descendants().OfType<IInvocationOperation>().Where(i => i.IsImplicit))
                {
                    // An outer creation reaches a nested initializer's Adds too; check each one once.
                    if (initializerAddsChecked.Add(add))
                    {
                        CheckInvocation(add);
                    }
                }
            }

            if (operation is IPropertyReferenceOperation { Property.IsIndexer: true } indexer
                && IsDictionary(indexer.Property.ContainingType)
                && !(indexer.Parent is ISimpleAssignmentOperation assignment && assignment.Target == indexer)) // `+=` and `??=` read first
            {
                Sink(indexer.Arguments[0].Value, "dictionary key (KeyNotFoundException echoes it)");
            }
        }

        void CheckMethodGroup(SyntaxNode node, IMethodSymbol method)
        {
            if (method.ContainingType?.ToDisplayString() == "Xunit.Assert")
            {
                Finding(node, "Xunit.Assert member used as a delegate");
            }
            else if (IsVettedHelper(method))
            {
                Finding(node, "assertion helper used as a delegate (its `what` would go unchecked)");
            }
            else if (IsChecked(method.ContainingType) || IsException(method.ContainingType) || IsException(method.ReturnType))
            {
                Finding(node, "output-writer or exception member used as a delegate");
            }
        }

        void CheckInvocation(IInvocationOperation call)
        {
            IMethodSymbol target = call.TargetMethod;
            string containing = target.ContainingType?.ToDisplayString() ?? string.Empty;
            if (IsEscape(target))
            {
                escapeCalls++;
            }

            if (containing == "Xunit.Assert")
            {
                if (!NonPrintingAssertions.Contains(target.Name))
                {
                    Finding(call.Syntax, "value-printing assertion Assert." + target.Name);
                    return;
                }

                foreach (IArgumentOperation argument in call.Arguments)
                {
                    if (argument.Parameter?.Type.SpecialType == SpecialType.System_String)
                    {
                        Sink(argument.Value, "assertion message");
                    }
                }
            }
            else if ((IsChecked(target.ContainingType) && call.Instance?.Type?.ToDisplayString() != "System.IO.StringWriter")
                || (containing == "System.Environment" && target.Name == "FailFast"))
            {
                Finding(call.Syntax, "write to an output stream, or use of Xunit.Sdk");
            }
            else if (DictionaryConversions.Contains(target.Name)
                || (target.Name is "CreateRange" or "AddRange" && containing.Contains("Dictionary", StringComparison.Ordinal)))
            {
                Finding(call.Syntax, "a duplicate key would be echoed by the exception");
            }
            else if (IsException(target.ContainingType) || IsException(target.ReturnType))
            {
                // Throw helpers, factories, and `: base(...)` initializers of exception types. A lambda argument
                // (`Record.Exception(() => …)`) is code, not text: what it does is checked where it does it.
                foreach (IArgumentOperation argument in call.Arguments)
                {
                    if (Unwrap(argument.Value) is not (IAnonymousFunctionOperation or IDelegateCreationOperation))
                    {
                        Sink(argument.Value, "exception argument");
                    }
                }
            }
            else if (target.Name == "Add" && call.Arguments.Length > 0
                && (IsDictionary(target.ContainingType)
                    || target.ContainingType is { Name: "ICollection", TypeArguments: [INamedTypeSymbol { Name: "KeyValuePair" }] }))
            {
                Sink(call.Arguments[0].Value, "dictionary key (a duplicate is echoed)");
            }
            else
            {
                foreach (IArgumentOperation argument in call.Arguments)
                {
                    if (argument.Parameter is { } parameter && vettedParameters.Contains(parameter.OriginalDefinition))
                    {
                        Sink(argument.Value, "assertion description");
                    }
                }
            }
        }

        // An exception type declared here could carry text into its Message through a property or static field set
        // after construction, which no argument sink sees. The file needs none, so declaring one is a finding.
        foreach (BaseTypeDeclarationSyntax declared in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            INamedTypeSymbol? declaredType = model.GetDeclaredSymbol(declared);
            if (IsException(declaredType))
            {
                Finding(declared, "an exception type declared in this file (its Message can be set out of sight)");
            }
            else if (declaredType?.BaseType?.OriginalDefinition.ToDisplayString().StartsWith("Xunit.TheoryData", StringComparison.Ordinal) == true)
            {
                Finding(declared, "a TheoryData subclass declared in this file (AddRow can feed text to display names)");
            }
        }

        // Theory data becomes the test's display name, which xUnit escapes only for C0 controls.
        foreach (AttributeSyntax attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            INamedTypeSymbol? attributeType = model.GetTypeInfo(attribute).Type as INamedTypeSymbol;
            string? name = attributeType?.ToDisplayString();
            if (name == "Xunit.MemberDataAttribute")
            {
                CheckMemberData(attribute);
            }
            else if (name != "Xunit.InlineDataAttribute" && DerivesFrom(attributeType, "Xunit.Sdk.DataAttribute"))
            {
                Finding(attribute, "theory data attribute other than InlineData/MemberData reaches the display name unchecked");
            }
        }

        void CheckMemberData(AttributeSyntax attribute)
        {
            if (attribute.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.Identifier.ValueText == "MemberType") == true)
            {
                Finding(attribute, "MemberData with a MemberType is resolved outside this check");
                return;
            }

            if (attribute.Parent?.Parent is not MethodDeclarationSyntax theory || model.GetDeclaredSymbol(theory) is not { } theorySymbol)
            {
                Finding(attribute, "MemberData that is not on a method");
                return;
            }

            string? member = attribute.ArgumentList?.Arguments.Count > 0
                ? model.GetConstantValue(attribute.ArgumentList.Arguments[0].Expression).Value as string
                : null;
            // EVERY member of that name must qualify — arguments to MemberData can select any overload, and xUnit
            // looks through base types for it.
            var data = new List<ITypeSymbol?>();
            for (INamedTypeSymbol? owner = theorySymbol.ContainingType; owner is not null; owner = owner.BaseType)
            {
                data.AddRange(owner.GetMembers(member ?? string.Empty)
                    .Select(m => m switch { IMethodSymbol method => method.ReturnType, IPropertySymbol property => property.Type, IFieldSymbol field => field.Type, _ => null }));
            }

            // The declared data type is what bounds the display name: xUnit renders the RAW row values, before any
            // conversion to the parameter types, so an int parameter does not stop string data from printing. The
            // parameter check is defense in depth on top of it.
            if (!theorySymbol.Parameters.All(p => IsTextFree(p.Type))
                || data.Count == 0
                || !data.All(t => t is INamedTypeSymbol { TypeArguments: { Length: > 0 } arguments } named
                    && named.OriginalDefinition.ToDisplayString().StartsWith("Xunit.TheoryData<", StringComparison.Ordinal)
                    && arguments.All(IsTextFree)))
            {
                Finding(attribute, "MemberData must yield only numbers, booleans or enums (the display name)");
            }
        }

        // An independent coverage oracle, so the guard cannot silently stop seeing part of the file: every member
        // that SYNTACTICALLY contains an assertion with a message, a helper call or an exception construction must
        // have contributed at least one checked sink. (A floor on the total could not catch one skipped member.)
        foreach (MemberDeclarationSyntax member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)
            {
                continue;
            }

            // Only assertions that carry a message are sites: Assert.True(cond) binds an overload with no text.
            int sites = member.DescendantNodes().Count(n => n switch
            {
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "True" or "False" } } call =>
                    call.ArgumentList.Arguments.Count >= 2,
                InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Fail" } } => true,
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: var name } } =>
                    name.StartsWith("AssertFixture", StringComparison.Ordinal) || name.StartsWith("AssertValue", StringComparison.Ordinal),
                // `throw new X()` with nothing to print is not a site.
                BaseObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation).Type is { } type && IsException(type)
                    && (creation.ArgumentList?.Arguments.Count > 0 || creation.Initializer is not null),
                _ => false,
            });
            if (sites > 0 && sinksByMember.GetValueOrDefault(member) == 0)
            {
                string name = member switch
                {
                    MethodDeclarationSyntax method => method.Identifier.Text,
                    PropertyDeclarationSyntax property => property.Identifier.Text,
                    ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
                    _ => member.Kind().ToString(),
                };
                findings.Add($"line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: the guard checked no "
                    + $"sink in {name}, which has {sites} assertion or exception site(s)");
            }
        }

        return new GuardResult(findings, compileErrors, sinks, escapeCalls, compilation);
    }

    // Conversions that throw on a duplicate key with the key in the message. (ToLookup groups duplicates instead.)
    private static readonly HashSet<string> DictionaryConversions = new(StringComparer.Ordinal)
    {
        "ToDictionary", "ToFrozenDictionary", "ToImmutableDictionary", "ToImmutableSortedDictionary", "ToSortedDictionary",
    };

    private static IOperation Unwrap(IOperation operation) => operation switch
    {
        IConversionOperation { IsImplicit: true } conversion => Unwrap(conversion.Operand),
        IParenthesizedOperation parenthesized => Unwrap(parenthesized.Operand),
        IArgumentOperation argument => Unwrap(argument.Value),
        _ => operation,
    };

    // A value of one of these types cannot carry text (char can: it is one code point, ESC included).
    private static bool IsTextFree(ITypeSymbol? type) => type is not null
        && (type.TypeKind == TypeKind.Enum
            || type.SpecialType is SpecialType.System_Boolean
                or SpecialType.System_SByte or SpecialType.System_Byte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal);

    private static bool DerivesFrom(ITypeSymbol? type, string baseType)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsException(ITypeSymbol? type) => DerivesFrom(type, "System.Exception");

    // An output writer (its members write to a stream the CI log may capture) or an Xunit.Sdk type (its
    // exception factories and assertion internals print their operands).
    private static bool IsChecked(ITypeSymbol? type) => type is not null
        && (OutputWriterTypes.Contains(type.ToDisplayString())
            || DerivesFrom(type, "System.IO.TextWriter")
            || type.ContainingNamespace?.ToDisplayString().StartsWith("Xunit.Sdk", StringComparison.Ordinal) == true);

    private static bool IsDictionary(ITypeSymbol? type) => type is INamedTypeSymbol named
        && new[] { named }.Concat(named.AllInterfaces).Any(t =>
            t.ToDisplayString() == "System.Collections.IDictionary"
            || (t.Name is "IDictionary" or "IReadOnlyDictionary" && t.TypeArguments.Length == 2));
}
