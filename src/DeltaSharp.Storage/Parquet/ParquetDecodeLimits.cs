using System.Globalization;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Operator-tunable limits for the <see cref="ParquetFileReader"/>'s <b>eager-decode</b> memory guard
/// (design §5.4 C-DECODE). The reader decodes each row group whole, so these bounds cap the transient
/// decode/allocation footprint: a crafted footer (inflating the decompressed size or row count) fails
/// closed rather than driving an out-of-memory allocation.
///
/// <para>Both are configurable so an operator can <b>lower</b> the ceiling below the smallest provisioned
/// (possibly multi-tenant) executor budget, or <b>raise</b> it for a trusted large-row-group workload —
/// keeping the safe defaults (<see cref="DefaultMaxRowGroupDecodedBytes"/> = 4&#160;GiB,
/// <see cref="DefaultMaxDecompressionRatio"/> = 1000:1) when unset. Values are validated at construction so
/// a misconfiguration fails fast rather than disabling the guard.</para>
/// </summary>
internal sealed record ParquetDecodeLimits
{
    /// <summary>The default absolute per-row-group eager-decode ceiling (4&#160;GiB).</summary>
    public const long DefaultMaxRowGroupDecodedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The default per-column-chunk decompression-ratio ceiling (1000:1) — real Parquet encodings
    /// stay well under this, so a higher declared ratio is a decompression bomb.</summary>
    public const long DefaultMaxDecompressionRatio = 1000;

    /// <summary>Creates decode limits, validating the bounds are usable (a non-positive ceiling, a ratio
    /// below 1, or a non-positive time budget would disable or invert a guard).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRowGroupDecodedBytes"/> is not
    /// positive, <paramref name="maxDecompressionRatio"/> is less than 1, or <paramref name="decodeTimeBudget"/>
    /// (when supplied) is not positive or exceeds <see cref="BoundedDecode.MaxBudget"/> (a budget that large
    /// disables the DoS control and is rejected as a misconfiguration).</exception>
    public ParquetDecodeLimits(
        long maxRowGroupDecodedBytes = DefaultMaxRowGroupDecodedBytes,
        long maxDecompressionRatio = DefaultMaxDecompressionRatio,
        TimeSpan? decodeTimeBudget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRowGroupDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDecompressionRatio, 1);
        TimeSpan budget = decodeTimeBudget ?? BoundedDecode.DefaultBudget;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.Ticks, nameof(decodeTimeBudget));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, BoundedDecode.MaxBudget, nameof(decodeTimeBudget));
        MaxRowGroupDecodedBytes = maxRowGroupDecodedBytes;
        MaxDecompressionRatio = maxDecompressionRatio;
        DecodeTimeBudget = budget;
    }

    /// <summary>The absolute per-row-group eager-decode memory ceiling (bytes), applied to both the declared
    /// decompressed bytes and the bytes a row group's declared row count would eagerly materialize.</summary>
    public long MaxRowGroupDecodedBytes { get; }

    /// <summary>The maximum plausible ratio of a column chunk's declared decompressed to compressed bytes;
    /// a higher declared ratio is rejected as a decompression bomb.</summary>
    public long MaxDecompressionRatio { get; }

    /// <summary>The wall-clock deadline for the data-file decode. It is an AGGREGATE per-read deadline: the
    /// open (<c>ParquetReader.CreateAsync</c> + footer materialization) AND every row-group decode of a single
    /// <c>ReadAsync</c>/<c>GetRowCountAsync</c> call share this ONE budget measured from the start of the call,
    /// so the worst-case total decode time is bounded by the single budget regardless of the
    /// (attacker-controlled) row-group count — not multiplied per step. Parquet.Net 6.0.3 can be driven into
    /// non-terminating, cancellation-ignoring work by one corrupted byte (#647/#699), so on expiry the reader
    /// fails closed with <see cref="StorageErrorKind.DecodeBudgetExceeded"/> (a resource fault distinct from
    /// <see cref="StorageErrorKind.CorruptData"/> — a wall-clock stall is not proof the bytes are corrupt)
    /// rather than hanging. Defaults to <see cref="BoundedDecode.DefaultBudget"/>. NOTE: today this is set only
    /// from tests — the production config seam that would let an operator lower it per latency-sensitive tier is
    /// tracked in #803; do not rely on a per-tier override until that lands.</summary>
    public TimeSpan DecodeTimeBudget { get; }

    /// <summary>The safe defaults used when no limits are supplied.</summary>
    public static ParquetDecodeLimits Default { get; } =
        new(DefaultMaxRowGroupDecodedBytes, DefaultMaxDecompressionRatio);

    /// <summary>A concise, invariant-culture description for diagnostics.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"ParquetDecodeLimits(maxRowGroupDecodedBytes={MaxRowGroupDecodedBytes}, "
        + $"maxDecompressionRatio={MaxDecompressionRatio}, decodeTimeBudget={DecodeTimeBudget})");
}
