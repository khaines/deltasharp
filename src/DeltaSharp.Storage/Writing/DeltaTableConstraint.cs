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
/// Collects the <see cref="DeltaTableConstraint"/>s a write must enforce (#581): named CHECK constraints
/// from <c>metaData.configuration</c> (<c>delta.constraints.*</c>) and column invariants from the schema
/// fields' <c>delta.invariants</c> metadata — including a <b>nested struct-field</b> invariant on an
/// all-struct path (<c>s.f</c>, <c>s.a.b</c>), collected with its fully-qualified path and enforced via
/// <c>GetStructField</c> (#595). An invariant reached <b>through an array or map</b> is <b>ignored</b>,
/// matching Delta's <c>Invariants.getFromSchema</c> (<c>filterRecursively(checkComplexTypes = false)</c>
/// recurses structs only, never a collection's elements/entries — #606). Collection still fails
/// <b>closed</b> on a constraint this build cannot honor — an empty CHECK predicate or a malformed
/// invariant value — so such a constraint is never silently dropped.
/// </summary>
internal static class DeltaTableConstraints
{
    private const string CheckConstraintKeyPrefix = "delta.constraints.";
    private const string InvariantKey = "delta.invariants";

    /// <summary>
    /// Collects the constraints a write to <paramref name="snapshot"/> with declared write schema
    /// <paramref name="writeSchema"/> must enforce: the snapshot's CHECK constraints (always) + the snapshot's
    /// column invariants (when <paramref name="includeSnapshotInvariants"/>), unioned (deduplicated) with any
    /// invariant declared on the incoming <paramref name="writeSchema"/> (so a fresh create, or a
    /// <c>mergeSchema</c> append that adds a constrained column, validates its own rows). Deterministically
    /// ordered so the first reported violation is stable.
    /// </summary>
    /// <param name="snapshot">The table snapshot whose active constraints apply, or <see langword="null"/> for
    /// a fresh create.</param>
    /// <param name="writeSchema">The declared write schema; its fields' own <c>delta.invariants</c> are always
    /// collected.</param>
    /// <param name="includeSnapshotInvariants">Whether the snapshot's OWN field <c>delta.invariants</c> apply.
    /// <see langword="true"/> for an append / same-schema write. <see langword="false"/> for an
    /// <c>overwriteSchema</c> replacement: the snapshot's named CHECK constraints (<c>delta.constraints.*</c>
    /// table config) survive the replacement and are still collected, but the OLD schema's field invariants
    /// are replaced wholesale by <paramref name="writeSchema"/>'s and so must NOT be collected from the
    /// snapshot.</param>
    /// <exception cref="DeltaProtocolException">A CHECK constraint or invariant predicate is malformed or
    /// empty.</exception>
    public static IReadOnlyList<DeltaTableConstraint> CollectForWrite(
        Snapshot? snapshot, StructType writeSchema, bool includeSnapshotInvariants = true)
    {
        ArgumentNullException.ThrowIfNull(writeSchema);

        var seen = new HashSet<DeltaTableConstraint>();
        var constraints = new List<DeltaTableConstraint>();

        void Add(DeltaTableConstraint constraint)
        {
            if (seen.Add(constraint))
            {
                constraints.Add(constraint);
            }
        }

        if (snapshot is not null)
        {
            CollectChecks(snapshot.Metadata.Configuration, Add);
            if (includeSnapshotInvariants)
            {
                CollectInvariants(snapshot.Schema, Add);
            }
        }

        CollectInvariants(writeSchema, Add);

        constraints.Sort(static (a, b) =>
        {
            int byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Name, b.Name);
        });
        return constraints;
    }

    /// <summary>Collects the CHECK constraints + column invariants active on <paramref name="snapshot"/>.</summary>
    public static IReadOnlyList<DeltaTableConstraint> Collect(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return CollectForWrite(snapshot, snapshot.Schema);
    }

    private static void CollectChecks(
        IReadOnlyDictionary<string, string> configuration, Action<DeltaTableConstraint> add)
    {
        foreach (KeyValuePair<string, string> entry in configuration)
        {
            if (!entry.Key.StartsWith(CheckConstraintKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = entry.Key[CheckConstraintKeyPrefix.Length..];
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                throw DeltaProtocolException.Unsupported(
                    $"CHECK constraint '{name}' declares an empty predicate; the write is refused fail-closed "
                    + "rather than silently skip a declared constraint.");
            }

            add(new DeltaTableConstraint(DeltaConstraintKind.Check, name, entry.Value));
        }
    }

    private static void CollectInvariants(StructType schema, Action<DeltaTableConstraint> add)
    {
        foreach (StructField field in schema)
        {
            if (field.Metadata.TryGetString(InvariantKey, out string? persisted))
            {
                add(new DeltaTableConstraint(
                    DeltaConstraintKind.Invariant, field.Name, ParseInvariantExpression(field.Name, persisted)));
            }

            CollectNestedInvariants(field.Name, field.DataType, add);
        }
    }

    // Collects nested-field column invariants with DELTA'S traversal semantics (#606, correcting #595's earlier
    // fail-closed reject): Delta's Invariants.getFromSchema walks the schema via
    // SchemaUtils.filterRecursively(checkComplexTypes = false) — it recurses into STRUCTs only and does NOT
    // descend into an array element or a map key/value. So an invariant on a struct field reached by an
    // ALL-STRUCT path (`s.f`, `s.a.b`) is collected and enforced (its predicate resolves via GetStructField
    // (#580) and evaluates through the nested StructFieldEvaluator (#589)); an invariant reached THROUGH an
    // array or map is IGNORED — matching Delta, which likewise never enforces it (delta-io/delta
    // constraints/Invariants.getFromSchema). An invariant declared directly on a top-level array/map FIELD is
    // still collected by CollectInvariants (the field itself is on an all-struct path from the root); this only
    // governs descent INTO a collection's elements/entries.
    private static void CollectNestedInvariants(string path, DataType type, Action<DeltaTableConstraint> add)
    {
        // checkComplexTypes = false: recurse into structs only; do not descend into ArrayType/MapType (or any
        // other non-struct type). An invariant under a collection is therefore silently not collected — the
        // exact Delta behavior.
        if (type is not StructType nested)
        {
            return;
        }

        foreach (StructField field in nested)
        {
            string childPath = path + "." + field.Name;
            if (field.Metadata.TryGetString(InvariantKey, out string? persisted))
            {
                add(new DeltaTableConstraint(
                    DeltaConstraintKind.Invariant, childPath, ParseInvariantExpression(childPath, persisted)));
            }

            CollectNestedInvariants(childPath, field.DataType, add);
        }
    }

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
            $"Column '{column}' declares a 'delta.invariants' value this build cannot parse as a persisted "
            + "expression of the form '{{\"expression\":{{\"expression\":\"<predicate>\"}}}}'; the write is "
            + "refused fail-closed rather than silently skip invariant enforcement.");
    }
}
