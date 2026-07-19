using System.Linq;

namespace DeltaSharp.Storage;

/// <summary>
/// Thrown by the Delta write seam (#598) when an <c>overwriteSchema</c> replacement (or a future
/// <c>ALTER</c>) DROPS, RENAMES, or RETYPES a column (including retyping a struct column so a surviving CHECK
/// can no longer read one of its nested fields, #600) that one or more SURVIVING <c>delta.constraints.*</c>
/// CHECK constraints still reference. The surviving predicate(s) can no longer resolve against the replacement
/// schema, so — rather than let the write fail with a raw "cannot resolve column" resolution error, or
/// (worse) commit a table that still declares a CHECK over a column that no longer exists (a dangling-CHECK
/// brick that would reject every future write) — the write is refused fail-closed with this Delta-parity
/// error naming the offending column and every dependent CHECK constraint. The remedy is to DROP the
/// dependent constraint(s) first, then change the column.
/// </summary>
/// <remarks>
/// Mirrors Delta's <c>DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE</c> (SQLSTATE <c>42K09</c>,
/// <c>DeltaErrors.foundViolatingConstraintsForColumnChange</c>), which aggregates ALL constraints dependent
/// on the changed column. It is an <b>error-quality / Spark-parity</b> surface only: the underlying write was
/// already refused fail-closed before this change (the constraint simply could not resolve against the new
/// schema) — this reclassifies that raw resolution failure into a clear, actionable diagnostic. Only named
/// CHECK constraints (which a user can <c>ALTER TABLE ... DROP CONSTRAINT</c>) are reported; a column
/// invariant is attached to its own field and cannot be dropped independently, so it never reaches here.
/// Distinct from <see cref="DeltaConstraintViolationException"/> (a <i>row</i> that violates a resolvable
/// predicate).
/// </remarks>
public sealed class DeltaConstraintDependentColumnException : Exception
{
    /// <summary>Delta's error class for this failure (kept for parity / telemetry branching).</summary>
    public const string ErrorClass = "DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE";

    private DeltaConstraintDependentColumnException(
        string message, string columnName, IReadOnlyList<DeltaTableConstraint> constraints)
        : base(message)
    {
        ColumnName = columnName;
        Constraints = constraints;
    }

    /// <summary>The column the schema change drops, renames, or retypes that the surviving CHECK constraint(s)
    /// reference.</summary>
    public string ColumnName { get; }

    /// <summary>Every surviving CHECK constraint that depends on <see cref="ColumnName"/>, in the deterministic
    /// order they were supplied (upstream, <c>CollectForWrite</c> emits them in Kind-then-name order).</summary>
    public IReadOnlyList<DeltaTableConstraint> Constraints { get; }

    /// <summary>
    /// Builds the failure for a schema change (<paramref name="operation"/>) that drops/renames
    /// <paramref name="columnName"/> while the surviving CHECK <paramref name="constraints"/> still reference
    /// it. All dependent constraints are listed together, mirroring Delta's aggregate error.
    /// </summary>
    /// <param name="columnName">The dropped/renamed column the constraints could not resolve.</param>
    /// <param name="constraints">The surviving CHECK constraints that depend on the changed column (at least one).</param>
    /// <param name="operation">The schema-change operation for the message (default: overwriteSchema replacement),
    /// so a future <c>ALTER</c> can name itself instead of hardcoding "overwriteSchema".</param>
    /// <returns>A populated <see cref="DeltaConstraintDependentColumnException"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="columnName"/>/<paramref name="operation"/> is null or
    /// empty, or <paramref name="constraints"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="constraints"/> is null.</exception>
    public static DeltaConstraintDependentColumnException ForColumnChange(
        string columnName,
        IReadOnlyList<DeltaTableConstraint> constraints,
        string operation = "overwriteSchema replacement")
    {
        ArgumentException.ThrowIfNullOrEmpty(columnName);
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentException.ThrowIfNullOrEmpty(operation);
        if (constraints.Count == 0)
        {
            throw new ArgumentException(
                "At least one dependent CHECK constraint is required.", nameof(constraints));
        }

        // Defensive copy so the public property is immutable regardless of the caller's list lifetime.
        DeltaTableConstraint[] dependents = constraints.ToArray();
        string listing = string.Join("", dependents.Select(c => $"\n  {c.Name} -> {c.Expression}"));
        string depends = dependents.Length == 1
            ? "this surviving CHECK constraint still depends"
            : "these surviving CHECK constraints still depend";
        string message =
            $"Cannot alter column '{columnName}' because this column is referenced by the following check "
            + $"constraint(s):{listing}\nThe {operation} changes '{columnName}' (drops, renames, or retypes it), "
            + $"but {depends} on it; committing would leave a dangling constraint that rejects every future "
            + "write. Drop the dependent constraint(s) first (e.g. ALTER TABLE ... DROP CONSTRAINT), then change "
            + $"the column. [{ErrorClass}]";
        return new DeltaConstraintDependentColumnException(message, columnName, dependents);
    }
}
