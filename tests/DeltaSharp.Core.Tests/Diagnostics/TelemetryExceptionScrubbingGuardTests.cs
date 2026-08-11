using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// Regression guard for the #455 audit. DeltaSharp diagnostics deliberately name the resource they concern —
/// <c>AnalysisException.TableOrViewNotFound</c> embeds the table identifier in its message; EXPLAIN /
/// <c>SinkDescriptor.SimpleString</c> render <c>table=&lt;identifier&gt;</c>. Correct for a user-facing
/// diagnostic, but it must NOT cross into telemetry unscrubbed: recording such an exception onto a span, or
/// flowing its <c>.Message</c>/<c>.SimpleString</c>/typed identifier property onto a span tag/status/event/name,
/// captures the tenant-bearing identifier as a span attribute, which observability-conventions.md requires to be
/// scrubbed/omitted at that boundary (checklists 09a/14).
/// <para>The audit found <b>zero</b> such sites today (telemetry export is host-gated, #458). These tests keep
/// the conclusion from silently regressing when telemetry lands:</para>
/// <list type="bullet">
/// <item>the <c>BannedSymbols.txt</c> RS0030 bans must stay present, exact, and (for the built-in sink)
///   signature-matched — asserted on the analyzer's own parse (uncommented lines) plus reflection, so a
///   commented-out entry or a BCL signature drift turns red instead of silently disabling the ban;</item>
/// <item>no production source may record an exception onto a span (<c>AddException</c>/<c>RecordException</c>,
///   including a <c>using static</c> bare call) NOR flow a diagnostic value onto a span via a span-attribution
///   sink (<c>SetTag</c>/<c>AddTag</c>/<c>SetStatus</c>/<c>AddEvent</c>/<c>ActivityEvent</c>/<c>SetBaggage</c>/
///   <c>AddBaggage</c>/<c>SetCustomProperty</c>/<c>StartActivity</c>/<c>DisplayName</c>) using a diagnostic
///   member (<c>.Message</c>, <c>.SimpleString</c>, <c>.ToString()</c> on an exception, <c>.FilePath</c>/
///   <c>.ColumnName</c>/<c>.Constraint</c>/<c>.Reference</c>/<c>.TableIdentifier</c>), an
///   <c>exception.message</c>/<c>exception.stacktrace</c>/<c>deltasharp.table</c> literal key, or the
///   <c>TableKey</c> constant — the value-side vectors a symbol ban cannot reach (the last is the interim pin
///   for #790, to be removed when that seam lands);</item>
/// <item>RS0030 must not be disabled repo-wide (an <c>.editorconfig</c>/<c>.globalconfig</c> severity override,
///   a <c>&lt;NoWarn&gt;</c>/<c>&lt;WarningsNotAsErrors&gt;</c>, or <c>&lt;TreatWarningsAsErrors&gt;false</c>),
///   and the analyzer wiring (<c>BannedSymbols.txt</c> as an <c>AdditionalFiles</c> and
///   <c>TreatWarningsAsErrors=true</c>) must stay intact — otherwise every entry is silently unbanned.</item>
/// </list>
/// The scan parses each file with the real C# parser (<see cref="CSharpSyntaxTree"/>) rather than a hand-rolled
/// masker, so string interpolation holes (<c>$"{ex.Message}"</c>), collection initializers, multi-line calls,
/// and comments/string literals are handled correctly by construction, and carries a positive-control battery +
/// a file-count floor so a scope regression cannot pass vacuously.
/// <para><b>Out of scope (review-enforced residuals, documented in observability-conventions.md):</b> a value
/// laundered through an intermediate local/field/helper (<c>var m = ex.Message; SetTag(k, m);</c>) is
/// intra-procedural dataflow beyond a syntactic scan — the authoritative dataflow-proof control there is the
/// RS0030 ban on the recording APIs plus review; an arbitrary human-chosen free-text EXPLAIN/plan string; and
/// the <c>deltasharp.table</c> producer scrubbing seam (#790, gated on #458).</para>
/// </summary>
public sealed class TelemetryExceptionScrubbingGuardTests
{
    // The exact BannedApiAnalyzers documentation-comment IDs. Activity.AddException is the built-in .NET 9+
    // sink (fires on net10.0 assemblies today); the OpenTelemetry entries are forward bans that resolve once
    // OpenTelemetry.Api is referenced. Pinned so a typo/rename/deletion cannot silently turn a ban into a no-op.
    private static readonly string[] RequiredBannedSymbolIds =
    [
        "M:System.Diagnostics.Activity.AddException(System.Exception,System.Diagnostics.TagList@,System.DateTimeOffset)",
        "M:OpenTelemetry.Trace.ActivityExtensions.RecordException(System.Diagnostics.Activity,System.Exception)",
        "M:OpenTelemetry.Trace.ActivityExtensions.RecordException(System.Diagnostics.Activity,System.Exception,System.Diagnostics.TagList@)",
        "M:OpenTelemetry.Trace.TelemetrySpan.RecordException(System.Exception)",
        "M:OpenTelemetry.Trace.TelemetrySpan.RecordException(System.String,System.String,System.String)",
    ];

    private const int MinScannedProductionFiles = 200;

    // Exception-recording APIs — banned outright; any invocation is an offender regardless of arguments.
    private static readonly HashSet<string> RecordingApis = new(StringComparer.Ordinal)
    {
        "AddException", "RecordException",
    };

    // Span-attribution sinks whose ARGUMENTS must not carry a diagnostic value.
    private static readonly HashSet<string> ValueFlowSinks = new(StringComparer.Ordinal)
    {
        "SetTag", "AddTag", "SetStatus", "AddEvent", "SetBaggage", "AddBaggage", "SetCustomProperty", "StartActivity",
    };

    // Member accesses that surface a tenant-bearing diagnostic value.
    private static readonly HashSet<string> DiagnosticMembers = new(StringComparer.Ordinal)
    {
        "Message", "SimpleString", "FilePath", "ColumnName", "Constraint", "Reference", "TableIdentifier",
    };

    // Literal tag keys that either are the OTel exception-event attributes or the tenant-bearing table key.
    private static readonly HashSet<string> TenantTagKeys = new(StringComparer.Ordinal)
    {
        "exception.message", "exception.stacktrace", "deltasharp.table",
    };

    private static readonly Regex ExceptionReceiver = new(
        @"^(ex|e|exc|err|error|exception)\w*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void BannedSymbols_ActivelyBanExceptionRecordingOntoSpans()
    {
        // Assert on the analyzer's OWN view: non-comment lines only, exact `id;[security]` prefix. Commenting
        // out an entry (`// M:…`) disables RS0030 while leaving the substring present — this parse turns red.
        HashSet<string> activeEntries = File
            .ReadAllLines(Path.Combine(RepoRoot(), "BannedSymbols.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.Split(';', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        foreach (string id in RequiredBannedSymbolIds)
        {
            Assert.Contains(id, activeEntries);
        }

        string bannedSymbols = File.ReadAllText(Path.Combine(RepoRoot(), "BannedSymbols.txt"));
        foreach (string id in RequiredBannedSymbolIds)
        {
            Assert.Contains($"{id};[security]", bannedSymbols, StringComparison.Ordinal);
        }
    }

#if NET10_0_OR_GREATER
    [Fact]
    public void BannedActivityAddException_DocIdMatchesTheRealSignature()
    {
        // Guard against a BCL signature drift (a new overload / added optional parameter) that would leave the
        // pinned doc-ID string byte-identical while the ban silently stops binding. The doc-ID encodes
        // (Exception, in TagList, DateTimeOffset); assert that exact shape still exists on Activity.
        MethodInfo[] addException = typeof(Activity)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "AddException")
            .ToArray();

        Assert.Single(addException);
        Type[] parameters = addException[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(Exception), parameters[0]);
        Assert.Equal("TagList&", parameters[1].Name); // in TagList -> by-ref TagList, encoded as TagList@ in the doc-ID
        Assert.Equal(typeof(DateTimeOffset), parameters[2]);
    }
#endif

    [Fact]
    public void NoProductionSource_MovesADiagnosticIdentifierOntoASpan()
    {
        // Positive control (anti-vacuity): the scan must flag each known-bad snippet — including the value-side
        // vectors, string interpolation (the shape the masker got wrong), collection initializers, DisplayName,
        // baggage, and the literal keys — so a future refactor cannot make every real file pass silently.
        string[] knownBad =
        [
            "activity.AddException(ex);",
            "using static OpenTelemetry.Trace.ActivityExtensions;\nclass C { void M(Activity a, Exception ex){ RecordException(a, ex); } }",
            "span.RecordException(\"type\", ex.Message, ex.ToString());",
            "activity?.SetStatus(ActivityStatusCode.Error, ex.Message);",
            "activity?.SetTag(\"error\", sink.SimpleString);",
            "activity?.SetTag(\"error\", $\"{ex.Message}\");",
            "activity?.SetTag(\"error\", $\"analysis failed: {ex.GetType().Name}: {ex.Message}\");",
            "activity?.SetStatus(ActivityStatusCode.Error, @$\"failed {ex.Message}\");",
            "activity?.AddEvent(new ActivityEvent(\"commit.failed\", tags: new ActivityTagsCollection { { \"detail\", ex.Message } }));",
            "activity?.AddEvent(new ActivityEvent(\"exception\", tags: new ActivityTagsCollection { { \"exception.message\", boundedValue } }));",
            "activity.DisplayName = ex.Message;",
            "activity?.SetBaggage(\"deltasharp.table\", string.Join(\".\", tableIdentifier));",
            "activity?.AddTag(\"error\", ex.ToString());",
            "activity?.SetTag(\"path\", ex.FilePath);",
            "var a = source.StartActivity($\"scan {sink.SimpleString}\");",
            "activity?.SetTag(DeltaSharpTelemetry.TableKey, scrubbed);",
        ];
        foreach (string bad in knownBad)
        {
            Assert.True(RecordsDiagnosticOntoSpan(bad), $"Positive control failed to flag: {bad}");
        }

        // Negative controls: benign bounded telemetry, and comments/strings mentioning a sink or a key, must NOT
        // be flagged (real-parser correctness — a comment/string is trivia/literal, not code).
        string[] knownGood =
        [
            "activity?.SetTag(\"deltasharp.outcome\", DeltaStorageTelemetry.ToLabel(outcome));",
            "activity?.SetTag(\"deltasharp.table.version\", version);",
            "// see AddException(ex) — banned; do not call it",
            "// the OTel exception event writes the \"exception.message\" attribute key — do not emit it",
            "var doc = \"https://x/y see .Message\"; activity?.SetTag(\"u\", boundedValue);",
            "var s = \"/* AddException(ex) */\"; activity?.SetTag(\"u\", boundedValue);",
        ];
        foreach (string good in knownGood)
        {
            Assert.False(RecordsDiagnosticOntoSpan(good), $"Negative control wrongly flagged: {good}");
        }

        int scanned = 0;
        var offenders = new List<string>();
        foreach (string file in EnumerateProductionSources())
        {
            scanned++;
            if (RecordsDiagnosticOntoSpan(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
            }
        }

        Assert.True(
            scanned >= MinScannedProductionFiles,
            $"Scan enumerated only {scanned} production files (< {MinScannedProductionFiles}); the scope likely "
            + "regressed, which would let a real offender pass unseen.");
        Assert.True(
            offenders.Count == 0,
            "Production source moves a diagnostic identifier onto a span (#455): recording an exception, or "
            + "flowing its .Message/.SimpleString/typed identifier property (or the deltasharp.table key) into a "
            + "span tag/status/event/name, leaks a tenant-bearing identifier via the span attributes. Scrub/omit "
            + "the identifier and emit a bounded structural tag instead (or, if this is a same-named member on an "
            + "unrelated type, rename it). Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Rs0030_IsNotDisabledOrUnwired()
    {
        // The bans rest entirely on RS0030 being an error (Directory.Build.props: TreatWarningsAsErrors) and on
        // BannedSymbols.txt being wired as an AdditionalFiles. A repo-wide severity override, a <NoWarn> /
        // <WarningsNotAsErrors>, <TreatWarningsAsErrors>false, or removing the wiring silently unbans every entry
        // (incl. the legitimate Expression.Compile/ADR-0001 bans) with no .cs change and no other test noticing.
        // Legitimate per-call suppressions elsewhere are file-scoped #pragmas for OTHER bans and are unaffected.
        var severityOverride = new Regex(
            @"dotnet_diagnostic\.RS0030\.severity\s*=\s*(none|silent|suggestion)"
            + @"|dotnet_analyzer_diagnostic\.category-ApiDesign\.severity\s*=\s*(none|silent|suggestion)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var noWarn = new Regex(
            @"<(NoWarn|WarningsNotAsErrors)>[^<]*\bRS0030\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var twaeOff = new Regex(
            @"<TreatWarningsAsErrors>\s*false\s*</TreatWarningsAsErrors>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (string file in EnumerateConfigFiles())
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            bool isEditorConfig = name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase);
            if ((isEditorConfig && severityOverride.IsMatch(text)) || noWarn.IsMatch(text) || twaeOff.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "RS0030 is disabled repo-wide (severity override, <NoWarn>/<WarningsNotAsErrors>, or "
            + "<TreatWarningsAsErrors>false), which silently unbans the #455 exception-recording ban. Remove it. "
            + "Offending files: " + string.Join(", ", offenders));

        // Positively pin the wiring the bans depend on (absence of an override is not enough — the AdditionalFiles
        // reference or TreatWarningsAsErrors could be deleted outright).
        string buildProps = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Matches(new Regex(@"<AdditionalFiles\s+Include=""[^""]*BannedSymbols\.txt", RegexOptions.IgnoreCase), buildProps);
        Assert.Matches(new Regex(@"<TreatWarningsAsErrors>\s*true\s*</TreatWarningsAsErrors>", RegexOptions.IgnoreCase), buildProps);
    }

    /// <summary>Parses <paramref name="source"/> with the real C# parser and returns whether it records an
    /// exception onto a span or flows a diagnostic value onto a span-attribution sink. Interpolation holes,
    /// collection initializers, multi-line calls, comments and string literals are handled by the parser.</summary>
    private static bool RecordsDiagnosticOntoSpan(string source)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? name = InvokedSimpleName(invocation);
            if (name is null)
            {
                continue;
            }

            if (RecordingApis.Contains(name))
            {
                return true;
            }

            if (ValueFlowSinks.Contains(name) && invocation.ArgumentList.Arguments.Any(a => CarriesDiagnostic(a)))
            {
                return true;
            }
        }

        foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (TypeSimpleName(creation.Type) == "ActivityEvent"
                && ((creation.ArgumentList?.Arguments.Any(a => CarriesDiagnostic(a)) ?? false)
                    || (creation.Initializer is not null && CarriesDiagnostic(creation.Initializer))))
            {
                return true;
            }
        }

        foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (AssignmentTargetSimpleName(assignment.Left) == "DisplayName" && CarriesDiagnostic(assignment.Right))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CarriesDiagnostic(SyntaxNode node)
    {
        foreach (SyntaxNode descendant in node.DescendantNodesAndSelf())
        {
            switch (descendant)
            {
                case MemberAccessExpressionSyntax member:
                    string memberName = member.Name.Identifier.ValueText;
                    if (DiagnosticMembers.Contains(memberName) || memberName == "TableKey")
                    {
                        return true;
                    }
                    if (memberName == "ToString" && ReceiverLooksLikeException(member.Expression))
                    {
                        return true;
                    }
                    break;
                case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == "TableKey":
                    // A `using static DeltaSharpTelemetry;` bare reference to the tenant-bearing table key.
                    return true;
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
                    && TenantTagKeys.Contains(literal.Token.ValueText):
                    return true;
            }
        }

        return false;
    }

    private static bool ReceiverLooksLikeException(ExpressionSyntax receiver)
    {
        string text = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty,
        };
        return ExceptionReceiver.IsMatch(text);
    }

    private static string? InvokedSimpleName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText, // a?.SetTag(...)
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,          // using static bare call
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };

    private static string? TypeSimpleName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };

    private static string? AssignmentTargetSimpleName(ExpressionSyntax target) => target switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private static IEnumerable<string> EnumerateProductionSources()
    {
        // src/ only — production assemblies. samples/ targets net8.0 (where Activity.AddException does not
        // resolve) and is not a production/tenant-serving assembly, so it is intentionally out of scope; tools/
        // likewise. Any move of samples/ to net10.0 should revisit this scope.
        string srcRoot = Path.Combine(RepoRoot(), "src");
        return Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact);
    }

    private static IEnumerable<string> EnumerateConfigFiles()
    {
        string root = RepoRoot();
        var patterns = new[] { ".editorconfig", "*.editorconfig", "*.globalconfig", "*.csproj", "*.props", "*.targets" };
        var files = new List<string>();
        foreach (string pattern in patterns)
        {
            files.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories));
        }
        return files.Where(IsNotBuildArtifact).Distinct(StringComparer.Ordinal);
    }

    private static bool IsNotBuildArtifact(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeltaSharp.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate DeltaSharp.sln above test base directory '{AppContext.BaseDirectory}'.");
    }
}
