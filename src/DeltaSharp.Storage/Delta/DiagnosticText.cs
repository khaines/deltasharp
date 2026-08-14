using System.Globalization;
using System.Text;
using DeltaSharp.Types;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// Message-hygiene helpers shared across the Storage write/commit/optimize surfaces (#667, following the
/// read-path #653 hardening). The DeltaSharp storage layer <b>cannot redact</b> (Core's
/// <c>SecretRedaction</c> is internal and unreachable here), so a fault message must never carry an
/// attacker-controllable or unbounded token in a form that could inject into a structured-log sink
/// (CRLF/control chars) or disclose file layout. The posture is TIERED, matching the token (#683):
/// <list type="bullet">
/// <item>a token whose very presence discloses something the message must not carry (physical data-schema
/// content, secrets) is <b>dropped</b> from the <c>Message</c> and, where useful, kept on a typed property a
/// caller can read and redact at its own sink;</item>
/// <item><b>data-derived content is dropped, never sanitized.</b> A table-relative object path is Hive-encoded
/// (<c>email=alice%40example.com/part-….parquet</c>), so its directory segments embed partition VALUES — which
/// are COLUMN VALUES, i.e. table data and potentially PII. <see cref="Sanitize"/> does not help: an email
/// address is neither a control character nor over the cap, so it would survive verbatim and
/// <c>Uri.UnescapeDataString</c> recovers it. Every path echo therefore goes through
/// <see cref="DescribePath"/>, which keeps the file name and the partition COLUMN NAMES (both sanitized) and
/// discards the values, with the raw path retained on a typed property. The same applies to a path that
/// arrives inside FOREIGN TEXT: stripping the absolute root from a framework exception message leaves the
/// table-RELATIVE remainder, which is still Hive-encoded, so a backend must also strip the <c>=value</c> out
/// of any surviving <c>key=value</c> segment before that text reaches a <c>Message</c>;</item>
/// <item>the <b>shape</b> of a path is not data and is kept: whether it was rooted, how many parent
/// traversals it used, how many directories were dropped, and whether it sits under a DeltaSharp-generated
/// control directory. These are counts and fixed literals. Keeping them is what stops a confinement
/// rejection of <c>../../../../etc/passwd</c> from reading identically to an innocent in-root file, and it
/// is why no second "control path" renderer that echoes raw is needed — a poisoned <c>add.path</c> reaches
/// the confinement guard WITH a partition value attached, so such a branch would reopen the disclosure;</item>
/// <item>an identifier the operator NEEDS in order to act — a schema/partition/physical column name or an
/// application id from a possibly-poisoned <c>_delta_log</c> — is echoed
/// through <see cref="Sanitize"/>, which strips control characters and caps length, closing the
/// log-injection and log-flooding vectors while preserving the diagnostic value. Dropping these outright
/// would leave an operator unable to identify WHICH column failed;</item>
/// <item>a LIST of such identifiers additionally goes through <see cref="SanitizeAndJoin"/>, which bounds
/// the rendered COUNT (<see cref="MaxEchoedListItems"/>) and elides the remainder, so per-item sanitization
/// alone cannot be defeated by sheer cardinality;</item>
/// <item>an inherently bounded token (a <c>StorageErrorKind</c>, a count, a version number, a
/// <c>DataType.TypeName</c>) is echoed <b>as-is</b>. Note that <c>DataType.SimpleString</c> is NOT such a
/// token for a nested type: it embeds every nested field name verbatim and recurses. It is safe ONLY where
/// the type is scalar by construction, and such a site must say so in a comment. A whole
/// <see cref="StructType"/> is never echoed via <c>SimpleString</c> — use <see cref="DescribeSchema"/>,
/// which renders the column COUNT plus a bounded sample of sanitized names.</item>
/// </list>
/// A guard that validates a token for some OTHER purpose does not discharge this one — e.g. the
/// path-segment safety check on a column-mapping physical name rejects Unicode <c>Cc</c> but not
/// <c>U+2028</c>/<c>U+2029</c>, so an echo must still route through <see cref="Sanitize"/>.
/// This is the same idiom as <c>ColumnMapping.SanitizeEchoedToken</c> (#516), lifted to a single shared
/// helper so the postures cannot drift across surfaces. The sanitizing PRIMITIVE itself now lives in
/// <c>DeltaSharp.Abstractions</c> (<c>DeltaSharp.Diagnostics.DiagnosticText</c>) because the
/// <c>DeltaSharp.Core</c> SQL parser needs the identical semantics (#687) and Core must not reference
/// Storage; this type keeps the Storage-specific caps/postures and forwards the primitive.
/// </summary>
internal static class DiagnosticText
{
    /// <summary>The default cap for an echoed identifier — generous enough for any real dotted column path
    /// (a physical name is <c>col-&lt;uuid&gt;</c> = 40 chars; a nested logical path is typically short) yet
    /// bounded so a crafted name cannot blow up a log line.
    /// <para><b>Aliases the shared primitive's constant rather than restating its value</b>, so there is
    /// exactly one definition. It was previously declared here verbatim AND in
    /// <c>DeltaSharp.Diagnostics.DiagnosticText</c>, unlinked — and because <see cref="Sanitize"/>'s optional
    /// parameter binds THIS one, editing the shared constant silently did nothing to Storage. That is the same
    /// silent-drift class as the elision bound above, so it is closed the same way (#687 follow-up).</para></summary>
    internal const int DefaultMaxLength = SharedDiagnosticText.DefaultMaxLength;

    /// <summary>The cap for an echoed <b>table-property / configuration VALUE</b> (e.g. a
    /// <c>metaData.configuration</c> entry such as <c>delta.appendOnly</c> or a retention duration) — the
    /// single source of truth shared by every config-value echo so the bound cannot drift across surfaces
    /// (#666). Tighter than <see cref="DefaultMaxLength"/> because a valid property value is a short protocol
    /// string (a boolean or a calendar-interval literal), unlike a dotted column path.</summary>
    internal const int ConfigTokenMaxLength = 64;

    /// <summary>The maximum number of items rendered from an attacker-influenceable LIST (a foreign table's
    /// unsupported reader/writer features, or the CHECK constraints dependent on a changed column) before the
    /// remainder is elided as <c>… (+N more)</c>. Bounds the AGGREGATE message length so a hostile list of
    /// thousands of (individually per-item-capped) entries cannot flood a log line (#666).
    /// <para><b>This is the single authoritative Storage elision bound, on every path.</b> Storage elides
    /// lists two ways — through <see cref="SanitizeAndJoin"/> (e.g. <c>DeltaProtocolException</c>'s
    /// reader/writer feature list) and by reading this constant directly for a hand-rolled listing (e.g.
    /// <c>DeltaConstraintDependentColumnException</c>'s dependent-CHECK listing). The forwarder therefore
    /// passes this constant EXPLICITLY to the shared primitive rather than letting it supply a bound of its
    /// own, so both paths are provably governed by this one declaration and cannot silently desynchronize.
    /// The shared primitive deliberately has no default to inherit (#687 follow-up).</para></summary>
    internal const int MaxEchoedListItems = 16;

    /// <summary>The directory separators recognized when parsing a table-relative object path into its
    /// Hive segments. Both are accepted because a poisoned <c>add.path</c> can use either.</summary>
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    /// Bounds and neutralizes an untrusted token before it is interpolated into a diagnostic message: caps
    /// the length (appending an ellipsis when truncated) and replaces every control character with U+FFFD, so
    /// a poisoned value cannot inject newlines/control sequences into a log line or render an unbounded string.
    /// <para>Forwards to the shared <c>DeltaSharp.Abstractions</c> primitive
    /// (<c>DeltaSharp.Diagnostics.DiagnosticText</c>) so the Storage config-value surfaces and the
    /// <c>DeltaSharp.Core</c> SQL-parser diagnostics (#687) sanitize <b>identically</b> — Core cannot reference
    /// Storage (wrong layering direction), so the one implementation lives in the assembly both reference.</para>
    /// </summary>
    /// <param name="raw">The token to sanitize. A <see langword="null"/> token renders as the literal
    /// <c>(null)</c> so the message stays well-formed.</param>
    /// <param name="maxLength">The maximum retained length before truncation.</param>
    internal static string Sanitize(string? raw, int maxLength = DefaultMaxLength) =>
        SharedDiagnosticText.Sanitize(raw, maxLength);

    /// <summary>
    /// Sanitizes each token in <paramref name="tokens"/> (per-item bounded via <see cref="Sanitize"/> with
    /// <paramref name="maxItemLength"/>) and joins them with <paramref name="separator"/>, rendering at most
    /// <see cref="MaxEchoedListItems"/> and appending <c>… (+N more)</c> when the list is longer — so an
    /// attacker-supplied LIST (e.g. a foreign table's thousands of forged reader/writer features) cannot flood
    /// a log line even though every element is individually bounded.
    /// </summary>
    internal static string SanitizeAndJoin(IEnumerable<string> tokens, int maxItemLength, string separator = ", ") =>
        SharedDiagnosticText.SanitizeAndJoin(tokens, maxItemLength, MaxEchoedListItems, separator);

    /// <summary>
    /// Renders a table-relative object path for a fault message or log line, keeping the partition
    /// <b>context</b> while dropping every partition <b>value</b>.
    /// <para>DeltaSharp lays data out Hive-style, so a relative path such as
    /// <c>email=alice%40example.com/region=EU/part-DD73….parquet</c> embeds COLUMN VALUES in its directory
    /// segments. Those values are table DATA — potentially PII — and no amount of sanitizing removes them:
    /// <see cref="Sanitize"/> strips control characters and caps length, but an email address is neither a
    /// control character nor longer than the cap, so it would survive verbatim (and
    /// <c>Uri.UnescapeDataString</c> recovers the original from the percent-encoding). Data-derived content is
    /// therefore <b>dropped</b>, never sanitized.</para>
    /// <para>Dropping the whole path would cost the operator the context they need, so this renders the file
    /// name plus the partition COLUMN NAMES parsed from the <c>key=value</c> segments — e.g.
    /// <c>'part-DD73….parquet' (partitioned by: email, region)</c>. Column names are the foreign-schema-name
    /// class, so they are sanitized and count-bounded; the file name is foreign text, so it is sanitized too.
    /// The full raw path stays available on a typed property a table owner can read and route deliberately.
    /// </para>
    /// <para>Non-partition intermediate directories are deliberately omitted rather than echoed: on a
    /// foreign/hostile table an arbitrary layout could carry data-derived text in a segment that does not look
    /// like <c>key=value</c> — a customer name, a tenant, a case number — and none of that is a control
    /// character or over the length cap, so sanitizing would leave it intact.</para>
    /// <para>Omitting them outright, however, would erase the <b>shape</b> of the path, and on a confinement
    /// rejection the shape IS the diagnostic: <c>../../../../etc/passwd</c>, <c>/etc/passwd</c> and an
    /// innocent in-root <c>passwd</c> would all render <c>'passwd'</c>. So the structural facts are kept
    /// alongside the omission — whether the path was rooted, how many parent traversals it used, how many
    /// directories were dropped, and whether it sits under a DeltaSharp-generated control directory. Every one
    /// of those is a count or a fixed literal; a directory count may reflect the number of separator characters in a withheld name (a weak cardinality signal, not the name itself — see the #719 decision block), and fixed literals are DeltaSharp-authored. A caller therefore does NOT
    /// need a second, shape-preserving renderer for control paths: a poisoned <c>add.path</c> such as
    /// <c>email=alice%40example.com/../../../etc/passwd</c> reaches the confinement guard WITH a partition
    /// value attached, so a "control paths may echo raw" branch would reopen the disclosure this method
    /// exists to close.</para>
    /// <para><b>The returned string is already QUOTED</b> (<c>'name.parquet'</c>). It replaced call sites that
    /// wrote <c>'{path}'</c> themselves, and it must own the quoting so a partition-directory path can render
    /// as <c>'(directory)'</c> rather than as an empty pair of quotes. Do not wrap the result again.</para>
    /// <para><b>Accepted residuals (Privacy seat, #686):</b> (1) a raw <c>/</c> INSIDE a partition value is
    /// treated as a component boundary (not part of the value), because on a real store it genuinely is one —
    /// so a foreign, non-canonical path <c>name=a/b</c> renders <c>'b' (partitioned by: name)</c>. This is the
    /// safe direction for a Hive path and is unreachable for DeltaSharp-written paths (the write door
    /// <c>Uri.EscapeDataString</c>s the value to <c>%2F</c>); the opposite <c>\</c> ambiguity is resolved the
    /// other way (never toward disclosure) because a backslash is only a separator on some platforms. (2) The
    /// TERMINAL segment is echoed as the file name; a foreign writer that names a file by a data subject
    /// (rather than <c>part-*.parquet</c>) could surface that name — bounded and sanitized, but not dropped.
    /// Both are documented residuals, not the value-in-a-partition-segment disclosure this method closes.</para>
    /// </summary>
    internal static PathDescription DescribePath(string? path)
    {
        string described = DescribePathCore(path);
#pragma warning disable RS0030 // #700/#696: DescribePath is the ONLY sanctioned PathDescription producer (BannedSymbols ctor ban).
        return new PathDescription(described);
#pragma warning restore RS0030
    }

    // #700: the body is unchanged; only DescribePath's PUBLIC signature moved to PathDescription. Keeping the
    // renderer as a string-returning core (rather than wrapping every return) preserves this method's surface
    // byte-for-byte, which minimizes the merge-conflict blast radius with the concurrent SanitizeAndJoin work
    // on the sibling Abstractions primitive.
    private static string DescribePathCore(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(null)";
        }

        // Scanned as SPANS, never Split into an array. Split materializes every segment before anything is
        // capped, so a 100,000-deep crafted path allocated ~9.5 MB to render a 93-char result (~24x
        // amplification) — an allocation-side flood that the OUTPUT bound does not close. At most
        // MaxEchoedListItems keys are ever rendered, so at most that many are ever materialized; the rest are
        // only COUNTED, which is all the "… (+N more)" elision needs.
        ReadOnlySpan<char> remaining = path;
        var partitionColumns = new List<string>(Math.Min(MaxEchoedListItems, 8));
        int partitionColumnCount = 0;
        int traversals = 0;
        int droppedDirectories = 0;
        string? controlDirectory = null;
        ReadOnlySpan<char> lastSegment = default;
        bool haveSegment = false;

        // A BACKSLASH CANNOT BE RELIED ON TO END A SEGMENT, so it cannot be relied on to START a file name.
        // On POSIX `\` is an ordinary filename character, so `email=al\ice.taylor@example.com` is ONE
        // component whose VALUE contains a backslash -- and splitting on it put the tail of that value in
        // the terminal position, where the default is to echo it as the file name. That is the same
        // platform guess LocalFileSystemBackend.Redact resolves in its value class, resolved there toward
        // over-redaction and resolved here toward disclosure.
        //
        // The rule, and it is narrower than banning `\` outright: once a Hive separator has appeared in a
        // BACKSLASH-DELIMITED RUN, everything after it in that run is value content and must not be echoed
        // as a name. A `/` ends the run, because `/` really does separate components everywhere. This
        // keeps traversal counting and directory dropping working on Windows-shaped paths -- those are
        // suspicions and omissions, where over-reporting is safe -- while refusing to NAME anything whose
        // segment boundary depends on which platform produced the path.
        bool hiveInBackslashRun = false;

        // The latch above stops a backslash run's TERMINAL segment being echoed as a file name. That was
        // only half the leak: every segment in the run is also offered to ClassifyDirectory, which harvests
        // `key=` prefixes into the partition-column list. A synthetic sub-key inside a value is therefore
        // still echoed -- as a column NAME rather than a file name, which is not an improvement, since the
        // text is value content either way. This carries the latch's value as of BEFORE the current
        // segment, which is what "was this segment already inside a value?" needs; the latch itself has
        // been updated to include the segment by the time the segment is classified.
        bool lastSegmentInsideHiveRun = false;

        // DECISION (#719, Option 3 — ACCEPTED AND DOCUMENTED): the directory COUNT is derived from both
        // separators, so `a\b\c\email=v` reports "3 directories omitted" where a POSIX reader sees one
        // component and no directories at all. Both readings misinform — counting only `/` reports "0 omitted"
        // while three names were in fact dropped on Windows — but neither reading discloses a partition VALUE
        // or any directory NAME: the count is a bounded
        // scalar derived from the number of separator characters, which leaks only a weak character-frequency
        // oracle over the withheld names — not the names themselves. Accepted. The safe direction for a
        // non-disclosing claim is OVER-REPORT (more directories omitted than truly were), which is exactly
        // what counting `\` produces on a POSIX-produced path. Counting only `/` would UNDER-REPORT on a
        // Windows-produced path, hiding traversal depth.
        //
        // Option 1 (count only `/`) is probably the correct LONG-TERM choice but requires re-pinning the
        // traversal-detection logic (which is security-relevant and gates `..` detection) before the
        // separator alphabet can be narrowed without risk of silently under-reporting a traversal. That
        // change belongs in its own focused PR, not as a side-effect of a message-hygiene edit. Until it
        // lands, this comment IS the resolution: the choice is deliberate (over-report, non-disclosing),
        // documented, and coupled to traversal detection.

        // A leading separator (POSIX absolute) or a drive/UNC prefix (Windows) means the path was never
        // table-relative. That is a fact about the REQUEST, not about the table's data, so it is safe to keep
        // and it is exactly what distinguishes an absolute-rooting attempt from an innocent relative name.
        // Recognized WITHOUT consulting the running OS: a Windows-shaped absolute path is just as much an
        // escape attempt when it arrives at a POSIX backend, and Path.IsPathFullyQualified would call it
        // relative there and hide the fact.
        bool rooted = path[0] == '/'
            || path[0] == '\\'
            || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));

        while (true)
        {
            int sep = remaining.IndexOfAny(PathSeparators);
            ReadOnlySpan<char> segment = sep < 0 ? remaining : remaining[..sep];

            if (!segment.IsEmpty)
            {
                // The PREVIOUS segment is now known to be an intermediate directory, so classify it.
                if (haveSegment)
                {
                    ClassifyDirectory(
                        lastSegment,
                        lastSegmentInsideHiveRun,
                        partitionColumns,
                        ref partitionColumnCount,
                        ref traversals,
                        ref droppedDirectories,
                        ref controlDirectory);
                }

                lastSegment = segment;
                lastSegmentInsideHiveRun = hiveInBackslashRun;
                haveSegment = true;
            }

            if (sep < 0)
            {
                break;
            }

            if (remaining[sep] == '/')
            {
                hiveInBackslashRun = false;
            }
            else if (!segment.IsEmpty)
            {
                hiveInBackslashRun = hiveInBackslashRun || HiveSeparatorIndex(segment, out _) >= 0;
            }

            remaining = remaining[(sep + 1)..];
        }

        if (!haveSegment)
        {
            // Separators only ("///////"). Distinct from a null/empty path: the caller DID supply something,
            // it just contained no nameable segment. Rendering it as "(null)" made a real (if odd) request
            // indistinguishable from a missing one, which is a diagnosability loss with no privacy benefit —
            // "no segments" carries no data-derived content either.
            return "(no segments)";
        }

        // The TERMINAL segment gets the same key=value test as every other one. A path can legitimately end at
        // a partition DIRECTORY (a listing prefix, or simply a poisoned add.path), and rendering that segment
        // as "the file name" would push the partition VALUE straight through Sanitize — which, by the very
        // argument this helper exists for, cannot remove an email address. The guard belongs in the helper,
        // not in caller discipline.
        //
        // `>= 0`, not `> 0`: an EMPTY Hive key ("=<value>") must still be recognized. This is the sibling of
        // the empty-key fail-open closed in LocalFileSystemBackend's Redact recognizer, and it is the more
        // dangerous half — an unrecognized INTERIOR segment is dropped, so declining to match fails closed
        // there, but the TERMINAL default is to ECHO as a file name, so the identical non-recognition fails
        // OPEN. Same rule, and the reason it is stated as a rule: a hygiene recognizer must fail CLOSED,
        // because any shape it declines to match is a redaction the attacker opted out of by choosing it.
        // Blast radius of the wider test is nil: `> 0` already rendered any name containing '=' past
        // position 0 as a directory, so `>= 0` additionally captures only names STARTING with '=', and
        // DeltaSharp writes `part-<guid>.parquet`.
        bool terminalCarriesHive = HiveSeparatorIndex(lastSegment, out _) >= 0;

        // `|| lastSegmentInsideHiveRun` is the fail-closed half: the terminal segment carries no Hive
        // separator of its own, but it sits after a backslash inside a run that already had one, so under
        // the POSIX reading it is the TAIL OF A PARTITION VALUE rather than a file name. Render it as the
        // directory it may well be, and never as a name.
        //
        // NOT `hiveInBackslashRun`: the live latch describes the END OF THE STRING, but this decision is
        // about the TERMINAL SEGMENT. A trailing `/` clears the live latch after the segment has already
        // been recorded, so consulting it here would lose suppression on
        // `email=x\alice.taylor%40example.com/`. `lastSegmentInsideHiveRun` is the latch state BEFORE the
        // terminal segment, which is exactly the state needed for both file-name and partition-key
        // suppression here. It strictly dominates the live latch at this point: once the terminal
        // segment is recorded, a following `/` can only CLEAR the live latch, never set it — so
        // reading the live latch could only ever LOSE suppression, which is the definition of a
        // fail-open read.
        bool terminalIsPartitionDirectory = terminalCarriesHive || lastSegmentInsideHiveRun;
        if (terminalCarriesHive && !lastSegmentInsideHiveRun)
        {
            CollectPartitionKey(lastSegment, partitionColumns, ref partitionColumnCount);
        }
        else if (lastSegment.SequenceEqual(".."))
        {
            // A path ending in ".." names a directory, not a file; count it with the other traversals rather
            // than rendering ".." as "the file name".
            traversals++;
            terminalIsPartitionDirectory = true;
        }

        string fileName = terminalIsPartitionDirectory
            ? "(directory)"
            : Sanitize(Truncated(lastSegment), DefaultMaxLength);

        var facts = new List<string>(4);
        if (rooted)
        {
            facts.Add("rooted");
        }

        if (traversals > 0)
        {
            facts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{traversals} parent traversal{(traversals == 1 ? string.Empty : "s")}"));
        }

        if (controlDirectory is not null)
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"under {controlDirectory}"));
        }

        if (droppedDirectories > 0)
        {
            facts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{droppedDirectories} director{(droppedDirectories == 1 ? "y" : "ies")} omitted"));
        }

        if (partitionColumnCount > 0)
        {
            var columns = new StringBuilder("partitioned by: ");
            for (int i = 0; i < partitionColumns.Count; i++)
            {
                if (i > 0)
                {
                    columns.Append(", ");
                }

                columns.Append(partitionColumns[i]);
            }

            if (partitionColumnCount > partitionColumns.Count)
            {
                columns.Append(CultureInfo.InvariantCulture, $", … (+{partitionColumnCount - partitionColumns.Count} more)");
            }

            facts.Add(columns.ToString());
        }

        if (facts.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"'{fileName}'");
        }

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"'{fileName}' (");
        for (int i = 0; i < facts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("; ");
            }

            builder.Append(facts[i]);
        }

        return builder.Append(')').ToString();
    }

    // The DeltaSharp-generated control directories. These are FIXED LITERALS emitted by this library, never
    // operator- or data-derived, so echoing one verbatim cannot disclose table content — and knowing that a
    // missing object was a LOG object rather than a data file is most of the diagnosis. Any other
    // non-partition directory is counted, not named.
    private static readonly string[] ControlDirectories = ["_delta_log", "_change_data", "_commits"];

    /// <summary>
    /// THE single answer to "is this path segment a Hive <c>key=value</c> pair, and where does the key end?"
    /// Returns the index at which the separator begins, or <c>-1</c>; <paramref name="separatorLength"/>
    /// receives its width so the caller can slice the key and the value without re-deriving the spelling.
    /// </summary>
    /// <remarks><para><b>Why this exists as a shared predicate rather than three <c>IndexOf('=')</c> calls and a
    /// regex.</b> DeltaSharp has TWO recognizers that independently answer this question — this one, which
    /// drops the partition VALUE from a described path, and <c>LocalFileSystemBackend.Redact</c>, which
    /// strips it out of an echoed framework message. They must consult one predicate and the shared
    /// <see cref="HiveSeparatorPattern"/> so description and redaction cannot drift.</para>
    /// <para><b>Recognized spellings.</b> A literal <c>=</c>, and its percent-encoding <c>%3D</c> in either
    /// case. A foreign writer may percent-encode the separator, and a segment whose separator arrived that
    /// way is still a Hive partition directory — declining it echoes the value, which is the fail-open class
    /// this whole family of guards exists to close.</para>
    /// <para><b>Archaeology note.</b> Three recognizer spellings were fixed in <c>Redact</c>
    /// and missed here in successive rounds — the empty key, the <c>IndexOf('=') &gt;= 0</c>
    /// off-by-one, and the percent-encoded separator. The shared predicate closes the class:
    /// any future spelling is added once and is covered in both recognizers by construction.</para>
    /// </remarks>
    internal static int HiveSeparatorIndex(ReadOnlySpan<char> segment, out int separatorLength)
    {
        for (int i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '=')
            {
                separatorLength = 1;
                return i;
            }

            if (segment[i] == '%'
                && i + 2 < segment.Length
                && segment[i + 1] == '3'
                && (segment[i + 2] == 'D' || segment[i + 2] == 'd'))
            {
                separatorLength = 3;
                return i;
            }
        }

        separatorLength = 0;
        return -1;
    }

    /// <summary>
    /// The regex half of <see cref="HiveSeparatorIndex"/>: the same set of separator spellings, expressed as
    /// a non-capturing group so it can be concatenated into a <c>[GeneratedRegex]</c> pattern (attribute
    /// arguments admit constant expressions, so this is genuinely shared rather than duplicated text).
    /// Keep in lockstep with <see cref="HiveSeparatorIndex"/>.
    /// </summary>
    internal const string HiveSeparatorPattern = "(?:=|%3[Dd])";

    // Classifies ONE intermediate directory segment: a Hive `key=value` pair contributes its key, ".."
    // contributes a traversal, a known control directory contributes its (safe, literal) name, and anything
    // else is counted and dropped.
    private static void ClassifyDirectory(
        ReadOnlySpan<char> segment,
        bool insideHiveRun,
        List<string> partitionColumns,
        ref int partitionColumnCount,
        ref int traversals,
        ref int droppedDirectories,
        ref string? controlDirectory)
    {
        // #683: `>= 0`, not `> 0`, and the test itself lives in HiveSeparatorIndex. An empty Hive key
        // ("=value") is still a Hive directory, and the guard must fail CLOSED — declining to recognize the
        // shape here would fall through to droppedDirectories, which is safe by luck, while the identical
        // non-recognition at the TERMINAL segment fell through to "echo as a file name" and leaked.
        if (!insideHiveRun && HiveSeparatorIndex(segment, out _) >= 0)
        {
            CollectPartitionKey(segment, partitionColumns, ref partitionColumnCount);
            return;
        }

        if (insideHiveRun)
        {
            // Inside a value, so the segment names nothing. It still COUNTS: the omission total may
            // over-report a directory that was never a directory, which discloses no content, whereas
            // naming it would. #719 tracks the over-count.
            droppedDirectories++;
            return;
        }

        if (segment.SequenceEqual("."))
        {
            return;
        }

        if (segment.SequenceEqual(".."))
        {
            traversals++;
            return;
        }

        foreach (string known in ControlDirectories)
        {
            if (segment.SequenceEqual(known))
            {
                controlDirectory ??= known;
                return;
            }
        }

        droppedDirectories++;
    }

    // Harvests the partition COLUMN NAME from a `key=value` segment, discarding the value. Only the first
    // MaxEchoedListItems keys are materialized; the remainder are counted so the elision marker stays honest.
    private static void CollectPartitionKey(
        ReadOnlySpan<char> segment, List<string> collected, ref int total)
    {
        int equals = HiveSeparatorIndex(segment, out _);
        if (equals < 0)
        {
            return;
        }

        total++;
        if (collected.Count < MaxEchoedListItems)
        {
            // #683: an EMPTY key still counts, and it contributes a fixed literal rather than nothing.
            // Returning early on `equals == 0` (the old `<= 0` guard) would have made the segment vanish
            // from every tally — neither a partition nor a dropped directory — and would have desynchronized
            // `total` from `collected`, so the "… (+N more)" elision marker would have lied.
            collected.Add(equals == 0
                ? "(empty)"
                : Sanitize(Truncated(segment[..equals]), DefaultMaxLength));
        }
    }

    // Materializes at most DefaultMaxLength + 1 characters. Sanitize truncates to DefaultMaxLength and appends
    // an ellipsis, so keeping one character beyond the cap preserves its "was this truncated?" decision while
    // bounding the intermediate ALLOCATION as well as the rendered output.
    private static string Truncated(ReadOnlySpan<char> value) =>
        value.Length > DefaultMaxLength + 1 ? value[..(DefaultMaxLength + 1)].ToString() : value.ToString();

    /// <summary>
    /// Renders a <see cref="StructType"/> for a diagnostic message as a BOUNDED description — the column
    /// count plus the first <see cref="MaxEchoedListItems"/> sanitized field names — rather than
    /// <c>DataType.SimpleString</c>.
    /// <para><c>StructType.SimpleString</c> is not a bounded type name: it appends every field name
    /// <b>verbatim</b> and recurses into nested types, so a 5,000-column write schema renders ~129,000
    /// characters carrying every attacker-authored name raw. That is simultaneously a log-injection echo and a
    /// log-flooding aggregate. The column COUNT plus a bounded sample of names is also strictly more useful to
    /// an operator than a 129 KB type string they cannot read.</para>
    /// </summary>
    internal static string DescribeSchema(StructType? schema)
    {
        if (schema is null)
        {
            return "(null)";
        }

        var names = new List<string>(Math.Min(MaxEchoedListItems, schema.Count));
        for (int i = 0; i < schema.Count && i < MaxEchoedListItems; i++)
        {
            names.Add(schema[i].Name);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"struct with {schema.Count} column(s): [{SanitizeAndJoinCounted(names, schema.Count)}]");
    }

    /// <summary>
    /// Renders an ARBITRARY <see cref="DataType"/> for a diagnostic message with a guaranteed bound, for the
    /// sites where the type's KIND is not known statically — typically a comparison between a
    /// metadata-declared type (foreign, any kind) and a file-derived one.
    /// <para>Only <see cref="AtomicType"/> and <see cref="DecimalType"/> render via <c>SimpleString</c>,
    /// because for those it is a fixed literal (<c>string</c>) or an integer-parameterized one
    /// (<c>decimal(10,2)</c>) — and that precision/scale detail is exactly what makes a decimal mismatch
    /// diagnosable. A <see cref="StructType"/> routes to <see cref="DescribeSchema"/>; an
    /// <see cref="ArrayType"/>/<see cref="MapType"/> renders only its bounded <see cref="DataType.TypeName"/>,
    /// since their <c>SimpleString</c> recurses into element/key/value types and would re-open the same
    /// unbounded, name-carrying render. The <c>_</c> arm cannot be reached today (<see cref="DataType"/>'s
    /// constructor is <c>private protected</c>, so the five kinds above are exhaustive) but is bounded rather
    /// than falling back to <c>SimpleString</c>, so a future kind is safe by default instead of by review.
    /// </para>
    /// </summary>
    internal static string DescribeType(DataType? type) => type switch
    {
        null => "(null)",
        StructType schema => DescribeSchema(schema),
        AtomicType or DecimalType => type.SimpleString,
        _ => type.TypeName,
    };

    // SanitizeAndJoin elides against the LIST it is given; here the list is pre-truncated (so the full schema
    // is never materialized), so the elision count comes from the real total instead.
    private static string SanitizeAndJoinCounted(IReadOnlyList<string> shown, int total)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < shown.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Sanitize(shown[i], DefaultMaxLength));
        }

        if (total > shown.Count)
        {
            builder.Append(CultureInfo.InvariantCulture, $", … (+{total - shown.Count} more)");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders <paramref name="exception"/> as <c>{TypeName}: {Message}</c> (optionally followed by
    /// <c>(Kind: {kind})</c>) plus its OWN stack trace, deliberately <b>omitting the
    /// <see cref="Exception.InnerException"/> chain</b>. DeltaSharp storage decode/validation paths are
    /// intended to keep raw decoder text out of <see cref="Exception.Message"/> and route
    /// attacker-influenceable tokens through message hygiene where implemented, while retaining the raw
    /// underlying cause as the inner for server-side diagnostics. Treat every storage message as untrusted;
    /// known unsanitized producers are scoped in
    /// <c>docs/engineering/design/storage-exception-log-routing.md</c> and tracked by #747/#749. The
    /// default <see cref="Exception.ToString"/> would re-surface that raw inner (as would the default
    /// <c>ILogger.LogError(ex, …)</c> providers, which render <c>ToString()</c>), re-leaking exactly what
    /// <see cref="Exception.Message"/> dropped. This override closes the
    /// <c>ToString()</c>/<c>ILogger</c>-<b>rendering</b> vector (#664): the inner stays attached (reachable via
    /// <see cref="Exception.InnerException"/>) for a debugger / deliberate server-side read, but is never
    /// auto-rendered — and because <see cref="Exception.ToString"/> recurses into an inner via the inner's own
    /// (overridden) <c>ToString()</c>, a covered exception nested inside an outer exception or an
    /// <see cref="AggregateException"/> is suppressed transitively.
    /// <para><b>Residual (by design).</b> This is <c>ToString()</c>-rendering parity with RF-8b, NOT the full
    /// RF-8b treatment: unlike <c>LocalFileSystemBackend.SurfaceFailure</c> (which replaces the raw framework
    /// exception object with a <i>synthetic, root-redacted</i> inner whose message is now also
    /// partition-value-redacted and line-break-sanitized — the untrusted residual there is the raw typed
    /// properties and reflection reach, #749), these types <b>retain the raw inner object</b>. The
    /// suppression works because <see cref="Exception.ToString"/> delegates the chain walk to the inner's own
    /// virtual <c>ToString()</c>, so it protects only a sink that lets <c>Exception.ToString()</c> do that
    /// walk. A sink that enumerates <see cref="Exception.InnerException"/> <b>itself</b> — measured on NLog's
    /// <c>${exception:maxInnerExceptionLevel=…}</c> and on Application Insights' <c>ExceptionDetails</c> list,
    /// both of which re-render the raw cause even while calling this override per level — or that serializes
    /// the exception <i>object graph</i> by reflection (a Serilog exception destructurer, <c>{@Ex}</c>,
    /// <c>JsonSerializer.Serialize(ex)</c>) still re-surfaces it, and reflection additionally reaches the raw
    /// typed properties. That is a sink-side encode-on-write concern: a tenant-visible sink MUST render
    /// <c>.Message</c>/<c>.ToString()</c> and MUST NOT walk <see cref="Exception.InnerException"/> or reflect
    /// over the object graph. See <c>docs/engineering/design/storage-exception-log-routing.md</c> for the
    /// measured per-sink matrix. The storage assembly's exact source-generated log-site signatures are pinned
    /// by <c>StorageExceptionToStringTests</c> and accept no <see cref="Exception"/> object.</para>
    /// </summary>
    internal static string DescribeWithoutInner(Exception exception, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder();
        builder.Append(exception.GetType().ToString()).Append(": ").Append(exception.Message);
        if (kind is not null)
        {
            builder.Append(" (Kind: ").Append(kind).Append(')');
        }

        if (exception.StackTrace is { } stackTrace)
        {
            builder.Append(Environment.NewLine).Append(stackTrace);
        }

        return builder.ToString();
    }
}
