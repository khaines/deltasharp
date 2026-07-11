using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeltaSharp.Types;
using Parquet;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #487 end-to-end tests for the Delta write door: they drive the PUBLIC Core API
/// (<see cref="DataFrame.Write"/> → <c>Format("delta")</c> → <c>Save(path)</c>) all the way to a real
/// Delta table on disk (Parquet data files + a <c>_delta_log</c>) via the executor's Delta sink and the
/// public Storage write facade. Each test reads the committed <c>_delta_log/*.json</c> back off disk and
/// asserts the version, <c>add</c>/<c>remove</c> actions, <c>dataChange</c>, partition values, row counts
/// (from the add <c>stats</c>), and the on-disk Parquet/partition-directory layout. The row-value multiset
/// round-trip (through the internal <c>DeltaLog</c>/<c>ParquetFileReader</c>) is proven in
/// <c>DeltaSharp.Storage.Tests.Writing.DeltaWriteTargetTests</c>; the executor layer cannot see those
/// Storage-internal readers (and the delta READ path is #499), so here we assert the on-disk Delta
/// structure the full API produced.
/// </summary>
[Collection(SessionExecutionTestCollection.Name)]
public sealed class DeltaWriteDoorEndToEndTests : IDisposable
{
    private static readonly StructType PeopleSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
    });

    // A partitioned table: region is the partition column.
    private static readonly StructType RegionSchema = new(new[]
    {
        new StructField("id", IntegerType.Instance, nullable: false),
        new StructField("name", StringType.Instance, nullable: true),
        new StructField("region", StringType.Instance, nullable: true),
    });

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "delta-writedoor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string Table(string name) => Path.Combine(_root, name);

    private static SparkSession NewSession()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
        return SparkSession.Builder().AppName("delta-write-door-e2e").GetOrCreate();
    }

    private static IReadOnlyList<Row> People() => new[]
    {
        new Row(PeopleSchema, 1, "alice"),
        new Row(PeopleSchema, 2, "bob"),
        new Row(PeopleSchema, 3, null),
    };

    private static IReadOnlyList<Row> Regional(params (int Id, string? Name, string? Region)[] rows) =>
        rows.Select(r => new Row(RegionSchema, r.Id, r.Name, r.Region)).ToList();

    // ---------------------------------------------------------------- append (create v0, then v1)

    [Fact]
    public void Delta_Append_ToFreshPath_CreatesVersion0_WithAddActions()
    {
        using SparkSession spark = NewSession();
        string table = Table("append0");
        DataFrame df = spark.CreateDataFrame(People(), PeopleSchema);

        df.Write.Format("delta").Mode("append").Save(table);

        Assert.True(File.Exists(CommitFile(table, 0)));
        IReadOnlyList<AddRecord> active = ActiveAdds(table);
        AddRecord add = Assert.Single(active);
        Assert.True(add.DataChange);
        Assert.Empty(add.PartitionValues);
        Assert.Equal(3, add.NumRecords);
        Assert.True(File.Exists(Path.Combine(table, add.Path)));
    }

    [Fact]
    public void Delta_Append_Twice_CreatesV0ThenV1_AndAccumulatesFiles()
    {
        string table = Table("append-twice");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            DataFrame more = spark.CreateDataFrame(
                new[] { new Row(PeopleSchema, 4, "dan") }, PeopleSchema);
            more.Write.Format("delta").Mode("append").Save(table);
        }

        Assert.True(File.Exists(CommitFile(table, 0)));
        Assert.True(File.Exists(CommitFile(table, 1)));
        IReadOnlyList<AddRecord> active = ActiveAdds(table);
        Assert.Equal(2, active.Count); // one file per append, both still active
        Assert.Equal(4, active.Sum(a => a.NumRecords));
    }

    // ---------------------------------------------------------------- static overwrite

    [Fact]
    public void Delta_StaticOverwrite_RemovesPriorFiles_AndAddsNew()
    {
        string table = Table("static-overwrite");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            DataFrame replacement = spark.CreateDataFrame(
                new[] { new Row(PeopleSchema, 99, "zoe") }, PeopleSchema);
            replacement.Write.Format("delta").Mode("overwrite").Save(table);
        }

        Assert.True(File.Exists(CommitFile(table, 1)));
        IReadOnlyList<AddRecord> active = ActiveAdds(table);
        AddRecord add = Assert.Single(active);
        Assert.Equal(1, add.NumRecords);
        // The v1 commit removed the v0 file (full static overwrite).
        Assert.True(HasRemove(table, 1));
    }

    // ---------------------------------------------------------------- dynamic partition overwrite

    [Fact]
    public void Delta_DynamicPartitionOverwrite_ReplacesOnlyTouchedPartitions()
    {
        string table = Table("dynamic-overwrite");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(
                    Regional((1, "a", "US"), (2, "b", "EU")), RegionSchema)
                .Write.Format("delta").PartitionBy("region").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(Regional((9, "z", "US")), RegionSchema)
                .Write.Format("delta")
                .PartitionBy("region")
                .Option("spark.sql.sources.partitionOverwriteMode", "dynamic")
                .Mode("overwrite")
                .Save(table);
        }

        IReadOnlyList<AddRecord> active = ActiveAdds(table);
        // EU (untouched) survives; US is replaced with the single new file.
        Dictionary<string, List<AddRecord>> byRegion = active
            .GroupBy(a => a.PartitionValues.TryGetValue("region", out string? rv) ? rv ?? "<null>" : "<none>")
            .ToDictionary(g => g.Key, g => g.ToList());

        Assert.True(byRegion.ContainsKey("EU"));
        Assert.True(byRegion.ContainsKey("US"));
        Assert.Equal(1, byRegion["EU"].Sum(a => a.NumRecords)); // original EU row preserved
        Assert.Equal(1, byRegion["US"].Sum(a => a.NumRecords)); // US replaced (still 1, but the new row)

        // Every active add lives under its Hive-style partition directory.
        foreach (AddRecord add in active)
        {
            Assert.Contains("region=", add.Path);
            Assert.True(File.Exists(Path.Combine(table, add.Path)));
        }
    }

    // ---------------------------------------------------------------- partitioned append layout

    [Fact]
    public void Delta_PartitionedAppend_WritesHiveStyleDirectories()
    {
        using SparkSession spark = NewSession();
        string table = Table("partitioned");
        DataFrame df = spark.CreateDataFrame(
            Regional((1, "a", "US"), (2, "b", "EU"), (3, "c", "US")), RegionSchema);

        df.Write.Format("delta").PartitionBy("region").Mode("append").Save(table);

        IReadOnlyList<AddRecord> active = ActiveAdds(table);
        Assert.Equal(2, active.Count); // one file per partition (US, EU)
        Assert.Equal(
            new[] { "EU", "US" },
            active.Select(a => a.PartitionValues["region"]).OrderBy(v => v, StringComparer.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(table, "region=US")));
        Assert.True(Directory.Exists(Path.Combine(table, "region=EU")));
        Assert.Equal(3, active.Sum(a => a.NumRecords));
    }

    // ---------------------------------------------------------------- Ignore / ErrorIfExists

    [Fact]
    public void Delta_IgnoreMode_OnExistingTable_IsNoOp()
    {
        string table = Table("ignore");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            // Ignore onto an existing table commits nothing (no v1).
            spark.CreateDataFrame(new[] { new Row(PeopleSchema, 7, "x") }, PeopleSchema)
                .Write.Format("delta").Mode("ignore").Save(table);
        }

        Assert.True(File.Exists(CommitFile(table, 0)));
        Assert.False(File.Exists(CommitFile(table, 1))); // ignored → no new commit
        Assert.Equal(3, ActiveAdds(table).Sum(a => a.NumRecords));
    }

    [Fact]
    public void Delta_IgnoreMode_OnFreshPath_CreatesTable()
    {
        using SparkSession spark = NewSession();
        string table = Table("ignore-fresh");

        // Ignore onto a NON-existent table writes it (Spark parity: ignore only skips existing targets).
        spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("ignore").Save(table);

        Assert.True(File.Exists(CommitFile(table, 0)));
        Assert.Equal(3, ActiveAdds(table).Sum(a => a.NumRecords));
    }

    [Fact]
    public void Delta_ErrorIfExistsMode_OnExistingTable_Throws_AndLeavesTableUnchanged()
    {
        string table = Table("erroriffexists");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            DataFrame df = spark.CreateDataFrame(
                new[] { new Row(PeopleSchema, 7, "x") }, PeopleSchema);

            // The default mode is ErrorIfExists; onto an existing table it must throw the stage-attributed
            // QueryExecutionException (a random NRE would no longer pass — assert the type, the Backend
            // stage, and the conflict message so a vacuous Assert.NotNull can't hide a wrong failure).
            QueryExecutionException ex = Assert.Throws<QueryExecutionException>(
                () => df.Write.Format("delta").Save(table));
            Assert.Equal(QueryExecutionStage.Backend, ex.Stage);
            Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ErrorIfExists", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // The existing table is unchanged (still v0 / 3 rows; no partial commit).
        Assert.False(File.Exists(CommitFile(table, 1)));
        Assert.Equal(3, ActiveAdds(table).Sum(a => a.NumRecords));
    }

    [Fact]
    public void Delta_Unpartitioned_WritesFileAtTableRoot()
    {
        using SparkSession spark = NewSession();
        string table = Table("unpartitioned");

        spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);

        AddRecord add = Assert.Single(ActiveAdds(table));
        Assert.DoesNotContain('/', add.Path); // no partition directory
        Assert.True(File.Exists(Path.Combine(table, add.Path)));
    }

    // ---------------------------------------------------------------- value oracle (real row VALUES)
    // Quality M1: the tests above assert only counts / log shape, so a mutation corrupting every `name`
    // value passed all of them. These read the committed Parquet data file(s) back through Parquet.Net's
    // low-level DataColumn API on the PUBLIC-API write path and assert the EXACT row multiset, so a
    // value-corruption mutation now FAILS an executor E2E test.

    [Fact]
    public void Delta_Append_Unpartitioned_DataFileRowValues_ExactMultiset()
    {
        using SparkSession spark = NewSession();
        string table = Table("value-append-flat");

        spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);

        Assert.Equal(
            new (int, string?, string?)[] { (1, "alice", null), (2, "bob", null), (3, null, null) },
            AllRowValues(table, partitionColumn: null));
    }

    [Fact]
    public void Delta_StaticOverwrite_Unpartitioned_DataFileRowValues_ExactMultiset()
    {
        string table = Table("value-overwrite-flat");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(People(), PeopleSchema).Write.Format("delta").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(new[] { new Row(PeopleSchema, 99, "zoe") }, PeopleSchema)
                .Write.Format("delta").Mode("overwrite").Save(table);
        }

        // Full static overwrite: only the replacement row remains.
        Assert.Equal(
            new (int, string?, string?)[] { (99, "zoe", null) },
            AllRowValues(table, partitionColumn: null));
    }

    [Fact]
    public void Delta_PartitionedAppend_DataFileRowValues_ExactMultiset()
    {
        using SparkSession spark = NewSession();
        string table = Table("value-append-part");

        spark.CreateDataFrame(Regional((1, "a", "US"), (2, "b", "EU"), (3, "c", "US")), RegionSchema)
            .Write.Format("delta").PartitionBy("region").Mode("append").Save(table);

        // Partition value is reconstructed from the Hive dir path; (id, name) come from the data file.
        Assert.Equal(
            new (int, string?, string?)[] { (1, "a", "US"), (2, "b", "EU"), (3, "c", "US") },
            AllRowValues(table, partitionColumn: "region"));
    }

    [Fact]
    public void Delta_PartitionedStaticOverwrite_DataFileRowValues_ExactMultiset()
    {
        string table = Table("value-overwrite-part");

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(Regional((1, "a", "US"), (2, "b", "EU")), RegionSchema)
                .Write.Format("delta").PartitionBy("region").Mode("append").Save(table);
        }

        using (SparkSession spark = NewSession())
        {
            spark.CreateDataFrame(Regional((9, "z", "US")), RegionSchema)
                .Write.Format("delta").PartitionBy("region").Mode("overwrite").Save(table);
        }

        // Static overwrite replaces the WHOLE table (EU dropped too): only the replacement row remains.
        Assert.Equal(
            new (int, string?, string?)[] { (9, "z", "US") },
            AllRowValues(table, partitionColumn: "region"));
    }

    // Reads every active data file's (id, name) rows back through Parquet.Net's low-level DataColumn API,
    // reconstructs the partition value from the Hive `col=value` directory path (partition columns are NOT
    // stored in the data file), and returns the exact row multiset ordered by id for a deterministic compare.
    private static List<(int Id, string? Name, string? Partition)> AllRowValues(string table, string? partitionColumn)
    {
        var rows = new List<(int, string?, string?)>();
        foreach (AddRecord add in ActiveAdds(table))
        {
            string? partition = partitionColumn is null ? null : PartitionFromHivePath(add.Path, partitionColumn);
            foreach ((int id, string? name) in ReadDataFileRows(Path.Combine(table, add.Path)))
            {
                rows.Add((id, name, partition));
            }
        }

        return rows.OrderBy(r => r.Item1).ToList();
    }

    // The (id, name) rows physically stored in one Parquet data file, via Parquet.Net's low-level column
    // reader (the executor layer cannot see Storage's internal ParquetFileReader).
    private static List<(int Id, string? Name)> ReadDataFileRows(string absolutePath)
    {
        using FileStream fs = File.OpenRead(absolutePath);
        ParquetReader reader = ParquetReader.CreateAsync(fs).GetAwaiter().GetResult();
        try
        {
            var fields = reader.Schema.GetDataFields();
            var idField = fields.Single(f => f.Name == "id");
            var nameField = fields.Single(f => f.Name == "name");
            var rows = new List<(int, string?)>();
            for (int rg = 0; rg < reader.RowGroupCount; rg++)
            {
                using var group = reader.OpenRowGroupReader(rg);
                int rowCount = checked((int)group.RowCount);
                var ids = new int[rowCount];
                group.ReadAsync(idField, new Memory<int>(ids), null, default)
                    .AsTask().GetAwaiter().GetResult();
                var names = new string?[rowCount];
                group.ReadAsync(nameField, new Memory<string?>(names), null, default)
                    .AsTask().GetAwaiter().GetResult();
                for (int i = 0; i < rowCount; i++)
                {
                    rows.Add((ids[i], names[i]));
                }
            }

            return rows;
        }
        finally
        {
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Reconstructs a partition value from a Hive-style `col=value/...` data-file path (null for the
    // __HIVE_DEFAULT_PARTITION__ sentinel), URL-decoding the encoded segment.
    private static string? PartitionFromHivePath(string relativePath, string column)
    {
        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.StartsWith(column + "=", StringComparison.Ordinal))
            {
                string encoded = segment[(column.Length + 1)..];
                return encoded == "__HIVE_DEFAULT_PARTITION__" ? null : Uri.UnescapeDataString(encoded);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- _delta_log readers (off disk)

    private static string CommitFile(string table, long version) =>
        Path.Combine(table, "_delta_log", version.ToString("D20") + ".json");

    private sealed record AddRecord(
        string Path, IReadOnlyDictionary<string, string?> PartitionValues, bool DataChange, long NumRecords);

    // Replays every JSON commit in order, applying add/remove, to compute the active add set — the same
    // reconstruction the Delta reader performs, done here off disk (Storage's Snapshot is internal).
    private static IReadOnlyList<AddRecord> ActiveAdds(string table)
    {
        var active = new Dictionary<string, AddRecord>(StringComparer.Ordinal);
        foreach (string file in CommitFiles(table))
        {
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("add", out JsonElement add))
                {
                    string path = add.GetProperty("path").GetString()!;
                    active[path] = new AddRecord(
                        path, ReadPartitionValues(add), ReadDataChange(add), ReadNumRecords(add));
                }
                else if (root.TryGetProperty("remove", out JsonElement remove))
                {
                    active.Remove(remove.GetProperty("path").GetString()!);
                }
            }
        }

        return active.Values.ToList();
    }

    private static bool HasRemove(string table, long version)
    {
        foreach (string line in File.ReadLines(CommitFile(table, version)))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("remove", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CommitFiles(string table)
    {
        string logDir = Path.Combine(table, "_delta_log");
        if (!Directory.Exists(logDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(logDir, "*.json")
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string?> ReadPartitionValues(JsonElement add)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (add.TryGetProperty("partitionValues", out JsonElement pv))
        {
            foreach (JsonProperty p in pv.EnumerateObject())
            {
                result[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
            }
        }

        return result;
    }

    private static bool ReadDataChange(JsonElement add) =>
        add.TryGetProperty("dataChange", out JsonElement dc) && dc.GetBoolean();

    private static long ReadNumRecords(JsonElement add)
    {
        if (!add.TryGetProperty("stats", out JsonElement statsElement)
            || statsElement.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        using JsonDocument stats = JsonDocument.Parse(statsElement.GetString()!);
        return stats.RootElement.TryGetProperty("numRecords", out JsonElement n) ? n.GetInt64() : 0;
    }
}
