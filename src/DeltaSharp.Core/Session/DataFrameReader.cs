using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// An entry point for reading data into a <see cref="DataFrame"/>, equivalent to Apache Spark's
/// <c>DataFrameReader</c> obtained from <c>spark.read</c>. It is a <b>mutable fluent builder</b>
/// (Spark parity): <see cref="Schema(StructType)"/> and the <c>Option</c> overloads stage read
/// configuration and return the same reader, and a terminal load method — <see cref="Parquet(string)"/>
/// in M1 — returns a <see cref="DataFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// Building a reader and staging options does <b>no</b> work; <see cref="Parquet(string)"/> records an
/// <b>unresolved</b> Parquet scan in the plan and opens <b>no</b> file (STORY-04.1.2 / #158, AC2). The
/// Parquet <i>reader</i> itself is EPIC-05 (Delta/Parquet storage): an action that analyzes a Parquet
/// scan fails with a deterministic diagnostic naming EPIC-05 ownership. Instances are created by
/// <see cref="SparkSession.Read"/> (a fresh reader per access), so the constructor is non-public. See
/// <c>docs/engineering/design/read-door.md</c>.
/// </para>
/// <para>
/// <b>Option-key validation is finalize-time.</b> <see cref="Option(string, string)"/> and its
/// overloads only stage keys; whether a key is a recognized read option is checked at the terminal
/// <see cref="Parquet(string)"/> call (Spark parity: options are validated when the read is finalized),
/// so options may be staged in any order.
/// </para>
/// </remarks>
public sealed class DataFrameReader
{
    /// <summary>The Spark Parquet read options DeltaSharp recognizes and records onto the scan node for
    /// the EPIC-05 reader to honor. Any other key is rejected at <see cref="Parquet(string)"/> time
    /// (AC3). Case-insensitive, matching Spark's case-insensitive option map.</summary>
    private static readonly FrozenSet<string> RecognizedParquetOptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mergeSchema",
            "recursiveFileLookup",
            "pathGlobFilter",
            "modifiedBefore",
            "modifiedAfter",
            "datetimeRebaseMode",
            "int96RebaseMode",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Delta read options DeltaSharp recognizes at <see cref="Load(string)"/> time and records
    /// onto the scan for the read door (#499) to honor: the two time-travel dimensions
    /// (<c>versionAsOf</c>/<c>timestampAsOf</c>, mutually exclusive — the analyzer rejects both). Any other
    /// key is rejected at <see cref="Load(string)"/> time (Spark parity). Case-insensitive.</summary>
    private static readonly FrozenSet<string> RecognizedDeltaOptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "versionAsOf",
            "timestampAsOf",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Spark's Delta source name — the one file-format read the read door (#499) resolves
    /// end-to-end (base + time travel). Any other <see cref="Format(string)"/> stays deferred to EPIC-05.</summary>
    private const string DeltaFormat = "delta";

    /// <summary>Spark's default read source (<c>spark.sql.sources.default</c>): a <see cref="Load(string)"/>
    /// with no <see cref="Format(string)"/> reads <c>parquet</c>, which in M1 routes to the EPIC-05 deferral,
    /// matching Spark's default while keeping the Parquet reader out of #499.</summary>
    private const string DefaultFormat = "parquet";

    private readonly SparkSession _session;
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private StructType? _userSchema;
    private string _format = DefaultFormat;

    internal DataFrameReader(SparkSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Specifies the input data-source format, mirroring Spark's <c>DataFrameReader.format(source)</c>
    /// (for example <c>"delta"</c>). The format is recorded for the terminal <see cref="Load(string)"/>;
    /// only <c>delta</c> is resolved end-to-end in #499 (base + time-travel reads), while any other format
    /// (including the default <c>parquet</c>) stays deferred to EPIC-05. Returns this reader for chaining.
    /// </summary>
    /// <param name="source">The format name (case-insensitive; recorded as given).</param>
    /// <returns>This reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is <see langword="null"/> or empty.</exception>
    public DataFrameReader Format(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        _format = source;
        return this;
    }

    /// <summary>
    /// Loads a path with the configured <see cref="Format(string)"/>, mirroring Spark's
    /// <c>DataFrameReader.load(path)</c>. This is the read door's format-generic <b>finalizer</b>: it
    /// validates the staged options and returns a <see cref="DataFrame"/> backed by an <b>unresolved</b>
    /// file scan. It opens <b>no</b> file and reads <b>no</b> log (the lazy invariant); the eager Delta-log
    /// metadata read that binds the schema and pins the snapshot version happens at analysis (an action).
    /// </summary>
    /// <remarks>
    /// For <c>delta</c> the recognized options are the time-travel dimensions <c>versionAsOf</c> and
    /// <c>timestampAsOf</c> (mutually exclusive; the analyzer rejects both — including when one rides on the
    /// <c>@v&lt;n&gt;</c> / <c>@yyyyMMddHHmmssSSS</c> path suffix). For any other format the recognized set is
    /// the Parquet read options, but the scan stays deferred to EPIC-05 at analysis.
    /// </remarks>
    /// <param name="path">The source path (may carry a <c>@v&lt;n&gt;</c>/<c>@…</c> time-travel suffix for delta).</param>
    /// <returns>A <see cref="DataFrame"/> over the unresolved file scan.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null/empty, or a staged option is not
    /// recognized for the configured format.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public DataFrame Load(string path)
    {
        _session.EnsureNotStopped(nameof(Load));
        ArgumentException.ThrowIfNullOrEmpty(path);
        bool isDelta = string.Equals(_format, DeltaFormat, StringComparison.OrdinalIgnoreCase);
        FrozenSet<string> recognized = isDelta ? RecognizedDeltaOptions : RecognizedParquetOptions;
        IReadOnlyDictionary<string, string> options = CanonicalizeOptions(recognized, _format);
        return new DataFrame(
            _session,
            new UnresolvedFileRelation(_format, path, options, _userSchema));
    }

    /// <summary>
    /// Specifies the read schema, mirroring Spark's <c>DataFrameReader.schema(StructType)</c>. The
    /// schema is recorded for the EPIC-05 reader to honor (avoiding schema inference). Returns this
    /// reader for chaining.
    /// </summary>
    /// <param name="schema">The read schema.</param>
    /// <returns>This reader.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <see langword="null"/>.</exception>
    public DataFrameReader Schema(StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _userSchema = schema;
        return this;
    }

    /// <summary>Adds a string read option, mirroring Spark's <c>option(String, String)</c>. Keys are
    /// case-insensitive. Returns this reader for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public DataFrameReader Option(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _options[key] = value;
        return this;
    }

    /// <summary>Adds a boolean read option, mirroring Spark's <c>option(String, Boolean)</c> (stored as
    /// <c>"true"</c>/<c>"false"</c>). Returns this reader for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public DataFrameReader Option(string key, bool value) =>
        Option(key, value ? "true" : "false");

    /// <summary>Adds a 64-bit integer read option, mirroring Spark's <c>option(String, Long)</c> (stored
    /// as its invariant-culture string). Returns this reader for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public DataFrameReader Option(string key, long value) =>
        Option(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Adds a double-precision read option, mirroring Spark's <c>option(String, Double)</c>
    /// (stored as its round-trippable invariant-culture string). Returns this reader for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This reader.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public DataFrameReader Option(string key, double value) =>
        Option(key, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Loads a Parquet file or directory, mirroring Spark's <c>DataFrameReader.parquet(path)</c>. This
    /// is the reader's <b>finalizer</b>: it validates the staged options and returns a
    /// <see cref="DataFrame"/> backed by an <b>unresolved</b> Parquet scan. It opens <b>no</b> file
    /// (AC2); the Parquet reader is EPIC-05, so an action that analyzes the returned frame fails with a
    /// deterministic diagnostic naming EPIC-05.
    /// </summary>
    /// <param name="path">The Parquet path.</param>
    /// <returns>A <see cref="DataFrame"/> over the unresolved Parquet scan.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty, or
    /// a staged option is not a recognized Parquet read option (the message names the option and the
    /// alternative — AC3).</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    public DataFrame Parquet(string path)
    {
        _session.EnsureNotStopped(nameof(Parquet));
        ArgumentException.ThrowIfNullOrEmpty(path);
        IReadOnlyDictionary<string, string> options = CanonicalizeOptions(RecognizedParquetOptions, "Parquet");
        return new DataFrame(
            _session,
            new UnresolvedFileRelation("parquet", path, options, _userSchema));
    }

    /// <summary>Validates every staged option against <paramref name="recognized"/> and returns the
    /// options keyed by their canonical recognized spelling, so option handling is case-insensitive
    /// (Spark parity) yet the scan node carries deterministic keys. An unrecognized key raises a
    /// deterministic diagnostic naming the offending option and the documented alternative (AC3).</summary>
    private IReadOnlyDictionary<string, string> CanonicalizeOptions(FrozenSet<string> recognized, string format)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> option in _options)
        {
            // TryGetValue returns the stored element under its canonical (recognized) spelling, so option
            // handling stays case-insensitive (Spark parity) while the scan node carries deterministic
            // keys — an O(1) frozen-set lookup instead of an O(n) scan.
            if (!recognized.TryGetValue(option.Key, out string? match))
            {
                string supported = string.Join(
                    ", ", recognized.OrderBy(o => o, StringComparer.OrdinalIgnoreCase));
                throw new ArgumentException(
                    $"Unsupported {format} reader option '{option.Key}'. DeltaSharp M1 recognizes these "
                    + $"{format} read options: [{supported}]. To fix a read schema, call "
                    + "DataFrameReader.Schema(StructType) instead of an option.");
            }

            canonical[match] = option.Value;
        }

        return canonical;
    }
}
