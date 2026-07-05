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
    /// <summary>The default maximum number of logical rows per row group (a row-count proxy for the
    /// design's ≈128&#160;MiB target; §2.9.2).</summary>
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

        // Apply any selection once, and build the flat logical-row map across all batches so row groups
        // can be sized independently of the input batch boundaries.
        var selectedColumns = new List<ColumnVector[]>(batches.Count);
        var rowMap = new List<(int Batch, int Row)>();
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
            for (int r = 0; r < batch.LogicalRowCount; r++)
            {
                rowMap.Add((b, r));
            }
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

        int start = 0;
        do
        {
            int size = Math.Min(_rowGroupRowLimit, rowMap.Count - start);
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            for (int c = 0; c < columnCount; c++)
            {
                await WriteColumnAsync(
                    rowGroup, fields[c], schema[c], selectedColumns, c, rowMap, start, size, cancellationToken)
                    .ConfigureAwait(false);
            }

            start += size;
        }
        while (start < rowMap.Count);
    }

    private static async Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        StructField schemaField,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<(int Batch, int Row)> rowMap,
        int start,
        int size,
        CancellationToken cancellationToken)
    {
        bool nullable = schemaField.Nullable;
        switch (schemaField.DataType)
        {
            case BooleanType:
                await WriteValueAsync<bool>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => vector.GetValue<bool>(row), cancellationToken).ConfigureAwait(false);
                break;
            case ByteType:
                await WriteValueAsync<sbyte>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => unchecked((sbyte)vector.GetValue<byte>(row)), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShortType:
                await WriteValueAsync<short>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => vector.GetValue<short>(row), cancellationToken).ConfigureAwait(false);
                break;
            case IntegerType:
                await WriteValueAsync<int>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => vector.GetValue<int>(row), cancellationToken).ConfigureAwait(false);
                break;
            case LongType:
                await WriteValueAsync<long>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => vector.GetValue<long>(row), cancellationToken).ConfigureAwait(false);
                break;
            case FloatType:
                await WriteValueAsync<float>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => vector.GetValue<float>(row), cancellationToken).ConfigureAwait(false);
                break;
            case DoubleType:
                await WriteValueAsync<double>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => vector.GetValue<double>(row), cancellationToken).ConfigureAwait(false);
                break;
            case DateType:
                await WriteValueAsync<DateTime>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => ParquetTypeMapping.EpochDayToDateTime(vector.GetValue<int>(row)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TimestampType:
                await WriteValueAsync<DateTime>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    static (vector, row) => ParquetTypeMapping.EpochMicrosToDateTime(vector.GetValue<long>(row)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecimalType decimalType:
                await WriteValueAsync<decimal>(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size,
                    (vector, row) => ParquetTypeMapping.ReadDecimal(vector, decimalType, row), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case StringType:
                await WriteStringAsync(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size)
                    .ConfigureAwait(false);
                break;
            case BinaryType:
                await WriteBinaryAsync(rowGroup, field, nullable, selectedColumns, columnIndex, rowMap, start, size)
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
        List<(int Batch, int Row)> rowMap,
        int start,
        int size,
        Func<ColumnVector, int, T> read,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (nullable)
        {
            var values = new T?[size];
            for (int i = 0; i < size; i++)
            {
                (int batch, int row) = rowMap[start + i];
                ColumnVector vector = selectedColumns[batch][columnIndex];
                values[i] = vector.IsNull(row) ? null : read(vector, row);
            }

            await rowGroup.WriteAsync<T>(field, new ReadOnlyMemory<T?>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var values = new T[size];
            for (int i = 0; i < size; i++)
            {
                (int batch, int row) = rowMap[start + i];
                ColumnVector vector = selectedColumns[batch][columnIndex];
                if (vector.IsNull(row))
                {
                    throw DeltaStorageException.CorruptData(
                        $"Non-nullable column '{field.Name}' holds a null at row {row}.");
                }

                values[i] = read(vector, row);
            }

            await rowGroup.WriteAsync<T>(field, new ReadOnlyMemory<T>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteStringAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<(int Batch, int Row)> rowMap,
        int start,
        int size)
    {
        var values = new string?[size];
        for (int i = 0; i < size; i++)
        {
            (int batch, int row) = rowMap[start + i];
            ColumnVector vector = selectedColumns[batch][columnIndex];
            if (vector.IsNull(row))
            {
                EnsureNullable(nullable, field, row);
                values[i] = null;
            }
            else
            {
                values[i] = System.Text.Encoding.UTF8.GetString(vector.GetBytes(row));
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
        List<(int Batch, int Row)> rowMap,
        int start,
        int size)
    {
        var values = new byte[]?[size];
        for (int i = 0; i < size; i++)
        {
            (int batch, int row) = rowMap[start + i];
            ColumnVector vector = selectedColumns[batch][columnIndex];
            if (vector.IsNull(row))
            {
                EnsureNullable(nullable, field, row);
                values[i] = null;
            }
            else
            {
                values[i] = vector.GetBytes(row).ToArray();
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
}
