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
/// or non-RFC URIs). It masks (a) the password half of a <c>scheme://user:password@host</c> userinfo
/// and (b) the value of any query-string parameter whose key looks credential-bearing
/// (<c>sig</c>, <c>signature</c>, <c>password</c>, <c>token</c>, <c>key</c>, <c>secret</c>,
/// <c>credential</c>, <c>sas</c>). Option <b>values</b> are never rendered at all (keys only), so only
/// the path needs masking.
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

        string result = UserInfoPassword().Replace(path, "$1" + Mask + "@");
        result = SensitiveQueryValue().Replace(result, "$1" + Mask);
        return result;
    }

    // scheme://user:password@host  →  capture "scheme://user:" and mask the password up to '@'.
    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.\-]*://[^/:@\s]+:)[^/@\s]*@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPassword();

    // ?sig=... / &token=... / &X-Amz-Signature=...  →  capture the "key=" and mask the value up to the
    // next '&', '#', or whitespace. Key match is case-insensitive and allows a vendor prefix/suffix.
    [GeneratedRegex(
        @"([?&][^=&\s]*(?:sig|signature|password|token|key|secret|credential|sas)[^=&\s]*=)[^&#\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryValue();
}
