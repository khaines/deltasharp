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

    internal RuntimeConfig(SparkSession session, IReadOnlyDictionary<string, string> initial)
    {
        _session = session;
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in initial)
        {
            _values[pair.Key] = pair.Value;
        }
    }

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
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _session.EnsureNotStopped("Conf.Set");
        lock (_gate)
        {
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
