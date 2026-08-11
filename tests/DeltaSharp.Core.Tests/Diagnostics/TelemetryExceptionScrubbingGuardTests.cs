using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// Regression guard for the #455 audit: DeltaSharp diagnostics deliberately name the resource they concern
/// — an <c>AnalysisException</c> (e.g. table-or-view-not-found) embeds the table identifier in its message,
/// and <c>SinkDescriptor.SimpleString</c>/EXPLAIN render <c>table=&lt;identifier&gt;</c>. That is correct for
/// a user-facing diagnostic but must NOT cross into telemetry unscrubbed: recording such an exception onto a
/// span captures its raw <c>exception.message</c> (with the tenant-bearing identifier) as a span attribute,
/// which the observability conventions require to be scrubbed/omitted at the diagnostic→telemetry boundary
/// (observability-conventions.md; checklists 09a/14).
/// <para>The audit found <b>zero</b> exception-onto-span recording call sites today (no telemetry export is
/// wired — that is host-gated, #458), so there is no live leak. These tests pin that conclusion and keep it
/// from silently regressing when telemetry lands: (1) the <c>BannedSymbols.txt</c> entries that make
/// <c>Activity.AddException</c> / <c>Activity.RecordException</c> a compile error (RS0030) must stay present
/// and exact, and (2) no production source may call either API. The mechanical guard covers the
/// "blindly record a raw diagnostic" concern; the sibling "no raw EXPLAIN/plan string attached to a span or
/// log" concern is a convention enforced by review (a free-text tag cannot be banned by API), documented
/// alongside these bans.</para>
/// </summary>
public sealed class TelemetryExceptionScrubbingGuardTests
{
    // The exact BannedApiAnalyzers documentation-comment IDs. Activity.AddException is the built-in .NET 9+
    // sink (resolves and fires on net10.0 assemblies today); the two OpenTelemetry RecordException overloads
    // are forward bans that resolve once OpenTelemetry.Api is referenced — until then the source-scan below is
    // their active enforcement. Pinned here so a typo/rename/deletion cannot silently turn a ban into a no-op.
    private static readonly string[] RequiredBannedSymbolIds =
    [
        "M:System.Diagnostics.Activity.AddException(System.Exception,System.Diagnostics.TagList@,System.DateTimeOffset)",
        "M:OpenTelemetry.Trace.ActivityExtensions.RecordException(System.Diagnostics.Activity,System.Exception)",
        "M:OpenTelemetry.Trace.ActivityExtensions.RecordException(System.Diagnostics.Activity,System.Exception,System.Diagnostics.TagList@)",
    ];

    [Fact]
    public void BannedSymbols_BanExceptionRecordingOntoSpans()
    {
        string bannedSymbols = File.ReadAllText(Path.Combine(RepoRoot(), "BannedSymbols.txt"));

        foreach (string id in RequiredBannedSymbolIds)
        {
            Assert.Contains($"{id};[security]", bannedSymbols, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoProductionSource_RecordsAnExceptionOntoASpan()
    {
        // The #455 audit invariant: recording an exception onto a span/Activity is banned because the
        // exception message can carry a tenant-bearing identifier. Assert zero call sites across every
        // production assembly — this catches the API on the net8.0 compilation (where the built-in symbol
        // does not resolve, so RS0030 cannot fire) and catches RecordException before OpenTelemetry is
        // referenced (where its ban is dormant). Comments are stripped so a doc-comment mention does not
        // false-positive.
        var callPattern = new Regex(@"\.\s*(AddException|RecordException)\s*\(", RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (string file in EnumerateProductionSources())
        {
            string stripped = StripComments(File.ReadAllText(file));
            if (callPattern.IsMatch(stripped))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Production source records an exception onto a span (#455). Recording a DeltaSharp diagnostic "
            + "onto a span leaks its tenant-bearing identifier via exception.message; scrub/omit the "
            + "identifier or record a structural form first. Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoRs0030Suppression_ReEnablesExceptionRecording()
    {
        // Defense-in-depth mirroring the SQL-door hygiene guard: the ban's only sanctioned escape is a
        // justified `#pragma warning disable RS0030` citing #455 for a genuinely tenant-free exception. Until
        // such a site exists, pin ZERO RS0030 suppressions in any production file that mentions these sinks,
        // so a pragma cannot re-open the leak next to an AddException/RecordException call unreviewed.
        var mentionsSink = new Regex(@"\b(AddException|RecordException)\b", RegexOptions.Compiled);
        var rs0030 = new Regex(@"#pragma\s+warning\s+disable\s+[^\r\n]*\bRS0030\b", RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (string file in EnumerateProductionSources())
        {
            string source = File.ReadAllText(file);
            if (mentionsSink.IsMatch(source) && rs0030.IsMatch(source))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "An RS0030 suppression sits in a production file that mentions AddException/RecordException (#455). "
            + "A tenant-free recording must carry its own justified pragma citing #455 at the call site, not a "
            + "blanket suppression. Offending files: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> EnumerateProductionSources()
    {
        string srcRoot = Path.Combine(RepoRoot(), "src");
        return Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string StripComments(string source)
    {
        string withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", string.Empty);
    }

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
