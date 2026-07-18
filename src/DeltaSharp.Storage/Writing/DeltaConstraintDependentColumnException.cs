namespace DeltaSharp.Storage;

/// <summary>
/// Thrown by the Delta write seam (#598) when an <c>overwriteSchema</c> replacement (or a future
/// <c>ALTER</c>) DROPS or RENAMES a column that a SURVIVING <c>delta.constraints.*</c> CHECK constraint
/// (or column invariant) still references. The surviving predicate can no longer resolve against the
/// replacement schema, so — rather than let the write fail with a raw "cannot resolve column" resolution
/// error, or (worse) commit a table that still declares a CHECK over a column that no longer exists (a
/// dangling-CHECK brick that would reject every future write) — the write is refused fail-closed with this
/// Delta-parity error naming the offending column and the dependent constraint. The remedy is to DROP the
/// dependent constraint first, then change the column.
/// </summary>
/// <remarks>
/// Mirrors Delta's <c>DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE</c> (SQLSTATE <c>42K09</c>,
/// <c>DeltaErrors.foundViolatingConstraintsForColumnChange</c>). It is an <b>error-quality / Spark-parity</b>
/// surface only: the underlying write was already refused fail-closed before this change (the constraint
/// simply could not resolve against the new schema) — this reclassifies that raw resolution failure into a
/// clear, actionable diagnostic. Distinct from <see cref="DeltaConstraintViolationException"/> (a
/// <i>row</i> that violates a resolvable predicate).
/// </remarks>
public sealed class DeltaConstraintDependentColumnException : Exception
{
    /// <summary>Delta's error class for this failure (kept for parity / telemetry branching).</summary>
    public const string ErrorClass = "DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE";

    private DeltaConstraintDependentColumnException(
        string message, string columnName, DeltaTableConstraint constraint)
        : base(message)
    {
        ColumnName = columnName;
        Constraint = constraint;
    }

    /// <summary>The column the schema change drops/renames that a surviving constraint still references.</summary>
    public string ColumnName { get; }

    /// <summary>The surviving constraint that depends on <see cref="ColumnName"/>.</summary>
    public DeltaTableConstraint Constraint { get; }

    /// <summary>
    /// Builds the failure for a schema change that drops/renames <paramref name="columnName"/> while the
    /// surviving <paramref name="constraint"/> still references it.
    /// </summary>
    /// <param name="constraint">The surviving constraint that depends on the changed column.</param>
    /// <param name="columnName">The dropped/renamed column reference the constraint could not resolve.</param>
    /// <returns>A populated <see cref="DeltaConstraintDependentColumnException"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="constraint"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is null or empty.</exception>
    public static DeltaConstraintDependentColumnException ForColumnChange(
        DeltaTableConstraint constraint, string columnName)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentException.ThrowIfNullOrEmpty(columnName);

        string subject = constraint.Kind == DeltaConstraintKind.Check
            ? $"CHECK constraint '{constraint.Name}'"
            : $"column invariant on '{constraint.Name}'";
        string message =
            $"Cannot alter column '{columnName}' because this column is referenced by the following check "
            + $"constraint(s):\n  {constraint.Name} -> {constraint.Expression}\n"
            + $"The overwriteSchema replacement drops or renames '{columnName}', but the surviving {subject} "
            + "still depends on it; committing would leave a dangling constraint that rejects every future "
            + $"write. Drop the dependent constraint first (e.g. ALTER TABLE ... DROP CONSTRAINT "
            + $"'{constraint.Name}'), then change the column. [{ErrorClass}]";
        return new DeltaConstraintDependentColumnException(message, columnName, constraint);
    }
}
