using System.Globalization;
using System.Text.Json;

namespace DeltaSharp.Storage.Delta.DeletionVectors;

/// <summary>
/// The <c>DeletionVectorDescriptor</c> that rides on an <c>add</c>/<c>remove</c> action (protocol
/// "Deletion Vector Descriptor Schema"): how to locate a DV (<see cref="StorageType"/>), where
/// (<see cref="PathOrInlineDv"/> + <see cref="Offset"/>), how big (<see cref="SizeInBytes"/>), and how many
/// rows it removes (<see cref="Cardinality"/>). A logical file is a data-file path plus (optionally) one of
/// these; the rows the DV names are physically present but logically deleted and MUST NOT be returned.
///
/// <para><b>Storage types.</b> <c>'u'</c> — relative path derived from a base85(Z85)-encoded UUID (with an
/// optional random directory prefix); <c>'i'</c> — inline in the log (Z85-encoded raw bytes, no
/// <see cref="Offset"/>); <c>'p'</c> — an absolute path. This build reads and writes <c>'u'</c> and reads
/// <c>'i'</c>; <c>'p'</c> is rejected fail-closed by the local backend (an absolute path may escape the
/// confined table root — failing the read is safe, silently ignoring the DV is not).</para>
///
/// <para><b>Derived identity.</b> <see cref="UniqueId"/> distinguishes the same data file carrying different
/// DVs across successive versions, so snapshot reconstruction and time travel replace a file's DV precisely
/// (a pre-DV version reads all rows; a post-DV version reads the survivors).</para>
/// </summary>
internal sealed record DeletionVectorDescriptor(
    string StorageType,
    string PathOrInlineDv,
    int? Offset,
    int SizeInBytes,
    long Cardinality)
{
    /// <summary>Relative-path-via-UUID storage (<c>'u'</c>).</summary>
    public const string StorageTypeUuidRelative = "u";

    /// <summary>Inline-in-log storage (<c>'i'</c>).</summary>
    public const string StorageTypeInline = "i";

    /// <summary>Absolute-path storage (<c>'p'</c>).</summary>
    public const string StorageTypeAbsolutePath = "p";

    private const string RelativeFileNamePrefix = "deletion_vector_";
    private const string BinExtension = ".bin";

    /// <summary>True when the DV bytes are stored inline in the log (Z85 in <see cref="PathOrInlineDv"/>).</summary>
    public bool IsInline => StorageType == StorageTypeInline;

    /// <summary>
    /// Uniquely identifies this DV for its data file (protocol "Derived Fields"): the concatenation of
    /// storage type and path/inline payload, plus <c>@offset</c> when an offset is present. Used as part of
    /// the active-file identity during snapshot replay so a file's DV can be replaced across versions.
    /// </summary>
    public string UniqueId => Offset is { } offset
        ? string.Create(CultureInfo.InvariantCulture, $"{StorageType}{PathOrInlineDv}@{offset}")
        : StorageType + PathOrInlineDv;

    /// <summary>
    /// The table-root-relative path of the on-disk <c>.bin</c> for a <c>'u'</c> DV (protocol "Derived
    /// Fields"): <c>&lt;random prefix&gt;/deletion_vector_&lt;uuid&gt;.bin</c>, or just
    /// <c>deletion_vector_&lt;uuid&gt;.bin</c> when there is no prefix. The last 20 characters of
    /// <see cref="PathOrInlineDv"/> are the Z85-encoded UUID; anything before them is the directory prefix.
    /// </summary>
    /// <exception cref="DeltaStorageException">This descriptor is not a relative-path DV, or its encoded
    /// UUID is malformed (fail closed).</exception>
    public string ResolveRelativePath()
    {
        if (StorageType != StorageTypeUuidRelative)
        {
            throw DeltaStorageException.CorruptData(
                "A relative on-disk path can only be derived for a 'u' (relative-path-via-UUID) deletion vector.");
        }

        if (PathOrInlineDv.Length < Z85.EncodedUuidLength)
        {
            throw DeltaStorageException.CorruptData(
                "A Delta deletion vector's relative path is shorter than the 20-character encoded UUID; the descriptor is corrupt.");
        }

        int prefixLength = PathOrInlineDv.Length - Z85.EncodedUuidLength;
        string prefix = PathOrInlineDv[..prefixLength];
        string encodedUuid = PathOrInlineDv[prefixLength..];
        Guid uuid = Z85.DecodeUuid(encodedUuid);
        string fileName = RelativeFileNamePrefix + uuid.ToString("D", CultureInfo.InvariantCulture) + BinExtension;
        return prefix.Length == 0 ? fileName : prefix + "/" + fileName;
    }

    /// <summary>The raw serialized DV bytes for an inline (<c>'i'</c>) DV — the Z85-decoded
    /// <see cref="PathOrInlineDv"/>, trimmed to <see cref="SizeInBytes"/>.</summary>
    /// <exception cref="DeltaStorageException">This descriptor is not inline, or its Z85 is malformed (fail closed).</exception>
    public byte[] DecodeInlineBytes()
    {
        if (!IsInline)
        {
            throw DeltaStorageException.CorruptData(
                "Inline bytes can only be decoded for an 'i' (inline) deletion vector.");
        }

        return Z85.DecodeBytes(PathOrInlineDv, SizeInBytes);
    }

    /// <summary>Builds a relative-path (<c>'u'</c>) descriptor for a freshly written on-disk DV.</summary>
    public static DeletionVectorDescriptor ForRelativePath(
        string pathOrInlineDv, int offset, int sizeInBytes, long cardinality) =>
        new(StorageTypeUuidRelative, pathOrInlineDv, offset, sizeInBytes, cardinality);

    /// <summary>Builds an inline (<c>'i'</c>) descriptor from raw serialized DV bytes (Z85-encoded).</summary>
    public static DeletionVectorDescriptor ForInline(ReadOnlySpan<byte> rawBytes, long cardinality) =>
        new(StorageTypeInline, Z85.Encode(rawBytes), Offset: null, SizeInBytes: rawBytes.Length, Cardinality: cardinality);

    /// <summary>The <c>&lt;random prefix&gt;&lt;20-char Z85 uuid&gt;</c> value for
    /// <see cref="PathOrInlineDv"/> of a <c>'u'</c> DV whose file name is derived from <paramref name="uuid"/>.</summary>
    public static string BuildRelativePathOrInlineDv(string randomPrefix, Guid uuid)
    {
        ArgumentNullException.ThrowIfNull(randomPrefix);
        return randomPrefix + Z85.EncodeUuid(uuid);
    }

    /// <summary>
    /// Parses a <c>deletionVector</c> JSON object into a validated descriptor (fail closed on any malformed
    /// field), or returns <see langword="null"/> when the property is absent/JSON-null — an <c>add</c>/
    /// <c>remove</c> without a DV.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The <c>deletionVector</c> object is malformed.</exception>
    public static DeletionVectorDescriptor? Parse(JsonElement parent, string action, long version, int line)
    {
        if (!parent.TryGetProperty("deletionVector", out JsonElement dv) || dv.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (dv.ValueKind != JsonValueKind.Object)
        {
            throw Malformed(action, version, line, "deletionVector must be a JSON object");
        }

        string storageType = RequireString(dv, "storageType", action, version, line);
        if (storageType is not (StorageTypeUuidRelative or StorageTypeInline or StorageTypeAbsolutePath))
        {
            throw Malformed(action, version, line, $"deletionVector.storageType '{storageType}' is not one of 'u','i','p'");
        }

        string pathOrInlineDv = RequireString(dv, "pathOrInlineDv", action, version, line);
        int sizeInBytes = RequireInt32(dv, "sizeInBytes", action, version, line);
        long cardinality = RequireInt64(dv, "cardinality", action, version, line);
        int? offset = OptionalInt32(dv, "offset", action, version, line);

        if (sizeInBytes < 0)
        {
            throw Malformed(action, version, line, "deletionVector.sizeInBytes is negative");
        }

        if (cardinality < 0)
        {
            throw Malformed(action, version, line, "deletionVector.cardinality is negative");
        }

        if (storageType == StorageTypeInline && offset is not null)
        {
            throw Malformed(action, version, line, "an inline ('i') deletionVector must not carry an offset");
        }

        if (offset is { } o && o < 0)
        {
            throw Malformed(action, version, line, "deletionVector.offset is negative");
        }

        return new DeletionVectorDescriptor(storageType, pathOrInlineDv, offset, sizeInBytes, cardinality);
    }

    /// <summary>Writes this descriptor as the <c>deletionVector</c> object on the currently-open action.</summary>
    public void Write(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject("deletionVector");
        writer.WriteString("storageType", StorageType);
        writer.WriteString("pathOrInlineDv", PathOrInlineDv);
        if (Offset is { } offset)
        {
            writer.WriteNumber("offset", offset);
        }

        writer.WriteNumber("sizeInBytes", SizeInBytes);
        writer.WriteNumber("cardinality", Cardinality);
        writer.WriteEndObject();
    }

    private static string RequireString(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw Malformed(action, version, line, $"deletionVector.{prop} is missing or not a string");
        }

        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw Malformed(action, version, line, $"deletionVector.{prop} is missing or not a 32-bit integer");
        }

        return result;
    }

    private static long RequireInt64(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
        {
            throw Malformed(action, version, line, $"deletionVector.{prop} is missing or not a 64-bit integer");
        }

        return result;
    }

    private static int? OptionalInt32(JsonElement obj, string prop, string action, long version, int line)
    {
        if (!obj.TryGetProperty(prop, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw Malformed(action, version, line, $"deletionVector.{prop} is not a 32-bit integer");
        }

        return result;
    }

    private static DeltaProtocolException Malformed(string action, long version, int line, string detail) =>
        DeltaProtocolException.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"Delta log version {version} line {line}: {action} {detail}."));
}
