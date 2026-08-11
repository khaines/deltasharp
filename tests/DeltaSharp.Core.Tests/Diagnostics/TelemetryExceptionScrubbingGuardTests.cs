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
/// <c>SinkDescriptor.SimpleString</c> / a plan <c>ToString()</c> render <c>table=&lt;identifier&gt;</c>. Correct
/// for a user-facing diagnostic, but it must NOT cross into telemetry unscrubbed: recording such an exception
/// onto a span, or flowing its message/identifier onto a span tag/status/event/name/baggage, captures the
/// tenant-bearing identifier as a span attribute, which observability-conventions.md requires to be
/// scrubbed/omitted at that boundary (checklists 09a/14).
/// <para>The audit found <b>zero</b> such sites today (telemetry export is host-gated, #458). These tests keep
/// the conclusion from silently regressing when telemetry lands: the <c>BannedSymbols.txt</c> RS0030 bans stay
/// present/exact/signature-matched; no production source records an exception onto a span or flows a diagnostic
/// value onto a span-attribution sink; and RS0030 is not disabled or unwired repo-wide.</para>
/// <para>The scan parses each file with the real C# parser (<see cref="CSharpSyntaxTree"/>), across both the
/// <c>net8.0</c> and <c>net10.0</c> conditional-compilation legs, so string interpolation, collection
/// initializers, multi-line calls, comments/string literals, and <c>#if</c> regions are handled by
/// construction, and asserts every production file parses without error so a parser/language-version drift
/// fails loudly rather than degrading the walk silently.</para>
/// <para><b>Deliberately conservative (fail-safe):</b> the scan favours false positives over false negatives —
/// e.g. it flags any <c>.ToString()</c>, or a same-named diagnostic member on an unrelated type, inside a
/// span-attribution sink. If that fires on genuinely bounded telemetry, pass the typed/bounded value (not
/// <c>.ToString()</c>/<c>.Message</c>) or rename the member. <b>Out of scope (review-enforced residuals,
/// documented in observability-conventions.md):</b> a value laundered through an intermediate local/field/helper
/// (intra-procedural dataflow — the RS0030 ban on the recording APIs is the dataflow-proof control there); an
/// arbitrary human-chosen free-text EXPLAIN/plan string; and the <c>deltasharp.table</c> producer scrubbing seam
/// (#790, gated on #458).</para>
/// </summary>
public sealed class TelemetryExceptionScrubbingGuardTests
{
    private static readonly string[] RequiredBannedSymbolIds =
    [
        "M:System.Diagnostics.Activity.AddException(System.Exception,System.Diagnostics.TagList@,System.DateTimeOffset)",
        "M:OpenTelemetry.Trace.ActivityExtensions.RecordException(System.Diagnostics.Activity,System.Exception)",
        "M:OpenTelemetry.Trace.ActivityExtensions.RecordException(System.Diagnostics.Activity,System.Exception,System.Diagnostics.TagList@)",
        "M:OpenTelemetry.Trace.TelemetrySpan.RecordException(System.Exception)",
        "M:OpenTelemetry.Trace.TelemetrySpan.RecordException(System.String,System.String,System.String)",
    ];

    private const int MinScannedProductionFiles = 200;

    private static readonly HashSet<string> RecordingApis = new(StringComparer.Ordinal)
    {
        "AddException", "RecordException",
    };

    private static readonly HashSet<string> ValueFlowSinks = new(StringComparer.Ordinal)
    {
        "SetTag", "AddTag", "SetStatus", "AddEvent", "SetBaggage", "AddBaggage", "SetCustomProperty",
        "StartActivity", "CreateActivity",
    };

    private static readonly HashSet<string> DiagnosticMembers = new(StringComparer.Ordinal)
    {
        "Message", "SimpleString", "FilePath", "ColumnName", "Constraint", "Reference", "TableIdentifier",
    };

    private static readonly HashSet<string> TenantTagKeys = new(StringComparer.Ordinal)
    {
        "exception.message", "exception.stacktrace", "deltasharp.table",
    };

    // Parse both conditional-compilation legs so a leak inside `#if NET10_0_OR_GREATER` (the natural home for a
    // net9+ API in a multi-targeted library) or `#if DEBUG` is not invisible.
    private static readonly CSharpParseOptions[] ParseLegs =
    [
        new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
            "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER", "DEBUG", "TRACE"),
        new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
            "NET", "NETCOREAPP", "NET9_0_OR_GREATER", "NET10_0", "NET10_0_OR_GREATER", "DEBUG", "TRACE"),
    ];

    [Fact]
    public void BannedSymbols_ActivelyBanExceptionRecordingOntoSpans()
    {
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
        MethodInfo[] addException = typeof(Activity)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "AddException")
            .ToArray();

        Assert.Single(addException);
        Type[] parameters = addException[0].GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(Exception), parameters[0]);
        Assert.Equal("TagList&", parameters[1].Name);
        Assert.Equal(typeof(DateTimeOffset), parameters[2]);
    }
#endif

    [Fact]
    public void NoProductionSource_MovesADiagnosticIdentifierOntoASpan()
    {
        string[] knownBad =
        [
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex){ a.AddException(ex); } }",
            "using static OpenTelemetry.Trace.ActivityExtensions;\nclass C { void M(System.Diagnostics.Activity a, System.Exception ex){ RecordException(a, ex); } }",
            "class C { void M(dynamic span, System.Exception ex){ span.RecordException(\"t\", ex.Message, ex.ToString()); } }",
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex){ a?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message); } }",
            "class C { void M(System.Diagnostics.Activity a, dynamic sink){ a?.SetTag(\"e\", sink.SimpleString); } }",
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex){ a?.SetTag(\"e\", $\"{ex.Message}\"); } }",
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex){ a?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, @$\"failed {ex.Message}\"); } }",
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex, dynamic tags){ a?.AddEvent(new System.Diagnostics.ActivityEvent(\"e\", tags: new System.Diagnostics.ActivityTagsCollection { { \"d\", ex.Message } })); } }",
            "class C { void M(System.Diagnostics.Activity a){ a.DisplayName = someException.Message; } }",
            "class C { void M(System.Diagnostics.Activity a, System.Collections.Generic.IReadOnlyList<string> id){ a?.SetBaggage(\"deltasharp.table\", string.Join(\".\", id)); } }",
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex){ a?.AddTag(\"e\", ex.ToString()); } }",
            "class C { void M(System.Diagnostics.Activity a){ try {} catch (System.Exception failure) { a?.SetTag(\"e\", failure.ToString()); } } }",
            "class C { void M(System.Diagnostics.Activity a){ try {} catch (System.Exception ex) { a?.SetTag(\"e\", ex); } } }",
            "class C { void M(System.Diagnostics.Activity a){ try {} catch (System.Exception ex) { a?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, $\"{ex}\"); } } }",
            "class C { void M(System.Diagnostics.Activity a, dynamic e){ a?.SetTag(\"e\", e?.Message); } }",
            "class C { void M(System.Diagnostics.Activity a){ a?.SetTag(\"p\", failure.FilePath); } }",
            "class C { System.Diagnostics.Activity? M(System.Diagnostics.ActivitySource s, dynamic sink){ return s.StartActivity($\"scan {sink.SimpleString}\"); } }",
            "class C { void M(System.Diagnostics.Activity a, string v){ a?.SetTag(DeltaSharpTelemetry.TableKey, v); } }",
            "class C { void M(System.Collections.Generic.IDictionary<string,object> tags, System.Exception ex){ tags[\"exception.message\"] = ex.Message; } }",
            "class C { void M(System.Diagnostics.Activity a, System.Exception ex){ System.Diagnostics.ActivityEvent e = new(\"x\", tags: new System.Diagnostics.ActivityTagsCollection { { \"d\", ex.Message } }); a?.AddEvent(e); } }",
        ];
        foreach (string bad in knownBad)
        {
            Assert.True(RecordsDiagnosticOntoSpan(bad), $"Positive control failed to flag: {bad}");
        }

        string[] knownGood =
        [
            "class C { void M(System.Diagnostics.Activity a, int outcome){ a?.SetTag(\"deltasharp.outcome\", DeltaStorageTelemetry.ToLabel(outcome)); } }",
            "class C { void M(System.Diagnostics.Activity a, long version){ a?.SetTag(\"deltasharp.table.version\", version); } }",
            "class C { void M(System.Diagnostics.Activity a){ a?.SetTag(\"k\", nameof(System.Exception.Message)); } }",
            "// see AddException(ex) — banned; do not call it",
            "class C { const string K = \"the OTel key is exception.message — do not emit it\"; }",
            "class C { void M(System.Diagnostics.Activity a, string boundedValue){ var doc = \"https://x/y see .Message\"; a?.SetTag(\"u\", boundedValue); } }",
        ];
        foreach (string good in knownGood)
        {
            Assert.False(RecordsDiagnosticOntoSpan(good), $"Negative control wrongly flagged: {good}");
        }

        int scanned = 0;
        var offenders = new List<string>();
        var parseFailures = new List<string>();
        foreach (string file in EnumerateProductionSources())
        {
            scanned++;
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

            // Parse-success canary: if the pinned parser cannot parse a production file (e.g. a newer C#
            // language construct than Microsoft.CodeAnalysis.CSharp supports), the walk would silently degrade.
            // Fail loudly instead so the parser package is bumped alongside the SDK.
            if (CSharpSyntaxTree.ParseText(text, ParseLegs[1]).GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                parseFailures.Add(relative);
            }

            if (RecordsDiagnosticOntoSpan(text))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            parseFailures.Count == 0,
            "The pinned C# parser failed to parse production files (a language-version drift would silently "
            + "degrade the scan); bump Microsoft.CodeAnalysis.CSharp to match the SDK. Files: "
            + string.Join(", ", parseFailures));
        Assert.True(
            scanned >= MinScannedProductionFiles,
            $"Scan enumerated only {scanned} production files (< {MinScannedProductionFiles}); the scope likely "
            + "regressed, which would let a real offender pass unseen.");
        Assert.True(
            offenders.Count == 0,
            "Production source moves a diagnostic identifier onto a span (#455): recording an exception, or "
            + "flowing its message/identifier (or the deltasharp.table key) into a span tag/status/event/name, "
            + "leaks a tenant-bearing identifier via the span attributes. Scrub/omit the identifier and emit a "
            + "bounded structural tag instead (or, if this is a bounded value/same-named member on an unrelated "
            + "type, pass its typed form or rename it). Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Rs0030_IsNotDisabledOrUnwired()
    {
        var severityOverride = new Regex(
            @"dotnet_diagnostic\.RS0030\.severity\s*=\s*(none|silent|suggestion)"
            + @"|dotnet_analyzer_diagnostic\.category-ApiDesign\.severity\s*=\s*(none|silent|suggestion)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var noWarn = new Regex(
            @"<(NoWarn|WarningsNotAsErrors)>[^<]*\bRS0030\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var twaeOff = new Regex(
            @"<TreatWarningsAsErrors>\s*false\s*</TreatWarningsAsErrors>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (string file in EnumerateWiringFiles())
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

        string buildProps = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Matches(new Regex(@"<AdditionalFiles\s+Include=""[^""]*BannedSymbols\.txt", RegexOptions.IgnoreCase), buildProps);
        Assert.Matches(new Regex(@"<TreatWarningsAsErrors>\s*true\s*</TreatWarningsAsErrors>", RegexOptions.IgnoreCase), buildProps);
    }

    /// <summary>Parses <paramref name="source"/> with the real C# parser (both conditional-compilation legs) and
    /// returns whether it records an exception onto a span or flows a diagnostic value onto a span-attribution
    /// sink.</summary>
    private static bool RecordsDiagnosticOntoSpan(string source) =>
        ParseLegs.Any(options => AnalyzeRoot(CSharpSyntaxTree.ParseText(source, options).GetRoot()));

    private static bool AnalyzeRoot(SyntaxNode root)
    {
        // Identifiers that syntactically denote an exception object: a `catch (T name)` binding or a parameter
        // typed `*Exception`. Passing one straight onto a span (or `$"{ex}"`) stores the object every exporter
        // stringifies — a direct, in-scope leak, not the intra-procedural laundering residual.
        HashSet<string> exceptionVars = CollectExceptionVariableNames(root);

        // A tenant-bearing literal key USED as a telemetry key (a call argument or a tag-bag indexer, e.g.
        // `tags["exception.message"] = …`) — but not the constant's own definition (a field initializer).
        foreach (LiteralExpressionSyntax literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression)
                && TenantTagKeys.Contains(literal.Token.ValueText)
                && literal.Ancestors().OfType<ArgumentSyntax>().Any())
            {
                return true;
            }
        }

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

            if (ValueFlowSinks.Contains(name)
                && invocation.ArgumentList.Arguments.Any(a => CarriesDiagnostic(a, exceptionVars)))
            {
                return true;
            }
        }

        foreach (BaseObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (CreationIsActivityEvent(creation)
                && ((creation.ArgumentList?.Arguments.Any(a => CarriesDiagnostic(a, exceptionVars)) ?? false)
                    || (creation.Initializer is not null && CarriesDiagnostic(creation.Initializer, exceptionVars))))
            {
                return true;
            }
        }

        foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (AssignmentTargetSimpleName(assignment.Left) == "DisplayName"
                && CarriesDiagnostic(assignment.Right, exceptionVars))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CarriesDiagnostic(SyntaxNode node, HashSet<string> exceptionVars)
    {
        // Do not descend into nameof(...): it yields a compile-time literal, no value flows.
        foreach (SyntaxNode descendant in node.DescendantNodesAndSelf(n => !IsNameOf(n)))
        {
            switch (descendant)
            {
                case MemberAccessExpressionSyntax member when IsDiagnosticMemberName(member.Name.Identifier.ValueText):
                    return true;
                case MemberBindingExpressionSyntax binding when IsDiagnosticMemberName(binding.Name.Identifier.ValueText):
                    // The `.Message` half of a null-conditional access `ex?.Message`.
                    return true;
                case IdentifierNameSyntax identifier
                    when identifier.Identifier.ValueText == "TableKey" || exceptionVars.Contains(identifier.Identifier.ValueText):
                    return true;
            }
        }

        return false;
    }

    // .ToString() has no receiver gate (fail-safe): a bounded value should be passed as its typed form, not
    // stringified onto a tag; Exception/plan ToString() renders the tenant-bearing message/EXPLAIN.
    private static bool IsDiagnosticMemberName(string name) =>
        DiagnosticMembers.Contains(name) || name == "TableKey" || name == "ToString";

    private static bool IsNameOf(SyntaxNode node) =>
        node is InvocationExpressionSyntax invocation && InvokedSimpleName(invocation) == "nameof";

    private static HashSet<string> CollectExceptionVariableNames(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (CatchDeclarationSyntax @catch in root.DescendantNodes().OfType<CatchDeclarationSyntax>())
        {
            if (!@catch.Identifier.IsKind(SyntaxKind.None) && @catch.Identifier.ValueText.Length > 0)
            {
                names.Add(@catch.Identifier.ValueText);
            }
        }
        foreach (ParameterSyntax parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Type is not null && TypeSimpleName(parameter.Type) is string typeName
                && typeName.EndsWith("Exception", StringComparison.Ordinal))
            {
                names.Add(parameter.Identifier.ValueText);
            }
        }
        return names;
    }

    private static bool CreationIsActivityEvent(BaseObjectCreationExpressionSyntax creation)
    {
        if (creation is ObjectCreationExpressionSyntax explicitCreation)
        {
            return TypeSimpleName(explicitCreation.Type) == "ActivityEvent";
        }

        // Target-typed `ActivityEvent e = new(...)`: read the type from the enclosing declaration.
        TypeSyntax? declaredType = creation.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault()?.Type;
        return declaredType is not null && TypeSimpleName(declaredType) == "ActivityEvent";
    }

    private static string? InvokedSimpleName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null,
    };

    private static string? TypeSimpleName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        NullableTypeSyntax nullable => TypeSimpleName(nullable.ElementType),
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
        // src/ only — production assemblies. samples/ (net8.0) and tools/ are out of scope.
        string srcRoot = Path.Combine(RepoRoot(), "src");
        return Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact);
    }

    private static IEnumerable<string> EnumerateWiringFiles()
    {
        // Restrict to repository-owned wiring: root-level config + everything under src/ and tests/. This keeps
        // the meta-guard deterministic — an untracked scratch/vendored repo copy left in the worktree cannot
        // affect the verdict.
        string root = RepoRoot();
        var files = new List<string>();
        foreach (string pattern in new[] { ".editorconfig", "*.editorconfig", "*.globalconfig", "*.props", "*.targets", "*.csproj" })
        {
            files.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly));
        }
        foreach (string dir in new[] { "src", "tests" })
        {
            string sub = Path.Combine(root, dir);
            if (Directory.Exists(sub))
            {
                foreach (string pattern in new[] { ".editorconfig", "*.editorconfig", "*.globalconfig", "*.props", "*.targets", "*.csproj" })
                {
                    files.AddRange(Directory.EnumerateFiles(sub, pattern, SearchOption.AllDirectories));
                }
            }
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
