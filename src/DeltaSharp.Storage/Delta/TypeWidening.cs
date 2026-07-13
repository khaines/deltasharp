using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The Delta <b>type-widening</b> allowlist — the single, authoritative classifier of which scalar type
/// changes this build actually <b>applies</b> (and, on read, <b>promotes</b>), shared by the write-side
/// <see cref="DeltaSchemaEnforcer"/> and the read-side <c>ParquetFileReader</c> so the two never diverge.
///
/// <para><b>Applied set (verified against Delta PROTOCOL.md "Type Widening" → "supported type changes").</b>
/// A deliberate, total subset of the protocol's list — the same-family, unambiguous, cheaply-promotable
/// widenings the Parquet read path can materialize by a plain CLR upcast (or a decimal rescale):
/// <list type="bullet">
/// <item>integral widening within {<c>byte</c> → <c>short</c> → <c>int</c> → <c>long</c>} (any strictly
/// wider rank);</item>
/// <item><c>float</c> → <c>double</c>;</item>
/// <item><c>decimal(p,s)</c> → <c>decimal(p',s')</c> <b>grow-only</b> — both the integer-digit range
/// (<c>p−s</c>) and the scale <c>s</c> are non-decreasing, so every representable value is preserved with no
/// rounding.</item>
/// </list></para>
///
/// <para><b>Deliberately NOT applied</b> (protocol-sanctioned but out of this build's scope, kept
/// fail-closed): the cross-family promotions <c>byte/short/int</c> → <c>double</c> and
/// <c>byte/short/int/long</c> → <c>decimal</c>, and nested (array-element/map-key/value) widening — tracked
/// in <b>#535</b>. Also <c>date</c> → <c>timestamp without timezone</c>: the
/// <see cref="IsDeferredWidening">deferred</see> case (<b>#533</b>) because Delta only sanctions
/// <c>date → timestamp_ntz</c> (NOT <c>date → timestamp</c> with a timezone), and this build has no
/// <c>TimestampNtzType</c>, so applying it against the sole (timezone-adjusted) <c>timestamp</c> would be a
/// semantically wrong promotion. It stays rejected until an NTZ type lands.</para>
/// </summary>
internal static class TypeWidening
{
    /// <summary>
    /// Whether changing a column/field's type from <paramref name="from"/> to <paramref name="to"/> is a
    /// widening this build <b>applies on write</b> and <b>promotes on read</b> (the applied allowlist above).
    /// Deliberately stricter than <see cref="TypeCoercion"/>'s common-type search — only these total,
    /// unambiguous, lossless cases count (so, e.g., <c>long → double</c> is never treated as a widening).
    /// Returns <see langword="false"/> when the types are equal (an equal type is not a <i>change</i>).
    /// </summary>
    public static bool IsSanctionedWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        // Integral widening: byte → short → int → long (any strictly higher rank).
        int fromRank = IntegralRank(from);
        int toRank = IntegralRank(to);
        if (fromRank >= 0 && toRank >= 0)
        {
            return toRank > fromRank;
        }

        // float → double.
        if (from is FloatType && to is DoubleType)
        {
            return true;
        }

        // decimal(p,s) → decimal(p',s') grow-only: both the integer-digit range (p − s) and the scale s
        // are non-decreasing, so no value is rounded or truncated.
        if (from is DecimalType a && to is DecimalType b)
        {
            return b.Scale >= a.Scale
                && (b.Precision - b.Scale) >= (a.Precision - a.Scale)
                && !(b.Precision == a.Precision && b.Scale == a.Scale);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a would-be widening that Delta sanctions
    /// but this build <b>defers</b> (keeps fail-closed): today only <c>date → timestamp</c>. Delta widens
    /// <c>date</c> to <c>timestamp_ntz</c> (timezone-<i>less</i>), which this build cannot represent (no
    /// <c>TimestampNtzType</c>); the only <c>timestamp</c> here is timezone-adjusted, so the change is not
    /// applied. Surfaced distinctly (as <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>) so the
    /// error names the NTZ deferral rather than the generic incompatible-type reason. Tracked in <b>#533</b>.
    /// </summary>
    public static bool IsDeferredWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return from is DateType && to is TimestampType;
    }

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a <b>cross-family</b> widening that Delta
    /// sanctions (Delta PROTOCOL.md "Type Widening" → "supported type changes") but this build <b>defers</b>
    /// (keeps fail-closed), tracked in <b>#535</b>: the integral-to-floating and integral-to-decimal
    /// promotions the read path does not yet materialize —
    /// <list type="bullet">
    /// <item><c>byte</c>/<c>short</c>/<c>int</c> → <c>double</c> (Delta sanctions integral→double up to
    /// <c>int</c>; <c>long → double</c> is <b>lossy</b> and is NOT sanctioned, so it stays
    /// <see cref="DeltaSchemaMismatchKind.IncompatibleType"/>);</item>
    /// <item><c>byte</c>/<c>short</c>/<c>int</c>/<c>long</c> → <c>decimal</c>.</item>
    /// </list>
    /// Recognized here so the enforcer surfaces them <b>distinctly</b> (as
    /// <see cref="DeltaSchemaMismatchKind.TypeWideningUnsupported"/>, naming the #535 deferral) rather than as
    /// the generic <see cref="DeltaSchemaMismatchKind.IncompatibleType"/> a <c>string→int</c> gets — they ARE
    /// Delta-sanctioned, just not applied yet. Symmetric to the <see cref="IsDeferredWidening">date→timestamp</see>
    /// (#533) deferral. Independent of enablement: a cross-family widening is deferred even when the table
    /// enables type widening.
    /// </summary>
    public static bool IsDeferredCrossFamilyWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        int fromRank = IntegralRank(from);
        if (fromRank < 0)
        {
            return false;
        }

        // byte/short/int → double (rank 0..2); long → double is lossy and NOT sanctioned.
        if (to is DoubleType)
        {
            return fromRank <= 2;
        }

        // byte/short/int/long → decimal.
        return to is DecimalType;
    }

    private static int IntegralRank(DataType type) => type switch
    {
        ByteType => 0,
        ShortType => 1,
        IntegerType => 2,
        LongType => 3,
        _ => -1,
    };
}
