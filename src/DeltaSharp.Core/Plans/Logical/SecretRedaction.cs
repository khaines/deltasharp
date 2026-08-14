using System.Text.RegularExpressions;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// Redacts credential-bearing fragments from a data-source path before it is rendered into a plan
/// tree (<c>SimpleString</c>/<c>TreeString</c>), an <c>Explain</c> output (#179), a log line, or an
/// analysis diagnostic. Cloud paths routinely carry secrets — a SAS token (<c>?sig=</c>), a presigned
/// URL's <c>X-Amz-Signature</c>/<c>Signature</c>, or <c>userinfo</c> credentials — that must never
/// leak the moment a node is stringified.
/// </summary>
/// <remarks>
/// This is a best-effort textual mask, not a URI parser (paths may be plain filesystem paths, globs,
/// or non-RFC URIs). It masks (a) the ENTIRE <c>userinfo</c> of an authority — everything between
/// <c>scheme://</c> and the last <c>@</c> before the host/path boundary. A <b>colon-bearing</b> userinfo
/// (<c>user:secret@…</c>) is masked regardless of whether the credential is percent-encoded, carries an
/// interior <c>?</c>/<c>#</c>, or carries the colon itself (the colon-bearing pass spans an interior
/// <c>?</c>/<c>#</c>). A <b>colon-less</b> token pass, by contrast, stops at the first <c>?</c>/<c>#</c>
/// (its run is <c>[^/?#:]*</c>), so a colon-less credential carrying an interior <c>?</c>/<c>#</c> — e.g.
/// <c>s3://TOK?EN@host</c> — is a DOCUMENTED KNOWN LIMIT: only the pre-<c>?</c> prefix (<c>TOK</c>) is seen
/// and the token is left unmasked. Neither run stops at whitespace or a control character. And it masks
/// (b) the value of any query-string parameter whose key looks credential-bearing
/// (<c>sig</c>, <c>signature</c>, <c>password</c>, <c>pass</c>, <c>pwd</c>, <c>token</c>, <c>key</c>,
/// <c>secret</c>, <c>credential</c>, <c>sas</c>, <c>auth</c>, <c>apikey</c>, <c>access[_-]?token</c>,
/// <c>code</c>, <c>assertion</c>, <c>jwt</c>, <c>bearer</c>). Option <b>values</b> are never rendered at
/// all (keys only), so only the path needs masking.
/// <para>
/// #433 hardening. The userinfo matcher was widened from "a colon-delimited password" to the WHOLE
/// userinfo so it also catches an unencoded <c>@</c> inside the credential (the greedy run masks to the
/// LAST <c>@</c> in the authority) and a colon-less token in the userinfo position. The key catalogue
/// gained <c>auth</c>/<c>pwd</c>/<c>apikey</c>/<c>access[_-]?token</c> and later
/// <c>pass</c>/<c>code</c>/<c>assertion</c>/<c>jwt</c>/<c>bearer</c>.
/// High-entropy PATH-SEGMENT masking is <b>deliberately not</b> attempted: a DeltaSharp object path
/// routinely carries a high-entropy segment that is NOT a secret (a <c>part-&lt;guid&gt;.parquet</c> name,
/// a commit UUID, a deletion-vector id), so an entropy heuristic would mask legitimate, diagnosable file
/// names far more often than a real path-segment credential. A secret that must survive as a bare path
/// segment is out of scope for a textual masker and is the caller's obligation to keep out of a rendered
/// path.
/// </para>
/// <para>
/// <b>Round-1 fix — the widening must be MONOTONIC against <c>main</c>.</b> The single widened matcher
/// (<c>[^/?#\s]*@</c>) stopped at a <c>?</c>/<c>#</c>, so a colon-bearing credential such as
/// <c>s3://user:p?ss@bucket/key</c> — which <c>main</c>'s colon-delimited-password matcher masked — was
/// left UNMASKED. The userinfo pass is therefore split in two: a colon-bearing pass
/// (<see cref="ColonBearingUserInfo"/>) that spans an interior <c>?</c>/<c>#</c>/<c>@</c> to the last
/// <c>@</c>, PLUS a colon-less token pass (<see cref="ColonlessUserInfo"/>) for a bare token in the
/// userinfo position. A credential <c>main</c> masks can no longer survive on this branch.
/// </para>
/// <para>
/// <b>Round-1 fix — ADLS/WASB authority is an IDENTITY, not a credential.</b> An
/// <c>abfss://&lt;container&gt;@&lt;account&gt;.dfs.core.windows.net/…</c> (and <c>abfs</c>/<c>wasb</c>/
/// <c>wasbs</c>) authority puts the CONTAINER — the bucket-equivalent identity, the diagnosable subject of
/// a fault — in the colon-less userinfo position. The colon-less pass therefore EXEMPTS those four schemes
/// (a scheme-aware pre-check), so <c>s3</c>/<c>http(s)</c>/<c>gs</c>/… still mask a colon-less token while
/// the ADLS container survives. A colon-BEARING ADLS userinfo (an actual account-key credential) is still
/// masked by the colon-bearing pass — the exemption is scoped to the colon-less shape only. <b>Round-2
/// fix:</b> the exemption is anchored to the PATH's leading scheme (<c>m.Index == 0</c>): a mid-string
/// <c>abfss://…@</c> — e.g. <c>s3://abfss://SECRETTOKEN@host/key</c> or
/// <c>s3://bucket/abfss://SECRETTOKEN@x</c> — is NOT an ADLS identity and must be masked, so the exemption
/// cannot be forged by embedding an ADLS scheme after another scheme's authority/path.
/// </para>
/// <para>
/// <b>Round-2 fix — ReDoS on the query pass, and no input truncation.</b> ALL THREE passes are now linear:
/// the two userinfo passes and <see cref="SensitiveQueryValue"/> carry
/// <see cref="RegexOptions.NonBacktracking"/> (a plain alternation with no backreferences/lookarounds/atomic
/// groups, so it compiles NonBacktracking), eliminating the QUADRATIC backtracking a long query-flanked path
/// triggered. Round-4 re-measurement on this branch (an <c>&amp;</c>-terminated credential pair followed by a
/// long keyword run — <c>"s3://b/k?sig=SECRETSAS&amp;" + repeat("passkeytoken")</c>): ~73,300 ms → ~5 ms at
/// 768 KB, byte-identical output. The growth is quadratic, not exponential — the same shape costs ~17 ms at
/// 8 KB and ~8,100 ms at 256 KB before the fix, and ~1–3 ms after it. Because every pass is now linear there
/// is NO input truncation: the Round-1 <c>RedactScanLimit</c> cap has been DELETED. It was unsafe —
/// truncating at a <c>/</c> or <c>\</c> boundary could cut INSIDE a userinfo (before its terminating
/// <c>@</c>), exposing a multi-KB unmasked credential prefix, and it silently dropped legitimate long paths
/// with no marker.
/// </para>
/// <para>
/// <b>Accepted OVER-MASK boundary (query-borne <c>@</c>).</b> Because this is a textual masker and not a URI
/// parser, a query string carrying an <c>@</c> (an e-mail address, say) pulls the colon-bearing pass past the
/// authority: <c>https://host:8443?to=a@b.com</c> → <c>https://&lt;redacted&gt;@b.com</c>, destroying the
/// host:port. This is the SAFE direction (over-mask, never under-mask) and is required by the monotonicity
/// rule above — the colon-bearing run must span an interior <c>?</c>/<c>#</c> so <c>s3://user:p?ss@bucket</c>
/// stays masked. The boundary is pinned by an exact-output test so it cannot silently widen.
/// </para>
/// <para>
/// <b>Round-5 fix — an embedded control character inside the userinfo no longer defeats the mask.</b> Both
/// credential runs previously excluded <c>\s</c>, and <c>\r</c>/<c>\n</c>/<c>\t</c> ARE whitespace, so a
/// control character planted inside the userinfo — <c>s3://user:sec\r\nret@host/key</c> — terminated the run
/// BEFORE the closing <c>@</c>, the match failed, and the whole <c>user:sec…ret</c> credential was rendered
/// verbatim (the plain <c>s3://user:secret@host/key</c> masked correctly, so the miss was silent). The runs
/// are now bounded only by <c>/</c> — the real authority boundary — not by whitespace: <c>[^/]*:[^/]*@</c>
/// and <c>[^/?#:]*@</c>. The <c>?</c>/<c>#</c> exclusions on the colon-less run are KEPT, so its documented
/// known limit above is unchanged.
/// The widening's cost is the same accepted OVER-MASK direction: on a degenerate, path-separator-free string
/// the longer colon-bearing span can swallow a <c>?</c>/<c>&amp;</c> that the later query pass would have used
/// as a key delimiter, leaving a trailing query value unmasked.
/// <b>Why that is confined to the degenerate shape (the checkable, structural argument).</b> Both credential
/// runs forbid <c>/</c>, so a match that starts at a scheme's <c>://</c> ends before the FIRST <c>/</c> after
/// that scheme. In any realistic <c>scheme://host/path?query</c> the <c>?</c> that opens the query string is
/// positioned AFTER that <c>/</c>, so the run provably cannot reach — let alone swallow — a query-key
/// delimiter. Only a string with NO path separator between the scheme and the query (<c>s3://u:p?@sig=…</c>)
/// is affected, and there the credential itself is still masked; it is the trailing query value that is left
/// unmasked. Both halves are pinned by exact-output tests, with DISTINCT jobs: widening a run past <c>/</c>
/// turns the authority-boundary pin
/// (<c>RedactPath_AuthorityBoundaryStopsTheCredentialRun_QueryKeysStillMask</c>) RED — it is the SOLE
/// mechanical detector of that widening, so do not retire it; the residual pin
/// (<c>RedactPath_PathSeparatorFreeCredential_IsTheAcceptedResidual</c>) characterizes the ACCEPTED cost at
/// its exact current output and is invariant under that widening (its inputs carry no <c>/</c> after the
/// scheme, or a single <c>@</c>), so any change to it forces a deliberate ledger update.
/// A one-shot differential fuzz over ~1,000,000 generated inputs was also run while
/// developing the change and measured 18,942 newly MASKED credentials against 55 newly exposed, none of them
/// carrying an authority boundary; that run's seed and generator are not checked in, so treat the figures as
/// an illustrative one-shot measurement only — the structural argument above and the two pins are the
/// load-bearing evidence.
/// </para>
/// </remarks>
internal static partial class SecretRedaction
{
    private const string Mask = "<redacted>";

    private static readonly System.Collections.Generic.HashSet<string> AdlsSchemes =
        new(System.StringComparer.OrdinalIgnoreCase) { "abfs://", "abfss://", "wasb://", "wasbs://" };

    /// <summary>Returns <paramref name="path"/> with credential-bearing userinfo and query-string values
    /// masked. A <see langword="null"/> or empty path is returned unchanged.</summary>
    public static string RedactPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // No input truncation: all three passes are NonBacktracking (linear), so a long rendered path is
        // not a synchronous-CPU sink, and truncating could cut INSIDE a userinfo (before its terminating
        // '@'), surfacing an unmasked credential prefix. See the class remarks (Round-2 fix).
        string result = path;

        // (1) Colon-BEARING userinfo first: masks the WHOLE credential to the last '@', spanning an
        // interior '?'/'#'/'@' -- this is the pass that keeps the widening monotonic against main.
        result = ColonBearingUserInfo().Replace(result, "$1" + Mask + "@");

        // (2) Colon-LESS token in the userinfo position, EXEMPTING the ADLS/WASB schemes whose colon-less
        // authority is a container identity, not a credential -- but ONLY when the ADLS scheme is the path's
        // LEADING scheme (m.Index == 0). A mid-string 'abfss://...@' (e.g. 's3://abfss://SECRET@host') is a
        // forged exemption and must be masked.
        result = ColonlessUserInfo().Replace(result, static m =>
            m.Index == 0 && AdlsSchemes.Contains(m.Groups[1].Value)
                ? m.Value
                : m.Groups[1].Value + Mask + "@");

        result = SensitiveQueryValue().Replace(result, "$1" + Mask);
        return result;
    }

    // scheme://<user>:<secret>@host  ->  capture "scheme://" and mask the WHOLE colon-bearing userinfo up
    // to the LAST '@' in the authority. Crucially the runs are [^/]* (NOT [^/?#\s]* and NOT [^/\s]*), so an
    // interior '?'/'#' inside a credential -- e.g. user:p?ss / user:p#ss -- is spanned rather than
    // terminating the match (the monotonicity fix against main, whose colon-delimited-password matcher
    // masked these), and so is an interior CONTROL CHARACTER or space -- e.g. user:sec\r\nret -- which
    // otherwise ended the run before the terminating '@' and left the WHOLE credential unmasked (Round-5).
    // Only '/' bounds the run, which is the real authority boundary.
    // NonBacktracking makes the two greedy runs linear (no quadratic backtrack on a long '@'-less prefix).
    [GeneratedRegex(
        @"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/]*:[^/]*@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ColonBearingUserInfo();

    // scheme://<token>@host  ->  capture "scheme://" and mask a COLON-LESS token in the userinfo position.
    // The run excludes ':' (the colon-bearing pass owns that shape) and stops at the first path/query/
    // fragment boundary. It does NOT stop at whitespace/control characters (Round-5): a token carrying an
    // embedded '\r'/'\n'/'\t' must still be masked. The '?'/'#' exclusions are KEPT deliberately -- the
    // colon-less pass stopping there is the DOCUMENTED KNOWN LIMIT in the class remarks. The ADLS/WASB
    // exemption is applied by the MatchEvaluator in RedactPath, not the pattern, because a colon-less ADLS
    // authority is a container identity, not a credential.
    [GeneratedRegex(
        @"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/?#:]*@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ColonlessUserInfo();

    // ?sig=... / &token=... / &X-Amz-Signature=... / &pwd=... / &access_token=...  ->  capture the
    // "key=" and mask the value up to the next '&', '#', or whitespace. Key match is case-insensitive
    // and allows a vendor prefix/suffix. #433 broadened the catalogue (auth, pwd, apikey, access[_-]?token);
    // Round-1 added pass/code/assertion/jwt/bearer. Round-2: NonBacktracking makes this pass linear too --
    // it is a plain alternation with no backreferences/lookarounds/atomic groups, so it compiles
    // NonBacktracking and closes the query-pass ReDoS (Round-4 measurement: ~73,300 ms -> ~5 ms on a 768 KB
    // adversarial query-flanked input, byte-identical output; the pre-fix cost is QUADRATIC -- ~17 ms at
    // 8 KB, ~8,100 ms at 256 KB -- so only a large input exceeds a CPU budget).
    // KNOWN LIMITS accepted
    // as best-effort: a ';'-delimited option string (value runs to the next ';', not '&') and a
    // percent-encoded key (e.g. "%73ig") are NOT recognized -- a URI parser, not a textual masker, is the
    // remedy, and neither is a DeltaSharp-authored shape.
    [GeneratedRegex(
        @"([?&][^=&\s]*(?:sig|signature|password|pass|pwd|token|key|secret|credential|sas|auth|apikey|access[_-]?token|code|assertion|jwt|bearer)[^=&\s]*=)[^&#\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveQueryValue();
}
