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

    /// <summary>The advanced reader features this build implements — none, in the v1 baseline.</summary>
    public static readonly ImmutableHashSet<string> SupportedReaderFeatures = ImmutableHashSet<string>.Empty;

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
