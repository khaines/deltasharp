using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The M1 in-memory <see cref="ICatalog"/>: a local registry that maps a table identifier to its
/// ADR-0008 <see cref="StructType"/> schema. It holds schemas only — no data, files, or readers —
/// so registration and lookup do no I/O and preserve the lazy/eager invariant.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers are joined into a single dotted key and matched <b>case-insensitively</b>
/// (<see cref="StringComparer.OrdinalIgnoreCase"/>), following Spark's default
/// <c>spark.sql.caseSensitive=false</c> for table names. Case-sensitive resolution and multi-level
/// namespaces are metastore concerns deferred behind the <see cref="ICatalog"/> seam.
/// </para>
/// <para>
/// The registry is not thread-safe; registration is expected to complete before analysis begins.
/// </para>
/// </remarks>
internal sealed class LocalCatalog : ICatalog
{
    private readonly Dictionary<string, StructType> _relations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) the single-part named source <paramref name="name"/> with
    /// <paramref name="schema"/>.</summary>
    /// <param name="name">The table name (non-empty).</param>
    /// <param name="schema">The source's ADR-0008 schema.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    public void Register(string name, StructType schema)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(schema);
        _relations[name] = schema;
    }

    /// <summary>Registers (or replaces) the multipart source <paramref name="identifier"/> with
    /// <paramref name="schema"/>.</summary>
    /// <param name="identifier">The multipart table identifier (for example <c>["db", "t"]</c>).</param>
    /// <param name="schema">The source's ADR-0008 schema.</param>
    /// <exception cref="ArgumentException"><paramref name="identifier"/> is empty or has an
    /// empty part.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Register(IReadOnlyList<string> identifier, StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _relations[KeyOf(identifier)] = schema;
    }

    /// <inheritdoc/>
    public bool TryGetRelation(
        IReadOnlyList<string> identifier, [NotNullWhen(true)] out StructType? schema) =>
        _relations.TryGetValue(KeyOf(identifier), out schema);

    private static string KeyOf(IReadOnlyList<string> identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (identifier.Count == 0)
        {
            throw new ArgumentException(
                "Identifier must have at least one part.", nameof(identifier));
        }

        return string.Join('.', identifier);
    }
}
