using System.Collections.Generic;
using System.Threading;

namespace DeltaSharp;

/// <summary>
/// The entry point to programming DeltaSharp with the DataFrame and SQL APIs, equivalent to Apache
/// Spark's <c>SparkSession</c>. Create one with the fluent <see cref="Builder"/>:
/// <c>SparkSession.Builder().AppName("app").GetOrCreate()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Creating a session and configuring it perform <b>no</b> query work — DeltaSharp's central
/// invariant is that transformations are lazy and only actions execute. The execution backend
/// (ADR-0001) is merely <i>recorded</i> at creation (see <see cref="ExecutionBackend"/>) for a later
/// action to consume.
/// </para>
/// <para>
/// A session is <b>active</b> until <see cref="Stop"/> or <see cref="Dispose"/> is called, after
/// which it is <b>stopped</b> and its data doors (<see cref="Read"/>, <see cref="Sql(string)"/>)
/// throw <see cref="SessionStoppedException"/>. Active-session tracking is thread-local
/// (<see cref="GetActiveSession"/>) and the default session is process-wide
/// (<see cref="GetDefaultSession"/>), matching Spark. See
/// <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </para>
/// <example>
/// <code>
/// using SparkSession spark = SparkSession.Builder()
///     .AppName("getting-started")
///     .Config("spark.deltasharp.execution.backend", "auto")
///     .GetOrCreate();
///
/// string app = spark.Conf.Get("spark.app.name");   // "getting-started"
/// // spark.Read / spark.Sql(...) arrive in later stories (#158/#159).
/// spark.Stop();                                     // or rely on the using block / Dispose.
/// </code>
/// </example>
/// </remarks>
public sealed class SparkSession : IDisposable
{
    /// <summary>The configuration key holding the application name (Spark's <c>spark.app.name</c>).</summary>
    internal const string AppNameConfigKey = "spark.app.name";

    /// <summary>The configuration key selecting the recorded execution backend (DeltaSharp-specific).</summary>
    internal const string ExecutionBackendConfigKey = "spark.deltasharp.execution.backend";

    private const int StateActive = 0;
    private const int StateStopped = 1;

    private static readonly object _globalLock = new();
    private static SparkSession? _defaultSession;

    private static readonly ThreadLocal<SparkSession?> _activeSession = new(() => null);

    private readonly RuntimeConfig _conf;
    private readonly string? _appName;
    private int _state = StateActive;

    private SparkSession(IReadOnlyDictionary<string, string> options, ExecutionBackend backend)
    {
        _conf = new RuntimeConfig(this, options);
        ExecutionBackend = backend;
        _appName = options.TryGetValue(AppNameConfigKey, out string? name) ? name : null;
    }

    /// <summary>Creates a fluent <see cref="SparkSessionBuilder"/> (Spark's <c>SparkSession.builder()</c>).</summary>
    /// <returns>A new builder.</returns>
    public static SparkSessionBuilder Builder() => new();

    /// <summary>
    /// Gets the session's runtime configuration (Spark's <c>spark.conf</c>): the values supplied to
    /// the builder plus any runtime mutations. Reads remain valid after the session is stopped.
    /// </summary>
    public RuntimeConfig Conf => _conf;

    /// <summary>
    /// Gets the execution backend recorded for this session (DeltaSharp-specific; Spark has no
    /// equivalent). The value is inert until an action consumes it and defaults to
    /// <see cref="ExecutionBackend.Auto"/>.
    /// </summary>
    public ExecutionBackend ExecutionBackend { get; }

    /// <summary>Indicates whether the session is still active (not stopped or disposed).</summary>
    public bool IsActive => Volatile.Read(ref _state) == StateActive;

    /// <summary>
    /// Gets a <see cref="DataFrameReader"/> for reading data into a <see cref="DataFrame"/> (Spark's
    /// <c>spark.read</c>).
    /// </summary>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    /// <exception cref="NotSupportedException">
    /// The session is active but the read door is not yet available in M1; it ships in STORY-04.1.2
    /// (#158).
    /// </exception>
    public DataFrameReader Read
    {
        get
        {
            EnsureNotStopped(nameof(Read));
            throw new NotSupportedException(
                "SparkSession.Read is not yet available in this milestone; it ships in " +
                "STORY-04.1.2 (#158).");
        }
    }

    /// <summary>
    /// Executes a SQL query and returns its result as a <see cref="DataFrame"/> (Spark's
    /// <c>spark.sql</c>).
    /// </summary>
    /// <param name="sqlText">The SQL text to execute.</param>
    /// <returns>A <see cref="DataFrame"/> backed by the query's logical plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sqlText"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    /// <exception cref="NotSupportedException">
    /// The session is active but the SQL door is not yet available in M1; it ships in STORY-04.1.3
    /// (#159).
    /// </exception>
    public DataFrame Sql(string sqlText)
    {
        EnsureNotStopped(nameof(Sql));
        ArgumentNullException.ThrowIfNull(sqlText);
        throw new NotSupportedException(
            "SparkSession.Sql is not yet available in this milestone; it ships in " +
            "STORY-04.1.3 (#159).");
    }

    /// <summary>
    /// Stops this session, transitioning it to the terminal stopped state (Spark's <c>stop()</c>).
    /// Clears this thread's active session when it is this one, and the process-wide default when it
    /// is this one. Idempotent.
    /// </summary>
    public void Stop()
    {
        // Idempotent: only the first transition does work.
        if (Interlocked.Exchange(ref _state, StateStopped) == StateStopped)
        {
            return;
        }

        if (_activeSession.Value == this)
        {
            _activeSession.Value = null;
        }

        lock (_globalLock)
        {
            if (ReferenceEquals(_defaultSession, this))
            {
                _defaultSession = null;
            }
        }
    }

    /// <summary>Stops the session (the .NET-idiomatic equivalent of <see cref="Stop"/>). Idempotent.</summary>
    public void Dispose() => Stop();

    /// <summary>
    /// Returns the active <see cref="SparkSession"/> for the current thread, or <see langword="null"/>
    /// when none is set (Spark's <c>getActiveSession</c>).
    /// </summary>
    /// <returns>The thread-local active session, or <see langword="null"/>.</returns>
    public static SparkSession? GetActiveSession() => _activeSession.Value;

    /// <summary>Sets the active <see cref="SparkSession"/> for the current thread (Spark's <c>setActiveSession</c>).</summary>
    /// <param name="session">The session to mark active on this thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public static void SetActiveSession(SparkSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _activeSession.Value = session;
    }

    /// <summary>Clears the active <see cref="SparkSession"/> for the current thread (Spark's <c>clearActiveSession</c>).</summary>
    public static void ClearActiveSession() => _activeSession.Value = null;

    /// <summary>
    /// Returns the process-wide default <see cref="SparkSession"/>, or <see langword="null"/> when
    /// none exists (Spark's <c>getDefaultSession</c>).
    /// </summary>
    /// <returns>The default session, or <see langword="null"/>.</returns>
    public static SparkSession? GetDefaultSession()
    {
        lock (_globalLock)
        {
            return _defaultSession;
        }
    }

    /// <summary>Clears the process-wide default <see cref="SparkSession"/> (Spark's <c>clearDefaultSession</c>).</summary>
    public static void ClearDefaultSession()
    {
        lock (_globalLock)
        {
            _defaultSession = null;
        }
    }

    /// <summary>
    /// Throws <see cref="SessionStoppedException"/> when the session has been stopped or disposed.
    /// The single lifecycle guard shared by every data door.
    /// </summary>
    /// <param name="memberName">The member being invoked, for the error message.</param>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    internal void EnsureNotStopped(string memberName)
    {
        if (Volatile.Read(ref _state) == StateStopped)
        {
            throw SessionStoppedException.ForMember(memberName, _appName);
        }
    }

    /// <summary>
    /// Implements <see cref="SparkSessionBuilder.GetOrCreate"/>: reuse the active then default
    /// session, applying <paramref name="options"/> to it, otherwise create and register a new one.
    /// </summary>
    internal static SparkSession GetOrCreate(IReadOnlyDictionary<string, string> options)
    {
        // Parse/validate backend selection first so an invalid value fails fast and deterministically,
        // regardless of whether a session will be reused or created. No engine work happens here.
        ExecutionBackend backend = ParseExecutionBackend(options);

        lock (_globalLock)
        {
            SparkSession? active = _activeSession.Value;
            if (active is not null && active.IsActive)
            {
                ApplyOptions(active, options);
                return active;
            }

            if (_defaultSession is not null && _defaultSession.IsActive)
            {
                ApplyOptions(_defaultSession, options);
                _activeSession.Value = _defaultSession;
                return _defaultSession;
            }

            SparkSession session = new(options, backend);
            _defaultSession = session;
            _activeSession.Value = session;
            return session;
        }
    }

    private static void ApplyOptions(SparkSession session, IReadOnlyDictionary<string, string> options)
    {
        foreach (KeyValuePair<string, string> option in options)
        {
            session._conf.SetInternal(option.Key, option.Value);
        }
    }

    private static ExecutionBackend ParseExecutionBackend(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue(ExecutionBackendConfigKey, out string? raw) || string.IsNullOrEmpty(raw))
        {
            return ExecutionBackend.Auto;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => ExecutionBackend.Auto,
            "interpreted" => ExecutionBackend.Interpreted,
            "compiled" => ExecutionBackend.Compiled,
            _ => throw new ArgumentException(
                $"Unrecognized '{ExecutionBackendConfigKey}' value '{raw}'. Expected one of: " +
                "auto, interpreted, compiled.",
                ExecutionBackendConfigKey),
        };
    }
}
