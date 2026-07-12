using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Types;

/// <summary>
/// Immutable, order-independent metadata attached to a <see cref="StructField"/> (for example a
/// column comment, a column-mapping id, or identity-column configuration). Values are typed
/// (<see cref="MetadataValue"/>) so Delta-log schema interop is lossless: numeric ids, booleans,
/// nested objects, and arrays round-trip with their JSON shape preserved (issue #330). The common
/// string-comment case is served by <see cref="FromEntries"/> and <see cref="TryGetString"/>.
/// </summary>
/// <remarks>
/// Entries are held in a key-sorted map so equality is order-independent and serialization is
/// deterministic (STORY-02.5.1 AC1/AC3).
/// </remarks>
public sealed class FieldMetadata : IReadOnlyDictionary<string, MetadataValue>, IEquatable<FieldMetadata>
{
    private readonly SortedDictionary<string, MetadataValue> _entries;

    private FieldMetadata(SortedDictionary<string, MetadataValue> entries) => _entries = entries;

    /// <summary>The shared empty metadata instance.</summary>
    public static FieldMetadata Empty { get; } =
        new(new SortedDictionary<string, MetadataValue>(StringComparer.Ordinal));

    /// <summary>
    /// Builds metadata from typed <paramref name="entries"/>. Keys are compared ordinally; on a
    /// duplicate key the last value wins. Returns <see cref="Empty"/> for an empty input. For the
    /// common all-string case, prefer <see cref="FromEntries"/>, which wraps raw strings for you.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/>, or a key or value in it,
    /// is null.</exception>
    public static FieldMetadata FromValues(IEnumerable<KeyValuePair<string, MetadataValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var map = new SortedDictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, MetadataValue> entry in entries)
        {
            if (entry.Key is null)
            {
                throw new ArgumentNullException(nameof(entries), "Metadata key cannot be null.");
            }

            if (entry.Value is null)
            {
                throw new ArgumentNullException(
                    nameof(entries), $"Metadata value for key '{entry.Key}' cannot be null.");
            }

            map[entry.Key] = entry.Value;
        }

        return map.Count == 0 ? Empty : new FieldMetadata(map);
    }

    /// <summary>
    /// Builds metadata from string <paramref name="entries"/>, the common column-comment case.
    /// Each value is wrapped as a <see cref="MetadataValue.String(string)"/>. Keys are compared
    /// ordinally; on a duplicate key the last value wins. Returns <see cref="Empty"/> for an empty
    /// input. For typed values (numeric ids, identity booleans, nested objects, arrays), use
    /// <see cref="FromValues"/> instead.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/>, or a key or value in it,
    /// is null.</exception>
    public static FieldMetadata FromEntries(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var map = new SortedDictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in entries)
        {
            if (entry.Key is null)
            {
                throw new ArgumentNullException(nameof(entries), "Metadata key cannot be null.");
            }

            if (entry.Value is null)
            {
                throw new ArgumentNullException(
                    nameof(entries), $"Metadata value for key '{entry.Key}' cannot be null.");
            }

            map[entry.Key] = MetadataValue.String(entry.Value);
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
    public IEnumerable<MetadataValue> Values => _entries.Values;

    /// <inheritdoc/>
    public MetadataValue this[string key] => _entries[key];

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out MetadataValue value) =>
        _entries.TryGetValue(key, out value);

    /// <summary>
    /// Gets the value for <paramref name="key"/> when it is present <b>and</b> a string value —
    /// the ergonomic path for column comments and other string-valued metadata.
    /// </summary>
    public bool TryGetString(string key, [MaybeNullWhen(false)] out string value)
    {
        if (_entries.TryGetValue(key, out MetadataValue? entry) && entry.TryGetString(out string? stringValue))
        {
            value = stringValue;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Gets the value for <paramref name="key"/> when it is present <b>and</b> an integral value —
    /// the ergonomic one-call path for numeric metadata such as <c>delta.columnMapping.id</c> or
    /// <c>delta.identity.start</c>/<c>.step</c> (mirrors Spark's <c>Metadata.getLong</c>).
    /// </summary>
    public bool TryGetLong(string key, out long value)
    {
        if (_entries.TryGetValue(key, out MetadataValue? entry) && entry.TryGetLong(out long longValue))
        {
            value = longValue;
            return true;
        }

        value = 0L;
        return false;
    }

    /// <summary>
    /// Gets the value for <paramref name="key"/> when it is present <b>and</b> a floating-point
    /// value (mirrors Spark's <c>Metadata.getDouble</c>).
    /// </summary>
    public bool TryGetDouble(string key, out double value)
    {
        if (_entries.TryGetValue(key, out MetadataValue? entry) && entry.TryGetDouble(out double doubleValue))
        {
            value = doubleValue;
            return true;
        }

        value = 0d;
        return false;
    }

    /// <summary>
    /// Gets the value for <paramref name="key"/> when it is present <b>and</b> a boolean value —
    /// the ergonomic one-call path for flags such as <c>delta.identity.allowExplicitInsert</c>
    /// (mirrors Spark's <c>Metadata.getBoolean</c>).
    /// </summary>
    public bool TryGetBoolean(string key, out bool value)
    {
        if (_entries.TryGetValue(key, out MetadataValue? entry) && entry.TryGetBoolean(out bool booleanValue))
        {
            value = booleanValue;
            return true;
        }

        value = false;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, MetadataValue>> GetEnumerator() => _entries.GetEnumerator();

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

        foreach (KeyValuePair<string, MetadataValue> entry in _entries)
        {
            if (!other._entries.TryGetValue(entry.Key, out MetadataValue? otherValue)
                || !entry.Value.Equals(otherValue))
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
        foreach (KeyValuePair<string, MetadataValue> entry in _entries)
        {
            // SortedDictionary enumerates in key order, so the hash is order-independent.
            hash = StableHash.Combine(hash, StableHash.OfString(entry.Key));
            hash = StableHash.Combine(hash, entry.Value.GetHashCode());
        }

        return hash;
    }
}
