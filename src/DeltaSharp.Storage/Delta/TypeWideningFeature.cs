using System.Collections.Immutable;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The <c>typeWidening</c> table feature and its enablement gate (Delta protocol "Type Widening"). A table
/// supports type widening only at reader version 3 / writer version 7 with <c>typeWidening</c> named in both
/// <c>readerFeatures</c> and <c>writerFeatures</c>; a writer may <b>apply</b> a widening type change only when
/// the table property <c>delta.enableTypeWidening</c> is <c>true</c> (Delta PROTOCOL.md "Writer Requirements
/// for Type Widening": "Writers must reject widening type changes when this property isn't set to true").
/// Readers must always promote pre-widening data files to the current type when the feature is supported.
///
/// <para>This mirrors <see cref="DeletionVectors.DeletionVectorsFeature"/>: enablement is <b>create-time</b>
/// (the table is created with the feature in its <c>protocol</c> and the property in its
/// <c>metaData.configuration</c>). This build does <b>not</b> silently upgrade an unprepared table's protocol
/// on a widening write — it fails closed (as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>),
/// exactly as DV writes fail closed against a table that has not enabled deletion vectors. Enabling type
/// widening on an existing non-enabled table (an <c>ALTER TABLE … SET TBLPROPERTIES</c>-style protocol
/// upgrade committed by the widening write itself) is a separate, tracked follow-up (<b>#534</b>).</para>
///
/// <para><b>Stable vs preview name.</b> The current protocol uses the stable feature name
/// <see cref="Feature"/> (<c>typeWidening</c>); an older reference-engine spelling was
/// <see cref="FeaturePreview"/> (<c>typeWidening-preview</c>). This build <b>reads</b> a table declaring
/// either name (both are in <see cref="ProtocolSupport.SupportedReaderFeatures"/>/<c>SupportedWriterFeatures</c>)
/// but always <b>writes</b> the stable name.</para>
/// </summary>
internal static class TypeWideningFeature
{
    /// <summary>The stable table-feature name in <c>readerFeatures</c>/<c>writerFeatures</c> — what this build writes.</summary>
    public const string Feature = "typeWidening";

    /// <summary>The older ("preview") feature-name spelling; still accepted on read for interop.</summary>
    public const string FeaturePreview = "typeWidening-preview";

    /// <summary>The table property that gates <b>applying</b> a widening type change.</summary>
    public const string EnablePropertyKey = "delta.enableTypeWidening";

    /// <summary>The configuration-key prefix for a named CHECK constraint (the <c>checkConstraints</c> feature,
    /// distinct from the column-<c>invariants</c> feature).</summary>
    private const string ConstraintKeyPrefix = "delta.constraints.";

    /// <summary>The field-metadata key for a column invariant (legacy <c>invariants</c> feature).</summary>
    private const string InvariantKey = "delta.invariants";

    /// <summary>The reader protocol version type widening requires.</summary>
    public const int ReaderVersion = 3;

    /// <summary>The writer protocol version type widening requires.</summary>
    public const int WriterVersion = 7;

    /// <summary>True when <paramref name="protocol"/> declares the <c>typeWidening</c> feature (stable or
    /// preview spelling) in both its reader and writer feature lists at reader v3 / writer v7.</summary>
    public static bool Supports(ProtocolAction protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        return protocol.MinReaderVersion >= ReaderVersion
            && protocol.MinWriterVersion >= WriterVersion
            && HasFeature(protocol.ReaderFeatures)
            && HasFeature(protocol.WriterFeatures);
    }

    /// <summary>True when the table property <c>delta.enableTypeWidening</c> is set to <c>true</c>
    /// (case-insensitive), which gates applying a widening type change.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.TryGetValue(EnablePropertyKey, out string? value)
            && bool.TryParse(value, out bool enabled)
            && enabled;
    }

    /// <summary>
    /// True when <paramref name="snapshot"/> both <see cref="Supports">declares the <c>typeWidening</c>
    /// feature</see> AND has <see cref="IsEnabled">the <c>delta.enableTypeWidening</c> property set</see>, so
    /// the enforcer may <b>apply</b> a sanctioned widening on a write against it. Both conditions are required
    /// (the protocol only honors the property when the feature is present), so a table carrying the property
    /// without the feature — a malformed/partial state — stays fail-closed.
    /// </summary>
    public static bool IsWriteEnabled(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Supports(snapshot.Protocol) && IsEnabled(snapshot.Metadata.Configuration);
    }

    /// <summary>The <c>protocol</c> action for a fresh type-widening-enabled table (reader v3 / writer v7
    /// declaring the stable <c>typeWidening</c> feature).</summary>
    public static ProtocolAction Protocol() => new(
        ReaderVersion,
        WriterVersion,
        ImmutableArray.Create(Feature),
        ImmutableArray.Create(Feature));

    /// <summary>The <c>metaData.configuration</c> entry that enables applying widenings on a fresh table.</summary>
    public static ImmutableSortedDictionary<string, string> EnabledConfiguration() =>
        ImmutableSortedDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add(EnablePropertyKey, "true");

    /// <summary>
    /// The upgraded <c>protocol</c> for enabling type widening on an EXISTING table (#534): the table-features
    /// reader (≥ <see cref="ReaderVersion"/>) and writer (≥ <see cref="WriterVersion"/>) versions with the
    /// stable <c>typeWidening</c> feature added to <b>both</b> feature lists, PRESERVING every feature the
    /// table already declares (e.g. <c>columnMapping</c>, <c>deletionVectors</c>, or the preview spelling).
    /// Matches the create-time shape (<see cref="Protocol"/>) and <see cref="ColumnMapping.NameModeProtocol"/>.
    ///
    /// <para><b>Legacy feature enumeration (#549).</b> Per Delta PROTOCOL.md "Table Features for New and
    /// Existing Tables" + "Active Features", upgrading must enumerate every implicitly-active legacy feature
    /// so it stays active. This build enumerates the <c>appendOnly</c> writer feature (writer-only — added to
    /// <c>writerFeatures</c>, never <c>readerFeatures</c>) when the table currently has
    /// <c>delta.appendOnly=true</c>, and enforces it at commit
    /// (<see cref="AppendOnlyFeature.EnsureCommitAllowed"/>). The other legacy features —
    /// <c>invariants</c> / <c>checkConstraints</c> — need per-row expression enforcement this build does not
    /// yet have (overlaps #190), so <see cref="EnsureUpgradeable"/> still refuses a legacy table carrying them
    /// rather than silently deactivating them (tracked in #568).</para>
    ///
    /// <para>Idempotent in shape: if a feature is already present (stable OR preview <c>typeWidening</c>
    /// spelling, or <c>appendOnly</c>) the feature lists are returned unchanged; the version floors are
    /// already met on any table declaring <c>typeWidening</c>.</para>
    /// </summary>
    public static ProtocolAction UpgradeProtocol(
        ProtocolAction existing, IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(configuration);

        // Enumerate the implicitly-active legacy appendOnly writer feature so it stays ACTIVE across the
        // table-features upgrade (Delta "Active Features": appendOnly active iff in writerFeatures AND
        // delta.appendOnly=true). This build now enforces it; invariants/constraints are NOT enumerated (no
        // per-row enforcement yet — EnsureUpgradeable refuses them fail-closed, #568).
        ImmutableArray<string> writerFeatures = WithFeature(existing.WriterFeatures);
        if (AppendOnlyFeature.IsEnabled(configuration))
        {
            writerFeatures = AppendOnlyFeature.WithWriterFeature(writerFeatures);
        }

        return new ProtocolAction(
            Math.Max(existing.MinReaderVersion, ReaderVersion),
            Math.Max(existing.MinWriterVersion, WriterVersion),
            WithFeature(existing.ReaderFeatures),
            writerFeatures);
    }

    // Adds the stable typeWidening feature to a feature list unless it is already present (stable OR preview
    // spelling — so an already-supported table's list is left unchanged rather than carrying a duplicate). A
    // default (uninitialized) array is treated as empty.
    private static ImmutableArray<string> WithFeature(ImmutableArray<string> features)
    {
        if (HasFeature(features))
        {
            return features;
        }

        return (features.IsDefault ? ImmutableArray<string>.Empty : features).Add(Feature);
    }

    /// <summary>
    /// Guards enable-on-existing (<see cref="DeltaTableWriter.EnableTypeWideningAsync"/>) against silently
    /// deactivating a legacy (writer &lt; <see cref="WriterVersion"/>) feature this build cannot carry as a
    /// table feature. Per Delta PROTOCOL.md "Table Features for New and Existing Tables" + "Active Features",
    /// upgrading to writer 7 must enumerate every ACTIVE feature the legacy protocol implicitly supported.
    /// <see cref="UpgradeProtocol"/> now enumerates <c>appendOnly</c> (which this build enforces), so an
    /// active <c>delta.appendOnly=true</c> is upgradeable. This build still cannot enumerate
    /// <c>invariants</c>/<c>checkConstraints</c> (no per-row expression enforcement — overlaps #190), so if a
    /// FOREIGN legacy table currently has an active such feature (a <c>delta.constraints.*</c> CHECK
    /// constraint or a <c>delta.invariants</c> column invariant) the upgrade would leave the constraint in
    /// <c>metaData</c> while dropping the feature from <c>writerFeatures</c> — silently deactivating
    /// enforcement for every engine. Rather than corrupt another engine's guarantees, refuse fail-closed
    /// (proper enumeration + enforcement tracked in #568). A table already at writer 7 keeps its
    /// (already-enumerated) features, so it is always upgradeable.
    /// </summary>
    /// <exception cref="DeltaProtocolException">A legacy table declares an active CHECK constraint / column
    /// invariant this build cannot preserve through the table-features upgrade.</exception>
    public static void EnsureUpgradeable(
        ProtocolAction protocol, StructType schema, IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(configuration);

        // A table already on the table-features writer version has every active feature explicitly named, and
        // UpgradeProtocol preserves them — so it is always safe to upgrade.
        if (protocol.MinWriterVersion >= WriterVersion)
        {
            return;
        }

        // appendOnly is now enumerated + enforced (#549) by UpgradeProtocol, so it no longer blocks the
        // upgrade. invariants / CHECK constraints still fail closed until their per-row enforcement lands
        // (#568, overlaps #190).
        foreach (string key in configuration.Keys)
        {
            if (key.StartsWith(ConstraintKeyPrefix, StringComparison.Ordinal))
            {
                throw RefuseLegacyFeature($"a CHECK constraint ('{key}')");
            }
        }

        if (SchemaHasInvariant(schema))
        {
            throw RefuseLegacyFeature($"a column invariant ('{InvariantKey}')");
        }
    }

    private static DeltaProtocolException RefuseLegacyFeature(string activeFeature) =>
        DeltaProtocolException.Unsupported(
            $"Cannot enable type widening on this table: it is on a legacy writer protocol (version < "
            + $"{WriterVersion}) and currently declares {activeFeature}, which this build cannot carry as an "
            + "explicit table feature when upgrading to the table-features protocol (writer version "
            + $"{WriterVersion}). Upgrading would silently deactivate it for other engines (Delta 'Active "
            + "Features'), so the operation is refused fail-closed. Enabling type widening on a table with an "
            + "active invariant / CHECK constraint is tracked in #568.");

    // True when any field in the schema carries a column invariant in its metadata — the legacy `invariants`
    // feature this build cannot enumerate as a table feature on upgrade. Recurses through EVERY nesting a
    // Delta invariant can be reachable under (Spark collects invariants through struct fields AND
    // array-element / map key/value structs), so an invariant inside e.g. `array<struct<x>>` or
    // `map<string, struct<y>>` is not silently missed.
    private static bool SchemaHasInvariant(StructType schema)
    {
        foreach (StructField field in schema)
        {
            if (field.Metadata.TryGetValue(InvariantKey, out _) || TypeHasInvariant(field.DataType))
            {
                return true;
            }
        }

        return false;
    }

    // Descends a field's DATA TYPE looking for a nested struct field that carries a column invariant, through
    // struct fields, array elements, and map keys/values.
    private static bool TypeHasInvariant(DataType type) => type switch
    {
        StructType structType => SchemaHasInvariant(structType),
        ArrayType arrayType => TypeHasInvariant(arrayType.ElementType),
        MapType mapType => TypeHasInvariant(mapType.KeyType) || TypeHasInvariant(mapType.ValueType),
        _ => false,
    };

    private static bool HasFeature(ImmutableArray<string> features) =>
        !features.IsDefault
        && (features.Contains(Feature, StringComparer.Ordinal)
            || features.Contains(FeaturePreview, StringComparer.Ordinal));
}
