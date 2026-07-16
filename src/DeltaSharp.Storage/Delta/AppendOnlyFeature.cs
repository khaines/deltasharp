using System.Collections.Immutable;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The legacy <c>appendOnly</c> writer feature and its write-time enforcement (Delta protocol
/// "Append-only Tables" / "Active Features"). When the table property <c>delta.appendOnly</c> is
/// <c>true</c>, the table may only be <b>appended</b> to: a commit that deletes or changes committed data
/// (a <c>remove</c> with <c>dataChange=true</c>, e.g. DELETE / OVERWRITE) is refused fail-closed.
/// Compaction removes (<c>dataChange=false</c>, e.g. OPTIMIZE) are <b>permitted</b> — they rewrite files
/// without changing the data — matching Spark's
/// <c>if (removes.exists(_.dataChange)) DeltaLog.assertRemovable(snapshot)</c>
/// (<c>OptimisticTransaction.scala</c>; <c>assertRemovable</c> reads <c>IS_APPEND_ONLY.fromMetaData</c>).
///
/// <para><b>Writer-only feature.</b> Unlike <see cref="TypeWideningFeature"/> / <c>deletionVectors</c>,
/// <c>appendOnly</c> is a <b>writer</b> feature only — it is registered in
/// <see cref="ProtocolSupport.SupportedWriterFeatures"/> and enumerated only into <c>writerFeatures</c>,
/// never into <c>readerFeatures</c> (Delta PROTOCOL.md "Writer Features"). It has no separate
/// <c>delta.enable*</c> gate: the property <c>delta.appendOnly=true</c> is itself the activation, and at
/// the table-features writer version the feature must additionally be named in <c>writerFeatures</c>
/// ("Active Features": appendOnly is active iff in <c>writerFeatures</c> AND <c>delta.appendOnly=true</c>).</para>
///
/// <para><b>Legacy interop.</b> A foreign legacy (writer v1–v2) table carries <c>appendOnly</c> implicitly
/// through its writer version; enforcement here keys off the table's own metadata property (as Spark does),
/// so it covers BOTH a legacy writer-2 table AND a writer-7 table that explicitly enumerates the feature.
/// <see cref="TypeWideningFeature.UpgradeProtocol"/> enumerates the feature into <c>writerFeatures</c> when
/// upgrading such a legacy table so append-only stays active across the table-features upgrade (#549).</para>
/// </summary>
internal static class AppendOnlyFeature
{
    /// <summary>The table-feature name in <c>writerFeatures</c> (a writer-only feature — never in
    /// <c>readerFeatures</c>).</summary>
    public const string Feature = "appendOnly";

    /// <summary>The table property that activates append-only. When <c>true</c>, removes that change data
    /// are refused.</summary>
    public const string PropertyKey = "delta.appendOnly";

    /// <summary>
    /// Whether the table is append-only: <c>true</c> when <c>delta.appendOnly</c> is present and
    /// case-insensitively exactly <c>"true"</c>, <c>false</c> when absent or exactly <c>"false"</c>. A present
    /// value that is anything else — including a whitespace-padded <c>" true "</c>/<c>" false "</c> — throws a
    /// fail-closed <see cref="DeltaProtocolException"/>, mirroring the Delta golden where
    /// <c>IS_APPEND_ONLY.fromMetaData</c> parses via Scala's <c>String.toBoolean</c>, which matches only the
    /// exact tokens <c>true</c>/<c>false</c> (no trimming) and THROWS otherwise (Spark also rejects a
    /// malformed value at SET time). This is deliberately STRICTER than the <c>bool.TryParse ⇒ false</c>
    /// convention used by the optional <b>enable-gate</b> features (<see cref="TypeWideningFeature.IsEnabled"/>,
    /// <c>DeletionVectorsFeature.IsEnabled</c>): for those an unparseable value safely leaves an OPTIONAL
    /// feature OFF, but here silently coercing a malformed value to <c>false</c> would DROP a data-protection
    /// guarantee (fail <b>open</b>) on a table a foreign engine relies on — the unsafe direction — so a
    /// malformed value fails <b>closed</b> instead. (<c>bool.TryParse</c> is NOT used precisely because it
    /// trims surrounding whitespace, which would fail open on <c>" false "</c>.)
    /// </summary>
    /// <exception cref="DeltaProtocolException"><c>delta.appendOnly</c> is present but not exactly
    /// <c>"true"</c>/<c>"false"</c> (case-insensitive, untrimmed).</exception>
    public static bool IsEnabled(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TryGetValue(PropertyKey, out string? value))
        {
            return false;
        }

        // Exact case-insensitive match (no trimming) — mirrors Scala String.toBoolean, and deliberately
        // avoids bool.TryParse, which trims and would fail OPEN on a whitespace-padded " false ".
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw DeltaProtocolException.Malformed(
            $"Table property '{PropertyKey}' has a non-boolean value '{value}'; it must be exactly 'true' or "
            + "'false' (case-insensitive, no surrounding whitespace). Refusing fail-closed rather than silently "
            + "treating the table as not append-only (a malformed value must not drop the append-only guarantee).");
    }

    /// <summary>
    /// Adds the <c>appendOnly</c> feature to a <c>writerFeatures</c> list unless already present (idempotent;
    /// a default/uninitialized array is treated as empty). Used by the legacy → table-features upgrade to
    /// enumerate an active legacy <c>appendOnly</c> feature so it stays active (#549). Writer-only — this is
    /// never applied to a <c>readerFeatures</c> list.
    /// </summary>
    public static ImmutableArray<string> WithWriterFeature(ImmutableArray<string> writerFeatures)
    {
        if (!writerFeatures.IsDefault && writerFeatures.Contains(Feature, StringComparer.Ordinal))
        {
            return writerFeatures;
        }

        return (writerFeatures.IsDefault ? ImmutableArray<string>.Empty : writerFeatures).Add(Feature);
    }

    /// <summary>
    /// Enforces append-only on a commit, matching the Delta golden
    /// <c>if (removes.exists(_.dataChange)) assertRemovable(snapshot)</c>: only when
    /// <paramref name="actions"/> contains a <c>remove</c> with <c>dataChange=true</c> (a DELETE / OVERWRITE
    /// that deletes or changes committed data) is the table's append-only status evaluated; if the table is
    /// then append-only (<see cref="IsEnabled"/>), the commit is refused fail-closed
    /// (<see cref="DeltaProtocolErrorKind.AppendOnlyViolation"/>). A commit with only appends, or whose
    /// removes are all <c>dataChange=false</c> (OPTIMIZE compaction), is allowed and does NOT evaluate the
    /// property — so a malformed <c>delta.appendOnly</c> value surfaces (as <see cref="IsEnabled"/>'s
    /// fail-closed throw) only on a data-changing commit, exactly as the golden's <c>assertRemovable</c>
    /// gate does.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The commit changes committed data and the table is
    /// append-only, or it carries a malformed <c>delta.appendOnly</c> value.</exception>
    public static void EnsureCommitAllowed(
        IReadOnlyDictionary<string, string> configuration, IReadOnlyList<DeltaAction> actions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(actions);

        // Golden gate: only a data-changing remove (DELETE / OVERWRITE) can violate append-only; a pure
        // append or an all-dataChange=false compaction (OPTIMIZE) never evaluates the property — mirroring
        // Spark, which calls assertRemovable (and thus parses delta.appendOnly) only when
        // removes.exists(_.dataChange).
        if (!actions.Any(action => action is RemoveFileAction { DataChange: true }))
        {
            return;
        }

        if (IsEnabled(configuration))
        {
            throw DeltaProtocolException.AppendOnly(
                "This table is configured to only allow appends ('delta.appendOnly'='true'), so a commit that "
                + "deletes or changes committed data (a DELETE / OVERWRITE — a remove with dataChange=true) is "
                + "refused fail-closed. Set 'delta.appendOnly'='false' to permit deletes/updates. (Compaction "
                + "removes such as OPTIMIZE, which do not change data, remain allowed.)");
        }
    }
}
