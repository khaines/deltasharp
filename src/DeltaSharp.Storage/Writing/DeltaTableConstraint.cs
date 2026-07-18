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
/// fields' <c>delta.invariants</c> metadata. Collection fails <b>closed</b> on any constraint this build
/// cannot fully honor — an empty CHECK predicate, a malformed invariant value, or a <b>nested-field</b>
/// invariant (per-row enforcement not wired yet, #595) — so a constraint is never silently dropped.
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
    /// <exception cref="DeltaProtocolException">A constraint is malformed, empty, or a nested-field invariant.</exception>
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

            RejectNestedInvariant(field.Name, field.DataType);
        }
    }

    // Fail closed if an invariant is attached anywhere in a nested type (struct field / array element / map
    // key or value) — enforcing it per row needs a nested field-path evaluator not yet wired (#595). Spark
    // attaches an invariant to the field it guards, including nested fields, so a silent skip is a fail-open.
    private static void RejectNestedInvariant(string path, DataType type)
    {
        switch (type)
        {
            case StructType nested:
                foreach (StructField field in nested)
                {
                    string childPath = path + "." + field.Name;
                    if (field.Metadata.TryGetString(InvariantKey, out _))
                    {
                        throw DeltaProtocolException.Unsupported(
                            $"Column '{childPath}' declares a nested 'delta.invariants' column invariant; "
                            + "per-row enforcement of a nested-field invariant is not supported yet (#595), so "
                            + "the write is refused fail-closed rather than silently skip it.");
                    }

                    RejectNestedInvariant(childPath, field.DataType);
                }

                break;
            case ArrayType array:
                RejectNestedInvariant(path + ".element", array.ElementType);
                break;
            case MapType map:
                RejectNestedInvariant(path + ".key", map.KeyType);
                RejectNestedInvariant(path + ".value", map.ValueType);
                break;
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
