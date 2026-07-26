using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
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
    public void Sanitize_StripsUnicodeLineAndParagraphSeparators()
    {
        // U+2028 (LINE SEPARATOR, Zl) / U+2029 (PARAGRAPH SEPARATOR, Zp) are NOT category Cc, so char.IsControl
        // misses them, yet several renderers/log viewers treat them as newlines — the sanitizer must strip them.
        Assert.Equal("a\uFFFDb\uFFFDc", DiagnosticText.Sanitize("a\u2028b\u2029c"));
    }

    [Fact]
    public void Sanitize_DoesNotSplitSurrogatePairAtCap()
    {
        // A cap that would land mid-surrogate-pair must back off so no lone surrogate is emitted.
        string raw = new string('x', DiagnosticText.DefaultMaxLength - 1) + "\U0001F600"; // astral emoji at the boundary
        string sanitized = DiagnosticText.Sanitize(raw);

        Assert.DoesNotContain('\uFFFD', sanitized); // no replacement injected by a bad split
        foreach (char c in sanitized)
        {
            Assert.False(char.IsHighSurrogate(c) && sanitized.IndexOf(c) == sanitized.Length - 1); // no dangling high surrogate
        }
    }

    [Fact]
    public void SchemaMismatch_EveryNameFactory_SanitizesControlCharsInMessage_ButKeepsRawPath()
    {
        // Locks the sanitize contract for ALL DeltaSchemaMismatchException name factories (not just the two
        // covered above): a CRLF-poisoned own-schema name is neutralized in the Message while the typed Path
        // property retains it raw. RED-on-revert against dropping DiagnosticText.Sanitize on any factory.
        const string poisoned = "c\r\nol\u2028x";
        DeltaSchemaMismatchException[] exceptions =
        [
            DeltaSchemaMismatchException.MissingRequiredColumn(poisoned),
            DeltaSchemaMismatchException.NewColumnNotAllowed(poisoned),
            DeltaSchemaMismatchException.NewColumnMustBeNullable(poisoned),
            DeltaSchemaMismatchException.NullabilityViolation(poisoned),
            DeltaSchemaMismatchException.IncompatibleType(poisoned, "integer", "string"),
            DeltaSchemaMismatchException.TypeWideningUnsupported(poisoned, "integer", "long"),
            DeltaSchemaMismatchException.PartitionColumnWideningDeferred(poisoned, "integer", "long"),
            DeltaSchemaMismatchException.CaseInsensitiveDuplicateColumn(poisoned, poisoned),
            DeltaSchemaMismatchException.PartitionColumnEvolution(poisoned, "integer", "long"),
        ];

        foreach (DeltaSchemaMismatchException ex in exceptions)
        {
            Assert.DoesNotContain('\n', ex.Message);
            Assert.DoesNotContain('\r', ex.Message);
            Assert.DoesNotContain('\u2028', ex.Message);
            Assert.DoesNotContain(poisoned, ex.Message, StringComparison.Ordinal);
            Assert.Equal(poisoned, ex.Path); // raw, on the typed property
        }
    }

    [Fact]
    public void Sanitize_NeutralizesLoneSurrogates_ButKeepsValidPairs()
    {
        // A pre-existing lone (unpaired) surrogate is malformed UTF-16 and is neutralized to U+FFFD; a valid
        // astral pair survives intact. (Red-team R1: the cap back-off only prevented SPLITTING a pair — an
        // already-lone surrogate in the input must also be neutralized.)
        Assert.Equal("a\uFFFDb", DiagnosticText.Sanitize("a\uD800b")); // lone HIGH surrogate
        Assert.Equal("a\uFFFDb", DiagnosticText.Sanitize("a\uDC00b")); // lone LOW surrogate
        Assert.Equal("a\U0001F600b", DiagnosticText.Sanitize("a\U0001F600b")); // valid pair preserved
        // Even with a negative/oversized cap (no length capping), a lone surrogate is still neutralized.
        Assert.Equal("\uFFFD", DiagnosticText.Sanitize("\uD800", maxLength: -10));
    }

    [Fact]
    public void ValidateLevelRange_SanitizesLeafPathInMessage()
    {
        // #665: the nested reader's out-of-range-level guard echoes the file-derived leaf path — it must be
        // sanitized so a poisoned name cannot inject into a log sink.
        const string poisoned = "leaf\r\npath";
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateLevelRange(new[] { 5 }, maxLevel: 3, poisoned, "definition"));

        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain(poisoned, ex.Message, StringComparison.Ordinal);
    }
}
