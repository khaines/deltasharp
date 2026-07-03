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
/// <see cref="SparkSession.Read"/>, so the constructor is non-public. See
/// <c>docs/engineering/design/read-door.md</c>.
/// </para>
/// </remarks>
public sealed class DataFrameReader
{
    /// <summary>The Spark Parquet read options DeltaSharp recognizes and records onto the scan node for
    /// the EPIC-05 reader to honor. Any other key is rejected at <see cref="Parquet(string)"/> time
    /// (AC3). Case-insensitive, matching Spark's case-insensitive option map.</summary>
    private static readonly IReadOnlySet<string> RecognizedParquetOptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mergeSchema",
            "recursiveFileLookup",
            "pathGlobFilter",
            "modifiedBefore",
            "modifiedAfter",
            "datetimeRebaseMode",
            "int96RebaseMode",
        };

    private readonly SparkSession _session;
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private StructType? _userSchema;

    internal DataFrameReader(SparkSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

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
        IReadOnlyDictionary<string, string> options = CanonicalizeOptions(RecognizedParquetOptions);
        return new DataFrame(
            _session,
            new UnresolvedFileRelation("parquet", path, options, _userSchema));
    }

    /// <summary>Validates every staged option against <paramref name="recognized"/> and returns the
    /// options keyed by their canonical recognized spelling, so option handling is case-insensitive
    /// (Spark parity) yet the scan node carries deterministic keys. An unrecognized key raises a
    /// deterministic diagnostic naming the offending option and the documented alternative (AC3).</summary>
    private IReadOnlyDictionary<string, string> CanonicalizeOptions(IReadOnlySet<string> recognized)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> option in _options)
        {
            string? match = recognized.FirstOrDefault(
                o => string.Equals(o, option.Key, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                string supported = string.Join(
                    ", ", recognized.OrderBy(o => o, StringComparer.OrdinalIgnoreCase));
                throw new ArgumentException(
                    $"Unsupported Parquet reader option '{option.Key}'. DeltaSharp M1 recognizes these "
                    + $"Parquet read options: [{supported}]. To fix a read schema, call "
                    + "DataFrameReader.Schema(StructType) instead of an option.",
                    nameof(recognized));
            }

            canonical[match] = option.Value;
        }

        return canonical;
    }
}
