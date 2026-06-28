using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Engine.Types;

/// <summary>
/// Immutable, order-independent metadata attached to a <see cref="StructField"/> (for example
/// a column comment). v1 supports string-valued entries — Spark's most common field metadata
/// and what Delta stores for column comments; richer typed metadata is a future extension.
/// </summary>
/// <remarks>
/// Entries are held in a key-sorted map so equality is order-independent and serialization is
/// deterministic (STORY-02.5.1 AC1/AC3).
/// </remarks>
public sealed class FieldMetadata : IReadOnlyDictionary<string, string>, IEquatable<FieldMetadata>
{
    private readonly SortedDictionary<string, string> _entries;

    private FieldMetadata(SortedDictionary<string, string> entries) => _entries = entries;

    /// <summary>The shared empty metadata instance.</summary>
    public static FieldMetadata Empty { get; } =
        new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Builds metadata from <paramref name="entries"/>. Keys are compared ordinally; on a
    /// duplicate key the last value wins. Returns <see cref="Empty"/> for an empty input.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    /// <exception cref="ArgumentException">A key or value is null.</exception>
    public static FieldMetadata FromEntries(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in entries)
        {
            if (entry.Key is null)
            {
                throw new ArgumentException("Metadata key cannot be null.", nameof(entries));
            }

            if (entry.Value is null)
            {
                throw new ArgumentException(
                    $"Metadata value for key '{entry.Key}' cannot be null.", nameof(entries));
            }

            map[entry.Key] = entry.Value;
        }

        return map.Count == 0 ? Empty : new FieldMetadata(map);
    }

    /// <summary>Whether there are no entries.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _entries.Keys;

    /// <inheritdoc/>
    public IEnumerable<string> Values => _entries.Values;

    /// <inheritdoc/>
    public string this[string key] => _entries[key];

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
        _entries.TryGetValue(key, out value);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(FieldMetadata? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_entries.Count != other._entries.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> entry in _entries)
        {
            if (!other._entries.TryGetValue(entry.Key, out string? otherValue)
                || !string.Equals(entry.Value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as FieldMetadata);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int hash = StableHash.OfString("metadata");
        foreach (KeyValuePair<string, string> entry in _entries)
        {
            // SortedDictionary enumerates in key order, so the hash is order-independent.
            hash = StableHash.Combine(hash, StableHash.OfString(entry.Key));
            hash = StableHash.Combine(hash, StableHash.OfString(entry.Value));
        }

        return hash;
    }
}
