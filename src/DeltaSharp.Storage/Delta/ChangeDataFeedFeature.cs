using System.Collections.Immutable;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The <c>changeDataFeed</c> writer feature and its optional enablement gate (Delta protocol "Change Data
/// Feed"). When the table property <c>delta.enableChangeDataFeed</c> is <c>true</c>, data-changing commits
/// materialize their row changes as <c>cdc</c> (<see cref="AddCdcFileAction"/>) files under
/// <c>_change_data/</c> so an explicit change-feed read (§2.6) can surface them; absent the property nothing
/// changes and no <c>cdc</c> files are written.
///
/// <para><b>Writer-only feature.</b> Like <see cref="AppendOnlyFeature"/>, <c>changeDataFeed</c> is a
/// <b>writer</b> feature only — it is registered in <see cref="ProtocolSupport.SupportedWriterFeatures"/> and
/// enumerated only into <c>writerFeatures</c>, never into <c>readerFeatures</c> (Delta PROTOCOL.md "Writer
/// Features"). A normal snapshot read of a CDF-enabled table needs no reader feature because snapshot
/// reconstruction ignores <c>cdc</c> actions entirely (§2.3, INV C1), so
/// <see cref="ProtocolSupport.SupportedReaderFeatures"/> is unchanged.</para>
///
/// <para><b>Optional enable-gate.</b> Unlike <see cref="AppendOnlyFeature"/> (whose malformed value fails
/// closed because dropping the guarantee would fail <b>open</b>), <c>changeDataFeed</c> is an OPTIONAL
/// <c>delta.enable*</c> gate: an absent or malformed value safely leaves the feature OFF, so
/// <see cref="IsEnabled"/> uses the LENIENT convention (mirroring <see cref="TypeWideningFeature.IsEnabled"/>
/// / <c>DeletionVectorsFeature.IsEnabled</c>) and never throws.</para>
/// </summary>
internal static class ChangeDataFeedFeature
{
    /// <summary>The table-feature name in <c>writerFeatures</c> (a writer-only feature — never in
    /// <c>readerFeatures</c>).</summary>
    public const string Feature = "changeDataFeed";

    /// <summary>The table property that enables Change Data Feed generation. When <c>true</c>, data-changing
    /// commits materialize their row changes as <c>cdc</c> files.</summary>
    public const string PropertyKey = "delta.enableChangeDataFeed";

    /// <summary>
    /// True when the table property <c>delta.enableChangeDataFeed</c> is set to <c>true</c>
    /// (case-insensitive). This is an OPTIONAL enable-gate, so — unlike the fail-closed
    /// <see cref="AppendOnlyFeature.IsEnabled"/> — an absent or malformed value LENIENTLY leaves the feature
    /// OFF and never throws (matching <see cref="TypeWideningFeature.IsEnabled"/>): treating a bad value as
    /// not-enabled fails <b>closed</b> for an optional feature (no <c>cdc</c> files), the safe direction.
    /// </summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.TryGetValue(PropertyKey, out string? value)
            && bool.TryParse(value, out bool enabled)
            && enabled;
    }

    /// <summary>
    /// Adds the <c>changeDataFeed</c> feature to a <c>writerFeatures</c> list unless already present
    /// (idempotent; a default/uninitialized array is treated as empty). Used by the legacy → table-features
    /// upgrade to enumerate an active <c>changeDataFeed</c> feature so it stays active. Writer-only — this is
    /// never applied to a <c>readerFeatures</c> list.
    /// </summary>
    public static ImmutableArray<string> WithWriterFeature(ImmutableArray<string> writerFeatures)
    {
        if (!writerFeatures.IsDefault && writerFeatures.Contains(Feature, StringComparer.Ordinal))
        {
            return writerFeatures;
        }

        return (writerFeatures.IsDefault ? ImmutableArray<string>.Empty : writerFeatures).Add(Feature);
    }
}
