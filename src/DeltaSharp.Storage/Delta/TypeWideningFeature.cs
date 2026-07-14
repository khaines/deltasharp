using System.Collections.Immutable;

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

    private static bool HasFeature(ImmutableArray<string> features) =>
        !features.IsDefault
        && (features.Contains(Feature, StringComparer.Ordinal)
            || features.Contains(FeaturePreview, StringComparer.Ordinal));
}
