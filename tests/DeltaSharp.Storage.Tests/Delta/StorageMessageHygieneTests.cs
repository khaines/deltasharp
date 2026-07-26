using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #667/#516/#665 storage message-hygiene contract: attacker-controllable/foreign tokens (file paths,
/// physical schemas) are DROPPED from fault messages (kept on typed properties where useful), and the
/// caller's own bounded identifiers (schema column names, the confined relative path) are echoed through
/// <see cref="DiagnosticText.Sanitize"/> — control-char stripped + length-capped — so a poisoned token can
/// never inject into a structured-log sink.
/// </summary>
public sealed class StorageMessageHygieneTests
{
    [Fact]
    public void Sanitize_StripsControlChars_ToReplacementChar()
    {
        // CRLF/NUL/tab (the log-injection payload) become U+FFFD; ordinary chars pass through.
        string sanitized = DiagnosticText.Sanitize("a\r\nb\tc\0d");

        Assert.Equal("a\uFFFD\uFFFDb\uFFFDc\uFFFDd", sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\t', sanitized);
        Assert.DoesNotContain('\0', sanitized);
    }

    [Fact]
    public void Sanitize_CapsLength_WithEllipsis()
    {
        string raw = new('x', DiagnosticText.DefaultMaxLength + 50);

        string sanitized = DiagnosticText.Sanitize(raw);

        Assert.Equal(DiagnosticText.DefaultMaxLength + 1, sanitized.Length); // capped chars + the ellipsis
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ShortCleanToken_IsUnchanged()
    {
        Assert.Equal("address.zip", DiagnosticText.Sanitize("address.zip"));
    }

    [Fact]
    public void Sanitize_Null_RendersBoundedLiteral_NotAThrow()
    {
        Assert.Equal("(null)", DiagnosticText.Sanitize(null));
    }

    [Fact]
    public void SchemaMismatch_ColumnName_IsSanitizedInMessage_ButRawOnPathProperty()
    {
        // A crafted own-schema column name carrying a CRLF injection payload: the MESSAGE must be sanitized
        // (no raw control chars) while the typed Path property retains the exact name for programmatic use.
        const string poisoned = "col\r\nInjected: fake-log-line";
        DeltaSchemaMismatchException ex = DeltaSchemaMismatchException.MissingRequiredColumn(poisoned);

        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain(poisoned, ex.Message, StringComparison.Ordinal);
        Assert.Contains("col\uFFFD\uFFFDInjected", ex.Message, StringComparison.Ordinal);
        Assert.Equal(poisoned, ex.Path); // raw, on the typed property
    }

    [Fact]
    public void SchemaMismatch_CaseInsensitiveDuplicate_SanitizesBothNames()
    {
        DeltaSchemaMismatchException ex =
            DeltaSchemaMismatchException.CaseInsensitiveDuplicateColumn("A\rB", "a\nb");

        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
    }

    [Fact]
    public void PhysicalWriteSchemaMismatch_MessageCarriesNoPathOrSchema()
    {
        DeltaSchemaMismatchException ex = DeltaSchemaMismatchException.PhysicalWriteSchemaMismatch();

        Assert.Equal(DeltaSchemaMismatchKind.PhysicalWriteSchemaMismatch, ex.Kind);
        // No file path, no physical data schema string is interpolated — only the bounded reason remains.
        Assert.DoesNotContain(".parquet", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("struct<", ex.Message, StringComparison.Ordinal);
    }
}
