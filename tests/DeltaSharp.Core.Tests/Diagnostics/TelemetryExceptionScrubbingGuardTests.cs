using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// Regression guard for the #455 audit. DeltaSharp diagnostics deliberately name the resource they concern —
/// <c>AnalysisException.TableOrViewNotFound</c> embeds the table identifier in its message; EXPLAIN /
/// <c>SinkDescriptor.SimpleString</c> render <c>table=&lt;identifier&gt;</c>. Correct for a user-facing
/// diagnostic, but it must NOT cross into telemetry unscrubbed: recording such an exception onto a span (or
/// flowing its <c>.Message</c>/<c>.SimpleString</c> into a span tag/status/event) captures the tenant-bearing
/// identifier as a span attribute, which observability-conventions.md requires to be scrubbed/omitted at that
/// boundary (checklists 09a/14).
/// <para>The audit found <b>zero</b> such sites today (telemetry export is host-gated, #458). These tests keep
/// the conclusion from silently regressing when telemetry lands, covering the whole tractable leak surface —
/// not just the two exception-recording APIs:</para>
/// <list type="bullet">
/// <item>the <c>BannedSymbols.txt</c> RS0030 bans must stay present, exact, and (for the built-in sink)
///   signature-matched — asserted on the analyzer's own parse (uncommented lines) plus reflection, so a
///   commented-out entry or a BCL signature drift turns red instead of silently disabling the ban;</item>
/// <item>no production source may record an exception onto a span (<c>AddException</c>/<c>RecordException</c>,
///   including a <c>using static</c> bare call) NOR flow a diagnostic value onto a span via
///   <c>SetTag</c>/<c>SetStatus</c>/<c>AddEvent</c>/<c>ActivityEvent</c> using <c>.Message</c>/<c>.SimpleString</c>
///   or an <c>exception.message</c>/<c>exception.stacktrace</c> tag key — the value-side vectors a symbol ban
///   cannot reach;</item>
/// <item>RS0030 must not be disabled repo-wide (an <c>.editorconfig</c>/<c>.globalconfig</c> severity override or
///   a <c>&lt;NoWarn&gt;</c>), which would silently unban every entry.</item>
/// </list>
/// The scan masks comments and string-literal contents first (a URL or <c>/*</c> inside a string can no longer
/// blind it), and carries its own positive-control + file-count floor so a scope regression cannot make it pass
/// vacuously. The genuinely un-mechanizable case (an arbitrary free-text EXPLAIN/plan string a human chooses to
/// attach) stays a review-enforced convention, documented alongside these guards.
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

    // A minimum number of production .cs files the scan must see; a scope regression that narrows enumeration
    // (and would let a real offender slip through green) trips this floor. The tree has hundreds of files.
    private const int MinScannedProductionFiles = 200;

    [Fact]
    public void BannedSymbols_ActivelyBanExceptionRecordingOntoSpans()
    {
        // Assert on the analyzer's OWN view: non-comment lines only, exact `id;[security] …` prefix. Commenting
        // out an entry (`// M:…`) disables RS0030 while leaving the substring present — a text-contains check
        // would stay green; this parse does not.
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

        // Every active entry must carry the [security] category (the remediation contract callers read).
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
        // Positive control (anti-vacuity): the scan pipeline must flag a known-bad snippet, so a future refactor
        // that breaks the masker/patterns cannot make every real file pass silently.
        string[] knownBad =
        [
            "activity.AddException(ex);",
            "using static OpenTelemetry.Trace.ActivityExtensions;\nRecordException(activity, ex);",
            "span.RecordException(\"type\", ex.Message, ex.ToString());",
            "activity?.SetStatus(ActivityStatusCode.Error, ex.Message);",
            "activity?.SetTag(\"error\", sink.SimpleString);",
            "activity?.AddEvent(new ActivityEvent(\"exception\", tags: new ActivityTagsCollection { { \"exception.message\", ex.Message } }));",
        ];
        foreach (string bad in knownBad)
        {
            Assert.True(RecordsDiagnosticOntoSpan(bad), $"Positive control failed to flag: {bad}");
        }
        // A benign bounded tag must NOT be flagged (guards against over-broad matching turning the codebase red).
        Assert.False(RecordsDiagnosticOntoSpan(
            "activity?.SetTag(\"deltasharp.outcome\", DeltaStorageTelemetry.ToLabel(outcome));"));
        // A comment or string literal mentioning a sink must NOT be flagged (masker correctness).
        Assert.False(RecordsDiagnosticOntoSpan("// see AddException(ex) — banned; do not call it"));
        Assert.False(RecordsDiagnosticOntoSpan("var doc = \"https://x/y\"; activity?.SetTag(\"u\", doc);"));

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
            + "flowing its .Message/.SimpleString into a span tag/status/event, leaks a tenant-bearing "
            + "identifier via the span attributes. Scrub/omit the identifier and emit a bounded structural tag "
            + "instead (or, if this is a same-named member on an unrelated type, rename it). Offending files: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Rs0030_IsNotDisabledRepoWide()
    {
        // The bans rest entirely on RS0030 being an error (Directory.Build.props: TreatWarningsAsErrors). A
        // repo-wide severity override or a <NoWarn> silently unbans every entry (incl. the legitimate
        // Expression.Compile/ADR-0001 bans) with no .cs change and no other test noticing. Legitimate per-call
        // suppressions elsewhere are file-scoped #pragmas for OTHER bans and are unaffected — this asserts only
        // that RS0030 is not turned OFF globally.
        var severityOverride = new Regex(
            @"dotnet_diagnostic\.RS0030\.severity\s*=\s*(none|silent|suggestion)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var noWarn = new Regex(@"<NoWarn>[^<]*\bRS0030\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (string file in EnumerateConfigFiles())
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            bool isEditorConfig = name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase);
            if ((isEditorConfig && severityOverride.IsMatch(text)) || noWarn.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "RS0030 is disabled repo-wide (severity override or <NoWarn>), which silently unbans the #455 "
            + "exception-recording ban. Remove it. Offending files: " + string.Join(", ", offenders));
    }

    /// <summary>Returns whether <paramref name="source"/> (a full C# file or a snippet) contains a call that
    /// records an exception onto a span or flows a diagnostic <c>.Message</c>/<c>.SimpleString</c> value onto a
    /// span. Comment and string-literal contents are masked first so a URL/<c>/*</c> inside a string cannot
    /// hide a following call; the literal <c>exception.message</c>/<c>exception.stacktrace</c> tag keys are
    /// matched on the RAW text (they are themselves the smoking gun).</summary>
    private static bool RecordsDiagnosticOntoSpan(string source)
    {
        string masked = MaskCommentsAndStringLiterals(source);

        // Exception-recording APIs. No leading-dot anchor, so a `using static … ; RecordException(a, ex)` bare
        // call is caught; a same-named member on an unrelated type is caught too (fail-safe over-match).
        if (Regex.IsMatch(masked, @"\b(AddException|RecordException)\s*\("))
        {
            return true;
        }

        // Value-flow onto a span: a diagnostic .Message/.SimpleString passed into a span tag/status/event, within
        // the same statement. Covers SetStatus(Error, ex.Message), SetTag(k, ex.Message)/SetTag(k, sink.SimpleString),
        // AddEvent(...) and new ActivityEvent(...).
        if (Regex.IsMatch(masked, @"\b(SetTag|SetStatus|AddEvent|ActivityEvent)\s*\([^;{}]*\.\s*(Message|SimpleString)\b"))
        {
            return true;
        }

        // The OpenTelemetry exception-event attribute keys — writing one is exactly what RecordException does
        // internally. Matched on raw text because the masker blanks string contents.
        if (Regex.IsMatch(source, "\"exception\\.(message|stacktrace)\""))
        {
            return true;
        }

        return false;
    }

    /// <summary>Replaces the CONTENTS of comments and string/char literals with spaces (preserving newlines and
    /// delimiters) so a regex over the result never matches inside a comment or literal. Handles line/block
    /// comments, regular/verbatim/raw string literals, and char literals with escapes.</summary>
    private static string MaskCommentsAndStringLiterals(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        int n = source.Length;
        while (i < n)
        {
            char c = source[i];

            // Line comment
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && source[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            // Block comment
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < n && !(source[i] == '*' && i + 1 < n && source[i + 1] == '/'))
                {
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < n) { sb.Append("  "); i += 2; }
                continue;
            }

            // Raw string literal (""" ... """), possibly multi-quote. Content masked, delimiters kept.
            if (c == '"' && i + 2 < n && source[i + 1] == '"' && source[i + 2] == '"')
            {
                int q = 0;
                while (i < n && source[i] == '"') { sb.Append('"'); i++; q++; }
                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        int run = 0;
                        int j = i;
                        while (j < n && source[j] == '"') { run++; j++; }
                        if (run >= q)
                        {
                            for (int k = 0; k < run; k++) { sb.Append('"'); }
                            i = j;
                            break;
                        }
                        for (int k = 0; k < run; k++) { sb.Append(' '); }
                        i = j;
                        continue;
                    }
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                continue;
            }

            // Verbatim string literal (@"..."), "" is an escaped quote.
            if (c == '@' && i + 1 < n && source[i + 1] == '"')
            {
                sb.Append("@\"");
                i += 2;
                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < n && source[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                        sb.Append('"'); i++; break;
                    }
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                continue;
            }

            // Regular string literal ("..."), with backslash escapes.
            if (c == '"')
            {
                sb.Append('"');
                i++;
                while (i < n && source[i] != '"' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < n) { sb.Append("  "); i += 2; continue; }
                    sb.Append(' '); i++;
                }
                if (i < n && source[i] == '"') { sb.Append('"'); i++; }
                continue;
            }

            // Char literal ('.'), with backslash escapes.
            if (c == '\'')
            {
                sb.Append('\'');
                i++;
                while (i < n && source[i] != '\'' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < n) { sb.Append("  "); i += 2; continue; }
                    sb.Append(' '); i++;
                }
                if (i < n && source[i] == '\'') { sb.Append('\''); i++; }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

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
        var patterns = new[] { ".editorconfig", "*.editorconfig", "*.globalconfig", "*.csproj", "*.props" };
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
