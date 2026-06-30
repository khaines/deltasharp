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

    /// <summary>Defensively copies <paramref name="options"/> into an ordinal read-only map.</summary>
    public static IReadOnlyDictionary<string, string> ToOptions(
        IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return EmptyOptions;
        }

        var copy = new Dictionary<string, string>(options.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in options)
        {
            copy[entry.Key] = entry.Value;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>Wraps <paramref name="items"/> in a read-only view that cannot be cast back to a
    /// mutable array, preserving node immutability.</summary>
    public static IReadOnlyList<T> AsReadOnly<T>(params T[] items) =>
        new ReadOnlyCollection<T>(items);

    /// <summary>The shared empty options map.</summary>
    public static IReadOnlyDictionary<string, string> EmptyOptions { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

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

    /// <summary>Order-independent equality of two string-keyed string maps (ordinal).</summary>
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
