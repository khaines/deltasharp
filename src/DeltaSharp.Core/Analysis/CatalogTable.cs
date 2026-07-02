using DeltaSharp.Types;

namespace DeltaSharp.Analysis;

/// <summary>
/// The kind of catalog object an identifier resolves to. M1 resolves tables only; the enum is a
/// forward-compat stub so a metastore-backed catalog can distinguish a table from a view (and later
/// from a temporary view/function) without reshaping the <see cref="ICatalog"/> seam. See follow-up
/// issue #392 (catalog table-type / listing surface).
/// </summary>
internal enum CatalogTableType
{
    /// <summary>A managed or external table (the only kind M1 registers).</summary>
    Table,

    /// <summary>A view. Reserved for a later metastore-backed catalog.</summary>
    View,
}

/// <summary>
/// The analyzer-facing descriptor a catalog lookup yields for a resolved by-name reference. It is a
/// <b>table descriptor</b>, not a bare schema: the seam resolves a <i>table</i> (the miss diagnostic
/// is literally <see cref="AnalysisErrorKind.TableOrViewNotFound"/>), so the contract carries the
/// resolved <see cref="Identifier"/> and its ADR-0008 <see cref="Schema"/> together, with room to
/// grow (<see cref="TableType"/>; a provider/table-properties slot can be added later without
/// touching the analyzer).
/// </summary>
/// <param name="Identifier">The multipart identifier the catalog resolved (for example
/// <c>["db", "t"]</c>).</param>
/// <param name="Schema">The resolved ADR-0008 <see cref="StructType"/> schema.</param>
/// <param name="TableType">The catalog object kind; defaults to <see cref="CatalogTableType.Table"/>
/// for the M1 in-memory catalog.</param>
internal sealed record CatalogTable(
    IReadOnlyList<string> Identifier,
    StructType Schema,
    CatalogTableType TableType = CatalogTableType.Table);
