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

    /// <summary>Creates decode limits, validating both bounds are usable (a non-positive ceiling or a ratio
    /// below 1 would disable or invert the guard).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRowGroupDecodedBytes"/> is not
    /// positive, or <paramref name="maxDecompressionRatio"/> is less than 1.</exception>
    public ParquetDecodeLimits(
        long maxRowGroupDecodedBytes = DefaultMaxRowGroupDecodedBytes,
        long maxDecompressionRatio = DefaultMaxDecompressionRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRowGroupDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDecompressionRatio, 1);
        MaxRowGroupDecodedBytes = maxRowGroupDecodedBytes;
        MaxDecompressionRatio = maxDecompressionRatio;
    }

    /// <summary>The absolute per-row-group eager-decode memory ceiling (bytes), applied to both the declared
    /// decompressed bytes and the bytes a row group's declared row count would eagerly materialize.</summary>
    public long MaxRowGroupDecodedBytes { get; }

    /// <summary>The maximum plausible ratio of a column chunk's declared decompressed to compressed bytes;
    /// a higher declared ratio is rejected as a decompression bomb.</summary>
    public long MaxDecompressionRatio { get; }

    /// <summary>The safe defaults used when no limits are supplied.</summary>
    public static ParquetDecodeLimits Default { get; } =
        new(DefaultMaxRowGroupDecodedBytes, DefaultMaxDecompressionRatio);

    /// <summary>A concise, invariant-culture description for diagnostics.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"ParquetDecodeLimits(maxRowGroupDecodedBytes={MaxRowGroupDecodedBytes}, maxDecompressionRatio={MaxDecompressionRatio})");
}
