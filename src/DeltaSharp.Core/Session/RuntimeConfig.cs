using System.Collections.Generic;
using System.Globalization;

namespace DeltaSharp;

/// <summary>
/// Runtime configuration for a <see cref="SparkSession"/>, equivalent to Apache Spark's
/// <c>RuntimeConfig</c> reached through <c>spark.conf</c>. Provides typed read and write access to
/// the session's string-valued configuration.
/// </summary>
/// <remarks>
/// <para>
/// All configuration values are stored as strings, matching Spark. Reads (<see cref="Get(string)"/>,
/// <see cref="GetAll"/>, <see cref="Contains"/>) are valid for the lifetime of the process even after
/// the owning session is stopped, because reading configuration is not query work. Mutating the
/// configuration with <see cref="Set(string, string)"/> on a stopped or disposed session throws
/// <see cref="SessionStoppedException"/>.
/// </para>
/// <para>
/// Reads are thread-safe. See <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </para>
/// <example>
/// <code>
/// using SparkSession spark = SparkSession.Builder()
///     .AppName("etl")
///     .Config("spark.deltasharp.execution.backend", "interpreted")
///     .GetOrCreate();
///
/// string app = spark.Conf.Get("spark.app.name");          // "etl"
/// string backend = spark.Conf.Get("spark.deltasharp.execution.backend", "auto");
/// </code>
/// </example>
/// </remarks>
public sealed class RuntimeConfig
{
    private readonly SparkSession _session;
    private readonly Dictionary<string, string> _values;
    private readonly object _gate = new();

    /// <summary>
    /// Test-only deterministic interleaving seam, invoked by <see cref="Set(string, string)"/> after
    /// its stopped-check has passed and immediately before the dictionary write. <see langword="null"/>
    /// (and therefore inert) in production. It exists so the F1 TOCTOU concurrency test can force the
    /// exact dangerous interleaving — pause a writer between the stopped-check and the write, drive a
    /// concurrent <see cref="SparkSession.Stop"/>, and observe whether the stop was able to complete
    /// before the write. With the fix the seam runs <i>under the gate</i>, so a racing <c>Stop</c>
    /// (which needs the same gate) cannot complete before the write; without the fix it can, exposing
    /// the stopped-session mutation. See <c>SparkSessionConcurrencyTests</c>.
    /// </summary>
    internal volatile Action? StopRaceProbe;

    internal RuntimeConfig(SparkSession session, IReadOnlyDictionary<string, string> initial)
    {
        _session = session;
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in initial)
        {
            _values[pair.Key] = pair.Value;
        }
    }

    /// <summary>
    /// The monitor guarding the value map. Exposed to the owning <see cref="SparkSession"/> so that
    /// <see cref="SparkSession.Stop"/> can perform its lifecycle state transition under this same gate
    /// (inside <c>_globalLock</c>, preserving the strict <c>_globalLock → _gate</c> lock order), making
    /// the transition mutually exclusive with <see cref="Set(string, string)"/>'s in-gate
    /// stopped-check + mutation. This closes the F1 TOCTOU window where a concurrent <c>Stop</c> could
    /// land between <c>Set</c>'s stopped-check and its dictionary write.
    /// </summary>
    internal object SyncRoot => _gate;

    /// <summary>Gets the value of the configuration key.</summary>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configured value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">
    /// The key is not set (Apache Spark raises <c>NoSuchElementException</c> for the same case).
    /// </exception>
    public string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_values.TryGetValue(key, out string? value))
            {
                return value;
            }
        }

        throw new KeyNotFoundException(
            $"Configuration key '{key}' is not set. Use Get(key, defaultValue) to supply a fallback.");
    }

    /// <summary>Gets the value of the configuration key, or a default when it is not set.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="defaultValue">The value returned when the key is not set.</param>
    /// <returns>The configured value, or <paramref name="defaultValue"/> when the key is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public string? Get(string key, string? defaultValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _values.TryGetValue(key, out string? value) ? value : defaultValue;
        }
    }

    /// <summary>Indicates whether the configuration key is set.</summary>
    /// <param name="key">The configuration key.</param>
    /// <returns><see langword="true"/> when the key has a value; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _values.ContainsKey(key);
        }
    }

    /// <summary>Returns an immutable snapshot of all configured key/value pairs.</summary>
    /// <returns>A snapshot dictionary; later mutations are not reflected in the returned instance.</returns>
    public IReadOnlyDictionary<string, string> GetAll()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }

    /// <summary>Sets a string configuration value on the live session.</summary>
    /// <remarks>
    /// <para>
    /// The stopped-check and the dictionary write happen together under the configuration gate so they
    /// are <b>atomic</b> with respect to <see cref="SparkSession.Stop"/> (which takes the same gate for
    /// its state transition). A <c>Set</c> that observes the session active under the gate cannot be
    /// concurrently stopped before it writes; a <c>Set</c> sequenced after the stop transition observes
    /// the stopped state under the gate and throws — so a stopped session can never retain a raced
    /// value (closes the F1 TOCTOU window).
    /// </para>
    /// <para>
    /// <b>Typed-key validation (fail-fast).</b> When <paramref name="key"/> is a typed configuration
    /// key whose value is constrained — currently the execution-backend key
    /// <c>spark.deltasharp.execution.backend</c> — the value is parsed and validated <i>at set time</i>
    /// and an invalid value throws <see cref="System.ArgumentException"/> immediately, before anything
    /// is stored. This keeps an invalid backend from ever being persisted (so it can never surface only
    /// later when <see cref="SparkSession.ExecutionBackend"/> is read); the property's parse remains the
    /// read-side guard. Validation runs before the lifecycle check, matching the builder/<c>GetOrCreate</c>
    /// fail-fast path.
    /// </para>
    /// </remarks>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is a typed configuration key and <paramref name="value"/> is invalid for it
    /// (for example an unrecognized <c>spark.deltasharp.execution.backend</c> value).
    /// </exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        // Fail-fast: reject an invalid typed value (e.g. a bad execution backend) at set time, before
        // the lifecycle check or any storage, so the invalid value can never be persisted.
        SparkSession.ValidateTypedConfigValue(key, value);

        lock (_gate)
        {
            // Re-check inside the gate, immediately before mutating: Stop() transitions the session
            // state under this same gate, so observing the session active here guarantees it cannot be
            // stopped before the write below completes (F1 atomicity).
            _session.EnsureNotStopped("Conf.Set");
            StopRaceProbe?.Invoke();
            _values[key] = value;
        }
    }

    /// <summary>Sets a Boolean configuration value (stored as <c>"true"</c>/<c>"false"</c>).</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    /// <summary>Sets an integer configuration value (stored using the invariant culture).</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Set(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Sets a floating-point configuration value (stored using the invariant culture, round-trip
    /// <c>"R"</c> format). Symmetric with <see cref="SparkSessionBuilder.Config(string, double)"/>.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Set(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Internal write used by session creation to seed the parsed configuration without a lifecycle
    /// check (the session is being constructed and is not yet stopped).
    /// </summary>
    internal void SetInternal(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = value;
        }
    }
}
