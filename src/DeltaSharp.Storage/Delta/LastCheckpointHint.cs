using System.Text.Json;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The parsed <c>_delta_log/_last_checkpoint</c> hint (design §2.10.3): a small JSON object
/// <c>{version, size?, parts?, sizeInBytes?, ...}</c> that points at the most recent checkpoint. It is a
/// <b>hint, not truth</b>: <see cref="TryParse"/> returns <see langword="null"/> for anything malformed,
/// and the caller still validates the referenced checkpoint files actually exist and are ≤ the target
/// version before trusting it — a missing/stale/corrupt hint simply degrades to listing the log for the
/// newest usable checkpoint (checklist anti-pattern: never silently read a corrupt checkpoint).
/// </summary>
internal readonly record struct LastCheckpointHint(long Version, int? Parts)
{
    /// <summary>The <c>_last_checkpoint</c> object key path, relative to the table root.</summary>
    public const string Path = "_delta_log/_last_checkpoint";

    /// <summary>Parses the hint, or returns <see langword="null"/> if it is not a JSON object with a
    /// non-negative integer <c>version</c> (fail soft — the hint is advisory).</summary>
    public static LastCheckpointHint? TryParse(ReadOnlySpan<byte> content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt64(out long version)
                || version < 0)
            {
                return null;
            }

            int? parts = null;
            if (root.TryGetProperty("parts", out JsonElement partsElement)
                && partsElement.ValueKind == JsonValueKind.Number
                && partsElement.TryGetInt32(out int partsValue)
                && partsValue >= 1)
            {
                parts = partsValue;
            }

            return new LastCheckpointHint(version, parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
