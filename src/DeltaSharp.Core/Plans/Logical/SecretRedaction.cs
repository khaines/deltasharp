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
/// <c>scheme://</c> and the last <c>@</c> before the host/path boundary, regardless of whether the
/// credential is percent-encoded or carries a colon — and (b) the value of any query-string parameter
/// whose key looks credential-bearing
/// (<c>sig</c>, <c>signature</c>, <c>password</c>, <c>pwd</c>, <c>token</c>, <c>key</c>, <c>secret</c>,
/// <c>credential</c>, <c>sas</c>, <c>auth</c>, <c>apikey</c>, <c>access[_-]?token</c>). Option
/// <b>values</b> are never rendered at all (keys only), so only the path needs masking.
/// <para>
/// #433 hardening. The userinfo matcher was widened from "a colon-delimited password" to the WHOLE
/// userinfo so it also catches an unencoded <c>@</c> inside the credential (the greedy run masks to the
/// LAST <c>@</c> in the authority) and a colon-less token in the userinfo position. The key catalogue
/// gained <c>auth</c>/<c>pwd</c>/<c>apikey</c>/<c>access[_-]?token</c>. High-entropy PATH-SEGMENT masking
/// is <b>deliberately not</b> attempted: a DeltaSharp object path routinely carries a high-entropy segment
/// that is NOT a secret (a <c>part-&lt;guid&gt;.parquet</c> name, a commit UUID, a deletion-vector id), so
/// an entropy heuristic would mask legitimate, diagnosable file names far more often than a real
/// path-segment credential. A secret that must survive as a bare path segment is out of scope for a
/// textual masker and is the caller's obligation to keep out of a rendered path.
/// </para>
/// </remarks>
internal static partial class SecretRedaction
{
    private const string Mask = "<redacted>";

    /// <summary>Returns <paramref name="path"/> with credential-bearing userinfo and query-string values
    /// masked. A <see langword="null"/> or empty path is returned unchanged.</summary>
    public static string RedactPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string result = UserInfo().Replace(path, "$1" + Mask + "@");
        result = SensitiveQueryValue().Replace(result, "$1" + Mask);
        return result;
    }

    // scheme://<userinfo>@host  ->  capture "scheme://" and mask the WHOLE userinfo up to the last '@'
    // in the authority (the greedy [^/?#\s]* run stops at the first path/query/fragment/whitespace
    // boundary, so it never reaches into the path -- but it DOES span an interior unencoded '@' inside
    // the userinfo, masking a user:p@ss credential in full -- and it does not require a colon, so a
    // bare token in the userinfo position is masked too). #433.
    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/?#\s]*@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();

    // ?sig=... / &token=... / &X-Amz-Signature=... / &pwd=... / &access_token=...  ->  capture the
    // "key=" and mask the value up to the next '&', '#', or whitespace. Key match is case-insensitive
    // and allows a vendor prefix/suffix. #433 broadened the catalogue (auth, pwd, apikey, access[_-]?token).
    [GeneratedRegex(
        @"([?&][^=&\s]*(?:sig|signature|password|pwd|token|key|secret|credential|sas|auth|apikey|access[_-]?token)[^=&\s]*=)[^&#\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryValue();
}
