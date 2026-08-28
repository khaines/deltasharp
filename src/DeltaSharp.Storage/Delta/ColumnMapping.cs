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
/// <para><b>Scope (#676).</b> Column mapping attaches to <see cref="StructField"/>s at every depth (C1):
/// a <c>struct&lt;scalars&gt;</c> is mapped recursively (name + id mode); an <c>array&lt;scalar&gt;</c>/
/// <c>map&lt;scalar,scalar&gt;</c> receives a top-level id only (name mode; id-mode array/map is deferred to
/// #839). Nested-within-nested (array&lt;struct&gt;, struct&lt;struct&gt;, …) is supported for NAME/none mode
/// (#866 866a — recursive assignment/validation over the depth&gt;1 tree); ID-mode nested-within-nested stays
/// fail-closed at the assignment/validation door (<see cref="RejectNestedWithinNested"/>) until #866 866b.</para>
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

    /// <summary>The Delta per-field metadata key that assigns <c>field_id</c>s to an <c>array</c>/<c>map</c>
    /// interior (element / key / value). It is a <c>Map[String,Long]</c> stored on the <b>containing</b>
    /// array/map <see cref="StructField"/> (C1 preserved: the metadata lives on a <see cref="StructField"/>,
    /// not on the interior node), keyed by the interior physical-name path
    /// (<c>&lt;physicalName&gt;.element</c> for an array; <c>&lt;physicalName&gt;.key</c>/<c>.value</c> for a
    /// map), valued by the interior <c>field_id</c> (#839, design §2.2). It is meaningful ONLY under id mode on
    /// an in-scope array/map; carried under any other mode/shape it is a foreign key rejected fail-closed
    /// (design §2.4 / §3.16b).</summary>
    public const string NestedIdsKey = "delta.columnMapping.nested.ids";

    /// <summary>The <c>nested.ids</c> key selector terminating an <c>array&lt;scalar&gt;</c> element path.</summary>
    private const string ElementSelector = "element";

    /// <summary>The <c>nested.ids</c> key selector terminating a <c>map</c> key path.</summary>
    private const string KeySelector = "key";

    /// <summary>The <c>nested.ids</c> key selector terminating a <c>map</c> value path.</summary>
    private const string ValueSelector = "value";

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

    // Bounds and redacts an untrusted configuration value (e.g. delta.columnMapping.mode) before it is
    // interpolated into an exception message (#516 log-injection hardening). Delegates to the shared
    // DiagnosticText.Sanitize with the shared config-token cap (short protocol strings, unlike a dotted column
    // path), so both the control-char/line-separator neutralization (#667) AND the bound (#666) stay in one
    // place and cannot drift from the other config-value echoes (AppendOnlyFeature, RetentionPolicy).
    private static string SanitizeEchoedToken(string raw) =>
        DiagnosticText.Sanitize(raw, DiagnosticText.ConfigTokenMaxLength);

    // The conservative, portable per-path-COMPONENT budget (in UTF-8 bytes) for a NAME that becomes a Hive
    // partition-directory segment ("name=value") — #572 deltaspec R7. Filesystems cap a single path component
    // at ~255 bytes (Linux NAME_MAX, macOS, NTFS are all ~255); the partition directory encodes the column
    // name as "name=value", so the NAME alone must stay well under that ceiling to leave room for the "=value"
    // suffix (the value is data-dependent and Uri.EscapeDataString-encoded — e.g. __HIVE_DEFAULT_PARTITION__ is
    // 26 bytes). 128 bytes is half the component budget: it accepts every real name by a wide margin (a minted
    // physical name is "col-<uuid>" = 40 bytes; a logical partition name is typically far shorter) while
    // guaranteeing >=127 bytes of headroom for "=value". A crafted ~300-byte name is rejected fail-closed at
    // commit/load instead of failing a later partitioned write at the path-resolution/confined-root guard.
    //
    // RESIDUAL (#806): this bound is on the RAW name in UTF-8 bytes, but since #708 the NAME is ALSO
    // Uri.EscapeDataString-encoded into the segment (previously only the value was). An all-non-ASCII 128-byte
    // name expands to ~384 encoded characters (each byte -> "%XX"), which can exceed NAME_MAX for the ENCODED
    // segment even though the raw name passes this check. Tightening the budget to the ENCODED length (or
    // adopting Spark's escapePathName alphabet) is tracked with the broader path-encoding parity work under
    // #806; today the fail-closed direction is a later confined-root/path-resolution guard, not disclosure.
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
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];

            // A well-formed high+low SURROGATE PAIR is one astral code point (emoji, CJK Ext-B, mathematical
            // alphanumerics) — legal Unicode, legal UTF-8, and a legal filesystem segment / Parquet column
            // name. char.GetUnicodeCategory is per UTF-16 CODE UNIT and reports Surrogate for BOTH halves, so
            // consume a valid VISIBLE pair here; a LONE surrogate falls through to the reject set below. The
            // astral planes ALSO carry FORMAT controls (Cf) — U+E0001 LANGUAGE TAG and the U+E0020–E007F TAG
            // block (invisible-ASCII smuggling: two physical names that render identically to an operator),
            // U+110BD/U+110CD, U+13430, U+1BCA0, U+1D173–U+1D17A — which are just as unsafe in a directory /
            // column segment as a BMP Cf. char.GetUnicodeCategory cannot see them (per code UNIT); the
            // CharUnicodeInfo (string,int) overload reads the whole code POINT, so an astral Cf pair is NOT
            // consumed here and instead falls through to the reject block, where the high surrogate matches the
            // UnicodeCategory.Surrogate arm and returns the shared "…format control…/unpaired surrogate" reason.
            // Cf is the only extra check a valid pair needs: no other reject category (Cc, Zl/Zp, '/'\'='':')
            // has any astral member. This is the SAME pair-aware rule DiagnosticText.Sanitize now uses (it
            // neutralizes an astral Cf to U+FFFD) — the two agree, or a table one accepts the other rejects.
            if (char.IsHighSurrogate(c) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1])
                && CharUnicodeInfo.GetUnicodeCategory(segment, i) != UnicodeCategory.Format)
            {
                whitespaceOnly = false;
                i++;
                continue;
            }

            // A path segment becomes a REAL filesystem directory name (`name=value`) AND a Parquet column
            // name, so reject the full display/injection-unsafe set, aligned with DiagnosticText's message
            // sanitizer: path separators / `=` / `:`, any control (Cc), a line/paragraph separator (Zl/Zp), a
            // bidirectional or other FORMAT control (Cf — e.g. U+202E RIGHT-TO-LEFT OVERRIDE spoofs a directory
            // name's rendered order; the zero-width joiners U+200C/U+200D are also Cf and are DELIBERATELY
            // rejected in a literal directory segment despite being orthographically meaningful in some
            // scripts; the astral U+E0020–E007F TAG block — an invisible mirror of ASCII, and the tail of an
            // emoji subdivision-flag sequence like 🏴󠁧󠁢󠁳󠁣󠁴󠁿 — is Cf too and is likewise rejected: a path segment is
            // not a display string, and the TAG block cannot be admitted for flags without admitting arbitrary
            // invisible-ASCII smuggling), or a LONE (unpaired) UTF-16 surrogate (Cs — malformed, non-encodable).
            // Cf and a lone Cs are NOT caught by char.IsControl.
            if (c is '/' or '\\' or '=' or ':'
                || char.IsControl(c)
                || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator
                    or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate)
            {
                return "contain a path separator ('/' or '\\'), '=', ':', a control character, a "
                    + "line/paragraph separator, a bidirectional/format control, or an unpaired surrogate";
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
                $"Column '{DiagnosticText.Sanitize(logicalName)}' has a '{PhysicalNameKey}' ('{SanitizeEchoedToken(physical)}') that is "
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
    /// <b>name AND value</b> are percent-encoded (<c>Uri.EscapeDataString</c>) into the directory segment
    /// (since #708 the NAME too, not only the value), so a slash/traversal/control character in either is
    /// neutralised at write time (the value is data, not committed metaData); the
    /// staged data-file name is a crypto-random hex token, never metaData-derived; and Parquet <b>column-name</b>
    /// legality imposes nothing (Parquet.Net 6.1.0 round-trips ANY column name verbatim — empirically verified),
    /// so the binding constraint on a physical name is uniformly the partition-directory path, not the
    /// footer. (Note: the length bound is on the RAW name; the ENCODED-length residual for an all-non-ASCII
    /// name is tracked under #806 — see <c>MaxPathSegmentNameBytes</c>.)</para>
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

        // #676: validate the full nested StructField tree (C1 — mapping attaches to StructFields at every
        // depth, never to an array element / map key / value). Global id uniqueness + the maxColumnId ceiling
        // hold across the whole tree; physicalName uniqueness is per struct LEVEL (the physical Parquet path is
        // <parentPhysical>.<childPhysical>, so sibling uniqueness suffices). Fail closed at load AND commit
        // BEFORE any column resolves.
        var ids = new HashSet<long>();
        ValidateMappedLevel(schema, mode, parentPath: null, isTopLevel: true, ids, maxColumnId);

        // #676: run the recursive case-insensitive sibling-collision guard from THIS load choke point too, so
        // a foreign mapped table with a nested case-insensitive collision (struct<city,CITY>) fails closed at
        // load — matching a case-insensitive reader such as Spark (COLUMN_ALREADY_EXISTS). It also runs at the
        // committer/evolve path; running it here additionally covers a RAW/foreign committed metaData read.
        EnsureNoCaseInsensitiveDuplicateColumns(schema);
    }

    // Validates one struct LEVEL of a mapped schema and recurses into nested interiors (#676 single-level,
    // extended to depth>1 by #866 866a for name/none mode). Each StructField at this level must carry a valid
    // (id, physicalName); ids are unique globally (via the shared <paramref name="ids"/> set) and within the
    // maxColumnId ceiling; physicalNames are unique within this sibling set. An array/map interior carries no
    // mapping itself (C1) but its interior StructFields are validated recursively (name/none mode). Fail-closed
    // doors, in most-specific-first order: an ID-mode nested-within-nested interior (#866); an id-mode
    // array/map column (#839); a foreign nested.ids key; an unsafe physical name; a duplicate physical name /
    // id; a missing/out-of-range/over-ceiling id.
    private static void ValidateMappedLevel(
        StructType level, ColumnMappingMode mode, string? parentPath, bool isTopLevel,
        HashSet<long> ids, long maxColumnId)
    {
        var physicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (StructField field in level)
        {
            string path = parentPath is null ? field.Name : parentPath + "." + field.Name;

            // Nested-within-nested (#866, 866a) — a container whose interior is itself nested
            // (array<struct>, struct<struct>, map<_,struct>, array<array>, …). Name/none mode RECURSES over
            // the depth>1 tree (the interior StructFields are validated by ValidateMappedInterior below); ID
            // mode RETAINS the fail-closed reject until 866b lifts the id-mode arm (mode-gated, design §2.4
            // G1). Checked first so an id-mode nested-within-nested shape is reported BEFORE the id-mode
            // array/map (#839) gate.
            switch (field.DataType)
            {
                case StructType nestedStruct:
                    if (nestedStruct.Count == 0)
                    {
                        throw DeltaProtocolException.Unsupported(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Column '{DiagnosticText.Sanitize(path)}' is a zero-field struct; a mapped struct must have "
                                + $"at least one field."));
                    }

                    if (mode == ColumnMappingMode.Id)
                    {
                        foreach (StructField child in nestedStruct)
                        {
                            RejectNestedWithinNested(child.DataType, path + "." + child.Name);
                        }
                    }

                    break;
                case ArrayType array:
                    if (mode == ColumnMappingMode.Id)
                    {
                        RejectNestedWithinNested(array.ElementType, path + ".element");
                    }

                    break;
                case MapType map:
                    if (mode == ColumnMappingMode.Id)
                    {
                        RejectNestedWithinNested(map.KeyType, path + ".key");
                        RejectNestedWithinNested(map.ValueType, path + ".value");
                    }

                    break;
            }

            string physical = PhysicalName(field, mode);

            // #839: nested.ids handling — LIFTED gate + mode-gated parse/validate (design §2.4). Under ID
            // mode the nested-within-nested (#866) reject above already fired for a container whose interior
            // is itself nested, so any id-mode array/map reaching here has a SCALAR interior; name/none mode
            // mints no nested.ids (its depth>1 interior is validated by ValidateMappedInterior below).
            //  * mode == Id && array/map: a valid delta.columnMapping.nested.ids is REQUIRED — parse + validate
            //    it (ValidateNestedIds below, after the container id is validated so uniqueness spans interior
            //    vs top-level). A plain id-mode array/map with NO nested.ids stays fail-closed (its interior has
            //    no representable id, its container id is on a Parquet group node DeltaSharp cannot read — §2.6).
            //  * ANY other field carrying nested.ids (mode != Id on any shape incl. array/map, or id mode on a
            //    non-array/map field): the UNCONDITIONAL fail-closed reject (#676 C1 corollary) — nested.ids is
            //    meaningful only under id mode on an in-scope array/map, so a stray/foreign one is never
            //    accepted-and-ignored (which would regress #676's fail-closed guarantee, §3.16b).
            bool hasNestedIds = field.Metadata.TryGetValue(NestedIdsKey, out MetadataValue? nestedIdsValue);
            bool inScopeIdArrayMap = mode == ColumnMappingMode.Id && field.DataType is ArrayType or MapType;
            if (inScopeIdArrayMap && !hasNestedIds)
            {
                throw DeltaProtocolException.Unsupported(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' is an {field.DataType.TypeName} under column-mapping id "
                        + $"mode but carries no '{NestedIdsKey}'; id-mode nested array/map column mapping (#839) requires a "
                        + $"'{NestedIdsKey}' assigning the interior element/key/value field_id. A plain id-mode array/map "
                        + $"(no nested.ids) is rejected fail-closed."));
            }

            if (!inScopeIdArrayMap && hasNestedIds)
            {
                throw DeltaProtocolException.Unsupported(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' carries '{NestedIdsKey}', which assigns ids to an "
                        + $"array/map interior, but it is not an array/map under column-mapping id mode; '{NestedIdsKey}' is "
                        + $"meaningful only under id mode on an array/map column, so this foreign key is rejected fail-closed."));
            }

            // Path-safety: a TOP-LEVEL physical name is a partition-directory path segment AND a Parquet column
            // name, so it gets the full safe-segment contract. A NESTED physical name is a Parquet path
            // component only (a nested column is never a partition column), so it gets the control-char/
            // separator contract PLUS a stricter embedded-dot reject (defense-in-depth — a legitimate
            // col-<uuid> never contains a dot; #676 §2.2).
            if (isTopLevel)
            {
                EnsureSafePhysicalName(field.Name, physical);
            }
            else
            {
                EnsureNestedPhysicalNameSafe(path, physical);
            }

            if (!physicalNames.Add(physical))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column mapping physical name '{SanitizeEchoedToken(physical)}' is assigned to more than one column "
                        + $"at the same struct level (near '{DiagnosticText.Sanitize(path)}'); under column mapping every "
                        + $"sibling field MUST have a unique '{PhysicalNameKey}'. The schema is inconsistent and cannot "
                        + $"be read safely."));
            }

            if (!TryGetId(field, out long id))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has no '{IdKey}' but the table uses column mapping; the schema is inconsistent and cannot be read safely."));
            }

            if (id <= 0)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has '{IdKey}'={id} which is outside the valid column-mapping "
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
                        $"Column '{DiagnosticText.Sanitize(path)}' has '{IdKey}'={id} which exceeds the tracked "
                        + $"'{MaxColumnIdKey}'={maxColumnId}; the schema is inconsistent and cannot be read "
                        + $"safely."));
            }

            // #839: parse + validate the in-scope id-mode array/map interior ids AFTER the container id is
            // added to `ids`, so interior-id uniqueness spans interior↔interior and interior↔top-level, and the
            // interior ceiling shares the container's maxColumnId. (Only reached for an id-mode array/map that
            // carries nested.ids — the gate/foreign-reject above guaranteed it.)
            if (inScopeIdArrayMap)
            {
                ValidateNestedIds(field.DataType, path, physical, nestedIdsValue!, ids, maxColumnId);
            }

            // #866 (866a): recurse into the field's nested interior to validate StructFields at depth>1
            // (name/none mode). A struct interior validates directly; an array/map interior descends its
            // element/key/value to reach an interior struct. ID mode fails closed above at depth>1, so this
            // carries only name/none depth>1 struct interiors (and the pre-existing single-level struct
            // recursion, unchanged).
            ValidateMappedInterior(field.DataType, path, mode, ids, maxColumnId);
        }
    }

    // Recurses a mapped field's nested INTERIOR to validate the StructFields reachable within it at depth>1
    // (#866, 866a). A struct interior is validated as its own level (each child's id/physicalName, per-level
    // physicalName uniqueness, the shared global id set + ceiling); an array/map interior descends its
    // element/key/value token to reach a deeper interior struct. Name/none mode only — an id-mode
    // nested-within-nested shape fails closed at the ValidateMappedLevel door before this is reached.
    private static void ValidateMappedInterior(
        DataType type, string path, ColumnMappingMode mode, HashSet<long> ids, long maxColumnId)
    {
        switch (type)
        {
            case StructType structType:
                ValidateMappedLevel(structType, mode, path, isTopLevel: false, ids, maxColumnId);
                break;
            case ArrayType array:
                ValidateMappedInterior(array.ElementType, path + "." + ElementSelector, mode, ids, maxColumnId);
                break;
            case MapType map:
                ValidateMappedInterior(map.KeyType, path + "." + KeySelector, mode, ids, maxColumnId);
                ValidateMappedInterior(map.ValueType, path + "." + ValueSelector, mode, ids, maxColumnId);
                break;
        }
    }

    // Parses and validates a delta.columnMapping.nested.ids value for an in-scope id-mode array/map field
    // (#839, design §2.3/§2.4). The value MUST be a JSON object (MetadataValueKind.Nested); its keys MUST be
    // exactly the interior selectors for the field's declared shape prefixed by the container's physical name
    // (<physical>.element for an array; <physical>.key + <physical>.value for a map — no extra/missing/
    // wrong-prefix keys); each value MUST be MetadataValueKind.Long (checked BEFORE it is read as an id — a
    // non-Long value is a TYPED reject, never an untyped AsLong() throw, Finding 3); each id MUST be in
    // [1, maxColumnId] and [1, int.MaxValue]; and each id MUST be globally unique (added to the shared
    // <paramref name="ids"/> set — interior↔interior and interior↔top-level). Every reject is a typed
    // DeltaProtocolException; the ORDER is (a) object kind, (b) key-shape, (c) value-Long, (d) range,
    // (e) uniqueness (design §2.4).
    private static void ValidateNestedIds(
        DataType containerType, string path, string physical, MetadataValue nestedIdsValue,
        HashSet<long> ids, long maxColumnId)
    {
        if (nestedIdsValue.Kind != MetadataValueKind.Nested)
        {
            throw DeltaProtocolException.Inconsistent(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{DiagnosticText.Sanitize(path)}' has a '{NestedIdsKey}' that is not a JSON object "
                    + $"(Map[String,Long]); the schema is inconsistent and cannot be read safely."));
        }

        FieldMetadata nestedIds = nestedIdsValue.AsNested();

        // The exact expected key set for the field's declared shape (selector order: array=element; map=key,value).
        string[] expectedKeys = containerType is MapType
            ? new[] { physical + "." + KeySelector, physical + "." + ValueSelector }
            : new[] { physical + "." + ElementSelector };

        // (b) key-shape: no extra/unknown/wrong-prefix key.
        foreach (string actualKey in nestedIds.Keys)
        {
            if (Array.IndexOf(expectedKeys, actualKey) < 0)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has a '{NestedIdsKey}' key "
                        + $"'{SanitizeEchoedToken(actualKey)}' that does not match the field's declared "
                        + $"{containerType.TypeName} interior shape (expected '{SanitizeEchoedToken(string.Join("', '", expectedKeys))}'); "
                        + $"the schema is inconsistent and cannot be read safely."));
            }
        }

        foreach (string expectedKey in expectedKeys)
        {
            // (b) key-shape: no missing required key.
            if (!nestedIds.TryGetValue(expectedKey, out MetadataValue? interior))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has a '{NestedIdsKey}' missing the required interior key "
                        + $"'{SanitizeEchoedToken(expectedKey)}' for its declared {containerType.TypeName} shape; the schema is "
                        + $"inconsistent and cannot be read safely."));
            }

            // (c) value-Long: guard Kind explicitly BEFORE reading the id (Finding 3 — never an untyped
            // AsLong() throw on a Double/String/Bool/Null/nested-object value).
            if (interior.Kind != MetadataValueKind.Long)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has a '{NestedIdsKey}' value for '{SanitizeEchoedToken(expectedKey)}' "
                        + $"that is not an integer (JSON kind '{interior.Kind}'); a nested.ids interior id MUST be a Long. The "
                        + $"schema is inconsistent and cannot be read safely."));
            }

            long interiorId = interior.AsLong();

            // (d) range: [1, maxColumnId] and [1, int.MaxValue].
            if (interiorId <= 0 || interiorId > int.MaxValue)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has a '{NestedIdsKey}' interior id {interiorId} for "
                        + $"'{SanitizeEchoedToken(expectedKey)}' outside the valid column-mapping id range [1, int.MaxValue]. "
                        + $"The schema is inconsistent and cannot be read safely."));
            }

            if (interiorId > maxColumnId)
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column '{DiagnosticText.Sanitize(path)}' has a '{NestedIdsKey}' interior id {interiorId} for "
                        + $"'{SanitizeEchoedToken(expectedKey)}' which exceeds the tracked '{MaxColumnIdKey}'={maxColumnId}; the "
                        + $"schema is inconsistent and cannot be read safely."));
            }

            // (e) uniqueness: interior↔interior and interior↔top-level (shared ids set).
            if (!ids.Add(interiorId))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Column mapping id {interiorId} (a '{NestedIdsKey}' interior id for '{SanitizeEchoedToken(expectedKey)}') is "
                        + $"assigned to more than one column; under column mapping every id MUST be unique. The schema is "
                        + $"inconsistent and cannot be read safely."));
            }
        }
    }

    // Enforces the physical-name contract for a NESTED (non-top-level) mapped StructField (#676 §2.2): the
    // control-char/separator/format-control safe-segment set (shared with the top-level check) PLUS a stricter
    // embedded-'.' reject. A nested physical name is a Parquet path component, never a partition-directory
    // segment, so it need not be partition-safe — but it must be a single dot-free segment because the physical
    // Parquet path joins parent and child with '.', and a dot inside a name would ambiguously split the path.
    private static void EnsureNestedPhysicalNameSafe(string path, string physical)
    {
        string? reason = FindUnsafePathSegmentReason(physical);
        if (reason is null && physical.Contains('.', StringComparison.Ordinal))
        {
            reason = "contain a '.' (a nested physical name must be a single dot-free path segment)";
        }

        if (reason is not null)
        {
            throw DeltaProtocolException.Inconsistent(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Nested column '{DiagnosticText.Sanitize(path)}' has a '{PhysicalNameKey}' ('{SanitizeEchoedToken(physical)}') "
                    + $"that is not a safe path component; a nested physical name MUST NOT {reason}. The schema is "
                    + $"inconsistent and cannot be read safely."));
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
                $"Column '{DiagnosticText.Sanitize(field.Name)}' has no '{PhysicalNameKey}' but the table uses column mapping; the "
                + $"schema is inconsistent and cannot be read safely."));
    }

    /// <summary>Reads a field's assigned column id, if present.</summary>
    public static bool TryGetId(StructField field, out long id)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Metadata.TryGetLong(IdKey, out id);
    }

    /// <summary>
    /// Reads the <c>array&lt;scalar&gt;</c> element interior <c>field_id</c> from <paramref name="field"/>'s
    /// <c>delta.columnMapping.nested.ids</c> (keyed by <c><paramref name="physicalName"/>.element</c>), for
    /// the id-mode read/write interior binding (#839, design §2.5). Returns <see langword="false"/> when the
    /// key is absent or its value is not a <see cref="MetadataValueKind.Long"/> (a caller then fails closed).
    /// </summary>
    public static bool TryGetArrayElementId(StructField field, string physicalName, out long elementId)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(physicalName);
        return TryGetNestedInteriorId(field, physicalName + "." + ElementSelector, out elementId);
    }

    /// <summary>
    /// Reads the <c>map&lt;scalar,scalar&gt;</c> key and value interior <c>field_id</c>s from
    /// <paramref name="field"/>'s <c>delta.columnMapping.nested.ids</c> (keyed by
    /// <c><paramref name="physicalName"/>.key</c> / <c>.value</c>), for the id-mode read/write interior
    /// binding (#839, design §2.5). Returns <see langword="false"/> unless BOTH interior ids are present and
    /// <see cref="MetadataValueKind.Long"/>.
    /// </summary>
    public static bool TryGetMapKeyValueIds(StructField field, string physicalName, out long keyId, out long valueId)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(physicalName);
        valueId = 0;
        return TryGetNestedInteriorId(field, physicalName + "." + KeySelector, out keyId)
            && TryGetNestedInteriorId(field, physicalName + "." + ValueSelector, out valueId);
    }

    // Reads one interior id out of a container field's nested.ids nested-object metadata by its exact dotted
    // key, requiring the value to be a Long (defense-in-depth — the read path never calls AsLong() unchecked).
    private static bool TryGetNestedInteriorId(StructField field, string nestedKey, out long id)
    {
        id = 0;
        if (!field.Metadata.TryGetValue(NestedIdsKey, out MetadataValue? nestedIdsValue)
            || nestedIdsValue.Kind != MetadataValueKind.Nested)
        {
            return false;
        }

        if (!nestedIdsValue.AsNested().TryGetValue(nestedKey, out MetadataValue? interior)
            || interior.Kind != MetadataValueKind.Long)
        {
            return false;
        }

        id = interior.AsLong();
        return true;
    }

    /// <summary>
    /// Assigns a fresh column mapping to a logical <paramref name="schema"/> (name-mode table creation):
    /// every <see cref="StructField"/> <b>at every depth</b> (#676, design §2.2) is given a monotonically
    /// increasing <c>delta.columnMapping.id</c> (1..N, pre-order) and a stable
    /// <c>delta.columnMapping.physicalName</c> from <paramref name="nameSource"/>. Mapping attaches to
    /// <see cref="StructField"/>s only — never to an <c>array</c> element or a <c>map</c> key/value (C1): a
    /// <c>struct&lt;scalars&gt;</c> recurses into its children, while an <c>array&lt;scalar&gt;</c>/
    /// <c>map&lt;scalar,scalar&gt;</c> column receives a top-level id only. Existing per-field metadata is
    /// preserved. Returns the mapped schema and the resulting <c>maxColumnId</c> (N — a count of assigned
    /// <see cref="StructField"/>s).
    /// </summary>
    /// <exception cref="DeltaProtocolException">An ID-mode nested-within-nested shape (e.g.
    /// <c>array&lt;struct&gt;</c>, <c>struct&lt;struct&gt;</c>, <c>map&lt;_,struct&gt;</c>) — retained
    /// fail-closed until #866 866b lifts the id-mode arm — or a zero-field mapped struct is encountered; both
    /// fail closed before any id is minted for the offending column. Name/none mode recurses over the depth&gt;1
    /// tree (#866 866a).</exception>
    public static (StructType Schema, long MaxColumnId) AssignFreshMapping(
        StructType schema, IColumnPhysicalNameSource nameSource, ColumnMappingMode mode = ColumnMappingMode.Name)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(nameSource);

        long nextId = 0;
        var mapped = new List<StructField>(schema.Count);
        foreach (StructField field in schema)
        {
            mapped.Add(AssignMappedField(field, field.Name, nameSource, mode, ref nextId));
        }

        return (new StructType(mapped), nextId);
    }

    // Assigns a fresh (id, physicalName) to <paramref name="field"/> in pre-order, then recursively assigns
    // its nested struct children (#676, C1). The container id/name are minted BEFORE descending, so the
    // committed id order is pre-order (container, then its children) — matching the design §2.4 example. In id
    // mode an array/map container additionally mints its interior element/key/value ids (§2.3) and carries a
    // delta.columnMapping.nested.ids value; name/none mode mints none.
    private static StructField AssignMappedField(
        StructField field, string path, IColumnPhysicalNameSource nameSource, ColumnMappingMode mode, ref long nextId)
    {
        long id = ++nextId;
        string physicalName = nameSource.NextPhysicalName();
        DataType mappedType = AssignMappedType(
            field.DataType, path, physicalName, nameSource, mode, ref nextId, out MetadataValue? nestedIds);
        return WithMapping(field, mappedType, id, physicalName, nestedIds);
    }

    // Recurses the nested surface (#676 single-level, extended to depth>1 by #866 866a for name/none mode,
    // design §2.2): a struct recurses into its children (each a StructField assigned its own id/physicalName);
    // an array/map carries NO interior StructField itself (its element/key/value are not StructFields, C1) but
    // in NAME/none mode recurses into its interior so an interior struct's StructFields are minted. In ID mode
    // (#839) the array/map interior scalar ids are minted here — array element = ++nextId; map key = ++nextId
    // then value = ++nextId (key-then-value, pre-order after the container) — and returned via
    // <paramref name="nestedIds"/> as the delta.columnMapping.nested.ids value keyed by the container's
    // physical name. Name/none mode mints none (nestedIds is null). An ID-mode nested-within-nested interior
    // fails closed naming #866 BEFORE any child id is minted, so a partial maxColumnId advance can never leak
    // past the reject; name/none mode recurses instead.
    private static DataType AssignMappedType(
        DataType type, string path, string containerPhysical, IColumnPhysicalNameSource nameSource,
        ColumnMappingMode mode, ref long nextId, out MetadataValue? nestedIds)
    {
        nestedIds = null;
        switch (type)
        {
            case StructType structType:
                if (structType.Count == 0)
                {
                    throw DeltaProtocolException.Unsupported(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Column '{DiagnosticText.Sanitize(path)}' is a zero-field struct; a mapped struct must have "
                            + $"at least one field."));
                }

                var mappedChildren = new List<StructField>(structType.Count);
                foreach (StructField child in structType)
                {
                    if (mode == ColumnMappingMode.Id)
                    {
                        RejectNestedWithinNested(child.DataType, path + "." + child.Name);
                    }

                    mappedChildren.Add(AssignMappedField(child, path + "." + child.Name, nameSource, mode, ref nextId));
                }

                return new StructType(mappedChildren);
            case ArrayType array:
                if (mode == ColumnMappingMode.Id)
                {
                    RejectNestedWithinNested(array.ElementType, path + ".element");
                    long elementId = ++nextId;
                    nestedIds = BuildNestedIds(new[]
                    {
                        new KeyValuePair<string, long>(containerPhysical + "." + ElementSelector, elementId),
                    });
                    return type;
                }

                // #866 (866a): name/none mode recurses into the element so an interior struct's StructFields
                // each get their own (id, physicalName). A scalar element returns verbatim (single-level,
                // unchanged); name mode mints no nested.ids.
                DataType mappedElement = AssignMappedType(
                    array.ElementType, path + "." + ElementSelector, containerPhysical + "." + ElementSelector,
                    nameSource, mode, ref nextId, out _);
                return new ArrayType(mappedElement, array.ContainsNull);
            case MapType map:
                if (mode == ColumnMappingMode.Id)
                {
                    RejectNestedWithinNested(map.KeyType, path + ".key");
                    RejectNestedWithinNested(map.ValueType, path + ".value");
                    long keyId = ++nextId;
                    long valueId = ++nextId;
                    nestedIds = BuildNestedIds(new[]
                    {
                        new KeyValuePair<string, long>(containerPhysical + "." + KeySelector, keyId),
                        new KeyValuePair<string, long>(containerPhysical + "." + ValueSelector, valueId),
                    });
                    return type;
                }

                // #866 (866a): name/none mode recurses into the key then the value so interior struct
                // StructFields get their own (id, physicalName); scalar interiors return verbatim.
                DataType mappedKey = AssignMappedType(
                    map.KeyType, path + "." + KeySelector, containerPhysical + "." + KeySelector,
                    nameSource, mode, ref nextId, out _);
                DataType mappedValue = AssignMappedType(
                    map.ValueType, path + "." + ValueSelector, containerPhysical + "." + ValueSelector,
                    nameSource, mode, ref nextId, out _);
                return new MapType(mappedKey, mappedValue, map.ValueContainsNull);
            default:
                return type;
        }
    }

    // Builds the delta.columnMapping.nested.ids metadata value (a JSON object Map[String,Long]) from the
    // interior selector→id entries (#839, design §2.2). Stored on the CONTAINING array/map StructField (C1).
    private static MetadataValue BuildNestedIds(IEnumerable<KeyValuePair<string, long>> entries)
    {
        var values = new List<KeyValuePair<string, MetadataValue>>();
        foreach (KeyValuePair<string, long> entry in entries)
        {
            values.Add(new KeyValuePair<string, MetadataValue>(entry.Key, MetadataValue.Long(entry.Value)));
        }

        return MetadataValue.Nested(FieldMetadata.FromValues(values));
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
    /// a retained column-mapped column carries no id, or an evolved field is an ID-mode nested-within-nested
    /// type (#866, retained until 866b) / a zero-field mapped struct.</exception>
    public static (StructType Schema, ImmutableSortedDictionary<string, string> Configuration) EvolveNameModeMapping(
        StructType evolvedSchema,
        StructType currentMappedSchema,
        ImmutableSortedDictionary<string, string> currentConfiguration,
        IColumnPhysicalNameSource nameSource,
        ColumnMappingMode mode = ColumnMappingMode.Name)
    {
        ArgumentNullException.ThrowIfNull(evolvedSchema);
        ArgumentNullException.ThrowIfNull(currentMappedSchema);
        ArgumentNullException.ThrowIfNull(currentConfiguration);
        ArgumentNullException.ThrowIfNull(nameSource);

        long nextId = ReadMaxColumnId(currentConfiguration);
        var mapped = new List<StructField>(evolvedSchema.Count);
        foreach (StructField field in evolvedSchema)
        {
            StructField? existing = currentMappedSchema.TryGetField(field.Name, out StructField match) ? match : null;
            mapped.Add(EvolveMappedField(field, existing, field.Name, nameSource, mode, ref nextId));
        }

        ImmutableSortedDictionary<string, string> configuration =
            currentConfiguration.SetItem(MaxColumnIdKey, nextId.ToString(CultureInfo.InvariantCulture));
        return (new StructType(mapped), configuration);
    }

    // Reconciles one evolved field against its existing mapped counterpart (matched by LOGICAL name per level,
    // #676). An existing column reuses its (id, physicalName) verbatim — never re-minted, so committed data
    // files under the prior physical name still resolve; a NEW column (or a NEW nested child) mints a fresh
    // monotonic id + physical name, bumping the counter. A column whose TYPE changed (e.g. struct→array under
    // overwriteSchema) keeps its own identity but its former struct children have no same-typed counterpart to
    // match against, so any struct children of the NEW type are minted fresh — the old children's identities
    // are retired (never re-parented), exactly like a dropped column. In id mode (#839) an array/map container
    // reuses its existing interior nested.ids verbatim when the existing counterpart is the SAME nested kind
    // (rename-immutable — the physical name is reused so the keys stay valid); a NEW array/map, or a container
    // whose type CHANGED, mints fresh interior ids (old interior identities retired, never re-parented).
    private static StructField EvolveMappedField(
        StructField evolvedField, StructField? existingField, string path,
        IColumnPhysicalNameSource nameSource, ColumnMappingMode mode, ref long nextId)
    {
        long id;
        string physicalName;
        if (existingField is { } existing)
        {
            physicalName = PhysicalName(existing, ColumnMappingMode.Name);
            if (!TryGetId(existing, out id))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Name-mode column '{DiagnosticText.Sanitize(path)}' has no '{IdKey}'; the table schema is "
                        + $"inconsistent and cannot be evolved."));
            }
        }
        else
        {
            id = ++nextId;
            physicalName = nameSource.NextPhysicalName();
        }

        DataType mappedType = EvolveMappedType(
            evolvedField.DataType, existingField?.DataType, path, nameSource, mode, ref nextId);
        MetadataValue? nestedIds = ResolveEvolveNestedIds(
            evolvedField.DataType, existingField, physicalName, mode, ref nextId);
        return WithMapping(evolvedField, mappedType, id, physicalName, nestedIds);
    }

    // Resolves the delta.columnMapping.nested.ids for an id-mode array/map container during an evolve (#839):
    // reuse the existing counterpart's interior ids verbatim when the existing field is the SAME nested kind
    // (rename-immutable — the physical name is reused so its dotted keys stay valid); otherwise (a NEW column
    // or a type change) mint fresh interior ids after the container id (pre-order). Name/none mode, and any
    // non-array/map type, carry no nested.ids.
    private static MetadataValue? ResolveEvolveNestedIds(
        DataType evolvedType, StructField? existingField, string physicalName, ColumnMappingMode mode, ref long nextId)
    {
        if (mode != ColumnMappingMode.Id || evolvedType is not (ArrayType or MapType))
        {
            return null;
        }

        if (existingField is not null
            && SameNestedKind(evolvedType, existingField.DataType)
            && existingField.Metadata.TryGetValue(NestedIdsKey, out MetadataValue? existingNestedIds))
        {
            return existingNestedIds;
        }

        if (evolvedType is MapType)
        {
            long keyId = ++nextId;
            long valueId = ++nextId;
            return BuildNestedIds(new[]
            {
                new KeyValuePair<string, long>(physicalName + "." + KeySelector, keyId),
                new KeyValuePair<string, long>(physicalName + "." + ValueSelector, valueId),
            });
        }

        long elementId = ++nextId;
        return BuildNestedIds(new[]
        {
            new KeyValuePair<string, long>(physicalName + "." + ElementSelector, elementId),
        });
    }

    private static bool SameNestedKind(DataType a, DataType b) =>
        (a is ArrayType && b is ArrayType) || (a is MapType && b is MapType);

    // Recurses the nested surface during an evolve (#676 single-level, extended to depth>1 by #866 866a for
    // name/none mode). A struct matches each evolved child against the existing struct's SAME-named child
    // (only when the existing field is itself a struct — a type change retires the old children); in NAME/none
    // mode an array/map recurses into its interior so an interior struct's new StructFields mint fresh ids and
    // existing ones are preserved. An ID-mode nested-within-nested interior fails closed naming #866 before any
    // id is minted (retained until 866b).
    private static DataType EvolveMappedType(
        DataType evolvedType, DataType? existingType, string path,
        IColumnPhysicalNameSource nameSource, ColumnMappingMode mode, ref long nextId)
    {
        switch (evolvedType)
        {
            case StructType structType:
                if (structType.Count == 0)
                {
                    throw DeltaProtocolException.Unsupported(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Column '{DiagnosticText.Sanitize(path)}' is a zero-field struct; a mapped struct must have "
                            + $"at least one field."));
                }

                StructType? existingStruct = existingType as StructType;
                var children = new List<StructField>(structType.Count);
                foreach (StructField child in structType)
                {
                    if (mode == ColumnMappingMode.Id)
                    {
                        RejectNestedWithinNested(child.DataType, path + "." + child.Name);
                    }

                    StructField? existingChild =
                        existingStruct is not null && existingStruct.TryGetField(child.Name, out StructField ec)
                            ? ec
                            : null;
                    children.Add(EvolveMappedField(child, existingChild, path + "." + child.Name, nameSource, mode, ref nextId));
                }

                return new StructType(children);
            case ArrayType array:
                if (mode == ColumnMappingMode.Id)
                {
                    RejectNestedWithinNested(array.ElementType, path + ".element");
                    return evolvedType;
                }

                DataType evolvedElement = EvolveMappedType(
                    array.ElementType, (existingType as ArrayType)?.ElementType, path + "." + ElementSelector,
                    nameSource, mode, ref nextId);
                return new ArrayType(evolvedElement, array.ContainsNull);
            case MapType map:
                if (mode == ColumnMappingMode.Id)
                {
                    RejectNestedWithinNested(map.KeyType, path + ".key");
                    RejectNestedWithinNested(map.ValueType, path + ".value");
                    return evolvedType;
                }

                var existingMap = existingType as MapType;
                DataType evolvedKey = EvolveMappedType(
                    map.KeyType, existingMap?.KeyType, path + "." + KeySelector, nameSource, mode, ref nextId);
                DataType evolvedValue = EvolveMappedType(
                    map.ValueType, existingMap?.ValueType, path + "." + ValueSelector, nameSource, mode, ref nextId);
                return new MapType(evolvedKey, evolvedValue, map.ValueContainsNull);
            default:
                return evolvedType;
        }
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
            physical.Add(ToPhysicalField(field, field, mode, field.Name));
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
            if (!tableMappedSchema.TryGetField(field.Name, out StructField tableField))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Write column '{DiagnosticText.Sanitize(field.Name)}' is not present in the {ModeName(mode)}-mode table schema, "
                        + $"so it has no '{PhysicalNameKey}' to stage under; the write is rejected fail-closed."));
            }

            // Physical NAME + id come from the TABLE's existing mapping (reused verbatim, never re-minted);
            // the write column's own type/nullability rides so the staged bytes line up with the partitioner
            // output. In id mode the id is carried so the staged Parquet stamps the field_id. For a nested
            // struct the same rule recurses per child (#676): the child's physical name + id come from the
            // table mapping, its type/nullability from the write field.
            fields.Add(ToPhysicalField(field, tableField, mode, field.Name));
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
                        $"Partition column '{DiagnosticText.Sanitize(column)}' is not present in the table schema."));
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
                        $"Partition column '{DiagnosticText.Sanitize(column)}' is not present in the table schema."));
            }

            // TypeName, not SimpleString: this guard fires ONLY when the type is non-atomic, so
            // StructType.SimpleString always recurses and appends every nested field name verbatim. Measured
            // 74,052 chars with raw U+2028 from a 3,000-field foreign struct -- both defects this sweep
            // closes, one line below a Sanitize. The KIND (struct/array/map/binary) is the whole diagnosis
            // here; the offending column is already named and sanitized.
            if (!DeltaWriteEncoding.IsSupportedPartitionType(field.DataType))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{DiagnosticText.Sanitize(column)}' has type '{field.DataType.TypeName}', which is not a "
                        + $"supported Delta partition-column type; only atomic types (not struct/array/map/binary) "
                        + $"may be partition columns."));
            }

            if (!seen.Add(column))
            {
                throw DeltaProtocolException.Inconsistent(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Partition column '{DiagnosticText.Sanitize(column)}' is listed more than once in metaData.partitionColumns; "
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
                        // #667 message hygiene: the schema column names are the caller's own identifiers, so
                        // they are echoed through DiagnosticText.Sanitize (control-char strip + length cap)
                        // to close the log-injection vector while preserving the diagnostic names.
                        $"Schema column '{DiagnosticText.Sanitize(path)}' collides case-insensitively with '{DiagnosticText.Sanitize(existingPath)}'; column names "
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
                    // Cap class: a partition column NAME is a schema identifier, so it uses the shared
                    // DefaultMaxLength like every other partition-column echo in this file — not the tighter
                    // config-token cap, which is for protocol VALUES and physical names.
                    $"Partition column '{DiagnosticText.Sanitize(column)}' is not a safe path segment; in none "
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
    // Builds one PHYSICAL StructField from a WRITE field (<paramref name="writeField"/> — its type/nullability
    // ride) and the table's MAPPED counterpart (<paramref name="mappedField"/> — the authoritative
    // physicalName + id). In name mode the physical field carries NO column-mapping metadata (a name-mode
    // physical file is field_id-free — #523 AC3, byte-unchanged output); in id mode it carries ONLY its
    // delta.columnMapping.id so the Parquet writer stamps the field_id an id-mode reader resolves by (#572).
    // For a nested struct the same rule recurses per child (#676, design §2.2): child physical names + ids come
    // from the mapped struct, matched by logical name, in WRITE order; in NAME/none mode array/map interiors
    // recurse so an interior struct's children are relabelled to their physical names (#866 866a). An ID-mode
    // nested-within-nested interior fails closed naming #866 (retained until 866b).
    private static StructField ToPhysicalField(
        StructField writeField, StructField mappedField, ColumnMappingMode mode, string logicalPath)
    {
        string physicalName = PhysicalName(mappedField, mode);
        DataType physicalType = ToPhysicalType(writeField.DataType, mappedField.DataType, mode, logicalPath);

        if (mode != ColumnMappingMode.Id)
        {
            return new StructField(physicalName, physicalType, writeField.Nullable);
        }

        if (!TryGetId(mappedField, out long id))
        {
            throw DeltaProtocolException.Inconsistent(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{DiagnosticText.Sanitize(logicalPath)}' has no '{IdKey}' but the table uses column mapping 'id' mode; the "
                    + $"schema is inconsistent and cannot be written safely."));
        }

        return new StructField(
            physicalName,
            physicalType,
            writeField.Nullable,
            BuildPhysicalIdMetadata(mappedField, id));
    }

    // Builds the id-mode physical field metadata: always delta.columnMapping.id (so the writer stamps the
    // leaf/struct-child field_id), plus delta.columnMapping.nested.ids when the mapped container carries it
    // (an id-mode array/map, #839 — the writer reads it to stamp the interior leaf field_id).
    private static FieldMetadata BuildPhysicalIdMetadata(StructField mappedField, long id)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>(2)
        {
            new(IdKey, MetadataValue.Long(id)),
        };
        if (mappedField.Metadata.TryGetValue(NestedIdsKey, out MetadataValue? nestedIds))
        {
            entries.Add(new KeyValuePair<string, MetadataValue>(NestedIdsKey, nestedIds));
        }

        return FieldMetadata.FromValues(entries);
    }

    // Recursively relabels a write DataType to its physical shape: a struct relabels each child (name only,
    // + id in id mode) matched against the mapped struct by logical name, in WRITE order; in NAME/none mode an
    // array/map recurses into its interior so an interior struct's children are relabelled to their physical
    // names (#866 866a — mode-independent name substitution at depth>1). A struct child absent from the mapped
    // struct fails closed; an ID-mode nested-within-nested interior fails closed naming #866 (retained until
    // 866b — an id-mode depth>1 schema can never reach here because the assign/validate door rejects it).
    private static DataType ToPhysicalType(
        DataType writeType, DataType mappedType, ColumnMappingMode mode, string logicalPath)
    {
        switch (writeType)
        {
            case StructType writeStruct:
                if (mappedType is not StructType mappedStruct)
                {
                    throw DeltaProtocolException.Inconsistent(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Column '{DiagnosticText.Sanitize(logicalPath)}' is a struct in the write schema but not in the "
                            + $"{ModeName(mode)}-mode table schema; the write is rejected fail-closed."));
                }

                var children = new List<StructField>(writeStruct.Count);
                foreach (StructField writeChild in writeStruct)
                {
                    if (mode == ColumnMappingMode.Id)
                    {
                        RejectNestedWithinNested(writeChild.DataType, logicalPath + "." + writeChild.Name);
                    }

                    if (!mappedStruct.TryGetField(writeChild.Name, out StructField mappedChild))
                    {
                        throw DeltaProtocolException.Inconsistent(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Write struct field '{DiagnosticText.Sanitize(logicalPath + "." + writeChild.Name)}' is not present in "
                                + $"the {ModeName(mode)}-mode table schema, so it has no '{PhysicalNameKey}' to stage under; "
                                + $"the write is rejected fail-closed."));
                    }

                    children.Add(ToPhysicalField(writeChild, mappedChild, mode, logicalPath + "." + writeChild.Name));
                }

                return new StructType(children);
            case ArrayType array:
                if (mode == ColumnMappingMode.Id)
                {
                    RejectNestedWithinNested(array.ElementType, logicalPath + ".element");
                    return writeType;
                }

                if (mappedType is not ArrayType mappedArray)
                {
                    throw DeltaProtocolException.Inconsistent(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Column '{DiagnosticText.Sanitize(logicalPath)}' is an array in the write schema but not in the "
                            + $"{ModeName(mode)}-mode table schema; the write is rejected fail-closed."));
                }

                return new ArrayType(
                    ToPhysicalType(array.ElementType, mappedArray.ElementType, mode, logicalPath + "." + ElementSelector),
                    array.ContainsNull);
            case MapType map:
                if (mode == ColumnMappingMode.Id)
                {
                    RejectNestedWithinNested(map.KeyType, logicalPath + ".key");
                    RejectNestedWithinNested(map.ValueType, logicalPath + ".value");
                    return writeType;
                }

                if (mappedType is not MapType mappedMap)
                {
                    throw DeltaProtocolException.Inconsistent(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Column '{DiagnosticText.Sanitize(logicalPath)}' is a map in the write schema but not in the "
                            + $"{ModeName(mode)}-mode table schema; the write is rejected fail-closed."));
                }

                return new MapType(
                    ToPhysicalType(map.KeyType, mappedMap.KeyType, mode, logicalPath + "." + KeySelector),
                    ToPhysicalType(map.ValueType, mappedMap.ValueType, mode, logicalPath + "." + ValueSelector),
                    map.ValueContainsNull);
            default:
                return writeType;
        }
    }

    private static string ModeName(ColumnMappingMode mode) => mode switch
    {
        ColumnMappingMode.Name => NameMode,
        ColumnMappingMode.Id => IdMode,
        _ => NoneMode,
    };

    // Builds a StructField carrying <paramref name="mappedType"/> and the authoritative (id, physicalName),
    // preserving every other per-field metadata entry (a column comment, a delta.typeChanges entry) exactly
    // as the flat mapping does. Shared by the fresh-assign and evolve paths (#676). When
    // <paramref name="nestedIds"/> is non-null (an id-mode array/map container, #839) the
    // delta.columnMapping.nested.ids value is (re)stamped; when null it is stripped (name/none mode, or a
    // container whose type changed away from array/map — its interior identities are retired).
    private static StructField WithMapping(
        StructField field, DataType mappedType, long id, string physicalName, MetadataValue? nestedIds = null)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>(field.Metadata.Count + 3);
        foreach (KeyValuePair<string, MetadataValue> existing in field.Metadata)
        {
            if (!string.Equals(existing.Key, IdKey, StringComparison.Ordinal)
                && !string.Equals(existing.Key, PhysicalNameKey, StringComparison.Ordinal)
                && !string.Equals(existing.Key, NestedIdsKey, StringComparison.Ordinal))
            {
                entries.Add(existing);
            }
        }

        entries.Add(new KeyValuePair<string, MetadataValue>(IdKey, MetadataValue.Long(id)));
        entries.Add(new KeyValuePair<string, MetadataValue>(PhysicalNameKey, MetadataValue.String(physicalName)));
        if (nestedIds is not null)
        {
            entries.Add(new KeyValuePair<string, MetadataValue>(NestedIdsKey, nestedIds));
        }

        return new StructField(field.Name, mappedType, field.Nullable, FieldMetadata.FromValues(entries));
    }

    // Fail-closed guard for the ID-mode nested-within-nested boundary (#866, design §1/§2.4 G5): NAME/none
    // mode maps depth>1 nested columns recursively (866a); ID mode maps a single level only. A struct child,
    // array element, or map key/value that is ITSELF a struct/array/map (array<struct>, struct<struct>,
    // map<_,struct>, array<array>, …) is retained fail-closed for ID mode here — at the assignment/validation
    // door, BEFORE any interior id is minted, so a reject never leaves a partial maxColumnId advance — until
    // #866 866b lifts the id-mode arm. All call sites are id-mode-gated (§2.4); name/none mode recurses instead.
    private static void RejectNestedWithinNested(DataType interior, string path)
    {
        if (interior is StructType or ArrayType or MapType)
        {
            throw DeltaProtocolException.Unsupported(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{DiagnosticText.Sanitize(path)}' is a nested type within a nested type "
                    + $"('{interior.TypeName}') under column-mapping id mode; id-mode nested-within-nested column "
                    + $"mapping is not yet supported (#866). This build maps id-mode nested columns at a single level "
                    + $"only (a struct of scalars, an array of a scalar, a map of scalar to scalar); name/none mode "
                    + $"supports depth>1."));
        }
    }
}
