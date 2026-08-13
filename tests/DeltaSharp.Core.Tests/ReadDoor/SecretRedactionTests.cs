using System.Diagnostics;
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
    public void RedactPath_MasksEntireUserInfo_ExactOutput(string path, string expected)
    {
        Assert.Equal(expected, SecretRedaction.RedactPath(path));
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
    public void RedactPath_BoundsInputAndTerminatesQuickly_OnAdversarialUnboundedInput()
    {
        // BEHAVIOUR PIN for the RedactScanLimit input bound (Round-1). A long scheme-prefixed path with no
        // '@' was the ReDoS trigger (128 KB -> 28 s, 256 KB -> 100 s BEFORE the NonBacktracking + input
        // bound). With both in place the render is linear AND the OUTPUT is bounded by the scan limit, so a
        // maintainer who silently removes the bound turns this red: the output length assertion fails and the
        // timing budget catches a re-introduced quadratic.
        string adversarial = "s3://" + new string('a', 256 * 1024) + "/tail.parquet";

        var sw = Stopwatch.StartNew();
        string redacted = SecretRedaction.RedactPath(adversarial);
        sw.Stop();

        Assert.True(
            redacted.Length <= SecretRedaction.RedactScanLimit,
            $"RedactPath output ({redacted.Length}) exceeded the RedactScanLimit "
            + $"({SecretRedaction.RedactScanLimit}); the input bound was removed or widened.");
        Assert.True(
            sw.ElapsedMilliseconds < 2000,
            $"RedactPath took {sw.ElapsedMilliseconds} ms on a 256 KB adversarial input — a quadratic/ReDoS "
            + "regression (expected sub-second with NonBacktracking + input bound).");
    }
}
