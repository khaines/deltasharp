using System;
using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// #687 round-2 guard, physical half. <c>LogicalOutput</c> re-derives output attributes for the physical
/// planner and independently auto-names a bare <c>ResolvedFunction</c> with
/// <c>CoercionHelpers.PrettyReference</c> — the RAW renderer, exactly as <c>Analyzer.SparkAutoName</c> does.
/// <para>
/// The two are in <b>different assemblies</b>, so routing only one of them through the diagnostic-bounded
/// <c>DiagnosticReference</c> compiles cleanly and leaves the whole suite green while the analyzed and
/// physical schemas silently disagree about a column's name. That asymmetric edit is the plausible one: the
/// analyzer's auto-name site sits four lines below six diagnostic sites that legitimately were converted.
/// </para>
/// <para>
/// So this asserts the invariant that actually matters — the physical name EQUALS the analyzed name for the
/// same plan — rather than each half in isolation. Its Core-side twin is
/// <c>DeltaSharp.Core.Tests.Analysis.AnalyzerAutoNameHygieneTests</c>; together they fail on either direction
/// of the collapse.
/// </para>
/// </summary>
public sealed class LogicalOutputAutoNameHygieneTests
{
    /// <summary>Long enough that <c>sum(&lt;name&gt;)</c> exceeds the 256-char diagnostic reference cap.</summary>
    private const int LongColumnNameLength = 300;

    /// <summary><c>sum(</c> + 300 + <c>)</c>, pinned as a literal so it cannot move with the mutation it
    /// guards.</summary>
    private const int ExpectedAutoNameLength = 305;

    private static readonly string LongColumnName = new('n', LongColumnNameLength);

    [Fact]
    public void PhysicalOutput_AutoNameOverTheDiagnosticCap_SurvivesWhole()
    {
        LogicalPlan analyzed = AnalyzeSumOverLongColumn(out _);

        AttributeReference physical = Assert.Single(LogicalOutput.Derive(analyzed).OutputOf(analyzed));

        Assert.Equal(ExpectedAutoNameLength, physical.Name.Length);
        Assert.False(physical.Name.EndsWith('…'), "the physical auto-named output column was elided");
        Assert.Equal("sum(" + LongColumnName + ")", physical.Name);
    }

    [Fact]
    public void PhysicalOutput_AutoName_AgreesWithTheAnalyzedOutput()
    {
        // The cross-assembly invariant. Converting EITHER auto-name site alone breaks this equality, which is
        // precisely the failure a per-assembly guard cannot see.
        LogicalPlan analyzed = AnalyzeSumOverLongColumn(out string analyzedName);

        AttributeReference physical = Assert.Single(LogicalOutput.Derive(analyzed).OutputOf(analyzed));

        Assert.Equal(analyzedName, physical.Name);
    }

    private static LogicalPlan AnalyzeSumOverLongColumn(out string analyzedName)
    {
        var schema = new StructType(new[]
        {
            new StructField(LongColumnName, LongType.Instance, nullable: true),
        });
        var catalog = new LocalCatalog();
        catalog.Register("t", schema);

        var plan = new Aggregate(
            Array.Empty<Expression>(),
            new Expression[]
            {
                new UnresolvedFunction("sum", new[] { new UnresolvedAttribute(new[] { LongColumnName }) }),
            },
            new UnresolvedRelation(new[] { "t" }));

        LogicalPlan analyzed = new Analyzer(catalog).Resolve(
            plan, out IReadOnlyList<(string Name, DataType Type, bool Nullable)> output);
        analyzedName = Assert.Single(output).Name;
        return analyzed;
    }
}
