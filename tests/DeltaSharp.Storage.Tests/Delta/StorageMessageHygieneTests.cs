using System.Collections.Generic;
using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
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
    public void Sanitize_StripsBidiAndZeroWidthFormatCharacters()
    {
        // Category Cf (Format) is NOT caught by char.IsControl, yet U+202E RIGHT-TO-LEFT OVERRIDE spoofs the
        // rendered order of a log line / filename and zero-width formatters (U+200B, U+FEFF, U+00AD) hide
        // payload. An identifier legitimately carries none of these, so the sanitizer must neutralize them.
        Assert.Equal("a\uFFFDb\uFFFDc\uFFFDd", DiagnosticText.Sanitize("a\u202Eb\u200Bc\uFEFFd"));
        // The classic bidi-override log-forge spoof is neutralized, not passed through.
        Assert.DoesNotContain('\u202E', DiagnosticText.Sanitize("acct\u202Etxt.forged"));
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
            DeltaSchemaMismatchException.IncompatibleType(poisoned, DataTypes.IntegerType, DataTypes.StringType),
            DeltaSchemaMismatchException.TypeWideningUnsupported(poisoned, DataTypes.IntegerType, DataTypes.LongType),
            DeltaSchemaMismatchException.PartitionColumnWideningDeferred(poisoned, DataTypes.IntegerType, DataTypes.LongType),
            DeltaSchemaMismatchException.CaseInsensitiveDuplicateColumn(poisoned, poisoned),
            DeltaSchemaMismatchException.PartitionColumnEvolution(poisoned, DataTypes.IntegerType, DataTypes.LongType),
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
    public void Sanitize_NeutralizesAstralFormatControls_TagBlockAndOthers()
    {
        // Regression (Balanced/Security/Connectors seats, R3 re-poll): the surrogate-pair fast path kept a
        // valid pair VERBATIM without category-checking the combined code point, so ASTRAL Format controls
        // (Cf) — invisible/spoofing-capable exactly like the BMP Cf that Sanitize_StripsBidiAndZeroWidth…
        // already neutralizes — survived. char.GetUnicodeCategory is per UTF-16 code UNIT and cannot see them;
        // the code-POINT check now neutralizes them to U+FFFD. This keeps Sanitize in lock-step with
        // ColumnMapping.FindUnsafePathSegmentReason (both reject astral Cf), which the in-code comment asserts.
        Assert.Equal("a\uFFFDb", DiagnosticText.Sanitize("a\U000E0001b")); // U+E0001 LANGUAGE TAG
        Assert.Equal("a\uFFFDb", DiagnosticText.Sanitize("a\U000E0020b")); // U+E0020 TAG SPACE (TAG block)
        Assert.Equal("a\uFFFDb", DiagnosticText.Sanitize("a\U000E007Fb")); // U+E007F CANCEL TAG
        Assert.Equal("a\uFFFDb", DiagnosticText.Sanitize("a\U0001D173b")); // U+1D173 MUSICAL SYMBOL (Cf)
        // The canonical invisible-ASCII-smuggling case: 'col1' vs 'col1'+U+E0020 render identically to an
        // operator; after sanitizing they are visibly distinct (the tag char becomes U+FFFD).
        Assert.NotEqual(DiagnosticText.Sanitize("col1"), DiagnosticText.Sanitize("col1\U000E0020"));
        // A VISIBLE astral pair is still preserved (the fix rejects only Cf, not all astral).
        Assert.Equal("a\U00020BB7b", DiagnosticText.Sanitize("a\U00020BB7b")); // CJK Ext-B — kept verbatim
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
    private static void AssertFullyNeutralized(string message, int expectedNewlines = 0)
    {
        // Exact structural-newline count — an injected LF (a sanitizer that leaked '\n') changes the count and
        // is caught, unlike a permissive "allow newlines" skip that hid LF-only regressions (#666 red-team R2).
        Assert.Equal(expectedNewlines, message.Count(c => c == '\n'));
        foreach (char c in new[] { '\r', '\0', '\t', '\u0085', '\u2028', '\u2029', '\uD800' })
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
        // aggregate message. The listing uses structural newlines — exactly 2 for one dependent (the "\n  "
        // listing prefix + the "\nThe {operation}…" sentence). Asserting the exact count catches an injected
        // LF (the attacker's own newline is neutralized to U+FFFD; only the message's formatting newlines remain).
        var deps = new[] { new DeltaTableConstraint(DeltaConstraintKind.Check, FullInjectionCorpus, FullInjectionCorpus) };
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange("amount", deps);

        AssertFullyNeutralized(ex.Message, expectedNewlines: 2);
        Assert.Equal(FullInjectionCorpus, Assert.Single(ex.Constraints).Expression); // raw retained on property
    }

    [Fact]
    public void ConstraintDependentColumn_ForColumnChange_SanitizesColumnName_RawOnProperty()
    {
        // #666 red-team R3 (High): the altered column name (derived from a foreign/hostile table's
        // schema/CHECK predicate) was echoed RAW twice — a live CRLF injection. It must be sanitized in the
        // message (one clean dependent → 2 structural newlines) while the raw name stays on the ColumnName
        // property.
        var deps = new[] { new DeltaTableConstraint(DeltaConstraintKind.Check, "ck", "x > 0") };
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange(FullInjectionCorpus, deps);

        AssertFullyNeutralized(ex.Message, expectedNewlines: 2);
        Assert.Equal(FullInjectionCorpus, ex.ColumnName); // raw retained on property
    }

    [Fact]
    public void UnsupportedFeatures_HugeList_IsBounded_ElidesRemainder()
    {
        // #666 red-team R2: per-item caps do NOT bound a hostile LIST. A forged readerFeatures array of
        // thousands must be elided ("… (+N more)") so the aggregate message cannot flood a log line.
        string[] many = Enumerable.Range(0, 5000).Select(i => "feat" + i).ToArray();
        var protocol = new ProtocolAction(3, 7, [.. many], []);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(() => ProtocolSupport.EnsureReadable(protocol));

        Assert.Contains("more)", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 2000, $"aggregate message not bounded: {ex.Message.Length} chars");
    }

    [Fact]
    public void ConstraintDependentColumn_HugeList_IsBounded_ElidesRemainder()
    {
        DeltaTableConstraint[] many = Enumerable.Range(0, 5000)
            .Select(i => new DeltaTableConstraint(DeltaConstraintKind.Check, "c" + i, "x > " + i)).ToArray();
        DeltaConstraintDependentColumnException ex =
            DeltaConstraintDependentColumnException.ForColumnChange("amount", many);

        Assert.Contains("more)", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 3000, $"aggregate message not bounded: {ex.Message.Length} chars");
    }

    // ---- #683: the residual own-identifier / schema-name echoes (the last raw-token family) ----

    [Fact]
    public void ColumnNotPresentInFile_SanitizesColumnNameInMessage()
    {
        // #685 (maintainer follow-up on the #664 R1 Security seat): DeltaStorageException.ColumnNotPresentInFile
        // interpolated the required column name RAW, unlike DeltaSchemaMismatchException. On a foreign table the
        // requested column name is attacker-authored schema text, so it must go through the shared sanitizer.
        DeltaStorageException ex = DeltaStorageException.ColumnNotPresentInFile(FullInjectionCorpus);

        Assert.Equal(StorageErrorKind.ColumnNotPresentInFile, ex.Kind); // still fails closed on the same kind
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void ColumnNotPresentInFile_OversizedColumnName_IsCappedInMessage()
    {
        string oversized = new('q', DiagnosticText.DefaultMaxLength + 200);

        DeltaStorageException ex = DeltaStorageException.ColumnNotPresentInFile(oversized);

        Assert.DoesNotContain(oversized, ex.Message, StringComparison.Ordinal);
        Assert.Contains("…", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedReader_ParallelRepetitionGuard_SanitizesColumnLabel()
    {
        // #683: the nested reader threads `columnName` as a pure DIAGNOSTIC LABEL into ~40 messages. It is now
        // sanitized ONCE at each entry point, so a poisoned label cannot inject at any of them.
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateParallelRepetition(
                new[] { 0 }, new[] { 0, 1 }, FullInjectionCorpus));

        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void NestedReader_ParallelDefinitionGuard_SanitizesColumnLabel()
    {
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateParallelDefinition(
                new[] { 0 }, new[] { 0, 1 }, mapMaxDef: 2, FullInjectionCorpus));

        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void NestedReader_ValidateShape_SanitizesColumnLabel()
    {
        // The shape-validation entry point (reached before any data page is read) echoes the label too.
        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => NestedParquetColumnReader.ValidateShape(
                new global::Parquet.Schema.DataField<int>("leaf"),
                new StructType(new[] { new StructField("f", IntegerType.Instance, nullable: true) }),
                FullInjectionCorpus));

        Assert.Equal(StorageErrorKind.SchemaMismatch, ex.Kind);
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void ParquetTypeMapping_NestedColumn_SanitizesFieldNameInMessage()
    {
        // #683: the Parquet mapping's own-schema {field.Name} echoes are the same class — a foreign table's
        // (or a column-mapped physical) name reaches the fail-closed unsupported-type message. Since #841 a
        // single-level struct-of-scalars is WRITABLE, so the poisoned name is carried on a shape that still
        // fails closed: a nested type within a nested type (the #585 boundary).
        var nested = new StructField(
            FullInjectionCorpus,
            new StructType(new[]
            {
                new StructField(
                    "x",
                    new StructType(new[] { new StructField("y", IntegerType.Instance, nullable: true) }),
                    nullable: true),
            }),
            nullable: true);

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(() => ParquetTypeMapping.CreateField(nested, honorReferenceNullability: false));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void ChangeDataWriter_NestedDataColumn_SanitizesFieldNameInMessage()
    {
        // #685 audit (write path): the CDF write-door's nested-column gate echoes a data column name that, on
        // a foreign table, is attacker-authored schema text.
        var schema = new StructType(new[]
        {
            new StructField(
                FullInjectionCorpus,
                new StructType(new[] { new StructField("x", IntegerType.Instance, nullable: true) }),
                nullable: true),
        });

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => ChangeDataWriter.EnsureWritableDataSchema(schema));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        AssertFullyNeutralized(ex.Message);
    }

    [Fact]
    public void ColumnMapping_PartitionColumnNotInSchema_SanitizesColumnNameInMessage()
    {
        // #683: metaData.partitionColumns entries are foreign table metadata; the absent-column guard echoed
        // the name raw.
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EnsurePartitionColumnsInSchema(
                StructType.Empty, ImmutableArray.Create(FullInjectionCorpus)));

        AssertFullyNeutralized(ex.Message);
    }
}
