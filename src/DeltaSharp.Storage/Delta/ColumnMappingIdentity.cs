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
    private readonly Dictionary<ColumnKey, ColumnPathKey> _identityPaths;

    private ColumnMappingIdentity(
        ColumnMappingMode mode,
        ImmutableArray<string> partitionColumns,
        Dictionary<ColumnPathKey, ColumnKey> columns,
        Dictionary<ColumnKey, ColumnPathKey> identityPaths)
    {
        _mode = mode;
        _partitionColumns = partitionColumns.IsDefault ? ImmutableArray<string>.Empty : partitionColumns;
        _columns = columns;
        _identityPaths = identityPaths;
    }

    /// <summary>
    /// Extracts the column-mapping identity from a <paramref name="metadata"/> action, keying each column by
    /// its structured logical path (an ordered segment sequence, recursing into direct structs). CDF output is
    /// by logical schema, so keying by logical path is correct: two versions "agree" on a column when the same
    /// logical column maps to the same physical identity. Mapped columns are also indexed by their field-id /
    /// physical-name identity so the comparison can detect literal-dot/nested masquerades without relying on a
    /// dotted string key.
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
        var identityPaths = new Dictionary<ColumnKey, ColumnPathKey>();
        if (SchemaJson.FromJson(metadata.SchemaString) is not StructType schema)
        {
            throw new SchemaValidationException(
                "A change-feed metadata schemaString parsed to a non-struct top-level type; a Delta table "
                + "schema must be a struct, so the log is inconsistent and the read fails closed.");
        }

        Collect(schema, ImmutableArray<string>.Empty, columns, identityPaths);
        return new ColumnMappingIdentity(
            ColumnMapping.ResolveMode(metadata.Configuration), metadata.PartitionColumns, columns, identityPaths);
    }

    /// <summary>
    /// True if <paramref name="historical"/> is a legal (immutability-preserving) predecessor of THIS (end)
    /// identity: identical mode, identical partition-column set, and — for every column present in BOTH — an
    /// identical (field id, physical name). A column present on only one side is permitted, because legitimate
    /// schema evolution ADDS (or DROPS) columns; only a COMMON column whose identity changed is a violation.
    /// In mapped modes, a missing-path end column whose mapping identity already exists in history under an
    /// ambiguously equivalent structured path is rejected, because that is the literal-dot/nested masquerade
    /// case this gate must fail closed. A freshly minted identity is a legitimate add.
    /// <para>A logical rename (column-mapping) keeps id + physical name but changes the logical path, so it
    /// changes the key: it is neither falsely flagged (legal) nor able to mask a reassignment of a
    /// still-present logical column (that column stays keyed and is compared).</para>
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

            if (_mode != ColumnMappingMode.None
                && !hasExactPath
                && end.Value.HasMappingIdentity
                && historical._identityPaths.TryGetValue(end.Value, out ColumnPathKey historicalPath)
                && end.Key.IsAmbiguousWith(historicalPath))
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
    // Recurses DIRECT StructField.DataType structs only. It intentionally does NOT descend through array
    // element or map key/value structs today. Those nested StructFields do carry Delta column-mapping identity,
    // but mapped complex types are currently rejected fail-closed upstream by ColumnMapping.EnsureLeaf and
    // ColumnMappingProjection.ResolvePhysicalNames before CDF reads can rely on this gate. #676 MUST extend
    // this collection to descend array element and map key/value structs before relaxing that upstream reject.
    private static void Collect(
        StructType schema,
        ImmutableArray<string> prefix,
        Dictionary<ColumnPathKey, ColumnKey> into,
        Dictionary<ColumnKey, ColumnPathKey> identityPaths)
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
            if (columnKey.HasMappingIdentity)
            {
                identityPaths.TryAdd(columnKey, pathKey);
            }

            if (field.DataType is StructType nested)
            {
                Collect(nested, path, into, identityPaths);
            }
        }
    }

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

        internal bool IsAmbiguousWith(ColumnPathKey other) => ProjectedLength == other.ProjectedLength
            && ProjectedEquals(other);

        private int Length => _segments.IsDefault ? 0 : _segments.Length;

        private int ProjectedLength
        {
            get
            {
                if (Length == 0)
                {
                    return 0;
                }

                int length = Length - 1;
                for (int i = 0; i < Length; i++)
                {
                    length += _segments[i].Length;
                }

                return length;
            }
        }

        private bool ProjectedEquals(ColumnPathKey other)
        {
            int leftSegment = 0;
            int leftOffset = 0;
            int rightSegment = 0;
            int rightOffset = 0;
            while (true)
            {
                bool hasLeft = TryReadProjectedChar(ref leftSegment, ref leftOffset, out char left);
                bool hasRight = other.TryReadProjectedChar(ref rightSegment, ref rightOffset, out char right);
                if (!hasLeft || !hasRight)
                {
                    return hasLeft == hasRight;
                }

                if (left != right)
                {
                    return false;
                }
            }
        }

        private bool TryReadProjectedChar(ref int segmentIndex, ref int offset, out char value)
        {
            if (segmentIndex >= Length)
            {
                value = default;
                return false;
            }

            string segment = _segments[segmentIndex];
            if (offset < segment.Length)
            {
                value = segment[offset++];
                return true;
            }

            if (segmentIndex + 1 < Length)
            {
                segmentIndex++;
                offset = 0;
                value = '.';
                return true;
            }

            segmentIndex++;
            value = default;
            return false;
        }
    }

    // A single column's column-mapping identity: its field id and physical name (either may be absent under
    // no-mapping). Record-struct value equality drives the immutability compare.
    private readonly record struct ColumnKey(long? Id, string? PhysicalName)
    {
        internal bool HasMappingIdentity => Id is not null || PhysicalName is not null;
    }
}
