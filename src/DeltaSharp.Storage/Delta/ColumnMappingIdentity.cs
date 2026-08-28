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
    private readonly Dictionary<ColumnPathKey, ColumnKey> _columns;

    private ColumnMappingIdentity(
        ColumnMappingMode mode,
        ImmutableArray<string> partitionColumns,
        Dictionary<ColumnPathKey, ColumnKey> columns)
    {
        _mode = mode;
        _partitionColumns = partitionColumns.IsDefault ? ImmutableArray<string>.Empty : partitionColumns;
        _columns = columns;
    }

    /// <summary>
    /// Extracts the column-mapping identity from a <paramref name="metadata"/> action, keying each column by
    /// its structured logical path (an ordered segment sequence, recursing into direct structs). CDF output is
    /// by logical schema, so keying by logical path is correct: two versions "agree" on a column when the same
    /// logical column maps to the same physical identity. Structured segments make a literal top-level
    /// <c>"a.b"</c> distinct from nested <c>a</c>→<c>b</c>, closing the old dotted-key collapse.
    /// </summary>
    /// <exception cref="SchemaValidationException">The <c>schemaString</c> is unparseable OR parses to a
    /// non-<see cref="StructType"/> top-level type (a Delta table schema is ALWAYS a struct, so either is a
    /// forged / inconsistent log); the caller fails the read closed.</exception>
    /// <exception cref="DeltaProtocolException">The metadata declares an unrecognized column-mapping mode
    /// (via <see cref="ColumnMapping.ResolveMode"/>); the caller fails the read closed.</exception>
    internal static ColumnMappingIdentity FromMetadata(MetadataAction metadata)
    {
        // A Delta table schema's top level is ALWAYS a struct. An unparseable schemaString throws inside
        // SchemaJson.FromJson; a schemaString that parses to a NON-struct DataType (e.g. a bare "long") is
        // likewise a forged/inconsistent log — fail closed rather than treat it as zero columns (which would
        // silently exempt the version from the per-column identity compare).
        var columns = new Dictionary<ColumnPathKey, ColumnKey>();
        if (SchemaJson.FromJson(metadata.SchemaString) is not StructType schema)
        {
            throw new SchemaValidationException(
                "A change-feed metadata schemaString parsed to a non-struct top-level type; a Delta table "
                + "schema must be a struct, so the log is inconsistent and the read fails closed.");
        }

        ColumnMappingMode mode = ColumnMapping.ResolveMode(metadata.Configuration);
        Collect(schema, ImmutableArray<string>.Empty, columns, mode);
        return new ColumnMappingIdentity(mode, metadata.PartitionColumns, columns);
    }

    /// <summary>
    /// True if <paramref name="historical"/> is a legal (immutability-preserving) predecessor of THIS (end)
    /// identity: identical mode, identical partition-column set, and — for every column present in BOTH — an
    /// identical (field id, physical name). A column present on only one side is permitted, because legitimate
    /// schema evolution ADDS (or DROPS) columns; only a COMMON column whose identity changed is a violation.
    /// <para>A logical rename (column-mapping) keeps id + physical name but changes the logical path, so it
    /// changes the key: it is neither falsely flagged (legal) nor able to mask a reassignment of a
    /// still-present logical column (that column stays keyed and is compared).</para>
    /// <para>Alternate structured paths with the same dotted spelling (literal-dot vs nested) are handled by
    /// the structured <c>ColumnPathKey</c> (a literal top-level <c>"a.b"</c> is one segment; nested
    /// <c>a</c>→<c>b</c> is two). Under #676 nested column mapping is enabled for the single-level surface and
    /// under #866 866a for the depth&gt;1 NAME/none-mode surface: <see cref="Collect"/> descends DIRECT struct
    /// children at every depth AND name/none-mode array element / map key-value interior structs (segment
    /// tokens element/key/value), so an <c>array&lt;struct&gt;</c> / <c>map&lt;*,struct&gt;</c> / nested
    /// interior struct child's (id, physicalName) participates in this immutability compare. ID-mode
    /// nested-within-nested is still rejected fail-closed upstream, so an id-mode array/map interior is not
    /// descended here.</para>
    /// <para><b>Rename-equivalent residual (not a preventable mismap).</b> Dropping a logical column
    /// <c>victim(id=1, phys=col-A)</c> and adding a NEW logical column <c>attacker(id=1, phys=col-A)</c> that
    /// REUSES the dropped id/physical name is byte-identical, in the metadata, to a legal RENAME of
    /// <c>victim</c>→<c>attacker</c> (a rename also preserves id + physical name). The read resolves by the
    /// identity anchor (field id in id mode, physical name in name mode), so the re-added column surfaces the
    /// same data a rename would — there is no metadata-only way to distinguish an illegal id/physical REUSE
    /// from a legal rename, so failing closed on it would also reject every legitimate rename (an availability
    /// regression). It therefore passes here and grants no capability beyond the issue's stipulated
    /// <c>_delta_log</c>-write / data-content-forgery threat model.</para>
    /// </summary>
    internal bool IsImmutableFrom(in ColumnMappingIdentity historical)
    {
        if (_mode != historical._mode
            || !_partitionColumns.SequenceEqual(historical._partitionColumns, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (KeyValuePair<ColumnPathKey, ColumnKey> end in _columns)
        {
            bool hasExactPath = historical._columns.TryGetValue(end.Key, out ColumnKey past);
            if (hasExactPath && past != end.Value)
            {
                return false;
            }
        }

        return true;
    }

    // Collects each logical column's (field id, physical name) keyed by a structured, segment-preserving
    // logical path. That makes a literal top-level name such as "a.b" (one segment) distinct from nested
    // a→b (two segments), so CDF compares identities across retained versions without a literal-dot collision.
    //
    // Recurses DIRECT StructField.DataType structs at every depth, AND — under #866 866a — descends the
    // NAME/none-mode interior structs of an array element / map key/value (segment tokens element/key/value),
    // so an array<struct>/map<*,struct>/nested interior struct child's (id, physicalName) participates in the
    // cross-version immutability compare (IsImmutableFrom) for exactly the shapes 866a enables. ID-mode
    // nested-within-nested is still rejected fail-closed upstream (ColumnMapping's assignment/validation
    // doors), so an id-mode array/map interior is NOT descended here (it can never legitimately appear).
    // Collect is bounded by SchemaJson.FromJson's MaxDepth (the schemaString is always JSON-parsed first).
    private static void Collect(
        StructType schema,
        ImmutableArray<string> prefix,
        Dictionary<ColumnPathKey, ColumnKey> into,
        ColumnMappingMode mode)
    {
        foreach (StructField field in schema)
        {
            ImmutableArray<string> path = prefix.Add(field.Name);
            long? id = ColumnMapping.TryGetId(field, out long value) ? value : null;
            string? physicalName =
                field.Metadata.TryGetString(ColumnMapping.PhysicalNameKey, out string? physical) && physical.Length > 0
                    ? physical
                    : null;
            var pathKey = new ColumnPathKey(path);
            var columnKey = new ColumnKey(id, physicalName);
            into[pathKey] = columnKey;

            DescendInterior(field.DataType, path, into, mode);
        }
    }

    // Descends a field's nested interior to reach deeper StructFields carrying column-mapping identity: a
    // struct is collected as its own level; in NAME/none mode an array element / map key/value that is (or
    // contains) a struct is descended under the canonical element/key/value segment token (mirrors
    // ColumnMapping.ValidateMappedInterior). ID-mode array/map interiors are skipped (rejected upstream).
    private static void DescendInterior(
        DataType type,
        ImmutableArray<string> path,
        Dictionary<ColumnPathKey, ColumnKey> into,
        ColumnMappingMode mode)
    {
        switch (type)
        {
            case StructType nested:
                Collect(nested, path, into, mode);
                break;
            case ArrayType array when mode != ColumnMappingMode.Id:
                DescendInterior(array.ElementType, path.Add(ElementSegment), into, mode);
                break;
            case MapType map when mode != ColumnMappingMode.Id:
                DescendInterior(map.KeyType, path.Add(KeySegment), into, mode);
                DescendInterior(map.ValueType, path.Add(ValueSegment), into, mode);
                break;
        }
    }

    private const string ElementSegment = "element";
    private const string KeySegment = "key";
    private const string ValueSegment = "value";

    // A collision-proof logical column path. ImmutableArray<T> does not provide sequence value equality, so
    // this key compares and hashes the ordered path segments explicitly.
    private readonly struct ColumnPathKey : IEquatable<ColumnPathKey>
    {
        private readonly ImmutableArray<string> _segments;

        internal ColumnPathKey(ImmutableArray<string> segments)
        {
            _segments = segments.IsDefault ? ImmutableArray<string>.Empty : segments;
        }

        public bool Equals(ColumnPathKey other)
        {
            if (Length != other.Length)
            {
                return false;
            }

            for (int i = 0; i < Length; i++)
            {
                if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ColumnPathKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Length);
            for (int i = 0; i < Length; i++)
            {
                string segment = _segments[i];
                hash.Add(segment.Length);
                hash.Add(StringComparer.Ordinal.GetHashCode(segment));
            }

            return hash.ToHashCode();
        }

        private int Length => _segments.IsDefault ? 0 : _segments.Length;
    }

    // A single column's column-mapping identity: its field id and physical name (either may be absent under
    // no-mapping). Record-struct value equality drives the immutability compare.
    private readonly record struct ColumnKey(long? Id, string? PhysicalName);
}
