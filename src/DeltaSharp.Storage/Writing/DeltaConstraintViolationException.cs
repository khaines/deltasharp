using DeltaSharp.Storage.Delta;

namespace DeltaSharp.Storage;

/// <summary>
/// Thrown by the Delta write seam (#581) when at least one written row violates an active per-row
/// constraint — a column <b>invariant</b> or a named <b>CHECK</b> constraint. Following Delta's
/// <c>CheckDeltaInvariant.assertRule</c>, a row violates when the constraint predicate evaluates to
/// anything other than <c>true</c> (i.e. <c>false</c> OR <c>null</c>). The write is rejected fail-closed
/// <b>before</b> any Parquet data file is staged or any log commit is attempted, so a violating write
/// never mutates the table.
/// </summary>
public sealed class DeltaConstraintViolationException : Exception
{
    /// <summary>Creates a violation for <paramref name="constraint"/> with an explicit <paramref name="message"/>.</summary>
    /// <param name="constraint">The constraint the write violated.</param>
    /// <param name="message">The human-readable violation message.</param>
    /// <exception cref="ArgumentNullException"><paramref name="constraint"/> is null.</exception>
    public DeltaConstraintViolationException(DeltaTableConstraint constraint, string message)
        : base(message)
    {
        Constraint = constraint ?? throw new ArgumentNullException(nameof(constraint));
    }

    /// <summary>The active constraint the write violated.</summary>
    public DeltaTableConstraint Constraint { get; }

    /// <summary>Builds a violation for <paramref name="constraint"/> naming the offending row.</summary>
    /// <param name="constraint">The violated constraint.</param>
    /// <param name="batchIndex">The zero-based index of the write batch containing the violating row.</param>
    /// <param name="rowIndex">The zero-based logical row index within that batch.</param>
    /// <returns>A populated <see cref="DeltaConstraintViolationException"/>.</returns>
    public static DeltaConstraintViolationException ForRow(
        DeltaTableConstraint constraint, int batchIndex, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        // constraint.Name (a delta.constraints.<name> CHECK key-suffix, or an invariant's column name) and
        // constraint.Expression (the predicate text) are attacker-authored on a foreign/hostile table —
        // sanitize both before echoing so a crafted name/predicate cannot inject control/line-break chars into
        // a structured-log sink or render unbounded (#666). The raw values remain on the typed Constraint
        // property for full programmatic inspection.
        string name = DiagnosticText.Sanitize(constraint.Name, DiagnosticText.ConfigTokenMaxLength);
        string subject = constraint.Kind == DeltaConstraintKind.Check
            ? $"CHECK constraint '{name}'"
            : $"column invariant on '{name}'";
        return new DeltaConstraintViolationException(
            constraint,
            $"The write violates the {subject}: the predicate "
            + $"'{DiagnosticText.Sanitize(constraint.Expression, DiagnosticText.DefaultMaxLength)}' did not "
            + $"evaluate to true for the row at batch {batchIndex}, position {rowIndex}. The write is rejected "
            + "fail-closed before any data is staged.");
    }
}
