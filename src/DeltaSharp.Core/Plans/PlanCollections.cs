using System.Collections.ObjectModel;

namespace DeltaSharp.Plans;

/// <summary>
/// Small immutable-collection and structural-comparison helpers shared by the logical-plan IR.
/// Centralizes the defensive-copy and structural-equality discipline every node relies on so
/// the nodes stay terse and consistent.
/// </summary>
internal static class PlanCollections
{
    /// <summary>
    /// Defensively copies <paramref name="items"/> into a read-only list, rejecting null
    /// elements. The returned view cannot be cast back to a mutable array, preserving node
    /// immutability.
    /// </summary>
    public static IReadOnlyList<T> ToImmutable<T>(IEnumerable<T> items, string paramName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items, paramName);
        T[] array = items.ToArray();
        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] is null)
            {
                throw new ArgumentException("Collection elements cannot be null.", paramName);
            }
        }

        return new ReadOnlyCollection<T>(array);
    }

    /// <summary>
    /// Defensively copies <paramref name="parts"/> into a non-empty read-only list of non-empty
    /// strings (used for multipart identifiers).
    /// </summary>
    public static IReadOnlyList<string> ToIdentifier(IEnumerable<string> parts, string paramName)
    {
        ArgumentNullException.ThrowIfNull(parts, paramName);
        string[] array = parts.ToArray();
        if (array.Length == 0)
        {
            throw new ArgumentException("Identifier must have at least one part.", paramName);
        }

        for (int i = 0; i < array.Length; i++)
        {
            if (string.IsNullOrEmpty(array[i]))
            {
                throw new ArgumentException("Identifier parts cannot be null or empty.", paramName);
            }
        }

        return new ReadOnlyCollection<string>(array);
    }

    /// <summary>
    /// Defensively copies <paramref name="options"/> into a <b>case-insensitive</b>
    /// (<see cref="StringComparer.OrdinalIgnoreCase"/>) read-only map, matching Spark's
    /// case-insensitive data-source option contract (the <see cref="DataFrameWriter"/>/reader collect
    /// options case-insensitively, so <c>Option("HEADER", …)</c> must be retrievable as
    /// <c>Options["header"]</c>). The copy uses the indexer (last write wins) so a source map whose keys
    /// collide only by case cannot throw here.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToOptions(
        IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return EmptyOptions;
        }

        var copy = new Dictionary<string, string>(options.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in options)
        {
            copy[entry.Key] = entry.Value;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>
    /// Defensively copies <paramref name="items"/> into a read-only view that cannot be cast back
    /// to a mutable array, preserving node immutability. The copy means the caller may safely pass
    /// a shared or later-mutated array — the returned view never aliases the input.
    /// </summary>
    public static IReadOnlyList<T> AsReadOnly<T>(params T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length == 0)
        {
            return Empty<T>();
        }

        var copy = new T[items.Length];
        Array.Copy(items, copy, items.Length);
        return new ReadOnlyCollection<T>(copy);
    }

    /// <summary>The shared, immutable, non-castable empty read-only list for <typeparamref name="T"/>.</summary>
    public static IReadOnlyList<T> Empty<T>() => EmptyHolder<T>.Value;

    private static class EmptyHolder<T>
    {
        public static readonly IReadOnlyList<T> Value =
            new ReadOnlyCollection<T>(Array.Empty<T>());
    }

    /// <summary>The shared empty options map (case-insensitive, matching <see cref="ToOptions"/>).</summary>
    public static IReadOnlyDictionary<string, string> EmptyOptions { get; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Ordinal element-wise equality of two string sequences.</summary>
    public static bool StringSequenceEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Order-independent equality of two string-keyed string maps. Keys are compared
    /// <b>case-insensitively</b> (the maps are built with <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// by <see cref="ToOptions"/>, so the lookup honors that comparer, matching Spark's case-insensitive
    /// option keys); values are compared <b>ordinally</b> (option values are case-sensitive).
    /// </summary>
    public static bool OptionsEqual(
        IReadOnlyDictionary<string, string> a,
        IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> entry in a)
        {
            if (!b.TryGetValue(entry.Key, out string? value)
                || !string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
