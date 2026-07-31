using System;
using System.Collections.Generic;
using DeltaSharp.Analysis;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// #687 round-2 guard. <c>CoercionHelpers.PrettyReference</c> is dual-purpose: diagnostics bound and neutralize
/// its result via <c>DiagnosticReference</c>, while <b>auto-naming</b> uses it raw because the result becomes a
/// real output-schema column NAME. <see cref="AnalyzerDiagnosticHygieneTests"/> pins that split at the
/// <b>renderer</b> — but a renderer-level guard only catches the SYMMETRIC collapse (hygiene pushed into the
/// shared renderer). It does not catch converting <b>one</b> auto-name call site and leaving the other:
/// <c>Analyzer.SparkAutoName</c> and <c>LogicalOutput</c>'s twin live in <b>different assemblies</b>, and the
/// analyzer's sits four lines below six diagnostic sites that legitimately were converted — so routing just
/// that one through <c>DiagnosticReference</c> is a one-line, entirely plausible edit that compiles, keeps the
/// whole suite green, and silently renames an output column.
/// <para>These are therefore <b>call-site</b> guards: they assert the property that actually matters — an
/// over-cap auto-name survives whole through the real analyzer entry point — rather than re-testing the
/// renderer in isolation. The physical-planner half lives in
/// <c>DeltaSharp.Executor.Tests.LogicalOutputAutoNameHygieneTests</c>, which must be in that assembly to reach
/// <c>LogicalOutput</c>.</para>
/// </summary>
public sealed class AnalyzerAutoNameHygieneTests
{
    /// <summary>A column name long enough that <c>sum(&lt;name&gt;)</c> exceeds
    /// <c>CoercionHelpers.DiagnosticReferenceMaxLength</c> (256) — so a diagnostic-style bound applied here
    /// would visibly elide it.</summary>
    private const int LongColumnNameLength = 300;

    /// <summary><c>sum(</c> + 300 + <c>)</c>. Pinned as a literal: asserting against a recomputed expression
    /// would move with the mutation it is meant to catch.</summary>
    private const int ExpectedAutoNameLength = 305;

    private static readonly string LongColumnName = new('n', LongColumnNameLength);

    [Fact]
    public void AnalyzedOutput_AutoNameOverTheDiagnosticCap_SurvivesWhole()
    {
        // The property: an auto-named output column is NOT subject to the diagnostic bound. If
        // Analyzer.SparkAutoName is routed through DiagnosticReference, this name comes back elided at
        // 257 chars ending in '…' — a silently renamed column in the analyzed schema.
        AnalyzeSumOverLongColumn(out string autoName);

        Assert.Equal(ExpectedAutoNameLength, autoName.Length);
        Assert.False(autoName.EndsWith('…'), "the auto-named output column was elided");
        Assert.Equal("sum(" + LongColumnName + ")", autoName);
    }

    [Fact]
    public void AnalyzedOutput_AutoNameWithInjectionUnsafeChars_IsNotNeutralized()
    {
        // The same separation on the CONTENT axis. A column name is data, not prose: whatever the schema
        // declares must round-trip into the output schema verbatim, U+FFFD-free — a caller selecting the
        // resulting column by name would otherwise stop finding it.
        const string oddName = "od\u200Dd";
        var schema = new StructType(new[] { new StructField(oddName, LongType.Instance, nullable: true) });
        var catalog = new LocalCatalog();
        catalog.Register("t", schema);

        var plan = new Aggregate(
            Array.Empty<Expression>(),
            new Expression[] { new UnresolvedFunction("sum", new[] { new UnresolvedAttribute(new[] { oddName }) }) },
            new UnresolvedRelation(new[] { "t" }));

        new Analyzer(catalog).Resolve(plan, out IReadOnlyList<(string Name, DataType Type, bool Nullable)> output);

        Assert.Equal("sum(" + oddName + ")", Assert.Single(output).Name);
        Assert.DoesNotContain('\uFFFD', output[0].Name);
    }

    /// <summary>Analyzes <c>SELECT sum(&lt;300-char column&gt;) FROM t</c>, returning the analyzed plan and the
    /// auto-name the analyzer derived for its single output column.</summary>
    internal static LogicalPlan AnalyzeSumOverLongColumn(out string autoName)
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
        autoName = Assert.Single(output).Name;
        return analyzed;
    }
}
