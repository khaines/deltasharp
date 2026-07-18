using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The Delta <c>delta.columnMapping.mode</c> a table declares (design §2.12.3, Delta protocol
/// <i>Column Mapping</i>). Column mapping gives each column a <b>stable physical identity</b> — a
/// <c>delta.columnMapping.physicalName</c> and a <c>delta.columnMapping.id</c> — so a logical
/// <b>rename</b> is a metadata-only edit and a <b>drop</b> removes a column from the logical schema
/// without rewriting any data file.
/// </summary>
internal enum ColumnMappingMode
{
    /// <summary>No column mapping: the physical Parquet column name equals the logical column name
    /// (the default; a table with no <c>delta.columnMapping.mode</c> property).</summary>
    None,

    /// <summary><c>name</c> mode: the physical Parquet column name is a stable
    /// <c>col-&lt;uuid&gt;</c> string carried in <c>delta.columnMapping.physicalName</c>. Readers resolve
    /// data columns and partition values by their physical name (Delta protocol, name-mode reader).</summary>
    Name,

    /// <summary><c>id</c> mode: readers resolve columns by the Parquet <c>field_id</c> given by
    /// <c>delta.columnMapping.id</c>. This build both <b>reads</b> (#523, columns resolved by
    /// <c>field_id</c>) and <b>writes</b> (#572, id-mode create/append/overwrite/delete) id-mode tables: the
    /// physical schema (<see cref="ColumnMapping.ToPhysicalSchema"/>) carries the id so
    /// <see cref="Parquet.ParquetTypeMapping.CreateField"/> stamps the Parquet <c>field_id</c>, and partition
    /// values/statistics stay keyed by the physical name — exactly the name-mode write machinery.</summary>
    Id,
}

/// <summary>Mints stable physical column names (<c>col-&lt;uuid&gt;</c>) when a name-mode table is
/// created. Injectable so golden fixtures can assign <b>deterministic</b> physical names while the
/// production default draws from the sanctioned cryptographic RNG (never the banned
/// <c>Guid.NewGuid</c>).</summary>
internal interface IColumnPhysicalNameSource
{
    /// <summary>Returns the next physical column name, in Delta's <c>col-&lt;uuid&gt;</c> form.</summary>
    string NextPhysicalName();
}

/// <summary>The production physical-name source: a fresh cryptographically-random
/// <c>col-&lt;uuid&gt;</c> per column (the deterministic RNG DeltaSharp uses everywhere it would
/// otherwise reach for the banned <c>Guid.NewGuid</c>).</summary>
internal sealed class RandomPhysicalNameSource : IColumnPhysicalNameSource
{
    /// <summary>The shared instance.</summary>
    public static RandomPhysicalNameSource Instance { get; } = new();

    /// <inheritdoc/>
    public string NextPhysicalName() => "col-" + new Guid(RandomNumberGenerator.GetBytes(16));
}

/// <summary>A <b>deterministic</b> physical-name source: it derives each <c>col-&lt;uuid&gt;</c> name
/// from a caller-supplied seed and a monotonically increasing counter via SHA-256, so a golden name-mode
/// fixture assigns byte-for-byte reproducible physical names (no ambient state, no banned symbols).
/// <para><b>Not thread-safe:</b> the internal counter is mutated without synchronization, so a single
/// instance must be driven by one thread (each name-mode table creation uses its own instance).</para></summary>
internal sealed class SeededPhysicalNameSource : IColumnPhysicalNameSource
{
    private readonly string _seed;
    private int _counter;

    /// <summary>Creates a deterministic source seeded by <paramref name="seed"/>.</summary>
    public SeededPhysicalNameSource(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _seed = seed;
    }

    /// <inheritdoc/>
    public string NextPhysicalName()
    {
        int index = _counter++;
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{_seed}:{index}")));
        return "col-" + new Guid(digest.AsSpan(0, 16));
    }
}

/// <summary>
/// The column-mapping model and physical/logical schema helpers (design §2.12.3; Delta protocol
/// <i>Column Mapping</i>). It parses <c>delta.columnMapping.mode</c> from a table's
/// <c>metaData.configuration</c>, reads/assigns the per-field <c>delta.columnMapping.id</c> and
/// <c>delta.columnMapping.physicalName</c>, and maps a <b>logical</b> schema (display names) to the
/// <b>physical</b> schema (physical Parquet names) used to read/write data files.
///
/// <para><b>All three modes are served.</b> This build reads AND writes <c>none</c>, <c>name</c>, and
/// <c>id</c> modes. <c>name</c> mode resolves DATA columns by their physical name; <c>id</c> mode resolves
/// DATA columns by the Parquet <c>field_id</c> (#523 read, #572 write) — the physical schema
/// (<see cref="ToPhysicalSchema"/>) carries the id so <see cref="Parquet.ParquetTypeMapping.CreateField"/>
/// stamps the <c>field_id</c>. In BOTH mapped modes partition-value keys and statistics stay keyed by the
/// physical name. An unrecognized mode is rejected fail-closed (never guessed).</para>
///
/// <para><b>Scope.</b> Only top-level (leaf) struct fields are mapped in this build; nested struct/array/map
/// column mapping is phased (the Parquet writer already rejects nested physical types, design §2.9). A
/// nested top-level field in a column-mapped schema is rejected fail-closed (<see cref="EnsureLeaf"/>).</para>
/// </summary>
internal static class ColumnMapping
{
    /// <summary>The <c>metaData.configuration</c> key selecting the column-mapping mode.</summary>
    public const string ModeKey = "delta.columnMapping.mode";

    /// <summary>The <c>metaData.configuration</c> key tracking the highest assigned column id
    /// (monotonic; an internal property users cannot set — Delta protocol writer requirements).</summary>
    public const string MaxColumnIdKey = "delta.columnMapping.maxColumnId";

    /// <summary>The <c>protocol</c> reader/writer feature name gating column mapping.</summary>
    public const string Feature = "columnMapping";

    /// <summary>The per-field metadata key holding the column's stable integer id.</summary>
    public const string IdKey = "delta.columnMapping.id";

    /// <summary>The per-field metadata key holding the column's stable physical Parquet name.</summary>
    public const string PhysicalNameKey = "delta.columnMapping.physicalName";

    private const string NoneMode = "none";
    private const string NameMode = "name";
    private const string IdMode = "id";

    /// <summary>Parses <c>delta.columnMapping.mode</c> from a table's <paramref name="configuration"/>
    /// (absent/empty ⇒ <see cref="ColumnMappingMode.None"/>). An unrecognized value is an inconsistent
    /// table property — fail closed rather than guess a mode.</summary>
    /// <exception cref="DeltaProtocolException">The property holds an unrecognized mode.</exception>
    public static ColumnMappingMode ResolveMode(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TryGetValue(ModeKey, out string? raw) || string.IsNullOrEmpty(raw))
        {
            return ColumnMappingMode.None;
        }

        return raw switch
        {
            NoneMode => ColumnMappingMode.None,
            NameMode => ColumnMappingMode.Name,
            IdMode => ColumnMappingMode.Id,
            _ => throw DeltaProtocolException.Unsupported(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unrecognized '{ModeKey}' value '{SanitizeEchoedToken(raw)}'; expected one of 'none', "
                    + $"'name', or 'id'. The table cannot be read safely.")),
        };
    }

    /// <summary>The maximum length of an untrusted configuration token echoed into a diagnostic.</summary>
    private const int MaxEchoedTokenLength = 64;

    // Bounds and redacts an untrusted configuration value (e.g. delta.columnMapping.mode) before it is
    // interpolated into an exception message (#516 log-injection hardening): caps the length and replaces
    // control characters with U+FFFD, so a poisoned table property cannot inject newlines/control sequences
    // into a log line or blow up a diagnostic with an unbounded string.
    private static string SanitizeEchoedToken(string raw)
    {
        string capped = raw.Length <= MaxEchoedTokenLength
            ? raw
            : string.Concat(raw.AsSpan(0, MaxEchoedTokenLength), "…");
        var builder = new StringBuilder(capped.Length);
        foreach (char c in capped)
        {
            builder.Append(char.IsControl(c) ? '\uFFFD' : c);
        }

        return builder.ToString();
    }

    // The conservative, portable per-path-COMPONENT budget (in UTF-8 bytes) for a NAME that becomes a Hive
    // partition-directory segment ("name=value") — #572 deltaspec R7. Filesystems cap a single path component
    // at ~255 bytes (Linux NAME_MAX, macOS, NTFS are all ~255); the partition directory encodes the column
    // name as "name=value", so the NAME alone must stay well under that ceiling to leave room for the "=value"
    // suffix (the value is data-dependent and Uri.EscapeDataString-encoded — e.g. __HIVE_DEFAULT_PARTITION__ is
    // 26 bytes). 128 bytes is half the component budget: it accepts every real name by a wide margin (a minted
    // physical name is "col-<uuid>" = 40 bytes; a logical partition name is typically far shorter) while
    // guaranteeing >=127 bytes of headroom for "=value". A crafted ~300-byte name is rejected fail-closed at
    // commit/load instead of failing a later partitioned write at the path-resolution/confined-root guard.
    private const int MaxPathSegmentNameBytes = 128;

    // Judges whether <segment> is a SAFE single filesystem path segment — the shared core of the
    // name->path-safety contract (#572 deltaspec R6 char-safety + R7 length bound). Returns null if safe, else
    // a "MUST NOT <clause>" reason phrase the caller wraps with context. Shared by the mapped-physicalName
    // check (name/id mode, EnsureSafePhysicalName) and the none-mode logical partition-name check
    // (EnsureNoneModePartitionNamesSafe) — the two metaData-controlled names that become a partition-directory
    // path segment ("name=value/"). A physicalName ALSO doubles as the Parquet column name, but Parquet.Net
    // round-trips ANY name verbatim (empirically verified — no footer constraint), so the binding constraint is
    // uniformly the partition path:
    //   - '/' or '\'  : a path separator splits/restructures the directory tree, and with '..' escapes the
    //                   confined table root (caught fail-closed at the backend, but rejected here earlier);
    //   - '='         : the Hive key=value delimiter — corrupts partition-dir parsing (the reader splits on '=');
    //   - ':'         : roots a Windows drive / NTFS alternate-data-stream path (the absolute/rooted vector);
    //   - control char: filesystem-hostile and a log/path-injection vector;
    //   - whitespace-only (or empty): a degenerate, filesystem-hostile segment (some filesystems trim it);
    //   - '.' or '..' : the WHOLE segment being dot/dot-dot is degenerate/traversal (a '..' SUBSTRING inside an
    //                   otherwise-safe segment — e.g. 'a..b' — is a valid filename, NOT a traversal, so it is
    //                   allowed, matching the confined-root guard's own posture);
    //   - > MaxPathSegmentNameBytes UTF-8 bytes: exceeds the portable path-component budget (R7).
    // Real names ('col-<uuid>', or a normal logical partition name) satisfy all of this, so only a crafted or
    // foreign metaData can trip it.
    private static string? FindUnsafePathSegmentReason(string segment)
    {
        bool whitespaceOnly = true;
        foreach (char c in segment)
        {
            if (c is '/' or '\\' or '=' or ':' || char.IsControl(c))
            {
                return "contain a path separator ('/' or '\\'), '=', ':', or a control character";
            }

            if (!char.IsWhiteSpace(c))
            {
                whitespaceOnly = false;
            }
        }

        if (whitespaceOnly)
        {
            return "be empty or whitespace-only";
        }

        if (string.Equals(segment, ".", StringComparison.Ordinal)
            || string.Equals(segment, "..", StringComparison.Ordinal))
        {
            return "be '.' or '..'";
        }

        if (Encoding.UTF8.GetByteCount(segment) > MaxPathSegmentNameBytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"exceed {MaxPathSegmentNameBytes} UTF-8 bytes (it becomes a 'name=value' partition-directory "
                + $"component, kept well under the ~255-byte filesystem path-component limit)");
        }

        return null;
    }

    // Enforces the SAFE-PATH-SEGMENT contract on a column-mapped field's physical name (#572 deltaspec R6/R7).
    // The physicalName is the partition-directory path segment (name/id mode) AND the Parquet column name; a
    // crafted/foreign metaData whose physicalName is not a safe segment fails closed here at COMMIT and LOAD
    // (this runs inside ValidateColumnMappingSchema). See FindUnsafePathSegmentReason for the full contract +
    // rationale (char-safety + length bound).
    private static void EnsureSafePhysicalName(string logicalName, string physical)
    {
        string? reason = FindUnsafePathSegmentReason(physical);
        if (reason is null)
        {
            return;
        }

        throw DeltaProtocolException.Inconsistent(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Column '{logicalName}' has a '{PhysicalNameKey}' ('{SanitizeEchoedToken(physical)}') that is "
                + $"not a safe path segment; under column mapping the physical name is used as a Parquet column "
                + $"name and a partition-directory segment, so it MUST NOT {reason}. The schema is inconsistent "
                + $"and cannot be read safely."));
    }

    /// <summary>
    /// The column-mapping gate applied when a snapshot is loaded (design §2.12.3; STORY-05.4.3 AC4):
    /// a table declaring any column-mapping mode MUST have a <paramref name="protocol"/> that supports the
    /// <c>columnMapping</c> feature — the Delta protocol says the <c>delta.columnMapping.mode</c> property is
    /// only honored when the protocol supports it, so a mode set without protocol support is rejected with a
    /// protocol-upgrade error rather than silently ignored. <see cref="ColumnMappingMode.None"/> is a no-op.
    ///
    /// <para>All three modes (<c>none</c>/<c>name</c>/<c>id</c>) are <b>readable</b> — id-mode read resolves
    /// columns by the Parquet <c>field_id</c> (#523) — and, since #572, all three are <b>writable</b>. This
    /// LOAD gate rejects only a mode declared without protocol support, independent of read vs. write.</para>
    /// </summary>
    /// <exception cref="DeltaProtocolException">A column-mapping mode is set without protocol support.</exception>
    public static void EnsureModeGate(ColumnMappingMode mode, ProtocolAction protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        if (mode == ColumnMappingMode.None)
        {
            return;
        }

        // The property is only honored when the protocol supports columnMapping: reader v2 (legacy), or
        // reader v3+ with the columnMapping reader feature (Delta protocol, Reader Requirements). Legacy
        // reader v2 is rejected earlier by ProtocolSupport.EnsureReadable, so a served column-mapping table
        // reaches here only via the table-features (reader v3) representation.
        bool supported =
            protocol.MinReaderVersion == ProtocolSupport.ColumnMappingReaderVersion
            || (protocol.MinReaderVersion >= ProtocolSupport.TableFeaturesReaderVersion
                && protocol.ReaderFeatures.Contains(Feature, StringComparer.Ordinal));
        if (!supported)
        {
            throw DeltaProtocolException.Unsupported(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The table sets '{ModeKey}' but its protocol (reader version "
                    + $"{protocol.MinReaderVersion}) does not declare the '{Feature}' feature. Column mapping "
                    + $"requires reader version 2, or reader version 3+ with the '{Feature}' reader feature; "
                    + $"upgrade the table protocol before enabling column mapping. The table cannot be read "
                    + $"safely."));
        }
    }

    /// <summary>
    /// The <b>schema well-formedness gate</b> for a column-mapped (<c>name</c> or <c>id</c>) table, enforced
    /// at the single snapshot-load choke point (design §2.12.3; Delta PROTOCOL.md "Column Mapping") AND at the
    /// committer before a mapped <c>metaData</c> is published (#572 N3). A column-mapped schema resolves
    /// partition values and statistics — and, in name mode, data columns — by
    /// <c>delta.columnMapping.physicalName</c>, and resolves id-mode data columns by
    /// <c>delta.columnMapping.id</c>; so a malformed mapping (a poisoned/foreign table, or a crafted raw
    /// <c>metaData</c>) could let one field's value be served under another's logical name, two logical columns
    /// map to one file column (a <b>silent misread</b>), or a valid-looking table fail a LATER op (append
    /// id-stamp, projection, partition planning). This gate rejects every such shape fail-closed instead (#523
    /// extended it to id mode — a foreign id-mode table is exactly the untrusted input this guards).
    /// <para>The <b>COMPLETE</b> set of mapped-schema invariants enforced here (#572 R5/R6/R7 completeness
    /// passes — a committed/loaded mapped table that passes is internally consistent for every downstream
    /// op):</para>
    /// <list type="number">
    /// <item><b>maxColumnId</b> is present, an integer, and <c>&gt;= 0</c> (<see cref="ReadMaxColumnId"/>) — a
    /// monotonic count of assigned ids that also upper-bounds every field id (below); the <c>&gt;= 0</c> rule
    /// covers the degenerate zero-field case the per-field loop cannot;</item>
    /// <item>every top-level mapped field is a <b>leaf</b> (non-<see cref="StructType"/>/<see cref="ArrayType"/>/
    /// <see cref="MapType"/>) column — the reader/projection maps only leaf columns; a nested top-level field
    /// is rejected BEFORE its inner fields are inspected, so inner mapping metadata cannot sneak through;</item>
    /// <item>every field carries a <c>delta.columnMapping.physicalName</c> that is (a) non-empty, (b) a
    /// <b>safe path segment</b> (<see cref="EnsureSafePhysicalName"/>: not <c>.</c>/<c>..</c>, not
    /// whitespace-only, free of a path separator (<c>/</c> or <c>\</c>), <c>=</c>, <c>:</c>, or a control char,
    /// and at most <see cref="MaxPathSegmentNameBytes"/> UTF-8 bytes — because the physical name doubles as a
    /// Parquet column name AND a Hive partition-directory segment (<c>physicalName=value/</c>), so an unsafe or
    /// over-long name would restructure/escape the directory tree, corrupt <c>key=value</c> parsing, or exceed
    /// the filesystem path-component limit), and (c) globally unique across <b>all</b> top-level fields (data +
    /// partition);</item>
    /// <item>every field carries a <c>delta.columnMapping.id</c> that is <b>positive</b> (<c>&gt;= 1</c> —
    /// Delta ids start at 1), <b>unique</b>, and <c>&lt;= maxColumnId</c>.</item>
    /// </list>
    /// <para><b>Deliberately deferred to the read/stamp layer</b> (documented, fail-closed there — never a
    /// silent corruption): the int32 UPPER bound. A field id — or a <c>maxColumnId</c> — above
    /// <c>int.MaxValue</c> is NOT rejected here: the long→int32 Parquet <c>field_id</c> cast guard
    /// (<c>ParquetTypeMapping.CreateField</c>, range <c>[1, int.MaxValue]</c>) plus the reader bound catch it
    /// fail-closed at read/append (such a table is only reachable via a crafted raw metaData — the writer
    /// would have to mint 2^31 columns — still loads, and any later mint overflow is refused at the append
    /// stamp, table unchanged). Enforcing the upper bound here would break that deliberate scoping and its
    /// pinned test <c>IdMode_RequestedIdAboveInt32Max_IsRejectedFailClosed</c>.</para>
    /// <para><b>Enforced elsewhere</b> (not mapping-specific, so intentionally NOT in this <c>mode != None</c>
    /// gate): partition columns ⊆ schema (all-mode, at the committer — <see cref="EnsurePartitionColumnsInSchema"/>);
    /// none-mode partition-name path-safety (the same safe-segment + length contract, applied to the LOGICAL
    /// partition names that become path segments when there is no physical mapping — at the committer,
    /// <see cref="EnsureNoneModePartitionNamesSafe"/>, #572 R7); unique LOGICAL field names (all-mode, the
    /// <see cref="StructType"/> ctor at schema parse); and a recognized mode value (<see cref="ResolveMode"/>).
    /// <c>none</c> mode is a no-op here (its fields may carry stray mapping metadata harmlessly — unchanged
    /// posture). This is an explicit choke point (not an incidental ctor throw), so the guarantee holds
    /// regardless of how the schema is built.</para>
    /// <para><b>Not a gap — the name→path dimension is complete across all modes (#572 R7).</b> Every
    /// metaData-controlled name that becomes a filesystem path segment is safe-segment + length validated: the
    /// mapped physicalName (name/id, here); the logical partition name (none, at the committer). A partition
    /// <b>value</b> is percent-encoded (<c>Uri.EscapeDataString</c>) into its directory segment, so a
    /// slash/traversal/control value is neutralised at write time (it is data, not committed metaData); the
    /// staged data-file name is a crypto-random hex token, never metaData-derived; and Parquet <b>column-name</b>
    /// legality imposes nothing (Parquet.Net 6.0.3 round-trips ANY column name verbatim — empirically verified),
    /// so the binding constraint on a physical name is uniformly the partition-directory path, not the
    /// footer.</para>
    /// </summary>
    /// <exception cref="DeltaProtocolException">A nested (non-leaf) mapped column, a missing/empty physical
    /// name, an unsafe (non-path-segment) physical name, a duplicate physical name, a missing id, a
    /// non-positive id, a duplicate id, an id above <c>maxColumnId</c>, or a missing/malformed/negative
    /// <c>maxColumnId</c> — the schema is inconsistent and cannot be resolved safely.</exception>
    public static void ValidateColumnMappingSchema(
        ColumnMappingMode mode, StructType schema, IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(configuration);
        if (mode == ColumnMappingMode.None)
        {
            return;
        }

        long maxColumnId = ReadMaxColumnId(configuration);

        var physicalNames = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<long>();
        foreach (StructField field in schema)
        {
            // Leaf-only invariant (#572 deltaspec N3/R4): the reader resolves — and this build maps — only
            // top-level LEAF columns; a nested (struct/array/map) mapped column would later throw "nested
            // column mapping is unsupported" at projection. The write doors reject it via EnsureLeaf, but a
            // RAW committed or foreign metaData bypasses that door, so enforce the same contract at this
            // shared choke point (load AND commit) BEFORE any column resolves. Checked first so a nested
            // column is reported as nested (its most specific defect) rather than tripping a later id check.
            EnsureLeaf(field);

            string physical = PhysicalName(field, mode);

            // Path-safety invariant (#572 deltaspec R6): the physical name is used as a Parquet column name
            // AND a Hive partition-directory path segment ("physicalName=value/"), so reject any name that is
            // not a safe single path segment. Checked right after the name is read (before the uniqueness
            // check) so a malformed name is reported as unsafe — its most specific defect.
            EnsureSafePhysicalName(field.Name, physical);

            if (!physicalNames.Add(physical))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column mapping physical name '{physical}' is assigned to more than one column; "
                        + $"under column mapping every top-level field (data and partition) MUST have a unique "
                        + $"'{PhysicalNameKey}'. The schema is inconsistent and cannot be read safely."));
            }

            if (!TryGetId(field, out long id))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{field.Name}' has no '{IdKey}' but the table uses column mapping; the schema is inconsistent and cannot be read safely."));
            }

            // Id lower-bound invariant (#572 deltaspec N3/R4): Delta column-mapping ids start at 1
            // (AssignFreshMapping mints 1, 2, …) — an id <= 0 is a value the writer NEVER emits and would
            // fail a later append at the Parquet field_id stamp guard (ParquetTypeMapping.CreateField), so
            // reject it here fail-closed at load AND commit. Checked before the uniqueness/max checks so a
            // non-positive id is reported as out-of-range (its most specific defect). The UPPER bound
            // (id > int.MaxValue) stays a read-layer concern (the long->int32 cast guard + reader bound), so a
            // table whose maxColumnId itself exceeds int.MaxValue still loads and is caught at read — see
            // IdMode_RequestedIdAboveInt32Max_IsRejectedFailClosed.
            if (id <= 0)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{field.Name}' has '{IdKey}'={id} which is outside the valid column-mapping "
                        + $"id range [1, int.MaxValue] (Delta column-mapping ids start at 1). The schema is "
                        + $"inconsistent and cannot be read safely."));
            }

            if (!ids.Add(id))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column mapping id {id} is assigned to more than one column; under column mapping every '{IdKey}' MUST be unique. The schema is inconsistent and cannot be read safely."));
            }

            if (id > maxColumnId)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{field.Name}' has '{IdKey}'={id} which exceeds the tracked "
                        + $"'{MaxColumnIdKey}'={maxColumnId}; the schema is inconsistent and cannot be read "
                        + $"safely."));
            }
        }
    }

    // Reads the tracked maxColumnId from a column-mapped (name or id) table's configuration. It is a
    // monotonic writer invariant that MUST be present and parseable for any column-mapped table; a
    // missing/malformed value is an inconsistent table property rejected fail-closed (never guessed).
    private static long ReadMaxColumnId(IReadOnlyDictionary<string, string> configuration)
    {
        if (!configuration.TryGetValue(MaxColumnIdKey, out string? raw)
            || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long maxColumnId))
        {
            throw DeltaProtocolException.Inconsistent(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The table uses column mapping but its '{MaxColumnIdKey}' is missing or "
                    + $"not an integer; the schema is inconsistent and cannot be read safely."));
        }

        // Lower-bound invariant (#572 deltaspec R5): maxColumnId is a monotonic COUNT of assigned ids — it is
        // 0 for a zero-field (or all-columns-retired) table and only ever increases (AssignFreshMapping starts
        // at 0 and mints 1..N; EvolveNameModeMapping only bumps), so it is NEVER negative. A NON-empty mapped
        // schema already rejects maxColumnId < min(id)=1 via the per-field `id > maxColumnId` check, but a
        // DEGENERATE zero-field schema skips that loop entirely — so a crafted empty id-mode metaData with
        // maxColumnId=-1 would otherwise commit + load and then mint id = maxColumnId+1 = 0 on the next
        // mergeSchema append, failing the [1, int.MaxValue] stamp guard. Reject maxColumnId < 0 here so the
        // empty-schema case is covered at BOTH load and commit (this method is the shared read used by both).
        //
        // DELIBERATELY DEFERRED (documented, NOT silently skipped): the int32 UPPER bound. maxColumnId
        // > int.MaxValue is NOT rejected here — same scoping as the per-field id upper bound (see
        // ValidateColumnMappingSchema / IdMode_RequestedIdAboveInt32Max_IsRejectedFailClosed): such a table is
        // only reachable via a crafted raw metaData (the writer would have to mint 2^31 columns), still loads,
        // and any subsequent evolution mint (maxColumnId+1) is caught FAIL-CLOSED at the append stamp guard
        // (ParquetTypeMapping.CreateField), so no corrupt field_id is ever written. Enforcing the upper bound
        // here would break that deliberate read-layer scoping and its pinned test.
        if (maxColumnId < 0)
        {
            throw DeltaProtocolException.Inconsistent(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The table uses column mapping but its '{MaxColumnIdKey}'={maxColumnId} is negative; "
                    + $"maxColumnId is a monotonic count of assigned column ids and MUST be >= 0. The schema "
                    + $"is inconsistent and cannot be read safely."));
        }

        return maxColumnId;
    }

    /// <summary>The physical Parquet name of <paramref name="field"/> under <paramref name="mode"/>: the
    /// declared <c>delta.columnMapping.physicalName</c> in <b>both</b> <c>name</c> and <c>id</c> mode, else
    /// (<c>none</c> mode) the field's own (logical) name. A column-mapped field (name or id) missing a physical
    /// name is an inconsistent schema — fail closed. (In id mode, DATA columns resolve by <c>field_id</c>, but
    /// partition-value keys and statistics are still keyed by the physical name.)</summary>
    /// <exception cref="DeltaProtocolException">A column-mapped field carries no physical name.</exception>
    public static string PhysicalName(StructField field, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (mode == ColumnMappingMode.None)
        {
            return field.Name;
        }

        // Both `name` AND `id` modes assign a physical name (Delta PROTOCOL.md "Column Mapping"). In name mode
        // the reader resolves DATA columns by it; in BOTH modes partition-value keys and statistics are keyed
        // by the physical name (a column-mapped table's add.partitionValues use physical names). id mode
        // additionally resolves DATA columns by field_id, but its partition-value keys are STILL physical —
        // returning the LOGICAL name here (#523's original bug) silently produced all-null partition columns.
        if (field.Metadata.TryGetString(PhysicalNameKey, out string? physical) && physical.Length > 0)
        {
            return physical;
        }

        throw DeltaProtocolException.Inconsistent(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Column '{field.Name}' has no '{PhysicalNameKey}' but the table uses column mapping; the "
                + $"schema is inconsistent and cannot be read safely."));
    }

    /// <summary>Reads a field's assigned column id, if present.</summary>
    public static bool TryGetId(StructField field, out long id)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Metadata.TryGetLong(IdKey, out id);
    }

    /// <summary>
    /// Assigns a fresh column mapping to a logical <paramref name="schema"/> (name-mode table creation):
    /// every top-level field is given a monotonically increasing <c>delta.columnMapping.id</c> (1..N) and a
    /// stable <c>delta.columnMapping.physicalName</c> from <paramref name="nameSource"/>. Existing per-field
    /// metadata is preserved. Returns the mapped schema and the resulting <c>maxColumnId</c> (N).
    /// </summary>
    /// <exception cref="DeltaProtocolException">A field is a nested (struct/array/map) type — nested column
    /// mapping is phased in this build.</exception>
    public static (StructType Schema, long MaxColumnId) AssignFreshMapping(
        StructType schema, IColumnPhysicalNameSource nameSource)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(nameSource);

        long nextId = 0;
        var mapped = new List<StructField>(schema.Count);
        foreach (StructField field in schema)
        {
            EnsureLeaf(field);
            long id = ++nextId;
            string physicalName = nameSource.NextPhysicalName();

            var entries = new List<KeyValuePair<string, MetadataValue>>(field.Metadata.Count + 2);
            foreach (KeyValuePair<string, MetadataValue> existing in field.Metadata)
            {
                if (!string.Equals(existing.Key, IdKey, StringComparison.Ordinal)
                    && !string.Equals(existing.Key, PhysicalNameKey, StringComparison.Ordinal))
                {
                    entries.Add(existing);
                }
            }

            entries.Add(new KeyValuePair<string, MetadataValue>(IdKey, MetadataValue.Long(id)));
            entries.Add(new KeyValuePair<string, MetadataValue>(
                PhysicalNameKey, MetadataValue.String(physicalName)));

            mapped.Add(new StructField(
                field.Name, field.DataType, field.Nullable, FieldMetadata.FromValues(entries)));
        }

        return (new StructType(mapped), nextId);
    }

    /// <summary>
    /// Reconciles a column-mapped (<c>name</c> or <c>id</c>) table's column mapping onto a target logical
    /// <paramref name="evolvedSchema"/> — an additive append/overwrite evolution (#541) or a wholesale
    /// <c>overwriteSchema</c> replacement (#542). Each field already present in
    /// <paramref name="currentMappedSchema"/> (matched by <b>logical</b> name)
    /// REUSES its existing <c>delta.columnMapping.id</c> + <c>delta.columnMapping.physicalName</c> verbatim —
    /// an <b>applied type widening</b> (or any type change under a destructive replace) keeps the column's
    /// identity, only its type changes — while each <b>new</b> field mints a fresh physical name from
    /// <paramref name="nameSource"/> plus a fresh monotonically increasing id (<c>maxColumnId + 1, …</c>). A
    /// column present in the current schema but ABSENT from the target (a <c>overwriteSchema</c> drop) is
    /// simply not emitted; its id is <b>retired</b>, never reused, because <c>maxColumnId</c> only ever
    /// increases. Every other per-field metadata carried on the target field (e.g. a <c>delta.typeChanges</c>
    /// entry, a column comment) is preserved. Returns the mapped schema and the configuration with the bumped
    /// <c>maxColumnId</c> (all other configuration entries preserved). Mirrors the create-path minting
    /// (<see cref="AssignFreshMapping"/>) but never re-mints an existing column's identity. The reconciliation
    /// is <b>mode-independent</b> — it produces the per-field id/physicalName the mode-aware physical mapping
    /// (<see cref="MapWriteSchemaToPhysical"/>) then stages under, so the same helper serves both name and id
    /// (#572): the current configuration's mode key is preserved verbatim.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The current schema's <c>maxColumnId</c> is missing/malformed,
    /// a retained column-mapped column carries no id, or an evolved field is a nested type.</exception>
    public static (StructType Schema, ImmutableSortedDictionary<string, string> Configuration) EvolveNameModeMapping(
        StructType evolvedSchema,
        StructType currentMappedSchema,
        ImmutableSortedDictionary<string, string> currentConfiguration,
        IColumnPhysicalNameSource nameSource)
    {
        ArgumentNullException.ThrowIfNull(evolvedSchema);
        ArgumentNullException.ThrowIfNull(currentMappedSchema);
        ArgumentNullException.ThrowIfNull(currentConfiguration);
        ArgumentNullException.ThrowIfNull(nameSource);

        long nextId = ReadMaxColumnId(currentConfiguration);
        var mapped = new List<StructField>(evolvedSchema.Count);
        foreach (StructField field in evolvedSchema)
        {
            EnsureLeaf(field);

            long id;
            string physicalName;
            if (currentMappedSchema.TryGetField(field.Name, out StructField existing))
            {
                // Existing (or applied-widened) column: reuse its identity verbatim — never re-mint, so
                // committed data files under the prior physical name still resolve.
                physicalName = PhysicalName(existing, ColumnMappingMode.Name);
                if (!TryGetId(existing, out id))
                {
                    throw DeltaProtocolException.Inconsistent(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Name-mode column '{field.Name}' has no '{IdKey}'; the table schema is "
                            + $"inconsistent and cannot be evolved."));
                }
            }
            else
            {
                // New column (#541): mint a fresh physical name + a fresh monotonic id, bumping maxColumnId.
                id = ++nextId;
                physicalName = nameSource.NextPhysicalName();
            }

            // Preserve every non-mapping metadata entry (e.g. delta.typeChanges, a column comment) the merged
            // field carries, then set the authoritative id + physicalName.
            var entries = new List<KeyValuePair<string, MetadataValue>>(field.Metadata.Count + 2);
            foreach (KeyValuePair<string, MetadataValue> entry in field.Metadata)
            {
                if (!string.Equals(entry.Key, IdKey, StringComparison.Ordinal)
                    && !string.Equals(entry.Key, PhysicalNameKey, StringComparison.Ordinal))
                {
                    entries.Add(entry);
                }
            }

            entries.Add(new KeyValuePair<string, MetadataValue>(IdKey, MetadataValue.Long(id)));
            entries.Add(new KeyValuePair<string, MetadataValue>(
                PhysicalNameKey, MetadataValue.String(physicalName)));

            mapped.Add(new StructField(
                field.Name, field.DataType, field.Nullable, FieldMetadata.FromValues(entries)));
        }

        ImmutableSortedDictionary<string, string> configuration =
            currentConfiguration.SetItem(MaxColumnIdKey, nextId.ToString(CultureInfo.InvariantCulture));
        return (new StructType(mapped), configuration);
    }

    /// <summary>The <b>physical</b> schema for a mapped logical <paramref name="schema"/>: the same field
    /// order and types, but each field renamed to its physical name. In <c>name</c> mode the column-mapping
    /// metadata is <b>stripped</b> (a name-mode physical file is field_id-free — #523 AC3, byte-unchanged
    /// output). In <c>id</c> mode the field carries ONLY its <c>delta.columnMapping.id</c> so the Parquet
    /// writer (<see cref="Parquet.ParquetTypeMapping.CreateField"/>) stamps the <c>field_id</c> an id-mode
    /// reader resolves by (#572). Either way this is the exact shape a Delta Parquet data file stores and is
    /// read back by. <c>none</c> mode returns the schema unchanged (logical == physical).</summary>
    /// <exception cref="DeltaProtocolException">A field is nested, or an id-mode field carries no id.</exception>
    public static StructType ToPhysicalSchema(StructType schema, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (mode == ColumnMappingMode.None)
        {
            return schema;
        }

        var physical = new List<StructField>(schema.Count);
        foreach (StructField field in schema)
        {
            EnsureLeaf(field);
            physical.Add(ToPhysicalField(field.Name, field.DataType, field.Nullable, PhysicalName(field, mode), field, mode));
        }

        return new StructType(physical);
    }

    /// <summary>
    /// Maps an incoming <paramref name="writeSchema"/> (LOGICAL column names, in write order) to the PHYSICAL
    /// schema the staged Parquet file must physically carry for an append/overwrite to an <b>existing</b>
    /// column-mapped (<c>name</c> or <c>id</c>) table (#525/#572). Each write field is renamed to the physical
    /// name the table's <paramref name="tableMappedSchema"/> already assigned that logical column,
    /// <b>preserving the write order</b> and the write field's own type/nullability (so the staged bytes line
    /// up exactly with the partitioner's output). The table's existing <c>delta.columnMapping.id</c> /
    /// <c>physicalName</c> are REUSED verbatim — never re-minted — so an append never assigns a fresh physical
    /// name to an existing logical column. In <c>id</c> mode the reused id rides on the physical field so the
    /// staged Parquet carries the correct <c>field_id</c>; in <c>name</c> mode no mapping metadata is carried.
    /// A write column absent from the table schema has no physical name to stage under (schema enforcement
    /// should have rejected it first) and fails closed.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A write column is absent from the column-mapped table schema,
    /// or a mapped field carries no physical name / no id (id mode).</exception>
    public static StructType MapWriteSchemaToPhysical(
        StructType writeSchema, StructType tableMappedSchema, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(tableMappedSchema);
        if (mode == ColumnMappingMode.None)
        {
            return writeSchema;
        }

        var fields = new List<StructField>(writeSchema.Count);
        foreach (StructField field in writeSchema)
        {
            EnsureLeaf(field);
            if (!tableMappedSchema.TryGetField(field.Name, out StructField tableField))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Write column '{field.Name}' is not present in the {ModeName(mode)}-mode table schema, "
                        + $"so it has no '{PhysicalNameKey}' to stage under; the write is rejected fail-closed."));
            }

            // Physical NAME + id come from the TABLE's existing mapping (reused verbatim, never re-minted);
            // the write column's own type/nullability rides so the staged bytes line up with the partitioner
            // output. In id mode the id is carried so the staged Parquet stamps the field_id.
            fields.Add(ToPhysicalField(
                field.Name, field.DataType, field.Nullable, PhysicalName(tableField, mode), tableField, mode));
        }

        return new StructType(fields);
    }

    /// <summary>Maps the table's logical <paramref name="partitionColumns"/> to their physical names, the
    /// form Delta records them (and their <c>add.partitionValues</c> keys) in the log under column mapping
    /// (Delta protocol writer requirement: partition values tracked by physical name in BOTH name and id
    /// mode).</summary>
    /// <exception cref="DeltaProtocolException">A partition column is absent from the schema.</exception>
    public static IReadOnlyList<string> PhysicalPartitionColumns(
        StructType mappedSchema, IReadOnlyList<string> partitionColumns, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(mappedSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        if (mode == ColumnMappingMode.None)
        {
            return partitionColumns;
        }

        var physical = new List<string>(partitionColumns.Count);
        foreach (string column in partitionColumns)
        {
            if (!mappedSchema.TryGetField(column, out StructField field))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{column}' is not present in the table schema."));
            }

            physical.Add(PhysicalName(field, mode));
        }

        return physical;
    }

    /// <summary>
    /// The <b>all-mode</b> (none/name/id) partition-column invariants checked at the committer: every logical
    /// <c>metaData.partitionColumns</c> entry MUST (a) name a top-level column present in the table
    /// <paramref name="schema"/>, (b) be a <b>partition-encodable atomic type</b> (not struct/array/map/binary —
    /// a partition value must render to a single directory segment), and (c) be <b>distinct</b> — no column
    /// listed more than once (#572 deltaspec N3/R4, R8, R9). <c>partitionColumns</c> stores <b>logical</b> names,
    /// so they are compared against the logical <see cref="StructType"/> field names (never physical) and to each
    /// other by ORDINAL identity (matching the schema's byte-exact logical-name uniqueness). This is NOT
    /// mapping-specific — it holds for <c>none</c> mode too — so it lives OUTSIDE
    /// <see cref="ValidateColumnMappingSchema"/> (which is <c>mode != None</c> only). Each violation commits and
    /// loads today: an absent column only surfaces at append/overwrite planning; a nested/binary partition
    /// column commits then fails the partition-value encode ("Type '…' is not supported as a Delta partition
    /// column"); a duplicate (e.g. <c>[region, region]</c>) doubles the partition-directory path and a strict
    /// reader (Spark Delta <c>COLUMN_ALREADY_EXISTS</c>) rejects the table. The committer runs this BEFORE
    /// publish so all fail closed at COMMIT (table unchanged, no bytes published). It is intentionally NOT run at
    /// snapshot load — a large corpus of hand-authored log/checkpoint fixtures uses a stub schema that omits
    /// partition columns, so a load-side check would be too broad; the committer guarantees no NEW bad-partition
    /// table is published. O(partitionColumns).
    /// </summary>
    /// <exception cref="DeltaProtocolException">A partition column is absent, a non-encodable type, or listed twice.</exception>
    public static void EnsurePartitionColumnsInSchema(StructType schema, ImmutableArray<string> partitionColumns)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string column in partitionColumns)
        {
            if (!schema.TryGetField(column, out StructField field))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{column}' is not present in the table schema."));
            }

            if (!DeltaWriteEncoding.IsSupportedPartitionType(field.DataType))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{column}' has type '{field.DataType.SimpleString}', which is not a "
                        + $"supported Delta partition-column type; only atomic types (not struct/array/map/binary) "
                        + $"may be partition columns."));
            }

            if (!seen.Add(column))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{column}' is listed more than once in metaData.partitionColumns; "
                        + $"partition columns must be distinct (a duplicate doubles the partition-directory path "
                        + $"and yields a table strict readers reject as a duplicate column)."));
            }
        }
    }

    /// <summary>
    /// The <b>all-mode</b> case-insensitive column-name uniqueness invariant (#572 deltaspec R9): a committed
    /// schema MUST NOT contain two fields at the same struct level whose names are equal under an ordinal
    /// case-insensitive compare (e.g. <c>region</c> and <c>REGION</c>). DeltaSharp stores names
    /// case-sensitively, but a strict reader that resolves names case-insensitively (Spark's default) rejects
    /// such a table (<c>COLUMN_ALREADY_EXISTS</c>) — so DeltaSharp must not author one. Runs at the committer
    /// for EVERY committed metaData (all modes), recursing into nested structs / array elements / map key+value
    /// so a collision is caught at any level. Complements the schema-<b>evolution</b> path's identical check
    /// (<see cref="DeltaSchemaEnforcer"/>) for the fresh-create / replace path. O(fields).
    /// </summary>
    /// <exception cref="DeltaProtocolException">Two field names at one struct level collide case-insensitively.</exception>
    public static void EnsureNoCaseInsensitiveDuplicateColumns(StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        CheckCaseInsensitiveDuplicates(schema, parentPath: null);
    }

    private static void CheckCaseInsensitiveDuplicates(StructType schema, string? parentPath)
    {
        var seen = new Dictionary<string, string>(schema.Count, StringComparer.OrdinalIgnoreCase);
        foreach (StructField field in schema)
        {
            string path = parentPath is null ? field.Name : parentPath + "." + field.Name;
            if (seen.TryGetValue(field.Name, out string? existing)
                && !string.Equals(existing, field.Name, StringComparison.Ordinal))
            {
                string existingPath = parentPath is null ? existing : parentPath + "." + existing;
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Schema column '{path}' collides case-insensitively with '{existingPath}'; column names "
                        + $"must be unique ignoring case (a case-insensitive reader such as Spark rejects the "
                        + $"table as a duplicate column)."));
            }

            seen[field.Name] = field.Name;
            RecurseCaseInsensitiveDuplicates(field.DataType, path);
        }
    }

    private static void RecurseCaseInsensitiveDuplicates(DataType type, string path)
    {
        switch (type)
        {
            case StructType nested:
                CheckCaseInsensitiveDuplicates(nested, path);
                break;
            case ArrayType array:
                RecurseCaseInsensitiveDuplicates(array.ElementType, path + ".element");
                break;
            case MapType map:
                RecurseCaseInsensitiveDuplicates(map.KeyType, path + ".key");
                RecurseCaseInsensitiveDuplicates(map.ValueType, path + ".value");
                break;
        }
    }

    /// <summary>
    /// The <b>none-mode</b> partition-name path-safety invariant (#572 deltaspec R7). In <c>none</c> mode a
    /// partition column's <b>logical</b> name IS the partition-directory path segment (<c>logicalName=value/</c>)
    /// — there is no physical mapping to decouple it — so every partition column name MUST be a <b>safe path
    /// segment</b> under the same char + length contract <see cref="EnsureSafePhysicalName"/> enforces on a
    /// mapped physical name (a separator, <c>=</c>, <c>:</c>, control char, <c>.</c>/<c>..</c>,
    /// whitespace-only, or over-<see cref="MaxPathSegmentNameBytes"/>-byte name would restructure/escape the
    /// directory tree or exceed the filesystem path-component limit). A crafted <c>partitionColumns</c> naming
    /// an unsafe column (e.g. <c>../escape</c>) commits and loads today and only fails a later partitioned
    /// write at the path/confined-root guard; this rejects it fail-closed at COMMIT (table unchanged). Like
    /// <see cref="EnsurePartitionColumnsInSchema"/> (the all-mode existence check) it runs at the committer —
    /// NOT at snapshot load — because the stub-schema log/checkpoint fixture corpus is too broad for a
    /// load-side name check; the committer guarantees no NEW unsafe-partition-name table is published. It is
    /// scoped to <c>none</c> mode and to PARTITION columns only: in name/id mode the path segment is the mapped
    /// physical name (validated by <see cref="ValidateColumnMappingSchema"/>) while the logical name is
    /// decoupled from the path (it may legitimately hold any Parquet-legal character — the very purpose of
    /// column mapping); and a non-partition logical name never reaches a path. O(partitionColumns).
    /// </summary>
    /// <exception cref="DeltaProtocolException">A partition column name is not a safe path segment.</exception>
    public static void EnsureNoneModePartitionNamesSafe(ImmutableArray<string> partitionColumns)
    {
        foreach (string column in partitionColumns)
        {
            string? reason = FindUnsafePathSegmentReason(column);
            if (reason is not null)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{SanitizeEchoedToken(column)}' is not a safe path segment; in none "
                        + $"mode a partition column's logical name becomes a partition-directory path segment, "
                        + $"so it MUST NOT {reason}. The table cannot be written safely."));
            }
        }
    }

    /// <summary>Builds the <c>metaData.configuration</c> for a name-mode table (the mode plus the tracked
    /// <c>maxColumnId</c>).</summary>
    public static ImmutableSortedDictionary<string, string> NameModeConfiguration(long maxColumnId) =>
        ColumnMappingConfiguration(NameMode, maxColumnId);

    /// <summary>Builds the <c>metaData.configuration</c> for an id-mode table (the mode plus the tracked
    /// <c>maxColumnId</c>) — the id-mode sibling of <see cref="NameModeConfiguration"/> (#572).</summary>
    public static ImmutableSortedDictionary<string, string> IdModeConfiguration(long maxColumnId) =>
        ColumnMappingConfiguration(IdMode, maxColumnId);

    private static ImmutableSortedDictionary<string, string> ColumnMappingConfiguration(string mode, long maxColumnId)
    {
        return ImmutableSortedDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add(ModeKey, mode)
            .Add(MaxColumnIdKey, maxColumnId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The <c>protocol</c> action a fresh name-mode table declares (see
    /// <see cref="ColumnMappingProtocol"/> — the protocol is mode-independent; the mode lives in the
    /// <c>metaData.configuration</c>).</summary>
    public static ProtocolAction NameModeProtocol() => ColumnMappingProtocol();

    /// <summary>The <c>protocol</c> action a fresh id-mode table declares — the id-mode sibling of
    /// <see cref="NameModeProtocol"/> (#572). Byte-identical to the name-mode protocol: column mapping's
    /// protocol requirement does not depend on the mode.</summary>
    public static ProtocolAction IdModeProtocol() => ColumnMappingProtocol();

    /// <summary>The <c>protocol</c> action a fresh column-mapped table (name OR id) declares: the
    /// table-features reader (v3) and writer (v7) versions with the <c>columnMapping</c> feature listed in
    /// both feature sets (Delta protocol: columnMapping requires reader ≥ 2 / writer ≥ 5; this build uses the
    /// table-features representation so <see cref="ProtocolSupport"/> can gate it by name).</summary>
    private static ProtocolAction ColumnMappingProtocol()
    {
        return new ProtocolAction(
            ProtocolSupport.TableFeaturesReaderVersion,
            ProtocolSupport.TableFeaturesWriterVersion,
            ImmutableArray.Create(Feature),
            ImmutableArray.Create(Feature));
    }

    // Builds one PHYSICAL StructField: renamed to <paramref name="physicalName"/>, carrying the write shape
    // (<paramref name="dataType"/>/<paramref name="nullable"/>). In id mode it carries ONLY the
    // delta.columnMapping.id (read from <paramref name="idSource"/> — the field that owns the mapping) so the
    // Parquet writer stamps the field_id an id-mode reader resolves by; in name mode it carries no
    // column-mapping metadata (a name-mode physical file is field_id-free — #523 AC3). <paramref name="logicalName"/>
    // is used only for a precise fail-closed diagnostic.
    private static StructField ToPhysicalField(
        string logicalName, DataType dataType, bool nullable, string physicalName, StructField idSource, ColumnMappingMode mode)
    {
        if (mode != ColumnMappingMode.Id)
        {
            return new StructField(physicalName, dataType, nullable);
        }

        if (!TryGetId(idSource, out long id))
        {
            throw DeltaProtocolException.Inconsistent(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{logicalName}' has no '{IdKey}' but the table uses column mapping 'id' mode; the "
                    + $"schema is inconsistent and cannot be written safely."));
        }

        return new StructField(
            physicalName,
            dataType,
            nullable,
            FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>(IdKey, MetadataValue.Long(id)),
            }));
    }

    private static string ModeName(ColumnMappingMode mode) => mode switch
    {
        ColumnMappingMode.Name => NameMode,
        ColumnMappingMode.Id => IdMode,
        _ => NoneMode,
    };

    private static void EnsureLeaf(StructField field)
    {
        if (field.DataType is StructType or ArrayType or MapType)
        {
            throw DeltaProtocolException.Unsupported(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{field.Name}' is a nested ({field.DataType.TypeName}) type; nested column "
                    + $"mapping is phased in this build (design §2.9/§2.12.3). Only top-level (leaf) columns "
                    + $"are supported in column mapping 'name'/'id' mode."));
        }
    }
}
