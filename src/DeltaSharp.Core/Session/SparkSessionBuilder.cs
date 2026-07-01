using System.Collections.Generic;
using System.Globalization;

namespace DeltaSharp;

/// <summary>
/// A fluent builder for creating or retrieving a <see cref="SparkSession"/>, equivalent to Apache
/// Spark's <c>SparkSession.Builder</c>. Obtain one from <see cref="SparkSession.Builder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors Spark's <c>builder().appName(...).config(...).getOrCreate()</c> pattern. The builder is
/// <b>mutable and not thread-safe</b>; configure and call <see cref="GetOrCreate"/> from a single
/// thread. Building a session does no query work — see
/// <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </para>
/// <example>
/// <code>
/// using SparkSession spark = SparkSession.Builder()
///     .AppName("word-count")
///     .Config("spark.deltasharp.execution.backend", "interpreted")
///     .GetOrCreate();
/// </code>
/// </example>
/// </remarks>
public sealed class SparkSessionBuilder
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);

    internal SparkSessionBuilder()
    {
    }

    /// <summary>
    /// Sets the application name, recorded under the <c>spark.app.name</c> configuration key
    /// (Spark's <c>appName</c>).
    /// </summary>
    /// <param name="name">The application name; must be non-empty.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public SparkSessionBuilder AppName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Application name must be a non-empty string.", nameof(name));
        }

        _options[SparkSession.AppNameConfigKey] = name;
        return this;
    }

    /// <summary>Sets a string configuration value (Spark's <c>config(key, value)</c>).</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The configuration value.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    public SparkSessionBuilder Config(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _options[key] = value;
        return this;
    }

    /// <summary>Sets a Boolean configuration value, stored as <c>"true"</c>/<c>"false"</c>.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The configuration value.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public SparkSessionBuilder Config(string key, bool value)
        => Config(key, value ? "true" : "false");

    /// <summary>Sets an integer configuration value, stored using the invariant culture.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The configuration value.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public SparkSessionBuilder Config(string key, long value)
        => Config(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Sets a floating-point configuration value, stored using the invariant culture.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The configuration value.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public SparkSessionBuilder Config(string key, double value)
        => Config(key, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Returns the existing active or default <see cref="SparkSession"/> if one exists, applying this
    /// builder's configuration to it; otherwise creates, registers, and returns a new session.
    /// </summary>
    /// <returns>A usable, active <see cref="SparkSession"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The <c>spark.deltasharp.execution.backend</c> config value is not one of <c>auto</c>,
    /// <c>interpreted</c>, or <c>compiled</c>.
    /// </exception>
    /// <remarks>
    /// Matches Spark's <c>getOrCreate</c> reuse semantics: an existing session is preferred (the
    /// thread-local active session first, then the process-wide default), and this builder's config is
    /// applied to that existing session's runtime configuration rather than constructing a new
    /// session. See <c>docs/engineering/design/sparksession-lifecycle.md</c>.
    /// </remarks>
    public SparkSession GetOrCreate() => SparkSession.GetOrCreate(_options);
}
