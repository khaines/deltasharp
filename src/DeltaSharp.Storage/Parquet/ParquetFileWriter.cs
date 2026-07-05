using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Writes an ordered sequence of same-schema <see cref="ColumnBatch"/>es to a <see cref="Stream"/> as
/// one standards-compliant Parquet file (design §2.9.2, STORY-05.1.2 / #181). Logical rows are packed
/// into row groups of at most <see cref="RowGroupRowLimit"/> rows; the footer carries the Spark/Delta
/// schema JSON in <c>key_value_metadata</c>, and per-column <c>Statistics</c> (min/max/null) are
/// produced automatically by Parquet.Net from the written values (checklist 17 statistics bullets).
/// </summary>
internal sealed class ParquetFileWriter
{
    /// <summary>The default maximum number of logical rows per row group. This is a <b>row-count
    /// proxy</b> for the design's ≈128&#160;MiB byte target (§2.9.2): a byte-aware flush that sizes row
    /// groups by encoded bytes directly (rather than by row count) is a tracked follow-up.</summary>
    public const int DefaultRowGroupRowLimit = 128 * 1024;

    private const string WriterIdentity = "DeltaSharp.Storage/0.1";

    private readonly int _rowGroupRowLimit;

    /// <summary>Creates a writer with the given row-group row cap.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowGroupRowLimit"/> is not positive.</exception>
    public ParquetFileWriter(int rowGroupRowLimit = DefaultRowGroupRowLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowGroupRowLimit);
        _rowGroupRowLimit = rowGroupRowLimit;
    }

    /// <summary>The maximum number of logical rows written into a single row group.</summary>
    public int RowGroupRowLimit => _rowGroupRowLimit;

    /// <summary>Writes <paramref name="batches"/> (each conforming to <paramref name="schema"/>) to
    /// <paramref name="output"/> as one Parquet file.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A batch's schema does not match <paramref name="schema"/>.</exception>
    /// <exception cref="DeltaStorageException">A column's type has no supported Parquet mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>), or a non-nullable column holds a null
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    public async Task WriteAsync(
        Stream output, StructType schema, IReadOnlyList<ColumnBatch> batches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);

        int columnCount = schema.Count;
        var fields = new DataField[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            fields[c] = ParquetTypeMapping.CreateField(schema[c]);
        }

        // Apply any selection once and record each batch's logical row count, so row groups can be sized
        // independently of the input batch boundaries with a running cursor — no O(total-rows) per-row
        // index is materialized (M5).
        var selectedColumns = new List<ColumnVector[]>(batches.Count);
        var batchRowCounts = new int[batches.Count];
        long totalRows = 0;
        for (int b = 0; b < batches.Count; b++)
        {
            ColumnBatch batch = batches[b] ?? throw new ArgumentNullException(nameof(batches), $"Batch {b} is null.");
            if (!batch.Schema.Equals(schema))
            {
                throw new ArgumentException(
                    $"Batch {b} has schema '{batch.Schema.SimpleString}' but the writer schema is "
                    + $"'{schema.SimpleString}'.", nameof(batches));
            }

            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            selectedColumns.Add(columns);
            batchRowCounts[b] = batch.LogicalRowCount;
            totalRows += batch.LogicalRowCount;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DeltaSchemaJson.SchemaMetadataKey] = DeltaSchemaJson.ToJson(schema),
            [DeltaSchemaJson.WriterMetadataKey] = WriterIdentity,
        };

        await using ParquetWriter writer =
            await ParquetWriter.CreateAsync(new ParquetSchema(fields), output, null, false, cancellationToken)
                .ConfigureAwait(false);
        writer.CustomMetadata = metadata;

        // L2: a pre-test loop, so zero input rows produce ZERO row groups (never one empty group).
        int cursorBatch = 0;
        int cursorRow = 0;
        long emitted = 0;
        var segments = new List<Segment>();
        while (emitted < totalRows)
        {
            int size = (int)Math.Min(_rowGroupRowLimit, totalRows - emitted);
            CollectSegments(batchRowCounts, ref cursorBatch, ref cursorRow, size, segments);

            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            for (int c = 0; c < columnCount; c++)
            {
                await WriteColumnAsync(
                    rowGroup, fields[c], schema[c], selectedColumns, c, segments, size, cancellationToken)
                    .ConfigureAwait(false);
            }

            emitted += size;
        }
    }

    // Advance the (batch, row) cursor by exactly `size` logical rows, recording the contiguous
    // (batch, start, length) segments spanned — which lets a row group straddle input batch boundaries
    // without a per-row index. Empty batches are skipped.
    private static void CollectSegments(
        int[] batchRowCounts, ref int cursorBatch, ref int cursorRow, int size, List<Segment> segments)
    {
        segments.Clear();
        int need = size;
        int b = cursorBatch;
        int r = cursorRow;
        while (need > 0)
        {
            int available = batchRowCounts[b] - r;
            if (available <= 0)
            {
                b++;
                r = 0;
                continue;
            }

            int take = Math.Min(available, need);
            segments.Add(new Segment(b, r, take));
            r += take;
            need -= take;
            if (r >= batchRowCounts[b])
            {
                b++;
                r = 0;
            }
        }

        cursorBatch = b;
        cursorRow = r;
    }

    private static async Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        StructField schemaField,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        CancellationToken cancellationToken)
    {
        bool nullable = schemaField.Nullable;
        switch (schemaField.DataType)
        {
            case BooleanType:
                await WriteValueAsync<bool>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<bool>(row), cancellationToken).ConfigureAwait(false);
                break;
            case ByteType:
                await WriteValueAsync<sbyte>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => unchecked((sbyte)vector.GetValue<byte>(row)), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShortType:
                await WriteValueAsync<short>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<short>(row), cancellationToken).ConfigureAwait(false);
                break;
            case IntegerType:
                await WriteValueAsync<int>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<int>(row), cancellationToken).ConfigureAwait(false);
                break;
            case LongType:
                await WriteValueAsync<long>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<long>(row), cancellationToken).ConfigureAwait(false);
                break;
            case FloatType:
                await WriteValueAsync<float>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<float>(row), cancellationToken).ConfigureAwait(false);
                break;
            case DoubleType:
                await WriteValueAsync<double>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<double>(row), cancellationToken).ConfigureAwait(false);
                break;
            case DateType:
                await WriteValueAsync<DateTime>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => ParquetTypeMapping.EpochDayToDateTime(vector.GetValue<int>(row)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TimestampType:
                await WriteValueAsync<DateTime>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => ParquetTypeMapping.EpochMicrosToDateTime(vector.GetValue<long>(row)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecimalType decimalType:
                // L1: thread decimalType through a non-capturing static writer instead of a closure so no
                // per-column-chunk delegate allocation occurs (mirrors the static primitive delegates).
                await WriteDecimalAsync(
                    rowGroup, field, nullable, selectedColumns, columnIndex, segments, size, decimalType,
                    cancellationToken).ConfigureAwait(false);
                break;
            case StringType:
                await WriteStringAsync(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size)
                    .ConfigureAwait(false);
                break;
            case BinaryType:
                await WriteBinaryAsync(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size)
                    .ConfigureAwait(false);
                break;
            default:
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet write for column '{schemaField.Name}' of type "
                    + $"'{schemaField.DataType.SimpleString}' is not supported.");
        }
    }

    private static async Task WriteValueAsync<T>(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        Func<ColumnVector, int, T> read,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (nullable)
        {
            var values = new T?[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    values[idx++] = vector.IsNull(row) ? null : read(vector, row);
                }
            }

            await rowGroup.WriteAsync<T>(field, new ReadOnlyMemory<T?>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var values = new T[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (vector.IsNull(row))
                    {
                        throw DeltaStorageException.CorruptData(
                            $"Non-nullable column '{field.Name}' holds a null at row {row}.");
                    }

                    values[idx++] = read(vector, row);
                }
            }

            await rowGroup.WriteAsync<T>(field, new ReadOnlyMemory<T>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteDecimalAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        DecimalType decimalType,
        CancellationToken cancellationToken)
    {
        if (nullable)
        {
            var values = new decimal?[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    values[idx++] = vector.IsNull(row) ? null : ParquetTypeMapping.ReadDecimal(vector, decimalType, row);
                }
            }

            await rowGroup.WriteAsync<decimal>(field, new ReadOnlyMemory<decimal?>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var values = new decimal[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (vector.IsNull(row))
                    {
                        throw DeltaStorageException.CorruptData(
                            $"Non-nullable column '{field.Name}' holds a null at row {row}.");
                    }

                    values[idx++] = ParquetTypeMapping.ReadDecimal(vector, decimalType, row);
                }
            }

            await rowGroup.WriteAsync<decimal>(field, new ReadOnlyMemory<decimal>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteStringAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size)
    {
        var values = new string?[size];
        int idx = 0;
        foreach (Segment segment in segments)
        {
            ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                if (vector.IsNull(row))
                {
                    EnsureNullable(nullable, field, row);
                    values[idx++] = null;
                }
                else
                {
                    values[idx++] = System.Text.Encoding.UTF8.GetString(vector.GetBytes(row));
                }
            }
        }

        await rowGroup.WriteAsync(field, (IReadOnlyCollection<string>)values!, null).ConfigureAwait(false);
    }

    private static async Task WriteBinaryAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size)
    {
        var values = new byte[]?[size];
        int idx = 0;
        foreach (Segment segment in segments)
        {
            ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
            for (int j = 0; j < segment.Length; j++)
            {
                int row = segment.Start + j;
                if (vector.IsNull(row))
                {
                    EnsureNullable(nullable, field, row);
                    values[idx++] = null;
                }
                else
                {
                    values[idx++] = vector.GetBytes(row).ToArray();
                }
            }
        }

        await rowGroup.WriteAsync(field, (IReadOnlyCollection<byte[]>)values!, null).ConfigureAwait(false);
    }

    private static void EnsureNullable(bool nullable, DataField field, int row)
    {
        if (!nullable)
        {
            throw DeltaStorageException.CorruptData(
                $"Non-nullable column '{field.Name}' holds a null at row {row}.");
        }
    }

    // A contiguous run of logical rows within a single input batch that a row group covers.
    private readonly struct Segment
    {
        internal Segment(int batch, int start, int length)
        {
            Batch = batch;
            Start = start;
            Length = length;
        }

        internal int Batch { get; }

        internal int Start { get; }

        internal int Length { get; }
    }
}
