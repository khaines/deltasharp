using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// End-to-end round-trip tests for the PUBLIC Delta write facade (<see cref="DeltaWriteTarget"/>, #487).
/// Each test stages a <see cref="ColumnBatch"/> through the facade (Parquet data files + a real
/// <c>_delta_log</c> commit), then reads the table back through the internal <see cref="DeltaLog"/> /
/// <see cref="ParquetFileReader"/> and asserts the row multiset, schema, partition layout, and the commit
/// actions (version, <c>add</c> actions, <c>dataChange</c>). Covers create+append (v0→v1), static
/// overwrite, dynamic partition overwrite (only touched partitions replaced), and unpartitioned tables.
/// </summary>
public sealed class DeltaWriteTargetTests : IDisposable
{
    private readonly string _root;

    // A partitioned table: region is the partition column; (id, name) are the data columns.
    private static readonly StructType PartitionedSchema = new(new[]
    {
        new StructField("region", DataTypes.StringType, nullable: true),
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType DataSchema = new(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
    });

    // An unpartitioned table: (id, name).
    private static readonly StructType FlatSchema = DataSchema;

    public DeltaWriteTargetTests() =>
        _root = Path.Combine(Path.GetTempPath(), "deltawrite-facade-" + Guid.NewGuid().ToString("N"));

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

    private DeltaWriteTarget Target() => DeltaWriteTarget.ForLocalPath(_root);

    private static ColumnBatch PartitionedBatch(params (string? Region, long Id, string? Name)[] rows)
    {
        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((string? r, long i, string? n) in rows)
        {
            AppendString(region, r);
            id.AppendValue(i);
            AppendString(name, n);
        }

        return new ManagedColumnBatch(
            PartitionedSchema, new ColumnVector[] { region, id, name }, rows.Length);
    }

    private static ColumnBatch FlatBatch(params (long Id, string? Name)[] rows)
    {
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, rows.Length);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, rows.Length);
        foreach ((long i, string? n) in rows)
        {
            id.AppendValue(i);
            AppendString(name, n);
        }

        return new ManagedColumnBatch(FlatSchema, new ColumnVector[] { id, name }, rows.Length);
    }

    private static void AppendString(MutableColumnVector vector, string? value)
    {
        if (value is null)
        {
            vector.AppendNull();
        }
        else
        {
            vector.AppendBytes(Encoding.UTF8.GetBytes(value));
        }
    }

    // ---------------------------------------------------------------- create + append

    [Fact]
    public async Task Append_ToFreshPath_CreatesTableAtVersion0()
    {
        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        Assert.Equal(0L, result.Version);
        Assert.Equal(2L, result.RowsWritten);

        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("EU", 2L, "bob"), ("US", 1L, "alice") },
            Sorted(rows));
    }

    [Fact]
    public async Task Append_Twice_CreatesV0ThenV1_AndUnionsRows()
    {
        using DeltaWriteTarget target = Target();
        DeltaWriteResult first = await target.AppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { PartitionedBatch(("US", 1, "alice")) });
        DeltaWriteResult second = await target.AppendAsync(
            PartitionedSchema, new[] { "region" }, new[] { PartitionedBatch(("US", 2, "bob")) });

        Assert.Equal(0L, first.Version);
        Assert.Equal(1L, second.Version);

        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("US", 1L, "alice"), ("US", 2L, "bob") },
            Sorted(rows));
    }

    [Fact]
    public async Task Append_CommitsAddActions_WithDataChangeTrue()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        Snapshot snapshot = await LoadSnapshotAsync();
        Assert.Equal(0L, snapshot.Version);
        Assert.Equal(2, snapshot.ActiveFiles.Length); // one file per touched partition
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.DataChange));
        Assert.All(snapshot.ActiveFiles, add => Assert.True(add.PartitionValues.ContainsKey("region")));
        Assert.Equal(
            new[] { "EU", "US" },
            snapshot.ActiveFiles.Select(a => a.PartitionValues["region"]).OrderBy(v => v, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- static overwrite

    [Fact]
    public async Task StaticOverwrite_ReplacesAllPriorFiles()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        DeltaWriteResult result = await target.OverwriteAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 9, "zoe")) }, DeltaPartitionOverwriteMode.Static);

        Assert.Equal(1L, result.Version);
        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(new (string?, long, string?)[] { ("US", 9L, "zoe") }, Sorted(rows)); // EU partition also removed (full overwrite)
    }

    // ---------------------------------------------------------------- dynamic partition overwrite

    [Fact]
    public async Task DynamicOverwrite_ReplacesOnlyTouchedPartitions()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 1, "alice"), ("EU", 2, "bob")) });

        // Overwrite only the US partition dynamically; EU must survive untouched.
        DeltaWriteResult result = await target.OverwriteAsync(
            PartitionedSchema, new[] { "region" },
            new[] { PartitionedBatch(("US", 9, "zoe")) }, DeltaPartitionOverwriteMode.Dynamic);

        Assert.Equal(1L, result.Version);
        List<(string?, long, string?)> rows = await ReadPartitionedAsync();
        Assert.Equal(
            new (string?, long, string?)[] { ("EU", 2L, "bob"), ("US", 9L, "zoe") },
            Sorted(rows));
    }

    // ---------------------------------------------------------------- unpartitioned

    [Fact]
    public async Task Append_Unpartitioned_WritesSingleFileAtRoot()
    {
        using DeltaWriteTarget target = Target();
        DeltaWriteResult result = await target.AppendAsync(
            FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a"), (2, "b"), (3, null)) });

        Assert.Equal(0L, result.Version);
        Snapshot snapshot = await LoadSnapshotAsync();
        AddFileAction add = Assert.Single(snapshot.ActiveFiles);
        Assert.Empty(add.PartitionValues);
        Assert.DoesNotContain('/', add.Path); // a file at the table root (no partition directory)

        List<(long, string?)> rows = await ReadFlatAsync();
        Assert.Equal(new[] { (1L, "a"), (2L, "b"), (3L, (string?)null) }, rows.OrderBy(r => r.Item1).ToList());
    }

    [Fact]
    public async Task Overwrite_Unpartitioned_ReplacesData()
    {
        using DeltaWriteTarget target = Target();
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        await target.OverwriteAsync(
            FlatSchema, Array.Empty<string>(), new[] { FlatBatch((5, "e")) }, DeltaPartitionOverwriteMode.Static);

        List<(long, string?)> rows = await ReadFlatAsync();
        Assert.Equal(new (long, string?)[] { (5L, "e") }, rows);
    }

    // ---------------------------------------------------------------- existence check

    [Fact]
    public async Task TableExists_IsFalseBeforeWrite_TrueAfter()
    {
        using DeltaWriteTarget target = Target();
        Assert.False(await target.TableExistsAsync());
        await target.AppendAsync(FlatSchema, Array.Empty<string>(), new[] { FlatBatch((1, "a")) });
        Assert.True(await target.TableExistsAsync());
    }

    // ---------------------------------------------------------------- read-back helpers

    private async Task<Snapshot> LoadSnapshotAsync()
    {
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var log = new DeltaLog(backend);
            return await log.LoadSnapshotAsync();
        }
        finally
        {
            backend.Dispose();
        }
    }

    private async Task<List<(string? Region, long Id, string? Name)>> ReadPartitionedAsync()
    {
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var log = new DeltaLog(backend);
            Snapshot snapshot = await log.LoadSnapshotAsync();
            var reader = new ParquetFileReader();
            var rows = new List<(string?, long, string?)>();
            foreach (AddFileAction add in snapshot.ActiveFiles)
            {
                add.PartitionValues.TryGetValue("region", out string? region);
                Stream stream = await backend.OpenReadAsync(add.Path, CancellationToken.None);
                await using (stream)
                {
                    await foreach (ColumnBatch batch in reader.ReadAsync(stream, DataSchema, null, CancellationToken.None))
                    {
                        ColumnVector id = batch.SelectedColumn(0);
                        ColumnVector name = batch.SelectedColumn(1);
                        for (int r = 0; r < batch.LogicalRowCount; r++)
                        {
                            rows.Add((region, id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
                        }
                    }
                }
            }

            return rows;
        }
        finally
        {
            backend.Dispose();
        }
    }

    private async Task<List<(long Id, string? Name)>> ReadFlatAsync()
    {
        var backend = new LocalFileSystemBackend(_root);
        try
        {
            var log = new DeltaLog(backend);
            Snapshot snapshot = await log.LoadSnapshotAsync();
            var reader = new ParquetFileReader();
            var rows = new List<(long, string?)>();
            foreach (AddFileAction add in snapshot.ActiveFiles)
            {
                Stream stream = await backend.OpenReadAsync(add.Path, CancellationToken.None);
                await using (stream)
                {
                    await foreach (ColumnBatch batch in reader.ReadAsync(stream, FlatSchema, null, CancellationToken.None))
                    {
                        ColumnVector id = batch.SelectedColumn(0);
                        ColumnVector name = batch.SelectedColumn(1);
                        for (int r = 0; r < batch.LogicalRowCount; r++)
                        {
                            rows.Add((id.GetValue<long>(r), name.IsNull(r) ? null : Encoding.UTF8.GetString(name.GetBytes(r))));
                        }
                    }
                }
            }

            return rows.OrderBy(r => r.Item1).ToList();
        }
        finally
        {
            backend.Dispose();
        }
    }

    private static List<(string?, long, string?)> Sorted(IEnumerable<(string? Region, long Id, string? Name)> rows) =>
        rows.OrderBy(r => r.Region, StringComparer.Ordinal).ThenBy(r => r.Id).ToList();
}
