using System.Diagnostics;
using System.Text.RegularExpressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.ReadDoor;

/// <summary>
/// #433 hardening for <see cref="SecretRedaction"/>, plus the Round-1 regression pins. The userinfo matcher
/// masks the ENTIRE userinfo (unencoded <c>@</c>/<c>:</c>/<c>?</c>/<c>#</c> inside the credential, and a
/// colon-less token in the userinfo position), EXCEPT the ADLS/WASB container identity; the query-key
/// catalogue gained <c>auth</c>/<c>pwd</c>/<c>apikey</c>/<c>access[_-]?token</c>/<c>pass</c>/<c>code</c>/
/// <c>assertion</c>/<c>jwt</c>/<c>bearer</c>. These assertions pin the EXACT redacted output (not mere
/// substring-absence) so "mask to the LAST <c>@</c>" and the partial-residue cases are actually proven.
/// </summary>
public sealed class SecretRedactionTests
{
    [Theory]
    // Colon-delimited password (the original case) — still masked to the whole userinfo.
    [InlineData("s3://user:hunter2@bucket/key", "s3://<redacted>@bucket/key")]
    // Colon-less token in the userinfo position — masked by the widened matcher (#433).
    [InlineData("s3://LONGOPAQUETOKEN@bucket/key", "s3://<redacted>@bucket/key")]
    // Unencoded '@' INSIDE the credential — greedy run masks to the LAST '@' in the authority (#433).
    [InlineData("s3://user:p@ss@bucket/key", "s3://<redacted>@bucket/key")]
    // Unencoded ':' plus token — whole userinfo masked (#433).
    [InlineData("s3://aVeryLong:Secret@host/path", "s3://<redacted>@host/path")]
    // ROUND-1 REGRESSION: a colon-bearing credential carrying '?' — main masked it; this branch must too.
    [InlineData("s3://user:p?ss@bucket/key", "s3://<redacted>@bucket/key")]
    // ROUND-1 REGRESSION: a colon-bearing credential carrying '#' — main masked it; this branch must too.
    [InlineData("s3://user:p#ss@bucket/key", "s3://<redacted>@bucket/key")]
    // A bare token still masked for a non-ADLS scheme (the exemption is scoped to ADLS only).
    [InlineData("https://TOKEN@host/path", "https://<redacted>@host/path")]
    // ACCEPTED OVER-MASK boundary (Round-4 pin): a query-borne '@' (an e-mail address) pulls the
    // colon-bearing pass past the authority, so host:port is destroyed. That is the SAFE direction and is
    // required by the monotonicity rule (the colon-bearing run must span an interior '?'). Pinned to exact
    // output so the over-mask cannot silently WIDEN without a deliberate golden update.
    [InlineData("https://host:8443?to=a@b.com", "https://<redacted>@b.com")]
    public void RedactPath_MasksEntireUserInfo_ExactOutput(string path, string expected)
    {
        Assert.Equal(expected, SecretRedaction.RedactPath(path));
    }

    [Theory]
    // DOCUMENTED KNOWN LIMIT (class remarks): the colon-LESS userinfo run is `[^/?#\s:]*`, so it stops at an
    // interior '?'/'#' and a colon-less credential carrying one is left UNMASKED. This characterization row
    // pins CURRENT behaviour — widening it later must be a deliberate golden update, not a silent drift.
    [InlineData("s3://TOK?EN@host/key")]
    [InlineData("s3://TOK#EN@host/key")]
    public void RedactPath_ColonlessUserInfoWithInteriorQueryOrFragment_IsLeftUnmasked_KnownLimit(string path)
    {
        Assert.Equal(path, SecretRedaction.RedactPath(path));
    }

    [Theory]
    // ROUND-1: the ADLS/WASB `container@account` authority is a bucket-equivalent IDENTITY, not a
    // credential — the container survives verbatim so the fault stays diagnosable.
    [InlineData("abfss://mycontainer@acct.dfs.core.windows.net/tbl/part-0.parquet")]
    [InlineData("abfs://c@acct.dfs.core.windows.net/tbl")]
    [InlineData("wasbs://mycontainer@acct.blob.core.windows.net/x")]
    [InlineData("wasb://c@acct.blob.core.windows.net/x")]
    public void RedactPath_LeavesAdlsContainerIdentityIntact(string path)
    {
        Assert.Equal(path, SecretRedaction.RedactPath(path));
    }

    [Fact]
    public void RedactPath_MasksColonBearingAdlsCredential_ButNotTheColonlessContainer()
    {
        // A colon-BEARING ADLS userinfo IS an account-key credential — the exemption is colon-less only.
        Assert.Equal(
            "abfss://<redacted>@acct.dfs.core.windows.net/tbl",
            SecretRedaction.RedactPath("abfss://acct:SECRETKEY@acct.dfs.core.windows.net/tbl"));
    }

    [Theory]
    // Pre-existing catalogue entries stay covered — pinned to exact output.
    [InlineData("s3://b/k?sig=SECRETSAS", "s3://b/k?sig=<redacted>")]
    [InlineData("s3://b/k?X-Amz-Signature=SECRETSAS", "s3://b/k?X-Amz-Signature=<redacted>")]
    [InlineData("s3://b/k?password=SECRETSAS", "s3://b/k?password=<redacted>")]
    // #433-added credential-bearing keys.
    [InlineData("s3://b/k?pwd=SECRETSAS", "s3://b/k?pwd=<redacted>")]
    [InlineData("s3://b/k?auth=SECRETSAS", "s3://b/k?auth=<redacted>")]
    [InlineData("s3://b/k?apikey=SECRETSAS", "s3://b/k?apikey=<redacted>")]
    [InlineData("s3://b/k?access_token=SECRETSAS", "s3://b/k?access_token=<redacted>")]
    [InlineData("s3://b/k?access-token=SECRETSAS", "s3://b/k?access-token=<redacted>")]
    [InlineData("s3://b/k?accesstoken=SECRETSAS", "s3://b/k?accesstoken=<redacted>")]
    // Round-1-added keys.
    [InlineData("s3://b/k?pass=SECRETSAS", "s3://b/k?pass=<redacted>")]
    [InlineData("s3://b/k?code=SECRETSAS", "s3://b/k?code=<redacted>")]
    [InlineData("s3://b/k?assertion=SECRETSAS", "s3://b/k?assertion=<redacted>")]
    [InlineData("s3://b/k?jwt=SECRETSAS", "s3://b/k?jwt=<redacted>")]
    [InlineData("s3://b/k?bearer=SECRETSAS", "s3://b/k?bearer=<redacted>")]
    public void RedactPath_MasksCredentialBearingQueryValue_ExactOutput(string path, string expected)
    {
        Assert.Equal(expected, SecretRedaction.RedactPath(path));
    }

    [Theory]
    // A high-entropy PATH SEGMENT that is a legitimate, diagnosable file name — deliberately NOT masked so
    // the diagnostic keeps its value (#433 rationale). No credential-bearing query key on these.
    [InlineData("s3://bucket/table/part-00000-3f2504e0-4f89-41d3-9a0c-0305e82c3301.parquet", "part-00000-3f2504e0-4f89-41d3-9a0c-0305e82c3301.parquet")]
    [InlineData("abfss://acct/table/_delta_log/00000000000000000001.json", "00000000000000000001.json")]
    public void RedactPath_LeavesLegitimateHighEntropyPathSegmentsIntact(string path, string survivingSegment)
    {
        string redacted = SecretRedaction.RedactPath(path);

        // The interesting file/segment name survives — entropy heuristics are deliberately not applied.
        Assert.Contains(survivingSegment, redacted);
    }

    [Fact]
    public void RedactPath_UserInfoMaskStopsAtPathBoundary_DoesNotSwallowPath()
    {
        // The greedy userinfo run must stop at the first '/', so the path after the host survives.
        string redacted = SecretRedaction.RedactPath("s3://user:secret@bucket/some/visible/key.parquet");

        Assert.Equal("s3://<redacted>@bucket/some/visible/key.parquet", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/tmp/plain/local/path.parquet")]
    [InlineData("s3://bucket/no-secrets/here.parquet?sp=r&limit=10")]
    public void RedactPath_LeavesNonCredentialInputsUnchanged(string path)
    {
        Assert.Equal(path, SecretRedaction.RedactPath(path));
    }

    [Fact]
    public void AllThreeRedactionPasses_CompileNonBacktracking_SoNoPassCanReDoS()
    {
        // STRUCTURAL PIN (Round-2). All three recognizer passes MUST carry RegexOptions.NonBacktracking so
        // none can ReDoS on a long scheme-prefixed / query-flanked path. Read .Options off each compiled
        // [GeneratedRegex] via its parameterless factory (they are private static partial). Removing
        // NonBacktracking from ANY of the three — including SensitiveQueryValue() — turns this RED.
        Type type = typeof(SecretRedaction);
        foreach (string factory in new[] { "ColonBearingUserInfo", "ColonlessUserInfo", "SensitiveQueryValue" })
        {
            var method = type.GetMethod(
                factory,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var regex = (Regex)method!.Invoke(null, null)!;
            Assert.True(
                regex.Options.HasFlag(RegexOptions.NonBacktracking),
                $"SecretRedaction.{factory}() is missing RegexOptions.NonBacktracking — that pass can ReDoS.");
        }
    }

    [Fact]
    public void RedactPath_QueryPass_TerminatesQuickly_OnAdversarialQueryFlankedInput()
    {
        // BEHAVIOURAL ReDoS pin for the QUERY pass specifically (Round-2; made DISCRIMINATING in Round-4).
        // The Round-2 input (8 KB) cost only ~7 ms pre-fix, so it could never breach the 10 s budget — the
        // test was VACUOUS. This is the reproducible worst shape instead: an '&'-terminated credential pair
        // (so the first match ends at the '&' and scanning RESUMES) followed by a long keyword-bearing run
        // that contains NO further '=', so every subsequent start position backtracks over the whole tail.
        // Measured on this branch: pre-fix (NonBacktracking stripped from SensitiveQueryValue only)
        // ~73,300 ms at 768 KB — quadratic, ~17 ms at 8 KB and ~8,100 ms at 256 KB, which is why the input
        // must be large; post-fix ~5 ms. The 10 s budget therefore sits ~7x BELOW the pre-fix cost and
        // ~1,800x ABOVE the post-fix cost, so it is RED on revert and cannot flake on a shared runner.
        // Timing stays a GENEROUS, SECONDARY canary (the structural .Options pin above is primary); do NOT
        // tighten this budget toward the observed post-fix time.
        string tail = string.Concat(Enumerable.Repeat("passkeytoken", 768 * 1024 / 12));
        string adversarial = "s3://b/k?sig=SECRETSAS&" + tail;

        var sw = Stopwatch.StartNew();
        string redacted = SecretRedaction.RedactPath(adversarial);
        sw.Stop();

        Assert.Equal("s3://b/k?sig=<redacted>&" + tail, redacted);
        Assert.True(
            sw.ElapsedMilliseconds < 10_000,
            $"RedactPath took {sw.ElapsedMilliseconds} ms on a 768 KB query-flanked input — a query-pass "
            + "ReDoS regression (expected single-digit ms with NonBacktracking on SensitiveQueryValue()).");
    }

    [Fact]
    public void RedactPath_BackslashBearingUserInfo_IsFullyMasked_NoRawRunLeaks()
    {
        // ROUND-2 REGRESSION PIN. The deleted RedactScanLimit truncation could cut INSIDE a userinfo at a
        // '/' or '\\' before its terminating '@', surfacing a multi-KB unmasked credential prefix. A
        // backslash-bearing colon-BEARING userinfo > 8 KB must now be fully masked to the last '@'.
        string path = "s3://user:" + new string('S', 5000) + "\\" + new string('T', 5000) + "@host/key";

        string redacted = SecretRedaction.RedactPath(path);

        Assert.Equal("s3://<redacted>@host/key", redacted);
        Assert.DoesNotContain("SSSS", redacted, System.StringComparison.Ordinal);
        Assert.DoesNotContain("TTTT", redacted, System.StringComparison.Ordinal);
    }

    [Theory]
    // ROUND-2 ADLS-spoof: a mid-string 'abfss://…@' is NOT the leading scheme, so the exemption must NOT
    // apply — the embedded credential must be MASKED (m.Index == 0 anchor).
    [InlineData("s3://abfss://SECRETTOKEN@host/key")]
    [InlineData("s3://bucket/abfss://SECRETTOKEN@x")]
    public void RedactPath_ForgedMidStringAdlsScheme_IsMasked_NotExempted(string path)
    {
        string redacted = SecretRedaction.RedactPath(path);

        Assert.Contains("<redacted>@", redacted, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETTOKEN", redacted, System.StringComparison.Ordinal);
    }
}
