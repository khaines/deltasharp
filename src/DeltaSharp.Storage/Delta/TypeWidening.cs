using DeltaSharp.Types;

namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The Delta <b>type-widening</b> allowlist — the single, authoritative classifier of which scalar type
/// changes this build actually <b>applies</b> (and, on read, <b>promotes</b>), shared by the write-side
/// <see cref="DeltaSchemaEnforcer"/> and the read-side <c>ParquetFileReader</c> so the two never diverge.
///
/// <para><b>Applied set (verified against Delta PROTOCOL.md "Type Widening" → "Supported Type Changes").</b>
/// A deliberate, total subset of the protocol's list — every widening the Parquet read path can materialize
/// losslessly, either by a plain CLR upcast, a decimal rescale, or an integral→floating/decimal promotion in
/// managed code:
/// <list type="bullet">
/// <item><b>Same family</b> (<see cref="IsSameFamilyWidening"/>): integral widening within
/// {<c>byte</c> → <c>short</c> → <c>int</c> → <c>long</c>} (any strictly wider rank); <c>float</c> →
/// <c>double</c>; <c>decimal(p,s)</c> → <c>decimal(p',s')</c> <b>grow-only</b> (both the integer-digit range
/// <c>p−s</c> and the scale <c>s</c> non-decreasing, so no value is rounded).</item>
/// <item><b>Cross family</b> (<see cref="IsCrossFamilyWidening"/>, #535): <c>byte</c>/<c>short</c>/<c>int</c>
/// → <c>double</c> — Delta sanctions integral→double only up to <c>int</c> because a 64-bit <c>long</c>
/// exceeds <c>double</c>'s 53-bit mantissa (so <c>long → double</c> is <b>lossy</b> and NOT sanctioned); and
/// <c>byte</c>/<c>short</c>/<c>int</c>/<c>long</c> → <c>decimal(p,s)</c> — but only when the target decimal
/// can represent the <b>full integral range</b> losslessly (its integer-digit capacity <c>p − s</c> is at
/// least the number of digits the widest source value needs). A decimal too narrow to hold the source range
/// is NOT a sanctioned widening (it would truncate) and stays fail-closed.</item>
/// </list></para>
///
/// <para><b>Deliberately NOT applied</b> (protocol-sanctioned but out of this build's scope, kept
/// fail-closed): nested (array-element/map-key/value) widening — it would need a <c>fieldPath</c> in
/// <c>delta.typeChanges</c> and the Parquet read path does not read nested types at all (tracked as the #535
/// follow-up, #546). Also <c>date</c> → <c>timestamp without timezone</c>: the
/// <see cref="IsDeferredWidening">deferred</see> case (<b>#533</b>) because Delta only sanctions
/// <c>date → timestamp_ntz</c> (NOT <c>date → timestamp</c> with a timezone), and this build has no
/// <c>TimestampNtzType</c>, so applying it against the sole (timezone-adjusted) <c>timestamp</c> would be a
/// semantically wrong promotion. It stays rejected until an NTZ type lands.</para>
/// </summary>
internal static class TypeWidening
{
    /// <summary>
    /// Whether changing a column/field's type from <paramref name="from"/> to <paramref name="to"/> is a
    /// widening this build <b>applies on write</b> and <b>promotes on read</b> — the full applied allowlist:
    /// the same-family cases (<see cref="IsSameFamilyWidening"/>) plus the cross-family cases
    /// (<see cref="IsCrossFamilyWidening"/>, #535). Deliberately stricter than <see cref="TypeCoercion"/>'s
    /// common-type search — only these total, unambiguous, lossless cases count (so, e.g., <c>long → double</c>
    /// is never treated as a widening). Returns <see langword="false"/> when the types are equal (an equal
    /// type is not a <i>change</i>).
    /// </summary>
    public static bool IsSanctionedWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return IsSameFamilyWidening(from, to) || IsCrossFamilyWidening(from, to);
    }

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a Delta-sanctioned widening <b>within the
    /// same type family</b> (Delta PROTOCOL.md "Type Widening" → "Supported Type Changes"): integral
    /// <c>byte</c> → <c>short</c> → <c>int</c> → <c>long</c> (any strictly wider rank), <c>float</c> →
    /// <c>double</c>, or <c>decimal(p,s)</c> → <c>decimal(p',s')</c> grow-only. This is the subset the
    /// partition-column guard applies rewrite-free (#537); cross-family partition widening stays deferred.
    /// </summary>
    public static bool IsSameFamilyWidening(DataType from, DataType to)
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
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a <b>cross-family</b> widening this build
    /// applies (Delta PROTOCOL.md "Type Widening" → "Supported Type Changes", #535):
    /// <list type="bullet">
    /// <item><c>byte</c>/<c>short</c>/<c>int</c> → <c>double</c> (Delta sanctions integral→double up to
    /// <c>int</c>; <c>long → double</c> is <b>lossy</b> — a 64-bit integer exceeds double's 53-bit mantissa —
    /// so it is NOT sanctioned);</item>
    /// <item><c>byte</c>/<c>short</c>/<c>int</c>/<c>long</c> → <c>decimal(p,s)</c>, but ONLY when the target
    /// decimal can represent the <b>full integral range</b> losslessly — its integer-digit capacity
    /// (<c>p − s</c>) must be at least the digits the widest source value needs (byte 3, short 5, int 10,
    /// long 19). A decimal too narrow to hold the source is NOT a sanctioned widening (it would truncate) and
    /// stays fail-closed.</item>
    /// </list>
    /// </summary>
    public static bool IsCrossFamilyWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        int fromRank = IntegralRank(from);
        if (fromRank < 0)
        {
            return false;
        }

        // byte/short/int → double (rank 0..2); long → double is lossy and NOT sanctioned by Delta.
        if (to is DoubleType)
        {
            return fromRank <= 2;
        }

        // byte/short/int/long → decimal, but only when the decimal's integer-digit capacity (p − s) can hold
        // every value of the source integral type without truncation.
        if (to is DecimalType decimalType)
        {
            return (decimalType.Precision - decimalType.Scale) >= IntegralDigits(fromRank);
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
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a Delta-sanctioned widening in <b>any</b> of
    /// this build's recognized families — the applied allowlist (<see cref="IsSanctionedWidening"/>, which now
    /// spans both same-family and cross-family #535) or the date→timestamp_ntz deferral
    /// (<see cref="IsDeferredWidening"/>, #533). This is the single <b>union</b> predicate the
    /// <see cref="DeltaSchemaEnforcer"/> partition-column guard uses so a partition-column type change is
    /// classified as an honest rewrite-free widening deferral (#537) for <b>every</b> sanctioned family — even
    /// the ones the partition guard defers (cross-family and date→timestamp) — and so a future family added to
    /// any classifier is covered automatically without editing the guard. Independent of enablement (mirrors
    /// the deferral classifier).
    /// </summary>
    public static bool IsAnySanctionedWidening(DataType from, DataType to) =>
        IsSanctionedWidening(from, to)
        || IsDeferredWidening(from, to);

    private static int IntegralRank(DataType type) => type switch
    {
        ByteType => 0,
        ShortType => 1,
        IntegerType => 2,
        LongType => 3,
        _ => -1,
    };

    // The number of base-10 integer digits the widest magnitude of an integral rank needs, so a decimal's
    // integer-digit capacity (precision − scale) must be at least this to hold the full range losslessly:
    //   byte  [-128, 127]                    → 3   (128)
    //   short [-32768, 32767]                → 5   (32768)
    //   int   [-2147483648, 2147483647]      → 10  (2147483648)
    //   long  [-9223372036854775808, …5807]  → 19  (9223372036854775808)
    private static int IntegralDigits(int rank) => rank switch
    {
        0 => 3,
        1 => 5,
        2 => 10,
        _ => 19,
    };
}
