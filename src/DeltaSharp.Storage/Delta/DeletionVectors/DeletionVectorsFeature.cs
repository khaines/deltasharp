using System.Collections.Immutable;

namespace DeltaSharp.Storage.Delta.DeletionVectors;

/// <summary>
/// The <c>deletionVectors</c> table feature and its enablement gate (Delta protocol "Deletion Vectors").
/// A table supports DVs only at reader version 3 / writer version 7 with <c>deletionVectors</c> named in
/// both <c>readerFeatures</c> and <c>writerFeatures</c>; a writer may create <b>new</b> DVs only when the
/// table property <c>delta.enableDeletionVectors</c> is <c>true</c>. Readers must always respect a committed
/// DV even when the property is not set.
///
/// <para>This is the protocol-gate half of STORY-05.5.1 AC3: a merge-on-read DELETE against a table that
/// does not <see cref="SupportsWrite">support DV writes</see> fails closed (never silently ignores the
/// request or writes a DV a v1 reader would skip), and the reader/writer feature sets in
/// <see cref="ProtocolSupport"/> open only because this build fully implements DV read and write.</para>
/// </summary>
internal static class DeletionVectorsFeature
{
    /// <summary>The table-feature name in <c>readerFeatures</c>/<c>writerFeatures</c>.</summary>
    public const string Feature = "deletionVectors";

    /// <summary>The table property that gates writing <b>new</b> deletion vectors.</summary>
    public const string EnablePropertyKey = "delta.enableDeletionVectors";

    /// <summary>The reader protocol version DVs require.</summary>
    public const int ReaderVersion = 3;

    /// <summary>The writer protocol version DVs require.</summary>
    public const int WriterVersion = 7;

    /// <summary>True when <paramref name="protocol"/> declares the <c>deletionVectors</c> feature in both its
    /// reader and writer feature lists (reader v3 / writer v7 table features).</summary>
    public static bool SupportsWrite(ProtocolAction protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        return protocol.MinReaderVersion >= ReaderVersion
            && protocol.MinWriterVersion >= WriterVersion
            && protocol.ReaderFeatures.Contains(Feature, StringComparer.Ordinal)
            && protocol.WriterFeatures.Contains(Feature, StringComparer.Ordinal);
    }

    /// <summary>True when the table property <c>delta.enableDeletionVectors</c> is set to <c>true</c>
    /// (case-insensitive), which gates writing new DVs.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.TryGetValue(EnablePropertyKey, out string? value)
            && bool.TryParse(value, out bool enabled)
            && enabled;
    }

    /// <summary>
    /// Verifies a merge-on-read DELETE may write deletion vectors against <paramref name="snapshot"/>, else
    /// throws a precise fail-closed <see cref="DeltaProtocolException"/>: the table protocol must declare the
    /// <c>deletionVectors</c> feature (reader v3 + writer v7) AND the <c>delta.enableDeletionVectors</c>
    /// property must be <c>true</c>. This build fails closed (it does not silently upgrade an unprepared
    /// table's protocol), so a DV write can never produce a table a peer's v1 reader would misread by
    /// skipping the DV.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The table does not declare the feature or has not enabled it.</exception>
    public static void EnsureWriteEnabled(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!SupportsWrite(snapshot.Protocol))
        {
            throw DeltaProtocolException.Unsupported(
                "This table's protocol does not declare the 'deletionVectors' table feature (reader version 3 "
                + "and writer version 7 with 'deletionVectors' in readerFeatures and writerFeatures), so a "
                + "merge-on-read DELETE cannot write a deletion vector. The operation fails closed rather than "
                + "silently upgrade the protocol or drop the delete. Enable deletion vectors on the table first.");
        }

        if (!IsEnabled(snapshot.Metadata.Configuration))
        {
            throw DeltaProtocolException.Unsupported(
                "This table has not enabled deletion vectors (the 'delta.enableDeletionVectors' property is not "
                + "set to true), so a merge-on-read DELETE cannot write a new deletion vector. The operation "
                + "fails closed. Set 'delta.enableDeletionVectors'='true' on the table to enable it.");
        }
    }

    /// <summary>The <c>protocol</c> action for a fresh deletion-vector-enabled table (reader v3 / writer v7
    /// declaring <c>deletionVectors</c>). Also declares <c>columnMapping</c> is NOT required — only DVs.</summary>
    public static ProtocolAction Protocol() => new(
        ReaderVersion,
        WriterVersion,
        ImmutableArray.Create(Feature),
        ImmutableArray.Create(Feature));

    /// <summary>The <c>metaData.configuration</c> entry that enables DV writes on a fresh table.</summary>
    public static ImmutableSortedDictionary<string, string> EnabledConfiguration() =>
        ImmutableSortedDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add(EnablePropertyKey, "true");
}
