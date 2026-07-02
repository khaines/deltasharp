using System.Diagnostics.CodeAnalysis;
using System.Text;
using DeltaSharp.Plans;
using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The M1 in-memory <see cref="ICatalog"/>: a local registry that maps a table identifier to its
/// resolved <see cref="CatalogTable"/> descriptor (identifier + ADR-0008 <see cref="StructType"/>
/// schema). It holds schemas only — no data, files, or readers — so registration and lookup do no
/// I/O and preserve the lazy/eager invariant.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers are keyed by a <b>collision-free, part-aware</b> composite key (each part is
/// length-prefixed) and matched <b>case-insensitively</b>
/// (<see cref="StringComparer.OrdinalIgnoreCase"/>), following Spark's default
/// <c>spark.sql.caseSensitive=false</c> for table names. A part-aware key is required so distinct
/// identifiers that share a naive dotted rendering — for example <c>["a.b", "c"]</c> and
/// <c>["a", "b.c"]</c> — never collide. Case-sensitive resolution and multi-level namespaces are
/// metastore concerns deferred behind the <see cref="ICatalog"/> seam.
/// </para>
/// <para>
/// The registry is not thread-safe; registration is expected to complete before analysis begins.
/// </para>
/// </remarks>
internal sealed class LocalCatalog : ICatalog
{
    private readonly Dictionary<string, CatalogTable> _relations =
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
        Register(new[] { name }, schema);
    }

    /// <summary>Registers (or replaces) the multipart source <paramref name="identifier"/> with
    /// <paramref name="schema"/>.</summary>
    /// <param name="identifier">The multipart table identifier (for example <c>["db", "t"]</c>).</param>
    /// <param name="schema">The source's ADR-0008 schema.</param>
    /// <exception cref="ArgumentException"><paramref name="identifier"/> is empty or has a null or
    /// empty part.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Register(IReadOnlyList<string> identifier, StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        IReadOnlyList<string> parts = PlanCollections.ToIdentifier(identifier, nameof(identifier));
        _relations[KeyOf(parts)] = new CatalogTable(parts, schema);
    }

    /// <inheritdoc/>
    public bool TryGetRelation(
        IReadOnlyList<string> identifier, [NotNullWhen(true)] out CatalogTable? table) =>
        _relations.TryGetValue(KeyOf(identifier), out table);

    /// <summary>
    /// Builds a collision-free composite key from <paramref name="identifier"/> by length-prefixing
    /// each part (<c>len:part</c>), so no two distinct part sequences ever map to the same string (a
    /// naive <c>string.Join('.')</c> collides <c>["a.b","c"]</c> with <c>["a","b.c"]</c>). The key
    /// is matched case-insensitively via the dictionary's
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    private static string KeyOf(IReadOnlyList<string> identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (identifier.Count == 0)
        {
            throw new ArgumentException(
                "Identifier must have at least one part.", nameof(identifier));
        }

        var key = new StringBuilder();
        foreach (string part in identifier)
        {
            key.Append(part.Length).Append(':').Append(part);
        }

        return key.ToString();
    }
}
