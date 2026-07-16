using System.Collections.Immutable;
using System.Linq;

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

    /// <summary>True when the table property <c>delta.appendOnly</c> is set to <c>true</c>
    /// (case-insensitive) — the activation signal Delta's <c>IS_APPEND_ONLY.fromMetaData</c> reads.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.TryGetValue(PropertyKey, out string? value)
            && bool.TryParse(value, out bool enabled)
            && enabled;
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
    /// Enforces append-only on a commit: throws a fail-closed <see cref="DeltaProtocolException"/> (kind
    /// <see cref="DeltaProtocolErrorKind.AppendOnlyViolation"/>) when <paramref name="configuration"/> has
    /// <c>delta.appendOnly=true</c> AND <paramref name="actions"/> contains a <c>remove</c> with
    /// <c>dataChange=true</c> (a DELETE / OVERWRITE that deletes or changes committed data). A commit with
    /// only appends, or whose removes are all <c>dataChange=false</c> (OPTIMIZE compaction), is allowed —
    /// matching Delta's <c>if (removes.exists(_.dataChange)) assertRemovable(snapshot)</c>.
    /// </summary>
    /// <exception cref="DeltaProtocolException">The table is append-only and the commit changes committed
    /// data.</exception>
    public static void EnsureCommitAllowed(
        IReadOnlyDictionary<string, string> configuration, IReadOnlyList<DeltaAction> actions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(actions);

        if (!IsEnabled(configuration))
        {
            return;
        }

        if (actions.Any(action => action is RemoveFileAction { DataChange: true }))
        {
            throw DeltaProtocolException.AppendOnly(
                "This table is configured to only allow appends ('delta.appendOnly'='true'), so a commit that "
                + "deletes or changes committed data (a DELETE / OVERWRITE — a remove with dataChange=true) is "
                + "refused fail-closed. Set 'delta.appendOnly'='false' to permit deletes/updates. (Compaction "
                + "removes such as OPTIMIZE, which do not change data, remain allowed.)");
        }
    }
}
