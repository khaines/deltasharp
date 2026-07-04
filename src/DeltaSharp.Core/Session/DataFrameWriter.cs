using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp;

/// <summary>
/// An entry point for writing a <see cref="DataFrame"/> to an external sink, equivalent to Apache
/// Spark's <c>DataFrameWriter</c> obtained from <c>df.write</c>. It is a <b>mutable fluent builder</b>
/// (Spark parity): <see cref="Format(string)"/>, <see cref="Mode(SaveMode)"/>/<see cref="Mode(string)"/>,
/// the <c>Option</c> overloads, <see cref="Options(IReadOnlyDictionary{string, string})"/>, and
/// <see cref="PartitionBy(string[])"/> stage write configuration and return the same writer, and a
/// terminal <see cref="Save()"/>/<see cref="Save(string)"/> executes the write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration is lazy; <see cref="Save()"/> is the eager action</b> (STORY-04.6.3 / #175, AC2 /
/// ADR-0001). Every <c>Format</c>/<c>Mode</c>/<c>Option</c>/<c>PartitionBy</c> call only updates the
/// writer's in-memory intent and returns the writer — no plan is analyzed, no row is read, no sink is
/// opened. <see cref="Save()"/> is the only member that executes: it builds a <b>write logical intent</b>
/// (a <c>WriteToSource</c> over this frame's analyzed plan) and drives the analyze→plan→execute pipeline
/// exactly once, draining the result into the sink. Instances are created by <see cref="DataFrame.Write"/>
/// (a fresh writer per access), so the constructor is non-public. See
/// <c>docs/engineering/design/write-door.md</c>.
/// </para>
/// <para>
/// <b>Format routing.</b> The M1 write door executes an engine-backed <b>local</b> sink end-to-end
/// (AC1). A recognized-but-deferred format (Delta/Parquet) routes ownership to EPIC-05 with a
/// deterministic analysis diagnostic (AC4); any other format is a deterministic unsupported-format
/// diagnostic (AC3). Both fire during analysis at <see cref="Save()"/>, before any output is committed.
/// A writer that never calls <see cref="Format(string)"/> uses Spark's default source (<c>parquet</c>),
/// so a bare <c>Save</c> routes to the EPIC-05 deferral.
/// </para>
/// </remarks>
public sealed class DataFrameWriter
{
    private readonly DataFrame _frame;
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private string _format = WriteFormats.Default;
    private SaveMode _mode = SaveMode.ErrorIfExists;
    private IReadOnlyList<string> _partitionColumns = System.Array.Empty<string>();
    private string? _path;

    internal DataFrameWriter(DataFrame frame) =>
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));

    /// <summary>Sets the sink format, mirroring Spark's <c>DataFrameWriter.format(source)</c>. Lazy: it
    /// only records the format and returns this writer.</summary>
    /// <param name="source">The data source format name (for example <c>"memory"</c>).</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is <see langword="null"/> or empty.</exception>
    public DataFrameWriter Format(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        _format = source;
        return this;
    }

    /// <summary>Sets the save mode, mirroring Spark's <c>DataFrameWriter.mode(SaveMode)</c>. Lazy: it
    /// only records the mode and returns this writer.</summary>
    /// <param name="saveMode">How the write behaves when the target exists.</param>
    /// <returns>This writer.</returns>
    public DataFrameWriter Mode(SaveMode saveMode)
    {
        _mode = saveMode;
        return this;
    }

    /// <summary>Sets the save mode from its Spark string name (case-insensitive), mirroring Spark's
    /// <c>DataFrameWriter.mode(String)</c>: <c>append</c>, <c>overwrite</c>, <c>ignore</c>, and
    /// <c>error</c>/<c>errorifexists</c>/<c>default</c>. Lazy: it only records the parsed mode. An
    /// unrecognized mode string is a deterministic diagnostic (AC3), raised here (Spark parity) before
    /// <see cref="Save()"/> so no output is ever committed for a bad mode.</summary>
    /// <param name="saveMode">The Spark save-mode name.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="saveMode"/> is <see langword="null"/>/empty or
    /// not a recognized save mode (the message names the offending value and the recognized modes).</exception>
    public DataFrameWriter Mode(string saveMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(saveMode);
        _mode = saveMode.ToLowerInvariant() switch
        {
            "append" => SaveMode.Append,
            "overwrite" => SaveMode.Overwrite,
            "ignore" => SaveMode.Ignore,
            "error" or "errorifexists" or "default" => SaveMode.ErrorIfExists,
            _ => throw new ArgumentException(
                $"Unsupported save mode '{saveMode}'. DeltaSharp recognizes these save modes: "
                + "[append, overwrite, ignore, error (errorifexists/default)].",
                nameof(saveMode)),
        };
        return this;
    }

    /// <summary>Adds a string write option, mirroring Spark's <c>option(String, String)</c>. Keys are
    /// case-insensitive. Lazy: it only records the option and returns this writer.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public DataFrameWriter Option(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _options[key] = value;
        return this;
    }

    /// <summary>Adds a boolean write option, mirroring Spark's <c>option(String, Boolean)</c> (stored as
    /// <c>"true"</c>/<c>"false"</c>). Returns this writer for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public DataFrameWriter Option(string key, bool value) =>
        Option(key, value ? "true" : "false");

    /// <summary>Adds a 64-bit integer write option, mirroring Spark's <c>option(String, Long)</c> (stored
    /// as its invariant-culture string). Returns this writer for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public DataFrameWriter Option(string key, long value) =>
        Option(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Adds a double-precision write option, mirroring Spark's <c>option(String, Double)</c>
    /// (stored as its round-trippable invariant-culture string). Returns this writer for chaining.</summary>
    /// <param name="key">The option key.</param>
    /// <param name="value">The option value.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public DataFrameWriter Option(string key, double value) =>
        Option(key, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Adds a set of string write options, mirroring Spark's <c>options(Map)</c>. Keys are
    /// case-insensitive; later keys overwrite earlier ones. Lazy: it only records the options.</summary>
    /// <param name="options">The options to add.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option key is <see langword="null"/> or empty.</exception>
    public DataFrameWriter Options(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (KeyValuePair<string, string> option in options)
        {
            Option(option.Key, option.Value);
        }

        return this;
    }

    /// <summary>Sets the partition columns, mirroring Spark's <c>DataFrameWriter.partitionBy(colNames)</c>.
    /// Lazy: it only records the partition-column names (as write intent) and returns this writer.</summary>
    /// <param name="colNames">The partition column names, in order.</param>
    /// <returns>This writer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="colNames"/> or any element is <see langword="null"/>.</exception>
    public DataFrameWriter PartitionBy(params string[] colNames)
    {
        ArgumentNullException.ThrowIfNull(colNames);
        if (colNames.Any(name => name is null))
        {
            throw new ArgumentNullException(nameof(colNames), "Partition column names must not be null.");
        }

        _partitionColumns = (string[])colNames.Clone();
        return this;
    }

    /// <summary>
    /// Executes the write to the previously staged path (or a path-less sink), mirroring Spark's
    /// <c>DataFrameWriter.save()</c>. This is the writer's <b>eager action</b>: it builds a write logical
    /// intent over this frame's plan, then analyzes, plans, and executes it exactly once, draining the
    /// result into the sink (AC1). An unsupported format/mode, or an EPIC-05-deferred format, produces a
    /// deterministic diagnostic during analysis — before any output is committed (AC3/AC4).
    /// </summary>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// reported a runtime failure; <see cref="QueryExecutionException.Stage"/> names the failed stage.</exception>
    public void Save() => Save(CancellationToken.None);

    /// <summary>The cancellable overload of <see cref="Save()"/>. Cancellation and any session-configured
    /// timeout are observed cooperatively while the result drains into the sink.</summary>
    /// <param name="cancellationToken">A token that cooperatively cancels the write.</param>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">A configured execution timeout elapsed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// reported a runtime failure; <see cref="QueryExecutionException.Stage"/> names the failed stage.</exception>
    public void Save(CancellationToken cancellationToken) =>
        _frame.ExecuteWrite(BuildSink(_path), cancellationToken);

    /// <summary>
    /// Sets the output path and executes the write, mirroring Spark's <c>DataFrameWriter.save(path)</c>
    /// (equivalent to staging the path then calling <see cref="Save()"/>). Setting the path mutates the
    /// writer (Spark parity); the execution is the eager action.
    /// </summary>
    /// <param name="path">The output path (the sink target).</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="InvalidOperationException">This frame is not bound to a <see cref="SparkSession"/>.</exception>
    /// <exception cref="SessionStoppedException">The owning session has been stopped or disposed.</exception>
    /// <exception cref="QueryExecutionException">No execution backend is registered, or the backend
    /// reported a runtime failure; <see cref="QueryExecutionException.Stage"/> names the failed stage.</exception>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        Save();
    }

    /// <summary>Snapshots the writer's staged intent into an immutable logical <see cref="SinkDescriptor"/>.
    /// A defensive copy of the mutable options/partition-columns keeps the built plan immutable even if
    /// the writer is mutated further afterwards. When no explicit <see cref="Save(string)"/> path was
    /// staged, a <c>path</c> option (case-insensitive, Spark parity: <c>save(path)</c> is
    /// <c>option("path", path).save()</c>) is reconciled into the descriptor's path so
    /// <c>Option("path", p).Save()</c> and <c>Save(p)</c> resolve to the same target; an explicit
    /// <see cref="Save(string)"/> path takes precedence.</summary>
    private SinkDescriptor BuildSink(string? path)
    {
        string? effectivePath = path;
        if (effectivePath is null
            && _options.TryGetValue("path", out string? optionPath)
            && !string.IsNullOrEmpty(optionPath))
        {
            effectivePath = optionPath;
        }

        return new(_format, _mode, effectivePath, tableIdentifier: null, partitionColumns: _partitionColumns, options: _options);
    }
}
