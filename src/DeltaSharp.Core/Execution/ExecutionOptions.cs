using System;
using System.Globalization;
using System.Threading;
using DeltaSharp.Types;

namespace DeltaSharp.Execution;

/// <summary>
/// The execution-time controls a <see cref="DataFrame"/> action threads across the
/// <see cref="IQueryExecutor"/> seam (STORY-04.6.4 / #176, discharging the deferred
/// <see href="https://github.com/khaines/deltasharp/issues/416">#416</see> seam): a
/// <see cref="System.Threading.CancellationToken"/>, an optional timeout, and optional driver
/// result / operator memory bounds. It is <b>internal</b> because <see cref="IQueryExecutor"/> is
/// internal; the executor lane (<c>DeltaSharp.Executor</c>) reads it through internals visibility.
/// </summary>
/// <remarks>
/// A bound left <see langword="null"/> is unbounded (the feature is disabled), so an action with no
/// configuration behaves exactly as before this story. Bounds are sourced from
/// <see cref="SparkSession"/> config keys (see <see cref="From"/>); the token comes from the action
/// call site. See <c>docs/engineering/design/execution-boundaries.md</c>.
/// </remarks>
internal sealed class ExecutionOptions
{
    /// <summary>The default: no cancellation, no timeout, no bounds.</summary>
    public static ExecutionOptions Default { get; } = new();

    /// <summary>Cooperative cancellation observed at batch/row boundaries.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>The action timeout, or <see langword="null"/> for no timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The maximum rows any materialization boundary may accumulate, or <see langword="null"/>.</summary>
    public long? MaxResultRows { get; init; }

    /// <summary>The maximum estimated bytes any materialization boundary may accumulate, or <see langword="null"/>.</summary>
    public long? MaxResultBytes { get; init; }

    /// <summary>The per-run operator memory budget in bytes, or <see langword="null"/> for unbounded.</summary>
    public long? MemoryBudgetBytes { get; init; }

    /// <summary>
    /// The ANSI strictness lens (#603) sourced from the session's <c>spark.sql.ansi.enabled</c>:
    /// <see cref="AnsiMode.Ansi"/> (default) reports arithmetic overflow / invalid cast, while
    /// <see cref="AnsiMode.Legacy"/> wraps to SQL <c>NULL</c>. Threaded into the physical planner so the
    /// query path AND the write-door (CHECK / invariant) enforcement share the session's strictness.
    /// </summary>
    public AnsiMode AnsiMode { get; init; } = AnsiMode.Ansi;

    /// <summary>
    /// Builds options from a session's live configuration and the action's cancellation token. Reads
    /// the <c>spark.deltasharp.execution.*</c> keys through the live <see cref="RuntimeConfig"/> so a
    /// runtime <c>Conf.Set</c> is honored on the next action.
    /// </summary>
    /// <param name="session">The owning session.</param>
    /// <param name="cancellationToken">The action's cancellation token.</param>
    /// <returns>The parsed execution options.</returns>
    /// <exception cref="ArgumentException">A configured bound/timeout value is not a non-negative integer.</exception>
    public static ExecutionOptions From(SparkSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        RuntimeConfig conf = session.Conf;

        long? timeoutMs = ReadPositiveLong(conf, SparkSession.ExecutionTimeoutMsConfigKey);
        if (timeoutMs is { } configuredMs && configuredMs > MaxTimeoutMilliseconds)
        {
            // CancellationTokenSource.CancelAfter caps the delay at ~49.7 days (uint.MaxValue - 1 ms) and
            // throws ArgumentOutOfRangeException above it. Clamp here — before the driver arms the timer —
            // so a huge configured timeout degrades to "effectively no timeout" rather than crashing the
            // action with a raw framework exception outside the stage-attributed driver try (#176 #7).
            timeoutMs = MaxTimeoutMilliseconds;
        }

        return new ExecutionOptions
        {
            CancellationToken = cancellationToken,
            Timeout = timeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            MaxResultRows = ReadPositiveLong(conf, SparkSession.MaxResultRowsConfigKey),
            MaxResultBytes = ReadPositiveLong(conf, SparkSession.MaxResultBytesConfigKey),
            MemoryBudgetBytes = ReadPositiveLong(conf, SparkSession.MemoryBudgetBytesConfigKey),
            AnsiMode = SparkSession.ReadAnsiMode(conf),
        };
    }

    /// <summary>
    /// The largest timeout (in milliseconds) a linked <see cref="CancellationTokenSource"/> can be armed
    /// with via <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> (<c>uint.MaxValue - 1</c>,
    /// ~49.7 days). A configured timeout above this is clamped to it in <see cref="From"/>.
    /// </summary>
    internal const long MaxTimeoutMilliseconds = uint.MaxValue - 1;

    // Absent -> null (unbounded/disabled). Present & parseable & > 0 -> the value. Present & <= 0 ->
    // null (explicitly disabled). Present & unparseable/negative -> fail-fast ArgumentException,
    // matching the execution-backend key's set-time validation discipline (RuntimeConfig.Set).
    private static long? ReadPositiveLong(RuntimeConfig conf, string key)
    {
        string? raw = conf.Get(key, null);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
        {
            throw new ArgumentException(
                $"Configuration key '{key}' must be a non-negative integer, but was '{raw}'.", key);
        }

        return value == 0 ? null : value;
    }
}
