using System.Collections.Generic;
using System.Collections.Immutable;
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

    // ---- #666: attacker-authored config / protocol-feature / CHECK-constraint VALUE & key echoes ----

    // The full injection/hygiene corpus exercised at every attacker-authored config-text sink: CR, LF, NUL,
    // tab, a C1 control (NEL U+0085), the Unicode LINE & PARAGRAPH separators (U+2028/U+2029), and a lone
    // (unpaired) high surrogate — every class DiagnosticText.Sanitize neutralizes. Kept under the config-token
    // cap so truncation never masks a "raw payload absent" assertion.
    private const string FullInjectionCorpus = "a\r\nb\0c\td\u0085e\u2028f\u2029g\uD800h";

    // Asserts a rendered message carries none of the corpus's injection characters (so nothing can break a log
    // line or render a control sequence) and not the raw payload verbatim. Messages that legitimately contain
    // structural newlines (e.g. the dependent-column listing) pass allowNewlines: true — the attacker's own
    // newline is still neutralized to U+FFFD, so only the message's own formatting newlines remain.
    private static void AssertFullyNeutralized(string message, bool allowNewlines = false)
    {
        var dangerous = new List<char> { '\r', '\0', '\t', '\u0085', '\u2028', '\u2029', '\uD800' };
        if (!allowNewlines)
        {
            dangerous.Add('\n');
        }

        foreach (char c in dangerous)
        {
            Assert.DoesNotContain(c, message);
        }

        Assert.DoesNotContain(FullInjectionCorpus, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigToken_SharedCap_IsTighterThanDefault()
    {
        // #666: the config-value cap is the single shared source of truth (used by AppendOnlyFeature,
        // RetentionPolicy, and ColumnMapping.SanitizeEchoedToken) and is deliberately tighter than the
        // default identifier cap — a valid property value is a short protocol string.
        Assert.True(DiagnosticText.ConfigTokenMaxLength < DiagnosticText.DefaultMaxLength);
    }

    [Fact]
    public void AppendOnly_MalformedValue_SanitizesValueInMessage_KeepsKey()
    {
        // #666: delta.appendOnly's VALUE is attacker-authored on a foreign/hostile table. A malformed value
        // fails closed (MalformedAction); its echo must be sanitized so a crafted payload cannot inject into a
        // structured-log sink, while the trusted protocol KEY stays verbatim for diagnosis.
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => AppendOnlyFeature.IsEnabled(new Dictionary<string, string> { ["delta.appendOnly"] = FullInjectionCorpus }));

        Assert.Equal(DeltaProtocolErrorKind.MalformedAction, ex.Kind);
        AssertFullyNeutralized(ex.Message);
        Assert.Contains("delta.appendOnly", ex.Message, StringComparison.Ordinal); // trusted KEY preserved
    }

    [Fact]
    public void AppendOnly_OversizedValue_IsCappedInMessage()
    {
        // A non-boolean value longer than the config-token cap is truncated (with an ellipsis) so a crafted
        // value cannot render an unbounded log line.
        string oversized = new('x', DiagnosticText.ConfigTokenMaxLength + 40);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => AppendOnlyFeature.IsEnabled(new Dictionary<string, string> { ["delta.appendOnly"] = oversized }));

        Assert.DoesNotContain(oversized, ex.Message, StringComparison.Ordinal); // full raw never rendered
        Assert.Contains("…", ex.Message, StringComparison.Ordinal); // truncation marker present
    }

    [Fact]
    public void RetentionPolicy_UnparseableDeletedFileRetention_SanitizesValueInMessage_KeepsKey()
    {
        // #666: delta.deletedFileRetentionDuration's VALUE is attacker-authored. An unparseable value fails
        // closed (FormatException); its echo must be sanitized while the trusted property KEY is preserved.
        ImmutableSortedDictionary<string, string> config = ImmutableSortedDictionary<string, string>.Empty
            .Add(RetentionPolicy.DeletedFileRetentionDurationKey, FullInjectionCorpus);

        FormatException ex = Assert.Throws<FormatException>(
            () => RetentionPolicy.Default.ResolveTableRetention(config));

        AssertFullyNeutralized(ex.Message);
        Assert.Contains(RetentionPolicy.DeletedFileRetentionDurationKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionPolicy_UnparseableLogRetention_SanitizesValueInMessage_KeepsKey()
    {
        ImmutableSortedDictionary<string, string> config = ImmutableSortedDictionary<string, string>.Empty
            .Add(RetentionPolicy.LogRetentionDurationKey, FullInjectionCorpus);

        FormatException ex = Assert.Throws<FormatException>(
            () => RetentionPolicy.Default.ResolveTableLogRetention(config));

        AssertFullyNeutralized(ex.Message);
        Assert.Contains(RetentionPolicy.LogRetentionDurationKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]  // delta.deletedFileRetentionDuration
    [InlineData(false)] // delta.logRetentionDuration
    public void RetentionPolicy_OversizedValue_IsCappedInMessage(bool deletedFileKey)
    {
        // An unparseable value longer than the config-token cap is truncated (ellipsis) at BOTH retention
        // sinks so a crafted value cannot render an unbounded log line (Quality R1: the sink-level cap must be
        // exercised, not just the shared Sanitize contract).
        string oversized = "interval " + new string('9', DiagnosticText.ConfigTokenMaxLength + 40) + " fortnights";
        string key = deletedFileKey
            ? RetentionPolicy.DeletedFileRetentionDurationKey
            : RetentionPolicy.LogRetentionDurationKey;
        ImmutableSortedDictionary<string, string> config = ImmutableSortedDictionary<string, string>.Empty
            .Add(key, oversized);

        FormatException ex = Assert.Throws<FormatException>(() => deletedFileKey
            ? RetentionPolicy.Default.ResolveTableRetention(config)
            : RetentionPolicy.Default.ResolveTableLogRetention(config));

        Assert.DoesNotContain(oversized, ex.Message, StringComparison.Ordinal); // full raw never rendered
        Assert.Contains("…", ex.Message, StringComparison.Ordinal); // truncation marker present
        Assert.Contains(key, ex.Message, StringComparison.Ordinal); // trusted KEY preserved
    }

    [Fact]
    public void UnsupportedFeatures_ReaderPath_SanitizesFeatureNameInMessage()
    {
        // #666 (strongest instance): a foreign table's readerFeatures are attacker-authored; an unsupported
        // one is echoed by EnsureReadable. A crafted feature name must be sanitized so it cannot inject into a
        // log sink or render unbounded — while the read still fails closed (UnsupportedProtocol).
        var protocol = new ProtocolAction(3, 7, [FullInjectionCorpus], []);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() => ProtocolSupport.EnsureReadable(protocol));

        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind); // still fails closed
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void UnsupportedFeatures_WriterPath_SanitizesFeatureNameInMessage()
    {
        var protocol = new ProtocolAction(3, 7, [], [FullInjectionCorpus]);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() => ProtocolSupport.EnsureWritable(protocol));

        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void UnsupportedFeatures_OversizedFeatureName_IsCappedInMessage()
    {
        string oversized = new('z', DiagnosticText.ConfigTokenMaxLength + 50);
        var protocol = new ProtocolAction(3, 7, [oversized], []);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() => ProtocolSupport.EnsureReadable(protocol));

        Assert.DoesNotContain(oversized, ex.Message, StringComparison.Ordinal);
        Assert.Contains("…", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstraintViolation_ForRow_SanitizesCheckNameAndPredicate_RawOnProperty()
    {
        // #666 / red-team: a CHECK constraint's NAME (delta.constraints.<name> key-suffix) and its predicate
        // Expression are attacker-authored config text on a foreign table; the per-row violation message must
        // sanitize both, while the raw values remain on the typed Constraint property for inspection.
        var constraint = new DeltaTableConstraint(DeltaConstraintKind.Check, FullInjectionCorpus, FullInjectionCorpus);
        DeltaConstraintViolationException ex = DeltaConstraintViolationException.ForRow(constraint, batchIndex: 0, rowIndex: 3);

        AssertFullyNeutralized(ex.Message);
        Assert.Equal(FullInjectionCorpus, ex.Constraint.Name);
        Assert.Equal(FullInjectionCorpus, ex.Constraint.Expression);
    }

    [Fact]
    public void ConstraintViolation_ForRow_InvariantColumnName_IsSanitized()
    {
        var constraint = new DeltaTableConstraint(DeltaConstraintKind.Invariant, FullInjectionCorpus, FullInjectionCorpus);
        DeltaConstraintViolationException ex = DeltaConstraintViolationException.ForRow(constraint, batchIndex: 1, rowIndex: 2);

        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void ConstraintDependentColumn_ForColumnChange_SanitizesNameAndPredicate_RawOnProperty()
    {
        // #666 / red-team: each dependent CHECK's name + predicate (attacker-authored) are listed in the
        // aggregate message. The listing uses structural newlines (allowNewlines: true) — the attacker's own
        // newline is still neutralized, so only the message's own formatting newlines remain.
        var deps = new[] { new DeltaTableConstraint(DeltaConstraintKind.Check, FullInjectionCorpus, FullInjectionCorpus) };
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange("amount", deps);

        AssertFullyNeutralized(ex.Message, allowNewlines: true);
        Assert.Equal(FullInjectionCorpus, Assert.Single(ex.Constraints).Expression); // raw retained on property
    }
}
