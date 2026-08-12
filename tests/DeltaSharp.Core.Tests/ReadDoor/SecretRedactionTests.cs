using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.ReadDoor;

/// <summary>
/// #433 hardening for <see cref="SecretRedaction"/>. Pins the widened best-effort credential masker:
/// the userinfo matcher now masks the ENTIRE userinfo (unencoded <c>@</c>/<c>:</c> inside the credential,
/// and colon-less token in the userinfo position), and the query-key catalogue gained
/// <c>auth</c>/<c>pwd</c>/<c>apikey</c>/<c>access[_-]?token</c>. The masker is a textual best-effort pass,
/// not a URI parser — these tests pin the exact adversarial cases that motivated the widening plus the
/// non-secret shapes that must survive so a diagnostic stays useful.
/// </summary>
public sealed class SecretRedactionTests
{
    private const string Mask = "<redacted>";

    [Theory]
    // Colon-delimited password (the original case) — still masked.
    [InlineData("s3://user:hunter2@bucket/key", "hunter2")]
    // Colon-less token in the userinfo position — masked by the widened matcher (#433).
    [InlineData("s3://LONGOPAQUETOKEN@bucket/key", "LONGOPAQUETOKEN")]
    // Unencoded '@' INSIDE the credential — greedy run masks to the LAST '@' in the authority (#433).
    [InlineData("s3://user:p@ss@bucket/key", "p@ss")]
    // Unencoded ':' plus token — whole userinfo masked (#433).
    [InlineData("abfss://acct:aVeryLong:Secret@host/path", "aVeryLong:Secret")]
    public void RedactPath_MasksEntireUserInfo(string path, string secret)
    {
        string redacted = SecretRedaction.RedactPath(path);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(Mask, redacted);
    }

    [Theory]
    // Pre-existing catalogue entries stay covered.
    [InlineData("s3://b/k?sig=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?X-Amz-Signature=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?password=SECRETSAS", "SECRETSAS")]
    // #433-added credential-bearing keys.
    [InlineData("s3://b/k?pwd=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?auth=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?apikey=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?access_token=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?access-token=SECRETSAS", "SECRETSAS")]
    [InlineData("s3://b/k?accesstoken=SECRETSAS", "SECRETSAS")]
    public void RedactPath_MasksCredentialBearingQueryValue(string path, string secret)
    {
        string redacted = SecretRedaction.RedactPath(path);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(Mask, redacted);
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

        Assert.DoesNotContain("secret", redacted);
        Assert.Contains("some/visible/key.parquet", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/tmp/plain/local/path.parquet")]
    [InlineData("s3://bucket/no-secrets/here.parquet?sp=r&limit=10")]
    public void RedactPath_LeavesNonCredentialInputsUnchanged(string path)
    {
        Assert.Equal(path, SecretRedaction.RedactPath(path));
    }
}
