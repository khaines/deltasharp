using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// <c>delta.columnMapping.id</c>. This build <b>reads</b> id-mode tables (#523); <b>writing</b> an id-mode
    /// table is gated fail-closed by <see cref="ColumnMapping.EnsureWriteSupported"/> (id-mode write deferred,
    /// #572).</summary>
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
/// <para><b>Fail-closed on <c>id</c> mode.</b> This build serves <c>none</c> and <c>name</c> modes.
/// <c>id</c> mode resolves columns by the Parquet <c>field_id</c>, which the name-based Parquet reader
/// does not implement, so an <c>id</c>-mode table is rejected with a precise error rather than risk a
/// positional/name misread (Delta protocol, name-mode vs. id-mode readers). See #523.</para>
///
/// <para><b>Scope.</b> Only top-level (leaf) struct fields are mapped in this build; nested struct/array/map
/// column mapping is phased (the Parquet writer already rejects nested physical types, design §2.9). A
/// nested top-level field in a name-mode schema is rejected fail-closed.</para>
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

    /// <summary>
    /// The column-mapping gate applied when a snapshot is loaded (design §2.12.3; STORY-05.4.3 AC4):
    /// a table declaring any column-mapping mode MUST have a <paramref name="protocol"/> that supports the
    /// <c>columnMapping</c> feature — the Delta protocol says the <c>delta.columnMapping.mode</c> property is
    /// only honored when the protocol supports it, so a mode set without protocol support is rejected with a
    /// protocol-upgrade error rather than silently ignored. <see cref="ColumnMappingMode.None"/> is a no-op.
    ///
    /// <para>All three modes (<c>none</c>/<c>name</c>/<c>id</c>) are <b>readable</b> — id-mode read resolves
    /// columns by the Parquet <c>field_id</c> (#523), so this LOAD gate no longer rejects id. WRITING a
    /// column-mapped table is a separate concern: <see cref="EnsureWriteSupported"/> — enforced centrally at
    /// the <c>DeltaCommitter</c> commit choke point (plus per-write-path guards) — still rejects <c>id</c> mode
    /// fail-closed (this build reads, but does not write, id-mode tables).</para>
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

        // Note: id mode is NOT rejected here — it is readable (#523). Write-time rejection of id mode is
        // enforced by EnsureWriteSupported on the commit paths, which is the correct place for it: this build
        // does not create/modify id-mode tables, but it reads them.
    }

    /// <summary>
    /// The fail-closed gate for <b>writing</b> a column-mapped table: this build writes
    /// <see cref="ColumnMappingMode.None"/> and <see cref="ColumnMappingMode.Name"/> only. <c>id</c> mode is
    /// <b>readable</b> (#523, resolved by Parquet <c>field_id</c>) but this build does not create or commit to
    /// an id-mode table, so every commit path calls this to refuse an id-mode write fail-closed — never falling
    /// back to a name/positional write that would mis-associate columns. (READS are gated at load by
    /// <see cref="EnsureModeGate"/>, which permits all three modes.)
    /// </summary>
    /// <exception cref="DeltaProtocolException"><paramref name="mode"/> is
    /// <see cref="ColumnMappingMode.Id"/> — writing is rejected fail-closed.</exception>
    public static void EnsureWriteSupported(ColumnMappingMode mode)
    {
        if (mode == ColumnMappingMode.Id)
        {
            throw DeltaProtocolException.Unsupported(
                "The table uses Delta column mapping mode 'id'. This build can READ id-mode tables (resolving "
                + "columns by the Parquet field_id, #523) but does not WRITE them — creating or committing to "
                + "an id-mode table is refused fail-closed rather than falling back to a name/positional write "
                + "that could silently mis-associate columns. Only 'name' (and 'none') mode is writable.");
        }
    }

    /// <summary>
    /// The <b>resolution-time uniqueness invariant</b> for a column-mapped (<c>name</c> or <c>id</c>) table,
    /// enforced at the single snapshot-load choke point BEFORE any column is resolved (design §2.12.3; Delta
    /// protocol column-mapping reader). A column-mapped schema resolves partition values and statistics — and,
    /// in name mode, data columns — by <c>delta.columnMapping.physicalName</c>, and resolves id-mode data
    /// columns by <c>delta.columnMapping.id</c>; so a duplicate physical name or a duplicate/missing id (a
    /// poisoned/malformed or foreign table) would let one field's value be served under another field's logical
    /// name, or two logical columns map to one file column — a <b>silent misread</b> with no exception. This
    /// gate rejects such a table fail-closed instead (#523 extended it to id mode — a foreign id-mode table is
    /// exactly the untrusted input this guards):
    /// <list type="number">
    /// <item>the set of <c>physicalName</c> across <b>all</b> top-level fields (data + partition) is globally
    /// unique;</item>
    /// <item>every field carries a <c>delta.columnMapping.id</c>, the ids are unique, and each id is
    /// ≤ the table's <c>delta.columnMapping.maxColumnId</c> (a monotonic writer invariant).</item>
    /// </list>
    /// <c>none</c> mode is a no-op. This is deliberately an explicit choke point (not an incidental
    /// <see cref="StructType"/> ctor throw), so the guarantee holds regardless of how the schema is built.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A duplicate physical name or id, a missing id, or an id above
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
            string physical = PhysicalName(field, mode);
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
    /// Reconciles a name-mode table's column mapping onto a target logical <paramref name="evolvedSchema"/> —
    /// an additive append/overwrite evolution (#541) or a wholesale <c>overwriteSchema</c> replacement (#542).
    /// Each field already present in <paramref name="currentMappedSchema"/> (matched by <b>logical</b> name)
    /// REUSES its existing <c>delta.columnMapping.id</c> + <c>delta.columnMapping.physicalName</c> verbatim —
    /// an <b>applied type widening</b> (or any type change under a destructive replace) keeps the column's
    /// identity, only its type changes — while each <b>new</b> field mints a fresh physical name from
    /// <paramref name="nameSource"/> plus a fresh monotonically increasing id (<c>maxColumnId + 1, …</c>). A
    /// column present in the current schema but ABSENT from the target (a <c>overwriteSchema</c> drop) is
    /// simply not emitted; its id is <b>retired</b>, never reused, because <c>maxColumnId</c> only ever
    /// increases. Every other per-field metadata carried on the target field (e.g. a <c>delta.typeChanges</c>
    /// entry, a column comment) is preserved. Returns the mapped schema and the configuration with the bumped
    /// <c>maxColumnId</c> (all other configuration entries preserved). Mirrors the create-path minting
    /// (<see cref="AssignFreshMapping"/>) but never re-mints an existing column's identity.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The current schema's <c>maxColumnId</c> is missing/malformed,
    /// a retained name-mode column carries no id, or an evolved field is a nested type.</exception>
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
    /// order and types, but each field renamed to its physical name with the column-mapping metadata
    /// stripped — the exact shape a name-mode Parquet data file stores and is read by.</summary>
    public static StructType ToPhysicalSchema(StructType schema, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (mode != ColumnMappingMode.Name)
        {
            return schema;
        }

        var physical = new List<StructField>(schema.Count);
        foreach (StructField field in schema)
        {
            EnsureLeaf(field);
            physical.Add(new StructField(PhysicalName(field, mode), field.DataType, field.Nullable));
        }

        return new StructType(physical);
    }

    /// <summary>
    /// Maps an incoming <paramref name="writeSchema"/> (LOGICAL column names, in write order) to the PHYSICAL
    /// schema the staged Parquet file must physically carry for an append/overwrite to an <b>existing</b>
    /// name-mode table (#525). Each write field is renamed to the physical name the table's
    /// <paramref name="tableMappedSchema"/> already assigned that logical column, <b>preserving the write
    /// order</b> and the write field's own type/nullability (so the staged bytes line up exactly with the
    /// partitioner's output). The table's existing <c>delta.columnMapping.id</c> / <c>physicalName</c> are
    /// REUSED verbatim — never re-minted — so an append never assigns a fresh physical name to an existing
    /// logical column. A write column absent from the table schema has no physical name to stage under (schema
    /// enforcement should have rejected it first) and fails closed.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A write column is absent from the name-mode table schema, or a
    /// mapped field carries no physical name.</exception>
    public static StructType MapWriteSchemaToPhysical(
        StructType writeSchema, StructType tableMappedSchema, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);
        ArgumentNullException.ThrowIfNull(tableMappedSchema);
        if (mode != ColumnMappingMode.Name)
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
                        $"Write column '{field.Name}' is not present in the name-mode table schema, so it has "
                        + $"no '{PhysicalNameKey}' to stage under; the write is rejected fail-closed."));
            }

            fields.Add(new StructField(PhysicalName(tableField, mode), field.DataType, field.Nullable));
        }

        return new StructType(fields);
    }

    /// <summary>Maps the table's logical <paramref name="partitionColumns"/> to their physical names, the
    /// form Delta records them (and their <c>add.partitionValues</c> keys) in the log under column mapping
    /// (Delta protocol writer requirement: partition values tracked by physical name).</summary>
    /// <exception cref="DeltaProtocolException">A partition column is absent from the schema.</exception>
    public static IReadOnlyList<string> PhysicalPartitionColumns(
        StructType mappedSchema, IReadOnlyList<string> partitionColumns, ColumnMappingMode mode)
    {
        ArgumentNullException.ThrowIfNull(mappedSchema);
        ArgumentNullException.ThrowIfNull(partitionColumns);
        if (mode != ColumnMappingMode.Name)
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

    /// <summary>Builds the <c>metaData.configuration</c> for a name-mode table (the mode plus the tracked
    /// <c>maxColumnId</c>).</summary>
    public static ImmutableSortedDictionary<string, string> NameModeConfiguration(long maxColumnId)
    {
        return ImmutableSortedDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add(ModeKey, NameMode)
            .Add(MaxColumnIdKey, maxColumnId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The <c>protocol</c> action a fresh name-mode table declares: the table-features reader
    /// (v3) and writer (v7) versions with the <c>columnMapping</c> feature listed in both feature sets
    /// (Delta protocol: columnMapping requires reader ≥ 2 / writer ≥ 5; this build uses the table-features
    /// representation so <see cref="ProtocolSupport"/> can gate it by name).</summary>
    public static ProtocolAction NameModeProtocol()
    {
        return new ProtocolAction(
            ProtocolSupport.TableFeaturesReaderVersion,
            ProtocolSupport.TableFeaturesWriterVersion,
            ImmutableArray.Create(Feature),
            ImmutableArray.Create(Feature));
    }

    private static void EnsureLeaf(StructField field)
    {
        if (field.DataType is StructType or ArrayType or MapType)
        {
            throw DeltaProtocolException.Unsupported(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{field.Name}' is a nested ({field.DataType.TypeName}) type; nested column "
                    + $"mapping is phased in this build (design §2.9/§2.12.3). Only top-level (leaf) columns "
                    + $"are supported in column mapping 'name' mode."));
        }
    }
}
