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
    /// <param name="batches">The write batches whose rows are validated, in write order. <b>May be empty</b>: a
    /// resolve-only caller (e.g. the ALTER DROP/RENAME dependent-CHECK guard, #616) passes zero batches — the
    /// implementation MUST still run the constraint <b>resolution</b> pass (and raise
    /// <see cref="DeltaConstraintDependentColumnException"/> for a dangling CHECK) even when there are no rows to
    /// evaluate; it must not early-return on empty input.</param>
    /// <param name="priorSchema">The table's PRIOR logical schema when this write REPLACES the schema (an
    /// <c>overwriteSchema</c> replacement), else <see langword="null"/>. When supplied, the enforcer additionally
    /// runs a <b>reference-based</b> dependent-column check (#619): a surviving CHECK that references a top-level
    /// column whose type CHANGED between <paramref name="priorSchema"/> and <paramref name="schema"/> (a retype
    /// that still type-resolves, which resolution-failure detection alone misses) is refused with
    /// <see cref="DeltaConstraintDependentColumnException"/> — matching Delta, which blocks an
    /// <c>ALTER CHANGE COLUMN</c> that a constraint depends on regardless of type-compatibility.</param>
    /// <exception cref="DeltaConstraintViolationException">A row does not satisfy a constraint.</exception>
    /// <exception cref="DeltaConstraintDependentColumnException">A surviving CHECK references a column the
    /// schema change drops, renames, or (when <paramref name="priorSchema"/> is supplied) retypes.</exception>
    void Enforce(
        StructType schema,
        IReadOnlyList<DeltaTableConstraint> constraints,
        IReadOnlyList<ColumnBatch> batches,
        StructType? priorSchema = null);
}
