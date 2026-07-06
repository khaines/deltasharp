using System.Collections.Immutable;
using System.Globalization;

namespace DeltaSharp.Storage.Delta;

/// <summary>The JSON scalar kind of a Delta statistic value, preserved so a min/max round-trips and
/// can later be interpreted against its column's <c>DataType</c> without re-reading the log.</summary>
internal enum DeltaStatKind
{
    /// <summary>A JSON string (also how Delta encodes date/timestamp/decimal bounds).</summary>
    String,

    /// <summary>A JSON integral number.</summary>
    Long,

    /// <summary>A JSON non-integral number.</summary>
    Double,

    /// <summary>A JSON boolean.</summary>
    Boolean,
}

/// <summary>
/// An immutable, type-preserving Delta statistic scalar (a per-column <c>minValues</c>/<c>maxValues</c>
/// entry). Retains both the JSON <see cref="Kind"/> and the invariant-culture <see cref="Raw"/> text so
/// the value round-trips losslessly and a consumer can coerce it to the column's schema type when it
/// plans a scan (design §2.10.5). Statistics are <b>advisory</b>: they enable file pruning but pruning
/// must never claim correctness from skipping (STORY-05.2.3 AC2), so an unparseable/absent stat simply
/// yields no pruning benefit — never a wrong answer.
/// </summary>
internal sealed record DeltaStatValue(DeltaStatKind Kind, string Raw)
{
    /// <summary>Builds a <see cref="DeltaStatKind.String"/> value.</summary>
    public static DeltaStatValue OfString(string value) => new(DeltaStatKind.String, value);

    /// <summary>Builds a <see cref="DeltaStatKind.Long"/> value.</summary>
    public static DeltaStatValue OfLong(long value) =>
        new(DeltaStatKind.Long, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Builds a <see cref="DeltaStatKind.Double"/> value.</summary>
    public static DeltaStatValue OfDouble(double value) =>
        new(DeltaStatKind.Double, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Builds a <see cref="DeltaStatKind.Boolean"/> value.</summary>
    public static DeltaStatValue OfBoolean(bool value) =>
        new(DeltaStatKind.Boolean, value ? "true" : "false");
}

/// <summary>
/// Parsed per-file statistics from an <c>add</c> action's <c>stats</c> JSON (design §2.10.1). Holds the
/// row count, the (top-level) column min/max bounds and null counts, and the <c>tightBounds</c> flag.
///
/// <para><b>v1 scope:</b> top-level (leaf) columns keyed by name. Nested-struct statistics (Delta encodes
/// them as nested objects) are tolerated and skipped — because statistics are advisory (pruning only,
/// §2.10.5 / STORY-05.2.3 AC2), skipping them costs a pruning opportunity, never correctness. Nested-stat
/// support is a tracked follow-up.</para>
/// </summary>
internal sealed record FileStatistics(
    long? NumRecords,
    ImmutableSortedDictionary<string, DeltaStatValue> MinValues,
    ImmutableSortedDictionary<string, DeltaStatValue> MaxValues,
    ImmutableSortedDictionary<string, long> NullCount,
    bool? TightBounds)
{
    /// <summary>Empty statistics (no <c>stats</c> present on the <c>add</c>).</summary>
    public static FileStatistics Empty { get; } = new(
        NumRecords: null,
        MinValues: ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal),
        MaxValues: ImmutableSortedDictionary<string, DeltaStatValue>.Empty.WithComparers(StringComparer.Ordinal),
        NullCount: ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal),
        TightBounds: null);
}
