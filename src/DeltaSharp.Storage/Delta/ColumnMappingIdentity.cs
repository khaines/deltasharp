using System.Collections.Immutable;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The column-mapping IDENTITY of a table's metadata: its mode, its partition-column set, and each logical
/// column's assigned <c>delta.columnMapping.id</c> (field id) plus physical name. A Change Data Feed read
/// interprets every file it touches — including a file a <c>remove</c> references but that was authored at a
/// prior version — through the END snapshot's identity (physical-name / field-id resolution). Delta
/// column-mapping identity is <b>immutable</b> for a legitimate table (mode is creation-only/sticky; field
/// ids and physical names are assigned once), so a retained historical version whose identity differs from
/// the end is a forged/illegal <c>_delta_log</c>: reading its file through the end identity would surface
/// MISMAPPED change rows (a mode flip, a field-id reassignment, or a physical-name change). This value type
/// is the single source of truth both the pre-range history scan (<see cref="DeltaLog"/>) and the in-range
/// per-version check (<c>ChangeFeedReader</c>) use to enforce that immutability across the full retained
/// window a CDF read can touch (#671).
/// </summary>
internal readonly struct ColumnMappingIdentity
{
    private readonly ColumnMappingMode _mode;
    private readonly ImmutableArray<string> _partitionColumns;
    private readonly Dictionary<string, ColumnKey> _columns;

    private ColumnMappingIdentity(
        ColumnMappingMode mode, ImmutableArray<string> partitionColumns, Dictionary<string, ColumnKey> columns)
    {
        _mode = mode;
        _partitionColumns = partitionColumns.IsDefault ? ImmutableArray<string>.Empty : partitionColumns;
        _columns = columns;
    }

    /// <summary>
    /// Extracts the column-mapping identity from a <paramref name="metadata"/> action, keying each column by
    /// its fully-qualified logical path (recursing into structs). CDF output is by logical schema, so keying
    /// by logical path is correct: two versions "agree" on a column when the same logical column maps to the
    /// same physical identity.
    /// </summary>
    /// <exception cref="SchemaValidationException">The <c>schemaString</c> is unparseable (a forged /
    /// inconsistent log); the caller fails the read closed.</exception>
    internal static ColumnMappingIdentity FromMetadata(MetadataAction metadata)
    {
        var columns = new Dictionary<string, ColumnKey>(StringComparer.Ordinal);
        if (SchemaJson.FromJson(metadata.SchemaString) is StructType schema)
        {
            Collect(schema, string.Empty, columns);
        }

        return new ColumnMappingIdentity(
            ColumnMapping.ResolveMode(metadata.Configuration), metadata.PartitionColumns, columns);
    }

    /// <summary>
    /// True if <paramref name="historical"/> is a legal (immutability-preserving) predecessor of THIS (end)
    /// identity: identical mode, identical partition-column set, and — for every column present in BOTH — an
    /// identical (field id, physical name). A column present on only one side is permitted, because
    /// legitimate schema evolution ADDS columns; only a COMMON column whose identity changed is a violation.
    /// A logical rename (column-mapping) keeps id + physical name but changes the logical path, so it changes
    /// the key and is neither falsely flagged (legal) nor able to mask a reassignment of a still-present
    /// logical column (that column stays keyed and is compared).
    /// </summary>
    internal bool IsImmutableFrom(in ColumnMappingIdentity historical)
    {
        if (_mode != historical._mode
            || !_partitionColumns.SequenceEqual(historical._partitionColumns, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (KeyValuePair<string, ColumnKey> end in _columns)
        {
            if (historical._columns.TryGetValue(end.Key, out ColumnKey past) && past != end.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static void Collect(StructType schema, string prefix, Dictionary<string, ColumnKey> into)
    {
        foreach (StructField field in schema)
        {
            string path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
            long? id = ColumnMapping.TryGetId(field, out long value) ? value : null;
            string? physicalName =
                field.Metadata.TryGetString(ColumnMapping.PhysicalNameKey, out string? physical) && physical.Length > 0
                    ? physical
                    : null;
            into[path] = new ColumnKey(id, physicalName);
            if (field.DataType is StructType nested)
            {
                Collect(nested, path, into);
            }
        }
    }

    // A single column's column-mapping identity: its field id and physical name (either may be absent under
    // no-mapping). Record-struct value equality drives the immutability compare.
    private readonly record struct ColumnKey(long? Id, string? PhysicalName);
}
