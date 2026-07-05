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
/// kept row group is always fully correct).
/// </summary>
/// <remarks>
/// <para><b>Streaming corruption contract (H3, design §3.2 EE-01/EE-02).</b> The reader is
/// <i>batch-atomic</i>, not file-atomic: every yielded <see cref="ColumnBatch"/> is <b>always</b>
/// complete — a torn/partial batch is never produced. Concretely:
/// <list type="bullet">
/// <item>Structural/footer/metadata corruption fails <b>before any batch is yielded</b>: the footer
/// (schema + row-group metadata) is read at open, so a malformed/truncated file throws a deterministic
/// <see cref="DeltaStorageException"/> up front.</item>
/// <item>A page-level defect inside row group <c>K</c> surfaces as a deterministic error <b>at batch
/// <c>K</c></b>: batches for groups <c>0..K-1</c> were already returned complete, then enumerating the
/// defective group throws — it never yields a partial batch <c>K</c>.</item>
/// </list>
/// The reader deliberately keeps streaming (it does not buffer every row group to make the whole file
/// atomic): the honest, design-consistent guarantee is that a batch is never torn, not that the whole
/// file is validated before the first batch.</para>
/// <para><b>Decode ceiling (H4, design §5.4 C-DECODE).</b> Before the eager per-column allocation, an
/// attacker-controllable <c>RowCount</c> is cross-checked against the physical stream length via
/// <see cref="EnsureDecodeCeiling"/>, failing closed on an implausible ratio so a crafted footer cannot
/// drive an out-of-memory decompression bomb.</para>
/// </remarks>
internal sealed class ParquetFileReader
{
    /// <summary>The decompression-bomb decode ceiling: the maximum plausible rows a single byte of the
    /// physical stream can encode (design §5.4 C-DECODE). Even a fully dictionary/RLE-encoded row costs
    /// more than a fraction of a byte once page/definition-level overhead is counted, so a
    /// <c>RowCount</c> exceeding <c>streamLength × MaxRowsPerByte</c> is a crafted footer, not a real
    /// file. Chosen generously so no legitimate file is ever rejected.</summary>
    internal const long MaxRowsPerByte = 64;

    /// <summary>A row-group pruning hint: return <see langword="false"/> to skip a row group whose
    /// <see cref="RowGroupStatistics"/> prove it cannot match. Pruning is a hint only — a kept group is
    /// still read in full and the residual predicate is the engine's responsibility (design §2.9.1).</summary>
    public delegate bool RowGroupPredicate(RowGroupStatistics statistics);

    /// <summary>Reads <paramref name="input"/>, projecting to <paramref name="requested"/> (a subset of
    /// the file schema by field name) and optionally skipping row groups via
    /// <paramref name="keepRowGroup"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeltaStorageException">A requested column type is unsupported
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>); the resolved file column's physical type or
    /// nullability does not match the requested engine type
    /// (<see cref="StorageErrorKind.SchemaMismatch"/>); or the file is malformed/truncated, a requested
    /// column is absent, or a row group's declared size exceeds the decode ceiling
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
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

        // Physical stream length for the decode ceiling (H4); -1 when the stream is not seekable.
        long streamLength = input.CanSeek ? input.Length : -1;

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            // Structural validation happens here (footer read at open) — schema/type mismatches fail
            // before any batch is yielded (H3).
            DataField[] fileFields = ResolveFileFields(reader.Schema, requested);
            for (int group = 0; group < reader.RowGroupCount; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ColumnBatch? batch = await ReadRowGroupAsync(
                    reader, group, requested, fileFields, keepRowGroup, streamLength, cancellationToken)
                    .ConfigureAwait(false);
                if (batch is not null)
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>Fails closed when a row group's declared <paramref name="rowCount"/> is implausible for
    /// the physical <paramref name="streamLength"/> (design §5.4 C-DECODE decompression-bomb control),
    /// so a crafted footer cannot drive an out-of-memory allocation. A negative row count is likewise
    /// rejected. No-op when the stream length is unknown (<paramref name="streamLength"/> &lt; 0).</summary>
    /// <exception cref="DeltaStorageException"><paramref name="rowCount"/> is negative or exceeds the
    /// decode ceiling (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    internal static void EnsureDecodeCeiling(long rowCount, long streamLength, int group)
    {
        if (rowCount < 0)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares a negative row count ({rowCount}).");
        }

        if (streamLength >= 0 && rowCount > streamLength * MaxRowsPerByte)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares {rowCount} rows for a {streamLength}-byte stream, exceeding "
                + $"the decode ceiling of {MaxRowsPerByte} rows/byte (possible decompression bomb).");
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
            StructField requestedField = requested[c];
            string name = requestedField.Name;
            if (!byName.TryGetValue(name, out DataField? field))
            {
                throw DeltaStorageException.CorruptData(
                    $"Requested column '{name}' is not present in the Parquet file schema.");
            }

            ValidateFileField(field, requestedField);
            resolved[c] = field;
        }

        return resolved;
    }

    // M2: cross-check the resolved file field's physical type, temporal annotation, decimal
    // precision/scale, and nullability against the requested engine type. A mismatch is a DISTINCT
    // SchemaMismatch error (not a generic "malformed" one), so a schema-evolution/type surprise is
    // never silently coerced or masked as corruption.
    private static void ValidateFileField(DataField fileField, StructField requestedField)
    {
        DataField expected = ParquetTypeMapping.CreateField(requestedField);

        if (fileField.ClrType != expected.ClrType)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{requestedField.Name}': file physical type '{fileField.ClrType.Name}' does not "
                + $"match the requested engine type '{requestedField.DataType.SimpleString}' "
                + $"(expected '{expected.ClrType.Name}').");
        }

        // A nullable file column cannot be read into a column the writer would have emitted as
        // non-nullable without risking a null in a required lane; reject rather than coerce. We compare
        // against the EXPECTED field's nullability (not the requested engine flag) because Parquet.Net
        // always models string/binary as nullable, so a required string legitimately maps to a nullable
        // physical column and must not trip this guard.
        if (fileField.IsNullable && !expected.IsNullable)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{requestedField.Name}': the file column is nullable but the requested engine "
                + "type is non-nullable.");
        }

        switch (requestedField.DataType)
        {
            case DateType:
                if (fileField is not DateTimeDataField { DateTimeFormat: DateTimeFormat.Date })
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{requestedField.Name}': expected a DATE column but the file annotation "
                        + "is not DATE.");
                }

                break;

            case TimestampType:
                if (fileField is not DateTimeDataField timestampField
                    || timestampField.DateTimeFormat == DateTimeFormat.Date)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{requestedField.Name}': expected a TIMESTAMP column but the file "
                        + "annotation is DATE or not a temporal type.");
                }

                break;

            case DecimalType decimalType:
                if (fileField is not DecimalDataField decimalField
                    || decimalField.Precision != decimalType.Precision
                    || decimalField.Scale != decimalType.Scale)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{requestedField.Name}': file decimal type does not match the requested "
                        + $"'{decimalType.SimpleString}' (precision/scale differ).");
                }

                break;

            default:
                break;
        }
    }

    private static async Task<ColumnBatch?> ReadRowGroupAsync(
        ParquetReader reader,
        int group,
        StructType requested,
        DataField[] fileFields,
        RowGroupPredicate? keepRowGroup,
        long streamLength,
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

        // H4/L3: reject an implausible or out-of-range row count BEFORE the eager allocation below, so a
        // crafted footer surfaces as a deterministic CorruptData error rather than an OOM or a raw
        // OverflowException escaping the codec contract.
        long declaredRows = rowGroup.RowCount;
        EnsureDecodeCeiling(declaredRows, streamLength, group);
        int rowCount;
        try
        {
            rowCount = checked((int)declaredRows);
        }
        catch (OverflowException ex)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares {declaredRows} rows, exceeding Int32.MaxValue.", ex);
        }

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

    // M9: for fixed-width primitive columns the read path materializes into the ColumnVector's backing
    // array with no per-row object allocation (AC #180.3). String/binary columns still materialize one
    // managed object per row via Parquet.Net's decoder; a native UTF-8/byte decode that removes that
    // per-row allocation is a documented, tracked follow-up (not in FEAT-05.1 scope).
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
                // L1: thread decimalType through a non-capturing static reader instead of a closure so
                // no per-column-chunk delegate allocation occurs (mirrors the static primitive delegates).
                await ReadDecimalAsync(rowGroup, fileField, vector, rowCount, decimalType, cancellationToken)
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

    private static async Task ReadDecimalAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        DecimalType decimalType,
        CancellationToken cancellationToken)
    {
        if (fileField.IsNullable)
        {
            var buffer = new decimal?[rowCount];
            await rowGroup.ReadAsync<decimal>(fileField, new Memory<decimal?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    ParquetTypeMapping.AppendDecimal(vector, decimalType, value);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new decimal[rowCount];
            await rowGroup.ReadAsync<decimal>(fileField, new Memory<decimal>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                ParquetTypeMapping.AppendDecimal(vector, decimalType, buffer[i]);
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

    // M2: the narrow set of exceptions Parquet.Net actually raises for a malformed, truncated, or
    // otherwise undecodable stream — I/O/format faults plus Parquet.Net's own format exceptions. Broad
    // CLR exceptions (InvalidOperationException/ArgumentException/IndexOutOfRangeException/
    // NotSupportedException/OverflowException/FormatException) are deliberately NOT swallowed here, so a
    // genuine bug in our own decode path surfaces as itself instead of being masked as "corrupt data".
    private static bool IsParquetDefect(Exception ex) => ex is
        IOException or
        InvalidDataException or
        EndOfStreamException or
        global::Parquet.ParquetException or
        global::Parquet.Meta.Proto.ThriftProtocolException;

    /// <summary>
    /// A read-only view of one row group's per-column statistics, exposed to a
    /// <see cref="RowGroupPredicate"/> for pruning.
    /// </summary>
    /// <remarks>
    /// <para><b>Min/Max are ENGINE-LANE-normalized (M3).</b> <see cref="Min"/>/<see cref="Max"/> return
    /// values in the same physical lane space the reader decodes columns into — <c>int</c> epoch-day for
    /// DATE, <c>long</c> epoch-microseconds for TIMESTAMP, unscaled <c>Int128</c> for DECIMAL — so a
    /// lane-space pruning predicate compares apples to apples and can never silently drop a matching row.
    /// The raw Parquet-space values remain available via <see cref="RawMin"/>/<see cref="RawMax"/>.</para>
    /// <para><b>Pruning is safe-by-construction (M6).</b> When a statistic is missing, cannot be
    /// lane-normalized without loss (a wide DECIMAL whose stat is a logical <c>BigDecimal</c>), or is a
    /// <c>NaN</c>-poisoned float/double bound, <see cref="Min"/>/<see cref="Max"/> return
    /// <see langword="null"/> — meaning "cannot prune", so the residual predicate is always evaluated and
    /// no row is ever wrongly skipped. TIMESTAMP bounds are additionally widened by ±1&#160;ms because
    /// Parquet's timestamp statistics are millisecond-truncated toward zero, keeping <see cref="Min"/> a
    /// true lower bound and <see cref="Max"/> a true upper bound.</para>
    /// </remarks>
    internal sealed class RowGroupStatistics
    {
        private readonly Dictionary<string, ColumnEntry> _byColumn;

        internal RowGroupStatistics(ParquetRowGroupReader rowGroup, StructType requested, DataField[] fileFields)
        {
            RowCount = rowGroup.RowCount;
            _byColumn = new Dictionary<string, ColumnEntry>(StringComparer.Ordinal);
            for (int c = 0; c < requested.Count; c++)
            {
                DataColumnStatistics? statistics =
                    rowGroup.ColumnExists(fileFields[c]) ? rowGroup.GetStatistics(fileFields[c]) : null;
                _byColumn[requested[c].Name] = new ColumnEntry(requested[c].DataType, statistics);
            }
        }

        /// <summary>The number of rows in the row group.</summary>
        public long RowCount { get; }

        /// <summary>The raw Parquet.Net statistics for <paramref name="column"/>, or
        /// <see langword="null"/> if absent.</summary>
        public DataColumnStatistics? ForColumn(string column) =>
            _byColumn.TryGetValue(column, out ColumnEntry entry) ? entry.Statistics : null;

        /// <summary>The engine-lane-normalized minimum for <paramref name="column"/>, or
        /// <see langword="null"/> when pruning is not safe (see the type remarks).</summary>
        public object? Min(string column) => LaneBound(column, isMin: true);

        /// <summary>The engine-lane-normalized maximum for <paramref name="column"/>, or
        /// <see langword="null"/> when pruning is not safe (see the type remarks).</summary>
        public object? Max(string column) => LaneBound(column, isMin: false);

        /// <summary>The raw Parquet-space minimum for <paramref name="column"/> (before lane
        /// normalization), or <see langword="null"/> if absent.</summary>
        public object? RawMin(string column) => ForColumn(column)?.MinValue;

        /// <summary>The raw Parquet-space maximum for <paramref name="column"/> (before lane
        /// normalization), or <see langword="null"/> if absent.</summary>
        public object? RawMax(string column) => ForColumn(column)?.MaxValue;

        /// <summary>The null count recorded for <paramref name="column"/>, or <see langword="null"/>.</summary>
        public long? NullCount(string column) =>
            _byColumn.TryGetValue(column, out ColumnEntry entry) ? entry.Statistics?.NullCount : null;

        private object? LaneBound(string column, bool isMin)
        {
            if (!_byColumn.TryGetValue(column, out ColumnEntry entry) || entry.Statistics is null)
            {
                return null;
            }

            object? raw = isMin ? entry.Statistics.MinValue : entry.Statistics.MaxValue;
            if (raw is null)
            {
                return null;
            }

            return Normalize(entry.DataType, raw, isMin);
        }

        // Convert a Parquet-space statistic to the engine lane value, returning null ("cannot prune")
        // whenever normalization would be lossy/ambiguous so a row is never wrongly skipped (M3/M6).
        private static object? Normalize(DataType type, object raw, bool isMin)
        {
            switch (type)
            {
                case BooleanType when raw is bool b:
                    return b;
                case ByteType when raw is int i:
                    return (sbyte)i; // INT8 stat is the signed logical tinyint value.
                case ShortType when raw is int i:
                    return (short)i;
                case IntegerType when raw is int i:
                    return i;
                case LongType when raw is long l:
                    return l;
                case FloatType when raw is float f:
                    return float.IsNaN(f) ? null : f; // NaN-poisoned bound => cannot prune.
                case DoubleType when raw is double d:
                    return double.IsNaN(d) ? null : d;
                case DateType when raw is int i:
                    return i; // Epoch-day already equals the engine lane.
                case TimestampType when raw is long millis:
                    return WidenTimestampMillis(millis, isMin);
                case DecimalType decimalType:
                    return NormalizeDecimal(decimalType, raw);
                default:
                    // string/binary and any unexpected physical type: not prunable in v1.
                    return null;
            }
        }

        // Parquet timestamp statistics are millisecond-truncated toward zero, so the engine-lane
        // (microsecond) bound must be widened by ±1 ms to stay a true lower/upper bound regardless of
        // sign; overflow of the widening or the ms→µs scale => null ("cannot prune").
        private static object? WidenTimestampMillis(long millis, bool isMin)
        {
            try
            {
                long widenedMillis = checked(isMin ? millis - 1 : millis + 1);
                return checked(widenedMillis * 1000L);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        private static object? NormalizeDecimal(DecimalType decimalType, object raw)
        {
            // A compact decimal (precision ≤ 18) stat is the Int64 unscaled value == the engine lane; a
            // wide decimal stat is a logical BigDecimal we cannot losslessly lane-normalize here, so we
            // return null (cannot prune) rather than risk an incorrect bound.
            return raw switch
            {
                long unscaled => (Int128)unscaled,
                int unscaled => (Int128)unscaled,
                _ => null,
            };
        }

        private readonly struct ColumnEntry
        {
            internal ColumnEntry(DataType dataType, DataColumnStatistics? statistics)
            {
                DataType = dataType;
                Statistics = statistics;
            }

            internal DataType DataType { get; }

            internal DataColumnStatistics? Statistics { get; }
        }
    }
}
