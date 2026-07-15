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
/// <c>byte</c>/<c>short</c>/<c>int</c>/<c>long</c> → <c>decimal(p,s)</c> — but only when the target decimal's
/// integer-digit capacity <c>p − s</c> meets Delta's threshold, which is keyed to the source's <b>Parquet
/// physical type</b> (NOT its value range): <c>byte</c>/<c>short</c>/<c>int</c> are all stored as
/// <c>INT32</c> ⇒ <c>p − s ≥ 10</c>; <c>long</c> is <c>INT64</c> ⇒ <c>p − s ≥ 20</c>. A decimal narrower than
/// that threshold is NOT a sanctioned widening and stays fail-closed.</item>
/// <item><b>Temporal</b> (<see cref="IsTemporalWidening"/>, #533): <c>date → timestamp_ntz</c> — Delta
/// widens <c>date</c> to <c>timestamp without timezone</c> (INT64 epoch-micros); a narrow <c>date</c>
/// (INT32 epoch-day) file is promoted to midnight-of-date micros on read, and it is
/// schema-evolution-eligible (<see cref="IsSchemaEvolutionWidening"/>) so a wider-typed append evolves the
/// table to <c>timestamp_ntz</c> and writes native <c>timestamp_ntz</c> (Parquet
/// <c>TIMESTAMP(isAdjustedToUTC=false)</c>).</item>
/// </list></para>
///
/// <para><b>Deliberately NOT applied</b> (protocol-sanctioned but out of this build's scope, kept
/// fail-closed): nested (array-element/map-key/value) widening — it would need a <c>fieldPath</c> in
/// <c>delta.typeChanges</c> and the Parquet read path does not read nested types at all (tracked as the #535
/// follow-up, #546). Note <c>date → timestamp</c> with a timezone (LTZ <see cref="TimestampType"/>) is NOT a
/// sanctioned widening at all — Delta only widens <c>date → timestamp_ntz</c> — so a <c>date → timestamp</c>
/// change is rejected as a plain incompatible type, not a deferred widening.</para>
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
        return IsSameFamilyWidening(from, to)
            || IsCrossFamilyWidening(from, to)
            || IsTemporalWidening(from, to);
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
    /// decimal meets Delta's threshold, which is keyed to the source's <b>Parquet physical type</b> (NOT its
    /// value-range digit count): <c>byte</c>/<c>short</c>/<c>int</c> are all stored as <c>INT32</c> → the
    /// reader supports <c>INT32 → Decimal(10,0)</c> and wider, so <c>p − s ≥ 10</c>; <c>long</c> is
    /// <c>INT64</c> → <c>Decimal(20,0)</c> and wider, so <c>p − s ≥ 20</c> (Spark
    /// <c>DecimalType.forType(Int)=Decimal(10,0)</c>/<c>forType(Long)=Decimal(20,0)</c>; Delta
    /// <c>d.isWiderThan(IntegerType)</c>/<c>isWiderThan(LongType)</c>). A decimal narrower than that threshold
    /// is NOT a sanctioned widening and stays fail-closed.</item>
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

        // byte/short/int/long → decimal, but only when the decimal's integer-digit capacity (p − s) meets
        // Delta's Parquet-physical-type threshold (INT32 sources → ≥ 10, INT64 → ≥ 20). See
        // MinDecimalIntegerDigits.
        if (to is DecimalType decimalType)
        {
            return (decimalType.Precision - decimalType.Scale) >= MinDecimalIntegerDigits(fromRank);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is the Delta-sanctioned <b>temporal</b>
    /// widening this build applies and read-promotes: <c>date → timestamp_ntz</c> (#533). Delta widens
    /// <c>date</c> to <c>timestamp without timezone</c> (<see cref="TimestampNtzType"/>), the timezone-<i>less</i>
    /// INT64 epoch-micros type — <b>not</b> to the timezone-adjusted <see cref="TimestampType"/> (a
    /// <c>date → timestamp</c> LTZ change is NOT sanctioned and is rejected as a plain incompatible type). On
    /// read, a narrow <c>date</c> (INT32 epoch-day) file is promoted to <c>timestamp_ntz</c> (INT64 epoch-micros
    /// at midnight of the date, no session offset); on a wider-typed append it is schema-evolution-eligible
    /// (<see cref="IsSchemaEvolutionWidening"/>), so the table schema evolves to <c>timestamp_ntz</c> and new
    /// rows are written as native <c>timestamp_ntz</c> (Parquet <c>TIMESTAMP(isAdjustedToUTC=false)</c> via
    /// <c>DateTimeFormat.Timestamp</c>).
    /// </summary>
    public static bool IsTemporalWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return from is DateType && to is TimestampNtzType;
    }

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a widening this build <b>auto-applies when a
    /// wider-typed write evolves the table schema</b> (an append/overwrite whose column is wider than the
    /// table's) — the mirror of Spark's <c>TypeWidening.isTypeChangeSupportedForSchemaEvolution</c>, a
    /// deliberate <b>subset</b> of the full <see cref="IsSanctionedWidening"/> allowlist: the same-family cases
    /// (<see cref="IsSameFamilyWidening"/>: integral <c>byte→…→long</c>, <c>float→double</c>, grow-only decimal)
    /// plus <c>date → timestamp_ntz</c> (<see cref="IsTemporalWidening"/>, #533). The cross-family cases
    /// (<see cref="IsCrossFamilyWidening"/>, #535) are read-promotable and ALTER-applicable but NOT
    /// schema-evolution-eligible, so they are excluded here and stay read-only.
    /// </summary>
    public static bool IsSchemaEvolutionWidening(DataType from, DataType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return IsSameFamilyWidening(from, to) || IsTemporalWidening(from, to);
    }

    /// <summary>
    /// Whether <paramref name="from"/> → <paramref name="to"/> is a Delta-sanctioned widening in <b>any</b> of
    /// this build's recognized families — the full applied allowlist (<see cref="IsSanctionedWidening"/>, which
    /// spans same-family, cross-family #535, and the temporal <c>date→timestamp_ntz</c> #533 cases). This is the
    /// single <b>union</b> predicate the <see cref="DeltaSchemaEnforcer"/> partition-column guard uses so a
    /// partition-column type change is classified as an honest rewrite-free widening deferral (#537) for
    /// <b>every</b> sanctioned family — even the ones the partition guard defers (cross-family and
    /// date→timestamp_ntz) — and so a future family added to any classifier is covered automatically without
    /// editing the guard. Independent of enablement.
    /// </summary>
    public static bool IsAnySanctionedWidening(DataType from, DataType to) =>
        IsSanctionedWidening(from, to);

    private static int IntegralRank(DataType type) => type switch
    {
        ByteType => 0,
        ShortType => 1,
        IntegerType => 2,
        LongType => 3,
        _ => -1,
    };

    // The minimum decimal integer-digit capacity (precision − scale) Delta requires for an integral→decimal
    // widening. Delta keys this to the source's Parquet PHYSICAL storage type, NOT its value-range digit
    // count: byte/short/int are all stored as INT32 → the Parquet reader supports INT32 → Decimal(10,0) and
    // wider, so p − s ≥ 10; long is INT64 → Decimal(20,0) and wider, so p − s ≥ 20. (Spark models
    // these as DecimalType.forType(Int) = Decimal(10,0) and forType(Long) = Decimal(20,0); Delta's
    // isTypeChangeSupported requires d.isWiderThan(IntegerType) / d.isWiderThan(LongType) respectively —
    // delta-io/delta TypeWidening.scala + DecimalType.scala. NB: 10/20 exceed the mathematical digit counts
    // 10/19, so e.g. long → decimal(19,0) — lossless by value but keyed to INT64 — is NOT sanctioned.)
    private static int MinDecimalIntegerDigits(int rank) => rank <= 2 ? 10 : 20;
}
