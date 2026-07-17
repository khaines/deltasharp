using System.Text.Json;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;

namespace DeltaSharp.Storage;

/// <summary>
/// The two kinds of per-row write constraint Delta enforces (#581): a legacy column <b>invariant</b>
/// (stored in a <see cref="StructField"/>'s <c>delta.invariants</c> metadata) and a named <b>CHECK</b>
/// constraint (a <c>delta.constraints.&lt;name&gt;</c> table property). Both carry a boolean predicate that
/// must hold for every written row; Delta's enforcement rule is identical for both — a row is rejected
/// when the predicate evaluates to <b>anything other than <c>true</c></b> (i.e. <c>false</c> OR
/// <c>null</c>), per <c>CheckDeltaInvariant.assertRule</c> in delta-io/delta.
/// </summary>
public enum DeltaConstraintKind
{
    /// <summary>A legacy column invariant (<c>delta.invariants</c> field metadata).</summary>
    Invariant,

    /// <summary>A named CHECK constraint (<c>delta.constraints.&lt;name&gt;</c> table property).</summary>
    Check,
}

/// <summary>
/// An active per-row write constraint resolved from a table snapshot (#581): its <see cref="Kind"/>,
/// its <see cref="Name"/> (the invariant's column name or the CHECK constraint's name), and the boolean
/// <see cref="Expression"/> text that must hold for every written row.
/// </summary>
/// <param name="Kind">Whether this is a column invariant or a named CHECK constraint.</param>
/// <param name="Name">The invariant's owning column name, or the CHECK constraint's name.</param>
/// <param name="Expression">The boolean predicate text (Delta SQL) the write path must enforce per row.</param>
public sealed record DeltaTableConstraint(DeltaConstraintKind Kind, string Name, string Expression);

/// <summary>
/// Collects the <see cref="DeltaTableConstraint"/>s active on a <see cref="Snapshot"/> (#581): the named
/// CHECK constraints from <c>metaData.configuration</c> (<c>delta.constraints.*</c>) and the column
/// invariants from the schema fields' <c>delta.invariants</c> metadata. The write seam resolves and
/// evaluates these over each write batch before staging.
/// </summary>
internal static class DeltaTableConstraints
{
    private const string CheckConstraintKeyPrefix = "delta.constraints.";
    private const string InvariantKey = "delta.invariants";

    /// <summary>Collects the CHECK constraints and column invariants active on <paramref name="snapshot"/>.</summary>
    /// <param name="snapshot">The target table snapshot the write lands against.</param>
    /// <returns>The active constraints, or an empty list when the table declares none.</returns>
    /// <exception cref="DeltaProtocolException">A <c>delta.invariants</c> metadata value is malformed.</exception>
    public static IReadOnlyList<DeltaTableConstraint> Collect(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<DeltaTableConstraint>? constraints = null;

        // Named CHECK constraints: delta.constraints.<name> = <boolean predicate>.
        foreach (KeyValuePair<string, string> entry in snapshot.Metadata.Configuration)
        {
            if (entry.Key.StartsWith(CheckConstraintKeyPrefix, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                (constraints ??= new List<DeltaTableConstraint>()).Add(new DeltaTableConstraint(
                    DeltaConstraintKind.Check,
                    entry.Key[CheckConstraintKeyPrefix.Length..],
                    entry.Value));
            }
        }

        // Column invariants: a field's delta.invariants metadata carries a persisted boolean predicate.
        foreach (StructField field in snapshot.Schema)
        {
            if (field.Metadata.TryGetString(InvariantKey, out string? persisted))
            {
                (constraints ??= new List<DeltaTableConstraint>()).Add(new DeltaTableConstraint(
                    DeltaConstraintKind.Invariant, field.Name, ParseInvariantExpression(field.Name, persisted)));
            }
        }

        return constraints ?? (IReadOnlyList<DeltaTableConstraint>)Array.Empty<DeltaTableConstraint>();
    }

    // A column invariant persists as a JSON PersistedExpression: {"expression":{"expression":"<sql>"}}
    // (delta-io/delta Invariants.PersistedExpression). Extract the inner boolean predicate text, failing
    // closed on a malformed value rather than silently dropping enforcement.
    private static string ParseInvariantExpression(string column, string persisted)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(persisted);
            if (document.RootElement.TryGetProperty("expression", out JsonElement outer)
                && outer.ValueKind == JsonValueKind.Object
                && outer.TryGetProperty("expression", out JsonElement inner)
                && inner.ValueKind == JsonValueKind.String
                && inner.GetString() is { Length: > 0 } sql)
            {
                return sql;
            }
        }
        catch (JsonException)
        {
            // fall through to the fail-closed throw below
        }

        throw DeltaProtocolException.Unsupported(
            $"Column '{column}' declares a 'delta.invariants' value this build cannot parse as a "
            + "'{{\"expression\":{{\"expression\":\"<predicate>\"}}}}' persisted expression; the write is "
            + "refused fail-closed rather than silently skip invariant enforcement.");
    }
}
