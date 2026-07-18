using System;
using DeltaSharp.Storage;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// Unit tests for <see cref="DeltaConstraintDependentColumnException"/> (#598): the Delta-parity
/// <c>DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE</c> error raised when an <c>overwriteSchema</c> drops/renames a
/// column a surviving CHECK constraint (or column invariant) still references. Asserts the factory names the
/// column and the dependent constraint, echoes the predicate and error class in the message, and guards its
/// arguments fail-loud. The end-to-end throw path is covered by the executor's
/// <c>DeltaConstraintEnforcementTests</c>.
/// </summary>
public sealed class DeltaConstraintDependentColumnExceptionTests
{
    [Fact]
    public void ForColumnChange_Check_NamesColumnConstraintPredicateAndErrorClass()
    {
        var constraint = new DeltaTableConstraint(DeltaConstraintKind.Check, "positive_id", "id > 0");

        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange(constraint, "id");

        Assert.Equal("id", ex.ColumnName);
        Assert.Same(constraint, ex.Constraint);
        Assert.Contains("positive_id", ex.Message);
        Assert.Contains("id > 0", ex.Message); // predicate echoed for actionability
        Assert.Contains("CHECK constraint 'positive_id'", ex.Message);
        Assert.Contains("DROP CONSTRAINT 'positive_id'", ex.Message); // the remedy
        Assert.Contains(DeltaConstraintDependentColumnException.ErrorClass, ex.Message);
        Assert.Equal("DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE", DeltaConstraintDependentColumnException.ErrorClass);
    }

    [Fact]
    public void ForColumnChange_Invariant_DescribesTheColumnInvariantSubject()
    {
        var constraint = new DeltaTableConstraint(DeltaConstraintKind.Invariant, "amount", "amount < 100");

        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange(constraint, "amount");

        Assert.Equal("amount", ex.ColumnName);
        Assert.Equal(DeltaConstraintKind.Invariant, ex.Constraint.Kind);
        Assert.Contains("column invariant on 'amount'", ex.Message);
        Assert.Contains("amount < 100", ex.Message);
    }

    [Fact]
    public void ForColumnChange_NullConstraint_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => DeltaConstraintDependentColumnException.ForColumnChange(null!, "id"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ForColumnChange_MissingColumnName_Throws(string? columnName)
    {
        var constraint = new DeltaTableConstraint(DeltaConstraintKind.Check, "c", "id > 0");
        // ThrowIfNullOrEmpty throws ArgumentNullException (a subclass of ArgumentException) for null and
        // ArgumentException for empty, so accept either.
        Assert.ThrowsAny<ArgumentException>(
            () => DeltaConstraintDependentColumnException.ForColumnChange(constraint, columnName!));
    }
}
