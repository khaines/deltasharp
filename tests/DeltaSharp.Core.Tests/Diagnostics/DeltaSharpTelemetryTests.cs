using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeltaSharp.Diagnostics;
using Xunit;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// Locks in the canonical telemetry names from FEAT-00.4 (STORY-00.4.1 #110 / STORY-00.4.2 #111): the
/// shared root prefix and the low-cardinality attribute keys shared across logs, metric tags, and span
/// attributes. These strings are a documented convention
/// (<c>docs/engineering/design/observability-conventions.md</c>), so the exact values are pinned here and
/// the structural invariants (dotted, lowercase, <c>deltasharp.</c>-prefixed, unique) are enforced so a
/// future edit cannot silently diverge from the doc.
/// </summary>
public class DeltaSharpTelemetryTests
{
    // The complete expected vocabulary. Pinning exact values (not just "contains") means renaming any
    // constant's VALUE fails the test; asserting the whole set means adding or dropping a key fails too.
    private static readonly IReadOnlyDictionary<string, string> ExpectedAttributeKeys = new Dictionary<string, string>
    {
        ["ComponentKey"] = "deltasharp.component",
        ["OperationKey"] = "deltasharp.operation",
        ["OutcomeKey"] = "deltasharp.outcome",
        ["BackendKey"] = "deltasharp.backend",
        ["ConflictClassKey"] = "deltasharp.conflict.class",
        ["JobIdKey"] = "deltasharp.job.id",
        ["StageKey"] = "deltasharp.stage",
        ["TaskIdKey"] = "deltasharp.task.id",
        ["ExecutorIdKey"] = "deltasharp.executor.id",
        ["TableKey"] = "deltasharp.table",
        ["TableVersionKey"] = "deltasharp.table.version",
        ["CorrelationIdKey"] = "deltasharp.correlation.id",
        ["AttemptKey"] = "deltasharp.attempt",
        ["PartitionKey"] = "deltasharp.partition",
    };

    [Fact]
    public void RootName_IsTheSharedDeltaSharpPrefix()
    {
        // The root prefix for every Meter name, ActivitySource name, and ILogger category. A change here
        // would silently re-namespace all future telemetry, so pin the exact value.
        Assert.Equal("DeltaSharp", DeltaSharpTelemetry.RootName);
    }

    [Fact]
    public void AttributeKeys_HaveExactDocumentedValues()
    {
        Assert.Equal("deltasharp.component", DeltaSharpTelemetry.ComponentKey);
        Assert.Equal("deltasharp.operation", DeltaSharpTelemetry.OperationKey);
        Assert.Equal("deltasharp.outcome", DeltaSharpTelemetry.OutcomeKey);
        Assert.Equal("deltasharp.backend", DeltaSharpTelemetry.BackendKey);
        Assert.Equal("deltasharp.conflict.class", DeltaSharpTelemetry.ConflictClassKey);
        Assert.Equal("deltasharp.job.id", DeltaSharpTelemetry.JobIdKey);
        Assert.Equal("deltasharp.stage", DeltaSharpTelemetry.StageKey);
        Assert.Equal("deltasharp.task.id", DeltaSharpTelemetry.TaskIdKey);
        Assert.Equal("deltasharp.executor.id", DeltaSharpTelemetry.ExecutorIdKey);
        Assert.Equal("deltasharp.table", DeltaSharpTelemetry.TableKey);
        Assert.Equal("deltasharp.table.version", DeltaSharpTelemetry.TableVersionKey);
        Assert.Equal("deltasharp.correlation.id", DeltaSharpTelemetry.CorrelationIdKey);
        Assert.Equal("deltasharp.attempt", DeltaSharpTelemetry.AttemptKey);
        Assert.Equal("deltasharp.partition", DeltaSharpTelemetry.PartitionKey);
    }

    [Fact]
    public void AttributeKeys_AreExactlyTheDocumentedSet()
    {
        Dictionary<string, string> actual = AttributeKeyConstants()
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        // No extra keys, no missing keys, values match — so adding/removing/renaming a key fails.
        Assert.Equal(ExpectedAttributeKeys.OrderBy(kv => kv.Key), actual.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void AttributeKeys_AreLowCardinalityFriendly_DottedLowercasePrefixedAndUnique()
    {
        string[] values = AttributeKeyConstants()
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(values);

        foreach (string key in values)
        {
            Assert.StartsWith("deltasharp.", key, StringComparison.Ordinal);
            Assert.Equal(key.ToLowerInvariant(), key);
            Assert.DoesNotContain(key, char.IsWhiteSpace);
            // OpenTelemetry-style keys are dotted segments; no path/glob/query characters that would
            // signal an unbounded or credential-bearing value leaked into a key.
            Assert.DoesNotContain(key, c => c is '/' or '\\' or '?' or '&' or '=' or ':');
        }

        // Every key is distinct so two dimensions can never collapse into one time series.
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
    }

    // The internal const string attribute keys — every string constant on the type except the RootName
    // prefix (which is not an attribute key).
    private static IEnumerable<FieldInfo> AttributeKeyConstants() =>
        typeof(DeltaSharpTelemetry)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Where(f => f.Name != nameof(DeltaSharpTelemetry.RootName));
}
