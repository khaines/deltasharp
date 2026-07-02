using System.Diagnostics.CodeAnalysis;

namespace DeltaSharp.Analysis;

/// <summary>
/// The catalog seam the analyzer's <c>ResolveRelations</c> rule binds a by-name table reference
/// through: given a multipart identifier it returns the resolved <see cref="CatalogTable"/> table
/// descriptor (identifier + ADR-0008 schema, with room to grow), or reports the source as unknown.
/// </summary>
/// <remarks>
/// <para>
/// This is the single, narrow abstraction that decouples name-to-table resolution from
/// <i>where</i> the metadata lives. M1 ships only the in-memory <see cref="LocalCatalog"/>, but a
/// metastore-backed catalog (Hive/Delta/native) can replace it later without touching the analyzer,
/// which depends on this interface alone. The seam yields a <see cref="CatalogTable"/> — a table
/// descriptor — rather than a bare schema, so a real catalog can carry table type/provider metadata
/// without reshaping the contract.
/// </para>
/// <para>
/// Lookups are total and side-effect-free: an implementation returns <see langword="false"/> for an
/// unknown source rather than throwing, letting the analyzer raise a single Spark-compatible
/// <see cref="AnalysisException"/> at the point of use (AC3, AC4).
/// </para>
/// </remarks>
internal interface ICatalog
{
    /// <summary>
    /// Attempts to resolve <paramref name="identifier"/> to its table descriptor.
    /// </summary>
    /// <param name="identifier">The multipart table identifier (for example <c>["db", "t"]</c>).</param>
    /// <param name="table">The resolved <see cref="CatalogTable"/> when the source is known;
    /// otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the source is registered; otherwise
    /// <see langword="false"/>.</returns>
    bool TryGetRelation(IReadOnlyList<string> identifier, [NotNullWhen(true)] out CatalogTable? table);
}
