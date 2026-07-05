using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Reads a Parquet <see cref="Stream"/> into <see cref="ColumnBatch"/>es — one batch per row group
/// (design §2.9.1, STORY-05.1.1 / #180). It supports <b>projection</b> (only the requested columns'
/// chunks are read) and <b>row-group pruning</b> (a caller predicate over each group's column
/// statistics may skip a group as a <i>hint</i>; the residual predicate stays the engine's job, so a
/// kept row group is always fully correct). A malformed, truncated, or unsupported file fails with a
/// deterministic <see cref="DeltaStorageException"/> and <b>never</b> yields partial rows.
/// </summary>
internal sealed class ParquetFileReader
{
    /// <summary>A row-group pruning hint: return <see langword="false"/> to skip a row group whose
    /// <see cref="RowGroupStatistics"/> prove it cannot match. Pruning is a hint only — a kept group is
    /// still read in full and the residual predicate is the engine's responsibility (design §2.9.1).</summary>
    public delegate bool RowGroupPredicate(RowGroupStatistics statistics);

    /// <summary>Reads <paramref name="input"/>, projecting to <paramref name="requested"/> (a subset of
    /// the file schema by field name) and optionally skipping row groups via
    /// <paramref name="keepRowGroup"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeltaStorageException">A requested column type is unsupported
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>); or the file is malformed/truncated, or a
    /// requested column is absent (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    public async IAsyncEnumerable<ColumnBatch> ReadAsync(
        Stream input,
        StructType requested,
        RowGroupPredicate? keepRowGroup,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(requested);

        // Validate every requested column maps to a supported Parquet type BEFORE any decode, so an
        // unsupported/nested projection fails deterministically without materializing a partial batch.
        for (int c = 0; c < requested.Count; c++)
        {
            _ = ParquetTypeMapping.CreateField(requested[c]);
        }

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            DataField[] fileFields = ResolveFileFields(reader.Schema, requested);
            for (int group = 0; group < reader.RowGroupCount; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ColumnBatch? batch = await ReadRowGroupAsync(
                    reader, group, requested, fileFields, keepRowGroup, cancellationToken).ConfigureAwait(false);
                if (batch is not null)
                {
                    yield return batch;
                }
            }
        }
    }

    private static async Task<ParquetReader> OpenAsync(Stream input, CancellationToken cancellationToken)
    {
        try
        {
            return await ParquetReader.CreateAsync(input, null, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsParquetDefect(ex))
        {
            throw DeltaStorageException.CorruptData(
                $"The Parquet stream is malformed or truncated: {ex.Message}", ex);
        }
    }

    private static DataField[] ResolveFileFields(ParquetSchema fileSchema, StructType requested)
    {
        var byName = new Dictionary<string, DataField>(StringComparer.Ordinal);
        foreach (DataField field in fileSchema.DataFields)
        {
            byName[field.Name] = field;
        }

        var resolved = new DataField[requested.Count];
        for (int c = 0; c < requested.Count; c++)
        {
            string name = requested[c].Name;
            if (!byName.TryGetValue(name, out DataField? field))
            {
                throw DeltaStorageException.CorruptData(
                    $"Requested column '{name}' is not present in the Parquet file schema.");
            }

            resolved[c] = field;
        }

        return resolved;
    }

    private static async Task<ColumnBatch?> ReadRowGroupAsync(
        ParquetReader reader,
        int group,
        StructType requested,
        DataField[] fileFields,
        RowGroupPredicate? keepRowGroup,
        CancellationToken cancellationToken)
    {
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);

        if (keepRowGroup is not null)
        {
            var statistics = new RowGroupStatistics(rowGroup, requested, fileFields);
            if (!keepRowGroup(statistics))
            {
                // Pruned: return without reading any column chunk for this group.
                return null;
            }
        }

        int rowCount = checked((int)rowGroup.RowCount);
        var columns = new ColumnVector[requested.Count];
        try
        {
            for (int c = 0; c < requested.Count; c++)
            {
                MutableColumnVector vector = ColumnVectors.Create(requested[c].DataType, Math.Max(rowCount, 1));
                await ReadColumnAsync(rowGroup, fileFields[c], requested[c], vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                columns[c] = vector;
            }
        }
        catch (Exception ex) when (IsParquetDefect(ex))
        {
            throw DeltaStorageException.CorruptData(
                $"Failed to decode Parquet row group {group}: {ex.Message}", ex);
        }

        return new ManagedColumnBatch(requested, columns, rowCount);
    }

    private static async Task ReadColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        switch (requestedField.DataType)
        {
            case BooleanType:
                await ReadValueAsync<bool>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case ByteType:
                await ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(unchecked((byte)value)), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShortType:
                await ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case IntegerType:
                await ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case LongType:
                await ReadValueAsync<long>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case FloatType:
                await ReadValueAsync<float>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case DoubleType:
                await ReadValueAsync<double>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case DateType:
                await ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochDay(value)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TimestampType:
                await ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochMicros(value)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecimalType decimalType:
                await ReadValueAsync<decimal>(rowGroup, fileField, vector, rowCount,
                    (v, value) => ParquetTypeMapping.AppendDecimal(v, decimalType, value), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case StringType:
                await ReadStringAsync(rowGroup, fileField, vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case BinaryType:
                await ReadBinaryAsync(rowGroup, fileField, vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet read for column '{requestedField.Name}' of type "
                    + $"'{requestedField.DataType.SimpleString}' is not supported.");
        }
    }

    private static async Task ReadValueAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        Action<MutableColumnVector, T> append,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (fileField.IsNullable)
        {
            var buffer = new T?[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    append(vector, value);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new T[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                append(vector, buffer[i]);
            }
        }
    }

    private static async Task ReadStringAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var buffer = new string?[rowCount];
        await rowGroup.ReadAsync(fileField, new Memory<string?>(buffer), null, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < rowCount; i++)
        {
            if (buffer[i] is { } value)
            {
                vector.AppendBytes(Encoding.UTF8.GetBytes(value));
            }
            else
            {
                vector.AppendNull();
            }
        }
    }

    private static async Task ReadBinaryAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[]?[rowCount];
        await rowGroup.ReadAsync(fileField, new Memory<byte[]?>(buffer), null, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < rowCount; i++)
        {
            if (buffer[i] is { } value)
            {
                vector.AppendBytes(value);
            }
            else
            {
                vector.AppendNull();
            }
        }
    }

    // The set of exceptions Parquet.Net raises for a malformed, truncated, or otherwise undecodable
    // stream. Each is mapped to a deterministic CorruptData error; a decode never yields partial rows.
    private static bool IsParquetDefect(Exception ex) => ex is
        IOException or
        InvalidDataException or
        EndOfStreamException or
        FormatException or
        NotSupportedException or
        OverflowException or
        InvalidOperationException or
        ArgumentException or
        IndexOutOfRangeException;

    /// <summary>
    /// A read-only view of one row group's per-column statistics, exposed to a
    /// <see cref="RowGroupPredicate"/> for pruning. Values are the Parquet CLR types (for example
    /// <see cref="int"/> for an integer column, <see cref="decimal"/> for a decimal column).
    /// </summary>
    internal sealed class RowGroupStatistics
    {
        private readonly Dictionary<string, DataColumnStatistics?> _byColumn;

        internal RowGroupStatistics(ParquetRowGroupReader rowGroup, StructType requested, DataField[] fileFields)
        {
            RowCount = rowGroup.RowCount;
            _byColumn = new Dictionary<string, DataColumnStatistics?>(StringComparer.Ordinal);
            for (int c = 0; c < requested.Count; c++)
            {
                DataColumnStatistics? statistics =
                    rowGroup.ColumnExists(fileFields[c]) ? rowGroup.GetStatistics(fileFields[c]) : null;
                _byColumn[requested[c].Name] = statistics;
            }
        }

        /// <summary>The number of rows in the row group.</summary>
        public long RowCount { get; }

        /// <summary>The raw statistics for <paramref name="column"/>, or <see langword="null"/> if absent.</summary>
        public DataColumnStatistics? ForColumn(string column) => _byColumn.GetValueOrDefault(column);

        /// <summary>The minimum value recorded for <paramref name="column"/>, or <see langword="null"/>.</summary>
        public object? Min(string column) => ForColumn(column)?.MinValue;

        /// <summary>The maximum value recorded for <paramref name="column"/>, or <see langword="null"/>.</summary>
        public object? Max(string column) => ForColumn(column)?.MaxValue;

        /// <summary>The null count recorded for <paramref name="column"/>, or <see langword="null"/>.</summary>
        public long? NullCount(string column) => ForColumn(column)?.NullCount;
    }
}
