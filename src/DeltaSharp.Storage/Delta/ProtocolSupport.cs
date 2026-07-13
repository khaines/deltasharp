using System.Collections.Immutable;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The Delta reader protocol this build implements, and the fail-closed <b>protocol negotiation</b> gate
/// applied before a snapshot is served (design §2.10.5; STORY-05.2.3 AC4). A read must not proceed past a
/// protocol version or named table feature the reader does not fully understand — doing so risks silently
/// wrong results — so <see cref="EnsureReadable"/> throws a precise <see cref="DeltaProtocolException"/>
/// naming the offending version/feature (checklist <c>Delta log protocol</c> bullet 2; anti-pattern:
/// never silently read an unsupported table feature).
///
/// <para><b>v1 baseline.</b> This reader implements the <i>basic</i> reader (protocol reader version 1) and
/// understands the reader-version-3 "table features" protocol, but implements <b>no advanced reader
/// features</b> (<see cref="SupportedReaderFeatures"/> is empty). Consequently:
/// <list type="bullet">
/// <item>reader version 1 tables are served;</item>
/// <item>reader version 2 tables (legacy column mapping) fail closed — column mapping is not implemented;</item>
/// <item>reader version 3 tables are served only when they require <b>no</b> reader features; any listed
/// reader feature (<c>columnMapping</c>, <c>deletionVectors</c>, <c>v2Checkpoint</c>, <c>timestampNtz</c>,
/// …) fails closed;</item>
/// <item>any other reader version fails closed.</item>
/// </list>
/// This is exactly what lets FEAT-05.2 fully serve baseline (string-only-metadata) tables while deferring
/// column mapping / typed metadata to a later feature story (design §9.1 D-6).</para>
/// </summary>
internal static class ProtocolSupport
{
    /// <summary>The basic reader protocol version (no table features).</summary>
    public const int BasicReaderVersion = 1;

    /// <summary>The legacy column-mapping reader protocol version, intentionally unsupported.</summary>
    public const int ColumnMappingReaderVersion = 2;

    /// <summary>The "table features" reader protocol version (features enumerated in <c>readerFeatures</c>).</summary>
    public const int TableFeaturesReaderVersion = 3;

    /// <summary>The advanced reader features this build implements. <c>columnMapping</c> is served in
    /// <c>name</c> mode (STORY-05.4.3 / #191): the feature gate opens for a column-mapped table, then the
    /// <c>id</c> mode is rejected fail-closed downstream by <see cref="ColumnMapping.EnsureReadWriteSupported"/>
    /// (deferred to #523). A legacy reader-version-2 column-mapping table still fails closed (see
    /// <see cref="EnsureReadable"/>) — this build serves column mapping only through the table-features
    /// (reader v3) representation.</summary>
    public static readonly ImmutableHashSet<string> SupportedReaderFeatures =
        ImmutableHashSet.Create(StringComparer.Ordinal, ColumnMapping.Feature);

    /// <summary>The absolute basic writer protocol version (no writer features).</summary>
    public const int BasicWriterVersion = 1;

    /// <summary>The highest legacy (non-table-features) writer protocol version this build will write to.
    /// Writer version 2 gates <c>appendOnly</c> + column <c>invariants</c>; a commit writer that only ever
    /// <b>adds</b> files trivially satisfies <c>appendOnly</c>, and invariant/constraint enforcement is a
    /// later story (schema enforcement, #190) that raises this as it lands.</summary>
    public const int MaxBasicWriterVersion = 2;

    /// <summary>The "table features" writer protocol version (features enumerated in <c>writerFeatures</c>).</summary>
    public const int TableFeaturesWriterVersion = 7;

    /// <summary>The advanced writer features this build implements. <c>columnMapping</c> is written in
    /// <c>name</c> mode (STORY-05.4.3 / #191); an <c>id</c>-mode write is rejected fail-closed downstream
    /// (<see cref="ColumnMapping.EnsureReadWriteSupported"/>, deferred to #523).</summary>
    public static readonly ImmutableHashSet<string> SupportedWriterFeatures =
        ImmutableHashSet.Create(StringComparer.Ordinal, ColumnMapping.Feature);

    /// <summary>
    /// Verifies the table's <paramref name="protocol"/> is <b>writable</b> by this build before a commit is
    /// published, else throws a precise <see cref="DeltaProtocolException"/> (kind
    /// <see cref="DeltaProtocolErrorKind.UnsupportedProtocol"/>). This is the writer half of protocol
    /// negotiation (design §2.11 / §2.14 P3): a commit must never proceed against a
    /// <c>minWriterVersion</c> or named <c>writerFeatures</c> whose write-time semantics this build does not
    /// enforce (for example generated columns, constraints, change-data-feed, column mapping) — doing so
    /// risks silently corrupting the table (checklist <c>ACID</c> bullet 6; anti-pattern: never write past
    /// an unsupported writer feature).
    ///
    /// <para><b>v1 baseline.</b> Writer versions 1–2 (basic + <c>appendOnly</c>/invariants era) are writable,
    /// and writer version 7 ("table features") is writable only when it requires <b>no</b> writer feature;
    /// every other writer version and any named writer feature fails closed. This mirrors the reader
    /// baseline (<see cref="EnsureReadable"/>) and is deliberately conservative — being <i>stricter</i> than
    /// Delta on an unimplemented feature never yields a wrong write.</para>
    /// </summary>
    /// <exception cref="DeltaProtocolException">The writer version is unsupported, or a required writer
    /// feature is not implemented (fail closed).</exception>
    public static void EnsureWritable(ProtocolAction protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        int version = protocol.MinWriterVersion;
        if (version == TableFeaturesWriterVersion)
        {
            ImmutableArray<string> unsupported = protocol.WriterFeatures
                .Where(feature => !SupportedWriterFeatures.Contains(feature))
                .ToImmutableArray();
            if (unsupported.Length > 0)
            {
                throw DeltaProtocolException.UnsupportedFeatures("writer", unsupported);
            }

            return;
        }

        if (version is < BasicWriterVersion or > MaxBasicWriterVersion)
        {
            throw DeltaProtocolException.UnsupportedVersion("writer", version, MaxBasicWriterVersion);
        }
    }

    /// <summary>
    /// Verifies the table's <paramref name="protocol"/> is readable by this build, else throws a precise
    /// <see cref="DeltaProtocolException"/> (kind <see cref="DeltaProtocolErrorKind.UnsupportedProtocol"/>).
    /// </summary>
    /// <exception cref="DeltaProtocolException">The reader version is unsupported, or a required reader
    /// feature is not implemented (fail closed).</exception>
    public static void EnsureReadable(ProtocolAction protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        int version = protocol.MinReaderVersion;
        switch (version)
        {
            case BasicReaderVersion:
                return;

            case ColumnMappingReaderVersion:
                // Reader v2 unconditionally requires column mapping, which is not implemented.
                throw DeltaProtocolException.UnsupportedFeatures("reader", ["columnMapping (reader version 2)"]);

            case TableFeaturesReaderVersion:
                ImmutableArray<string> unsupported = protocol.ReaderFeatures
                    .Where(feature => !SupportedReaderFeatures.Contains(feature))
                    .ToImmutableArray();
                if (unsupported.Length > 0)
                {
                    throw DeltaProtocolException.UnsupportedFeatures("reader", unsupported);
                }

                return;

            default:
                throw DeltaProtocolException.UnsupportedVersion("reader", version, TableFeaturesReaderVersion);
        }
    }
}
