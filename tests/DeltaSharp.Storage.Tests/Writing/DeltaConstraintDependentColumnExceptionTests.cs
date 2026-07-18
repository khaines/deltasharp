using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Storage;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// Unit tests for <see cref="DeltaConstraintDependentColumnException"/> (#598): the Delta-parity
/// <c>DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE</c> error raised when an <c>overwriteSchema</c> drops/renames a
/// column one or more surviving CHECK constraints still reference. Asserts the factory names the column and
/// AGGREGATES every dependent CHECK (Delta parity), echoes each predicate + the error class, parameterizes the
/// operation phrase, and guards its arguments fail-loud. The end-to-end throw path (including the CHECK-only
/// scoping and multi-dependent ordering) is covered by the executor's <c>DeltaConstraintEnforcementTests</c>.
/// </summary>
public sealed class DeltaConstraintDependentColumnExceptionTests
{
    private static DeltaTableConstraint Check(string name, string expression) =>
        new(DeltaConstraintKind.Check, name, expression);

    [Fact]
    public void ForColumnChange_SingleDependent_NamesColumnPredicateAndErrorClass()
    {
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange("id", new[] { Check("positive_id", "id > 0") });

        Assert.Equal("id", ex.ColumnName);
        Assert.Equal("positive_id", Assert.Single(ex.Constraints).Name);
        Assert.Contains("positive_id -> id > 0", ex.Message); // predicate echoed for actionability
        Assert.Contains("this surviving CHECK constraint still depends", ex.Message); // singular wording
        Assert.Contains("DROP CONSTRAINT", ex.Message); // the remedy
        Assert.Contains("overwriteSchema replacement", ex.Message); // default operation
        Assert.Contains(DeltaConstraintDependentColumnException.ErrorClass, ex.Message);
        Assert.Equal("DELTA_CONSTRAINT_DEPENDENT_COLUMN_CHANGE", DeltaConstraintDependentColumnException.ErrorClass);
    }

    [Fact]
    public void ForColumnChange_MultipleDependents_ListsAllInGivenOrderWithPluralWording()
    {
        var deps = new[] { Check("amount_cap", "amount < 100"), Check("amount_positive", "amount > 0") };

        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange("amount", deps);

        Assert.Equal(new[] { "amount_cap", "amount_positive" }, ex.Constraints.Select(c => c.Name).ToArray());
        Assert.Contains("amount_cap -> amount < 100", ex.Message);
        Assert.Contains("amount_positive -> amount > 0", ex.Message);
        Assert.Contains("these surviving CHECK constraints still depend", ex.Message); // plural wording
    }

    [Fact]
    public void ForColumnChange_CustomOperation_NamesTheOperation()
    {
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange(
                "id", new[] { Check("c", "id > 0") }, operation: "ALTER TABLE DROP COLUMN");

        Assert.Contains("The ALTER TABLE DROP COLUMN drops or renames 'id'", ex.Message);
    }

    [Fact]
    public void ForColumnChange_KeepsDefensiveCopy_ImmuneToCallerMutation()
    {
        var deps = new List<DeltaTableConstraint> { Check("c", "id > 0") };
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange("id", deps);

        deps.Add(Check("d", "id < 9")); // mutate the caller's list after construction
        Assert.Single(ex.Constraints); // the exception kept its own copy
    }

    [Fact]
    public void ForColumnChange_NullConstraints_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => DeltaConstraintDependentColumnException.ForColumnChange("id", null!));

    [Fact]
    public void ForColumnChange_EmptyConstraints_Throws() =>
        Assert.Throws<ArgumentException>(
            () => DeltaConstraintDependentColumnException.ForColumnChange("id", Array.Empty<DeltaTableConstraint>()));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ForColumnChange_MissingColumnName_Throws(string? columnName) =>
        // ThrowIfNullOrEmpty throws ArgumentNullException (a subclass of ArgumentException) for null and
        // ArgumentException for empty, so accept either.
        Assert.ThrowsAny<ArgumentException>(
            () => DeltaConstraintDependentColumnException.ForColumnChange(columnName!, new[] { Check("c", "id > 0") }));
}
