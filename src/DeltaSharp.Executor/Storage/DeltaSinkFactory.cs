using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Storage;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// The Storage↔Executor <b>write</b> adapter (#487): an <see cref="ILocalSinkFactory"/> that resolves the
/// <c>delta</c> write format to a <see cref="DeltaLocalSink"/> driving the storage layer's public
/// <see cref="DeltaWriteTarget"/> facade (Parquet data files + <c>_delta_log</c> commit). It is the mirror
/// of <see cref="InMemorySinkRegistry"/> for a real, durable table. The base <c>delta</c> <b>read</b>
/// provider is #499's responsibility and registers alongside this factory through the same
/// <see cref="DeltaStorageAdapter"/> composition seam, so wiring the read path never restructures the write
/// path.
/// </summary>
internal sealed class DeltaSinkFactory : ILocalSinkFactory
{
    public static DeltaSinkFactory Instance { get; } = new();

    /// <inheritdoc/>
    public bool TryCreate(SinkDescriptor descriptor, StructType schema, [NotNullWhen(true)] out ILocalSink? sink)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        if (!string.Equals(descriptor.Format, WriteFormats.Delta, StringComparison.OrdinalIgnoreCase))
        {
            sink = null;
            return false;
        }

        sink = new DeltaLocalSink(descriptor, schema);
        return true;
    }
}

/// <summary>
/// An <see cref="ILocalSink"/> that commits the write's rows to a real Delta table through the storage
/// layer's public <see cref="DeltaWriteTarget"/> facade. It converts the materialized rows to a columnar
/// batch (the inverse of the read-door encode, reusing <see cref="LocalRelationBatches"/>), then drives the
/// facade's Append/Overwrite lifecycle per the descriptor's <see cref="SaveMode"/>: Append and Overwrite
/// write (creating the table on first write); Ignore skips an existing target; ErrorIfExists throws on one.
/// The facade stages the Parquet files <b>before</b> the log commit, so a mode conflict or a concurrent-write
/// abort leaves the staged files as VACUUM-reclaimable orphans — never a partial commit.
/// </summary>
internal sealed class DeltaLocalSink : ILocalSink
{
    // Spark's DataFrameWriter option / SQL conf selecting how an overwrite replaces data. Both spellings are
    // accepted (case-insensitively) so `.option("partitionOverwriteMode", "dynamic")` and the fully-qualified
    // `spark.sql.sources.partitionOverwriteMode` both work.
    private const string PartitionOverwriteModeOption = "partitionOverwriteMode";
    private const string PartitionOverwriteModeConf = "spark.sql.sources.partitionOverwriteMode";

    private readonly SinkDescriptor _descriptor;
    private readonly StructType _schema;

    public DeltaLocalSink(SinkDescriptor descriptor, StructType schema)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    /// <inheritdoc/>
    public long Commit(StructType schema, IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);

        string path = ResolvePath();
        IReadOnlyList<ColumnBatch> batches = LocalRelationBatches.Build(schema, rows);
        IReadOnlyList<string> partitionColumns = _descriptor.PartitionColumns;

        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(path);
        switch (_descriptor.Mode)
        {
            case SaveMode.Append:
                return RunAppend(target, schema, partitionColumns, batches);

            case SaveMode.Overwrite:
                return target
                    .OverwriteAsync(schema, partitionColumns, batches, ResolvePartitionOverwriteMode())
                    .GetAwaiter().GetResult().RowsWritten;

            case SaveMode.Ignore:
                // Re-check existence at the commit boundary (a race that created the table since the
                // pre-commit probe still skips cleanly — Spark parity).
                if (TableExists(target))
                {
                    return 0;
                }

                return RunAppend(target, schema, partitionColumns, batches);

            case SaveMode.ErrorIfExists:
                if (TableExists(target))
                {
                    throw ErrorIfExistsConflict(path);
                }

                return RunAppend(target, schema, partitionColumns, batches);

            default:
                throw new InvalidOperationException($"Unknown save mode '{_descriptor.Mode}'.");
        }
    }

    /// <inheritdoc/>
    public bool ShouldSkipOrThrow()
    {
        if (_descriptor.Mode is not (SaveMode.Ignore or SaveMode.ErrorIfExists))
        {
            return false;
        }

        string path = ResolvePath();
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(path);
        if (!TableExists(target))
        {
            return false;
        }

        if (_descriptor.Mode == SaveMode.Ignore)
        {
            return true;
        }

        throw ErrorIfExistsConflict(path);
    }

    private static long RunAppend(
        DeltaWriteTarget target, StructType schema, IReadOnlyList<string> partitionColumns, IReadOnlyList<ColumnBatch> batches) =>
        target.AppendAsync(schema, partitionColumns, batches).GetAwaiter().GetResult().RowsWritten;

    private static bool TableExists(DeltaWriteTarget target) =>
        target.TableExistsAsync().GetAwaiter().GetResult();

    private DeltaPartitionOverwriteMode ResolvePartitionOverwriteMode()
    {
        string? value = null;
        if (_descriptor.Options.TryGetValue(PartitionOverwriteModeOption, out string? optionValue))
        {
            value = optionValue;
        }
        else if (_descriptor.Options.TryGetValue(PartitionOverwriteModeConf, out string? confValue))
        {
            value = confValue;
        }

        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "static" => DeltaPartitionOverwriteMode.Static,
            "dynamic" => DeltaPartitionOverwriteMode.Dynamic,
            _ => throw new InvalidOperationException(
                $"Unsupported '{PartitionOverwriteModeOption}' value '{value}'. DeltaSharp recognizes "
                + "'static' and 'dynamic'."),
        };
    }

    // The delta sink writes to a real storage path; a path is mandatory. The DataFrameWriter reconciles a
    // `path` option into the descriptor path, but resolve one here too (case-insensitively) so a descriptor
    // built without that reconciliation still routes correctly.
    private string ResolvePath()
    {
        if (!string.IsNullOrEmpty(_descriptor.Path))
        {
            return _descriptor.Path;
        }

        foreach (KeyValuePair<string, string> option in _descriptor.Options)
        {
            if (string.Equals(option.Key, "path", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(option.Value))
            {
                return option.Value;
            }
        }

        throw new InvalidOperationException(
            "A delta write requires an output path (df.write.format(\"delta\").save(path)).");
    }

    private static InvalidOperationException ErrorIfExistsConflict(string path) =>
        new($"Cannot write to '{SecretRedaction.RedactPath(path)}': it already exists and the save mode is "
            + "ErrorIfExists. Use Overwrite, Append, or Ignore to write to an existing table.");
}
