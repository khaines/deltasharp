using System.Collections.Immutable;
using System.Globalization;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Decodes one checkpoint row group's columns (via <see cref="CheckpointSchema"/>) into row-aligned
/// scalar arrays and per-row maps/lists, then assembles one <see cref="DeltaAction"/> per row. The Dremel
/// reconstruction of nested maps/lists follows the definition/repetition levels of the low-level raw
/// column data (design §2.10.3). All parsing is fail-closed: a physical-type surprise, a slot-count
/// mismatch, or an action missing a required field throws <see cref="DeltaProtocolException"/> so the
/// caller falls back to JSON replay rather than materialize invented state.
/// </summary>
internal sealed class CheckpointColumns
{
    private static readonly ImmutableSortedDictionary<string, string?> EmptyNullableMap =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> EmptyMap =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    // add
    private string?[] _addPath = [];
    private long?[] _addSize = [];
    private long?[] _addModificationTime = [];
    private bool?[] _addDataChange = [];
    private string?[] _addStats = [];
    private ImmutableSortedDictionary<string, string?>[] _addPartitionValues = [];
    private ImmutableSortedDictionary<string, string>[] _addTags = [];

    // remove
    private string?[] _removePath = [];
    private long?[] _removeDeletionTimestamp = [];
    private bool?[] _removeDataChange = [];
    private bool?[] _removeExtendedFileMetadata = [];
    private long?[] _removeSize = [];
    private ImmutableSortedDictionary<string, string?>[] _removePartitionValues = [];

    // metaData
    private string?[] _metaId = [];
    private string?[] _metaName = [];
    private string?[] _metaDescription = [];
    private string?[] _metaSchemaString = [];
    private long?[] _metaCreatedTime = [];
    private string?[] _formatProvider = [];
    private ImmutableSortedDictionary<string, string>[] _formatOptions = [];
    private ImmutableArray<string>[] _metaPartitionColumns = [];
    private ImmutableSortedDictionary<string, string>[] _metaConfiguration = [];

    // protocol
    private int?[] _protocolMinReaderVersion = [];
    private int?[] _protocolMinWriterVersion = [];
    private ImmutableArray<string>[] _protocolReaderFeatures = [];
    private ImmutableArray<string>[] _protocolWriterFeatures = [];

    // txn
    private string?[] _txnAppId = [];
    private long?[] _txnVersion = [];
    private long?[] _txnLastUpdated = [];

    // Per-row "the action struct is present" flags, derived from each action's primary-key column
    // definition levels (def ≥ 1). Used to fail closed on a partial action row whose struct is present but
    // whose required key is null — even when its only content is an empty map/list (which field-content
    // checks cannot detect).
    private bool[] _addPresent = [];
    private bool[] _removePresent = [];
    private bool[] _metaPresent = [];
    private bool[] _protocolPresent = [];
    private bool[] _txnPresent = [];

    private CheckpointColumns()
    {
    }

    public static async Task<CheckpointColumns> ReadAsync(
        ParquetRowGroupReader rowGroup, CheckpointSchema schema, int rowCount, CancellationToken cancellationToken)
    {
        var addPresent = new bool[rowCount];
        var removePresent = new bool[rowCount];
        var metaPresent = new bool[rowCount];
        var protocolPresent = new bool[rowCount];
        var txnPresent = new bool[rowCount];
        var columns = new CheckpointColumns
        {
            _addPresent = addPresent,
            _removePresent = removePresent,
            _metaPresent = metaPresent,
            _protocolPresent = protocolPresent,
            _txnPresent = txnPresent,
            _addPath = await ReadStringScalarAsync(rowGroup, schema.AddPath, rowCount, cancellationToken, addPresent).ConfigureAwait(false),
            _addSize = await ReadLongScalarAsync(rowGroup, schema.AddSize, rowCount, cancellationToken).ConfigureAwait(false),
            _addModificationTime = await ReadLongScalarAsync(rowGroup, schema.AddModificationTime, rowCount, cancellationToken).ConfigureAwait(false),
            _addDataChange = await ReadBoolScalarAsync(rowGroup, schema.AddDataChange, rowCount, cancellationToken).ConfigureAwait(false),
            _addStats = await ReadStringScalarAsync(rowGroup, schema.AddStats, rowCount, cancellationToken).ConfigureAwait(false),
            _addPartitionValues = await ReadNullableMapAsync(rowGroup, schema.AddPartitionValues, rowCount, cancellationToken).ConfigureAwait(false),
            _addTags = await ReadStringMapAsync(rowGroup, schema.AddTags, rowCount, cancellationToken).ConfigureAwait(false),

            _removePath = await ReadStringScalarAsync(rowGroup, schema.RemovePath, rowCount, cancellationToken, removePresent).ConfigureAwait(false),
            _removeDeletionTimestamp = await ReadLongScalarAsync(rowGroup, schema.RemoveDeletionTimestamp, rowCount, cancellationToken).ConfigureAwait(false),
            _removeDataChange = await ReadBoolScalarAsync(rowGroup, schema.RemoveDataChange, rowCount, cancellationToken).ConfigureAwait(false),
            _removeExtendedFileMetadata = await ReadBoolScalarAsync(rowGroup, schema.RemoveExtendedFileMetadata, rowCount, cancellationToken).ConfigureAwait(false),
            _removeSize = await ReadLongScalarAsync(rowGroup, schema.RemoveSize, rowCount, cancellationToken).ConfigureAwait(false),
            _removePartitionValues = await ReadNullableMapAsync(rowGroup, schema.RemovePartitionValues, rowCount, cancellationToken).ConfigureAwait(false),

            _metaId = await ReadStringScalarAsync(rowGroup, schema.MetaId, rowCount, cancellationToken, metaPresent).ConfigureAwait(false),
            _metaName = await ReadStringScalarAsync(rowGroup, schema.MetaName, rowCount, cancellationToken).ConfigureAwait(false),
            _metaDescription = await ReadStringScalarAsync(rowGroup, schema.MetaDescription, rowCount, cancellationToken).ConfigureAwait(false),
            _metaSchemaString = await ReadStringScalarAsync(rowGroup, schema.MetaSchemaString, rowCount, cancellationToken).ConfigureAwait(false),
            _metaCreatedTime = await ReadLongScalarAsync(rowGroup, schema.MetaCreatedTime, rowCount, cancellationToken).ConfigureAwait(false),
            _formatProvider = await ReadStringScalarAsync(rowGroup, schema.FormatProvider, rowCount, cancellationToken).ConfigureAwait(false),
            _formatOptions = await ReadStringMapAsync(rowGroup, schema.FormatOptions, rowCount, cancellationToken).ConfigureAwait(false),
            _metaPartitionColumns = await ReadStringListAsync(rowGroup, schema.MetaPartitionColumns, rowCount, cancellationToken).ConfigureAwait(false),
            _metaConfiguration = await ReadStringMapAsync(rowGroup, schema.MetaConfiguration, rowCount, cancellationToken).ConfigureAwait(false),

            _protocolMinReaderVersion = await ReadIntScalarAsync(rowGroup, schema.ProtocolMinReaderVersion, rowCount, cancellationToken, protocolPresent).ConfigureAwait(false),
            _protocolMinWriterVersion = await ReadIntScalarAsync(rowGroup, schema.ProtocolMinWriterVersion, rowCount, cancellationToken).ConfigureAwait(false),
            _protocolReaderFeatures = await ReadStringListAsync(rowGroup, schema.ProtocolReaderFeatures, rowCount, cancellationToken).ConfigureAwait(false),
            _protocolWriterFeatures = await ReadStringListAsync(rowGroup, schema.ProtocolWriterFeatures, rowCount, cancellationToken).ConfigureAwait(false),

            _txnAppId = await ReadStringScalarAsync(rowGroup, schema.TxnAppId, rowCount, cancellationToken, txnPresent).ConfigureAwait(false),
            _txnVersion = await ReadLongScalarAsync(rowGroup, schema.TxnVersion, rowCount, cancellationToken).ConfigureAwait(false),
            _txnLastUpdated = await ReadLongScalarAsync(rowGroup, schema.TxnLastUpdated, rowCount, cancellationToken).ConfigureAwait(false),
        };

        return columns;
    }

    /// <summary>Builds the single action a checkpoint row encodes (exactly one action struct is non-null),
    /// or null for an all-null row (defensively tolerated, like an empty JSON action object).</summary>
    /// <exception cref="DeltaProtocolException">The row encodes more than one action, an action is
    /// present but missing its required primary key, or a required field is absent.</exception>
    public DeltaAction? BuildAction(int row, int group)
    {
        bool isAdd = _addPath[row] is not null;
        bool isRemove = _removePath[row] is not null;
        bool isMeta = _metaId[row] is not null;
        bool isProtocol = _protocolMinReaderVersion[row] is not null;
        bool isTxn = _txnAppId[row] is not null;

        int count = (isAdd ? 1 : 0) + (isRemove ? 1 : 0) + (isMeta ? 1 : 0) + (isProtocol ? 1 : 0) + (isTxn ? 1 : 0);
        if (count > 1)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Checkpoint row {row} (group {group}) encodes {count} actions but a row must encode exactly one."));
        }

        // Fail closed on a PARTIAL action: a row whose action struct is present (its primary-key column's
        // definition level says so) but whose required primary key is null is a malformed/corrupt checkpoint.
        // It must throw (→ the caller falls back to JSON replay), never be silently skipped — a silent skip
        // would drop a committed file or the metaData from the reconstructed state with no error. The
        // struct-present signal (from the key column's def level) catches a struct whose only content is an
        // empty map/list; the field-content disjunction is a redundant, defence-in-depth backstop.
        RequireKeyIfPresent(!isAdd, "add", "path", row, group,
            _addPresent[row]
            || _addSize[row] is not null || _addModificationTime[row] is not null || _addDataChange[row] is not null
            || _addStats[row] is not null || _addPartitionValues[row].Count > 0 || _addTags[row].Count > 0);
        RequireKeyIfPresent(!isRemove, "remove", "path", row, group,
            _removePresent[row]
            || _removeDeletionTimestamp[row] is not null || _removeDataChange[row] is not null
            || _removeExtendedFileMetadata[row] is not null || _removeSize[row] is not null
            || _removePartitionValues[row].Count > 0);
        RequireKeyIfPresent(!isMeta, "metaData", "id", row, group,
            _metaPresent[row]
            || _metaName[row] is not null || _metaDescription[row] is not null || _metaSchemaString[row] is not null
            || _metaCreatedTime[row] is not null || _formatProvider[row] is not null || _formatOptions[row].Count > 0
            || _metaPartitionColumns[row].Length > 0 || _metaConfiguration[row].Count > 0);
        RequireKeyIfPresent(!isProtocol, "protocol", "minReaderVersion", row, group,
            _protocolPresent[row]
            || _protocolMinWriterVersion[row] is not null || _protocolReaderFeatures[row].Length > 0
            || _protocolWriterFeatures[row].Length > 0);
        RequireKeyIfPresent(!isTxn, "txn", "appId", row, group,
            _txnPresent[row]
            || _txnVersion[row] is not null || _txnLastUpdated[row] is not null);

        if (isAdd)
        {
            long size = Require(_addSize[row], "add", "size", row, group);
            return new AddFileAction(
                _addPath[row]!,
                _addPartitionValues[row],
                size,
                _addModificationTime[row] ?? 0L,
                _addDataChange[row] ?? true,
                DeltaLogActionReader.ParseStatsString(_addStats[row]),
                _addTags[row]);
        }

        if (isRemove)
        {
            return new RemoveFileAction(
                _removePath[row]!,
                _removeDeletionTimestamp[row],
                _removeDataChange[row] ?? true,
                _removeExtendedFileMetadata[row] ?? false,
                _removePartitionValues[row],
                _removeSize[row]);
        }

        if (isMeta)
        {
            string schemaString = _metaSchemaString[row]
                ?? throw Missing("metaData", "schemaString", row, group);
            string provider = _formatProvider[row]
                ?? throw Missing("metaData.format", "provider", row, group);
            return new MetadataAction(
                _metaId[row]!,
                _metaName[row],
                _metaDescription[row],
                new TableFormat(provider, _formatOptions[row]),
                schemaString,
                _metaPartitionColumns[row],
                _metaConfiguration[row],
                _metaCreatedTime[row]);
        }

        if (isProtocol)
        {
            int minWriter = Require(_protocolMinWriterVersion[row], "protocol", "minWriterVersion", row, group);
            return new ProtocolAction(
                _protocolMinReaderVersion[row]!.Value,
                minWriter,
                _protocolReaderFeatures[row],
                _protocolWriterFeatures[row]);
        }

        if (isTxn)
        {
            long version = Require(_txnVersion[row], "txn", "version", row, group);
            return new TxnAction(_txnAppId[row]!, version, _txnLastUpdated[row]);
        }

        return null;
    }

    private static T Require<T>(T? value, string action, string field, int row, int group) where T : struct =>
        value ?? throw Missing(action, field, row, group);

    /// <summary>Fails closed when an action struct is present (<paramref name="anyFieldPresent"/>) yet its
    /// required primary key is absent (<paramref name="keyAbsent"/>) — a partial/corrupt checkpoint row
    /// that must never be silently skipped.</summary>
    private static void RequireKeyIfPresent(
        bool keyAbsent, string action, string keyField, int row, int group, bool anyFieldPresent)
    {
        if (keyAbsent && anyFieldPresent)
        {
            throw Missing(action, keyField, row, group);
        }
    }

    private static DeltaProtocolException Missing(string action, string field, int row, int group) =>
        DeltaProtocolException.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"Checkpoint '{action}' at row {row} (group {group}) is missing required field '{field}'."));

    // ---- scalar column readers (row-aligned nullable arrays) ----

    private static async Task<string?[]> ReadStringScalarAsync(
        ParquetRowGroupReader rowGroup, DataField? field, int rowCount, CancellationToken cancellationToken,
        bool[]? structPresent = null)
    {
        var result = new string?[rowCount];
        if (field is null)
        {
            return result;
        }

        RawColumn<ReadOnlyMemory<char>> col = await ReadRawAsync<ReadOnlyMemory<char>>(rowGroup, field, cancellationToken).ConfigureAwait(false);
        FillScalar(col, rowCount, field, (r, v) => result[r] = v.ToString(), structPresent);
        return result;
    }

    private static async Task<long?[]> ReadLongScalarAsync(
        ParquetRowGroupReader rowGroup, DataField? field, int rowCount, CancellationToken cancellationToken)
    {
        var result = new long?[rowCount];
        if (field is null)
        {
            return result;
        }

        RawColumn<long> col = await ReadRawAsync<long>(rowGroup, field, cancellationToken).ConfigureAwait(false);
        FillScalar(col, rowCount, field, (r, v) => result[r] = v);
        return result;
    }

    private static async Task<int?[]> ReadIntScalarAsync(
        ParquetRowGroupReader rowGroup, DataField? field, int rowCount, CancellationToken cancellationToken,
        bool[]? structPresent = null)
    {
        var result = new int?[rowCount];
        if (field is null)
        {
            return result;
        }

        RawColumn<int> col = await ReadRawAsync<int>(rowGroup, field, cancellationToken).ConfigureAwait(false);
        FillScalar(col, rowCount, field, (r, v) => result[r] = v, structPresent);
        return result;
    }

    private static async Task<bool?[]> ReadBoolScalarAsync(
        ParquetRowGroupReader rowGroup, DataField? field, int rowCount, CancellationToken cancellationToken)
    {
        var result = new bool?[rowCount];
        if (field is null)
        {
            return result;
        }

        RawColumn<bool> col = await ReadRawAsync<bool>(rowGroup, field, cancellationToken).ConfigureAwait(false);
        FillScalar(col, rowCount, field, (r, v) => result[r] = v);
        return result;
    }

    private static void FillScalar<T>(RawColumn<T> col, int rowCount, DataField field, Action<int, T> assign,
        bool[]? structPresent = null)
        where T : struct
    {
        if (col.MaxRepetition != 0)
        {
            throw DeltaProtocolException.Malformed($"Checkpoint scalar column '{field.Path}' is unexpectedly repeated.");
        }

        if (col.MaxDefinition == 0)
        {
            // A required column with no null encoding: one value per row.
            if (col.Values.Length != rowCount)
            {
                throw SlotMismatch(field, col.Values.Length, rowCount);
            }

            for (int r = 0; r < rowCount; r++)
            {
                assign(r, col.Values[r]);
                if (structPresent is not null)
                {
                    structPresent[r] = true;
                }
            }

            return;
        }

        if (col.Definition.Length != rowCount)
        {
            throw SlotMismatch(field, col.Definition.Length, rowCount);
        }

        int valueIndex = 0;
        for (int r = 0; r < rowCount; r++)
        {
            if (col.Definition[r] == col.MaxDefinition)
            {
                assign(r, col.Values[valueIndex++]);
            }

            // Definition ≥ 1 means the enclosing (optional) action struct is PRESENT for this row, even
            // when the key value itself is null — the signal BuildAction uses to fail closed on a partial
            // action whose only content is empty maps/lists (which the field-content check cannot see).
            if (structPresent is not null)
            {
                structPresent[r] = col.Definition[r] >= 1;
            }
        }
    }

    // ---- map / list column readers (per-row reconstruction from Dremel levels) ----

    private static async Task<ImmutableSortedDictionary<string, string?>[]> ReadNullableMapAsync(
        ParquetRowGroupReader rowGroup, CheckpointSchema.MapLeaves? leaves, int rowCount, CancellationToken cancellationToken)
    {
        var result = new ImmutableSortedDictionary<string, string?>[rowCount];
        if (leaves is null)
        {
            Array.Fill(result, EmptyNullableMap);
            return result;
        }

        var builders = new ImmutableSortedDictionary<string, string?>.Builder?[rowCount];
        RawColumn<ReadOnlyMemory<char>> keys = await ReadRawAsync<ReadOnlyMemory<char>>(rowGroup, leaves.Key, cancellationToken).ConfigureAwait(false);
        RawColumn<ReadOnlyMemory<char>> values = await ReadRawAsync<ReadOnlyMemory<char>>(rowGroup, leaves.Value, cancellationToken).ConfigureAwait(false);
        ForEachMapEntry(keys, values, rowCount, leaves.Key, (row, key, value) =>
        {
            (builders[row] ??= ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal))[key] = value;
        });

        for (int r = 0; r < rowCount; r++)
        {
            result[r] = builders[r]?.ToImmutable() ?? EmptyNullableMap;
        }

        return result;
    }

    private static async Task<ImmutableSortedDictionary<string, string>[]> ReadStringMapAsync(
        ParquetRowGroupReader rowGroup, CheckpointSchema.MapLeaves? leaves, int rowCount, CancellationToken cancellationToken)
    {
        var result = new ImmutableSortedDictionary<string, string>[rowCount];
        if (leaves is null)
        {
            Array.Fill(result, EmptyMap);
            return result;
        }

        var builders = new ImmutableSortedDictionary<string, string>.Builder?[rowCount];
        RawColumn<ReadOnlyMemory<char>> keys = await ReadRawAsync<ReadOnlyMemory<char>>(rowGroup, leaves.Key, cancellationToken).ConfigureAwait(false);
        RawColumn<ReadOnlyMemory<char>> values = await ReadRawAsync<ReadOnlyMemory<char>>(rowGroup, leaves.Value, cancellationToken).ConfigureAwait(false);
        ForEachMapEntry(keys, values, rowCount, leaves.Key, (row, key, value) =>
        {
            // Delta string-valued maps (tags/configuration/format.options) carry non-null values; a null
            // value is dropped defensively (advisory) rather than fabricating an empty string.
            if (value is not null)
            {
                (builders[row] ??= ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal))[key] = value;
            }
        });

        for (int r = 0; r < rowCount; r++)
        {
            result[r] = builders[r]?.ToImmutable() ?? EmptyMap;
        }

        return result;
    }

    private static async Task<ImmutableArray<string>[]> ReadStringListAsync(
        ParquetRowGroupReader rowGroup, DataField? element, int rowCount, CancellationToken cancellationToken)
    {
        var result = new ImmutableArray<string>[rowCount];
        if (element is null)
        {
            Array.Fill(result, ImmutableArray<string>.Empty);
            return result;
        }

        var builders = new ImmutableArray<string>.Builder?[rowCount];
        RawColumn<ReadOnlyMemory<char>> elements = await ReadRawAsync<ReadOnlyMemory<char>>(rowGroup, element, cancellationToken).ConfigureAwait(false);
        ForEachListElement(elements, rowCount, element, (row, value) =>
        {
            (builders[row] ??= ImmutableArray.CreateBuilder<string>()).Add(value);
        });

        for (int r = 0; r < rowCount; r++)
        {
            result[r] = builders[r]?.ToImmutable() ?? ImmutableArray<string>.Empty;
        }

        return result;
    }

    private static void ForEachMapEntry(
        RawColumn<ReadOnlyMemory<char>> keys,
        RawColumn<ReadOnlyMemory<char>> values,
        int rowCount,
        DataField keyField,
        Action<int, string, string?> onEntry)
    {
        if (keys.MaxRepetition == 0)
        {
            throw DeltaProtocolException.Malformed($"Checkpoint map column '{keyField.Path}' is not repeated.");
        }

        if (keys.Definition.Length != values.Definition.Length)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Checkpoint map column '{keyField.Path}' has mismatched key/value slot counts "
                + $"({keys.Definition.Length} vs {values.Definition.Length})."));
        }

        int slots = keys.Definition.Length;
        int keyIndex = 0;
        int valueIndex = 0;
        int row = -1;
        for (int i = 0; i < slots; i++)
        {
            if (keys.Repetition[i] == 0)
            {
                row++;
            }

            EnsureRowInRange(row, rowCount, keyField);
            if (keys.Definition[i] == keys.MaxDefinition)
            {
                string key = keys.Values[keyIndex++].ToString();
                string? value = values.Definition[i] == values.MaxDefinition ? values.Values[valueIndex++].ToString() : null;
                onEntry(row, key, value);
            }
        }

        EnsureRowCount(row, rowCount, keyField);
    }

    private static void ForEachListElement(
        RawColumn<ReadOnlyMemory<char>> elements, int rowCount, DataField elementField, Action<int, string> onElement)
    {
        if (elements.MaxRepetition == 0)
        {
            throw DeltaProtocolException.Malformed($"Checkpoint list column '{elementField.Path}' is not repeated.");
        }

        int slots = elements.Definition.Length;
        int elementIndex = 0;
        int row = -1;
        for (int i = 0; i < slots; i++)
        {
            if (elements.Repetition[i] == 0)
            {
                row++;
            }

            EnsureRowInRange(row, rowCount, elementField);
            if (elements.Definition[i] == elements.MaxDefinition)
            {
                onElement(row, elements.Values[elementIndex++].ToString());
            }
        }

        EnsureRowCount(row, rowCount, elementField);
    }

    private static void EnsureRowInRange(int row, int rowCount, DataField field)
    {
        if (row < 0 || row >= rowCount)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Checkpoint column '{field.Path}' repetition levels address row {row}, outside [0, {rowCount})."));
        }
    }

    private static void EnsureRowCount(int lastRow, int rowCount, DataField field)
    {
        // Every row contributes exactly one repetition-0 slot, so the reconstruction must cover all rows.
        if (lastRow + 1 != rowCount)
        {
            throw DeltaProtocolException.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Checkpoint column '{field.Path}' reconstructed {lastRow + 1} rows but the row group has {rowCount}."));
        }
    }

    private static DeltaProtocolException SlotMismatch(DataField field, int actual, int rowCount) =>
        DeltaProtocolException.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"Checkpoint scalar column '{field.Path}' produced {actual} slots for a {rowCount}-row group."));

    // ---- low-level raw column read ----

    private readonly struct RawColumn<T>
        where T : struct
    {
        public RawColumn(T[] values, int[] definition, int[] repetition, int maxDefinition, int maxRepetition)
        {
            Values = values;
            Definition = definition;
            Repetition = repetition;
            MaxDefinition = maxDefinition;
            MaxRepetition = maxRepetition;
        }

        public T[] Values { get; }

        public int[] Definition { get; }

        public int[] Repetition { get; }

        public int MaxDefinition { get; }

        public int MaxRepetition { get; }
    }

    private static async Task<RawColumn<T>> ReadRawAsync<T>(
        ParquetRowGroupReader rowGroup, DataField field, CancellationToken cancellationToken)
        where T : struct
    {
        RawColumnData raw = await rowGroup.ReadRawColumnDataBaseAsync(field, cancellationToken).ConfigureAwait(false);
        try
        {
            return Extract<T>(raw, field);
        }
        catch (InvalidCastException ex)
        {
            throw DeltaProtocolException.Malformed(
                $"Checkpoint column '{field.Path}' has an unexpected physical type.", ex);
        }
        finally
        {
            raw.Dispose();
        }
    }

    // Synchronous so the ref-struct Span<T> from RawColumnData<T>.Values never crosses an await.
    private static RawColumn<T> Extract<T>(RawColumnData raw, DataField field)
        where T : struct
    {
        var typed = (RawColumnData<T>)raw;
        T[] values = typed.Values.ToArray();
        int[] definition = field.MaxDefinitionLevel > 0 ? raw.DefinitionLevels.ToArray() : [];
        int[] repetition = field.MaxRepetitionLevel > 0 ? raw.RepetitionLevels.ToArray() : [];
        return new RawColumn<T>(values, definition, repetition, field.MaxDefinitionLevel, field.MaxRepetitionLevel);
    }
}
