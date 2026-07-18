using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Storage;

/// <summary>
/// The per-row constraint-enforcement collaborator the Delta write primitive
/// (<see cref="DeltaWriteTarget.AppendAsync"/> / <see cref="DeltaWriteTarget.OverwriteAsync"/>) calls to
/// validate a write's rows against the table's active constraints (column <b>invariants</b> + named
/// <b>CHECK</b> constraints, #581) <b>before</b> any Parquet data file is staged or log commit is attempted.
///
/// <para>Enforcement lives <b>inside</b> the write primitive — collected from the SAME snapshot the commit
/// bases on and run after the physical write shape is resolved — so it shares one snapshot with the commit
/// (closing the read-constraints-vs-commit TOCTOU), cannot be bypassed by a non-sink caller of the public
/// write door, and validates the shape that is actually committed (#596). The primitive supplies the
/// already-collected <see cref="DeltaTableConstraint"/>s; the enforcer only <i>evaluates</i> each predicate
/// over the write batches and throws <see cref="DeltaConstraintViolationException"/> on the first violating
/// row.</para>
///
/// <para><b>Layering.</b> Evaluating a constraint predicate needs the query engine's parse/resolve/translate
/// and vectorized expression evaluation, which the storage layer cannot reach without a dependency cycle.
/// The interface is therefore declared here (storage) and implemented in the executor (the Delta write sink),
/// which owns the resolve→translate→evaluate pipeline. A write to a constrained table with no enforcer wired
/// is refused fail-closed rather than committed unvalidated.</para>
/// </summary>
public interface IWriteConstraintEnforcer
{
    /// <summary>
    /// Validates every row of <paramref name="batches"/> against each constraint in
    /// <paramref name="constraints"/>, resolving each predicate against <paramref name="schema"/> (the write's
    /// logical schema, which the batches conform to positionally). A row violates — and the write must be
    /// rejected fail-closed — when a constraint predicate evaluates to anything other than <c>true</c> (i.e.
    /// <c>false</c> OR <c>null</c>), matching Delta's <c>CheckDeltaInvariant.assertRule</c>.
    /// </summary>
    /// <param name="schema">The write's logical schema; the constraint predicates resolve against it and the
    /// batches conform to it (same column order).</param>
    /// <param name="constraints">The active constraints the write must satisfy (already collected from the
    /// commit's base snapshot + the write schema's own invariants). Never empty when this is called.</param>
    /// <param name="batches">The write batches whose rows are validated, in write order.</param>
    /// <exception cref="DeltaConstraintViolationException">A row does not satisfy a constraint.</exception>
    void Enforce(
        StructType schema,
        IReadOnlyList<DeltaTableConstraint> constraints,
        IReadOnlyList<ColumnBatch> batches);
}
