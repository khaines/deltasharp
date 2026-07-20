using System.Collections;
using System.Collections.Generic;
using System.Threading;
using DeltaSharp.Analysis;
using DeltaSharp.Execution;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Sql;
using DeltaSharp.Types;

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
/// which it is <b>stopped</b> and its data doors (<see cref="Read"/>, <see cref="Sql(string)"/>,
/// <see cref="CreateDataFrame(System.Collections.IEnumerable)"/>) throw
/// <see cref="SessionStoppedException"/>. Active-session tracking is thread-local
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
/// DataFrame df = spark.Sql("SELECT * FROM t");      // lazy plan; #158 Read / #159 Sql doors are open.
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

    /// <summary>The action-timeout key, in milliseconds (STORY-04.6.4 / #176); unset/&lt;=0 disables the timeout.</summary>
    internal const string ExecutionTimeoutMsConfigKey = "spark.deltasharp.execution.timeoutMs";

    /// <summary>The driver result row cap (STORY-04.6.4 / #176); unset/&lt;=0 is unbounded.</summary>
    internal const string MaxResultRowsConfigKey = "spark.deltasharp.execution.maxResultRows";

    /// <summary>The driver result byte cap (STORY-04.6.4 / #176); unset/&lt;=0 is unbounded.</summary>
    internal const string MaxResultBytesConfigKey = "spark.deltasharp.execution.maxResultBytes";

    /// <summary>The per-run operator memory budget in bytes (STORY-04.6.4 / #176); unset/&lt;=0 is unbounded.</summary>
    internal const string MemoryBudgetBytesConfigKey = "spark.deltasharp.execution.memoryBudgetBytes";

    /// <summary>Spark's ANSI-mode flag (<c>spark.sql.ansi.enabled</c>): <c>true</c>/unset selects
    /// <see cref="AnsiMode.Ansi"/> (the DeltaSharp default — arithmetic overflow / invalid cast is reported),
    /// <c>false</c> selects <see cref="AnsiMode.Legacy"/> (wrap to SQL <c>NULL</c>). Read per action and
    /// threaded into the physical planner so the query path AND the write-door (CHECK/invariant) share the
    /// session's strictness (#603).</summary>
    internal const string AnsiEnabledConfigKey = "spark.sql.ansi.enabled";

    private const int StateActive = 0;
    private const int StateStopped = 1;

    private static readonly object _globalLock = new();
    private static SparkSession? _defaultSession;

    /// <summary>
    /// The process-wide factory the executor lane (STORY-04.6.2 / #174) installs via
    /// <see cref="RegisterQueryExecutorFactory"/> to build the real backend for every session. It is
    /// <see langword="null"/> until #174 (or a test) registers one, in which case a session falls back
    /// to <see cref="UnsupportedQueryExecutor"/>.
    /// </summary>
    private static volatile Func<SparkSession, IQueryExecutor>? _queryExecutorFactory;

    /// <summary>
    /// The process-wide read-door resolver the executor lane installs via
    /// <see cref="RegisterFileRelationResolver"/> so the analyzer can bind a <c>delta</c> path scan to its
    /// schema + pinned snapshot version (#499). It is <see langword="null"/> until the Executor lane (or a
    /// test) registers one, in which case a <c>delta</c> read fails closed with a clear diagnostic.
    /// </summary>
    private static volatile IFileRelationResolver? _fileRelationResolver;

    private static readonly ThreadLocal<SparkSession?> _activeSession = new(() => null);

    private readonly RuntimeConfig _conf;
    private readonly LocalCatalog _catalog = new();
    private IQueryExecutor? _queryExecutor;
    private int _state = StateActive;

    /// <summary>
    /// Test-only deterministic interleaving seam, invoked by <see cref="GetOrCreate"/> after it has
    /// read this session's <see cref="IsActive"/> state <b>under <c>_globalLock</c></b> and committed
    /// to reusing it, immediately before it returns the session. <see langword="null"/> (and therefore
    /// inert — a single volatile read, no allocation, no call) in production. It exists so the B2
    /// TOCTOU concurrency test can force the exact dangerous interleaving — pause the getter inside the
    /// lock at the reuse window and drive a concurrent <see cref="Stop"/> — and observe whether the
    /// stop's state transition could land before the getter returns. With the fix the seam runs
    /// <i>under <c>_globalLock</c></i>, so a racing <c>Stop</c> (whose transition needs the same lock)
    /// cannot flip the session to stopped while the getter holds it; without the fix it can, exposing
    /// the reuse of a stopped session. Symmetric with <see cref="RuntimeConfig.StopRaceProbe"/> (the F1
    /// seam). See <c>SparkSessionConcurrencyTests</c>.
    /// </summary>
    internal volatile Action? ReuseRaceProbe;

    private SparkSession(IReadOnlyDictionary<string, string> options)
    {
        _conf = new RuntimeConfig(this, options);
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
    /// <remarks>
    /// The backend is <b>derived from the live <see cref="Conf"/></b> (the
    /// <c>spark.deltasharp.execution.backend</c> key) on every read rather than cached at
    /// construction, so it can never diverge from the configuration: a later
    /// <see cref="SparkSessionBuilder.GetOrCreate"/> reuse or a <see cref="RuntimeConfig.Set(string, string)"/>
    /// that changes the backend is always reflected here, and the #174 physical-planning bridge that
    /// consumes this property always observes a value consistent with conf. Reading is a cold-path
    /// parse that performs no engine work. An invalid configured value throws the same deterministic
    /// <see cref="System.ArgumentException"/> that <see cref="SparkSessionBuilder.GetOrCreate"/> raises.
    /// </remarks>
    /// <exception cref="System.ArgumentException">
    /// The configured <c>spark.deltasharp.execution.backend</c> value is not one of <c>auto</c>,
    /// <c>interpreted</c>, or <c>compiled</c>.
    /// </exception>
    public ExecutionBackend ExecutionBackend =>
        ParseExecutionBackend(_conf.Get(ExecutionBackendConfigKey, null));

    /// <summary>Indicates whether the session is still active (not stopped or disposed).</summary>
    public bool IsActive => Volatile.Read(ref _state) == StateActive;

    /// <summary>
    /// The session's in-memory catalog (M1 <see cref="LocalCatalog"/>): the name-to-schema registry
    /// the analyzer binds by-name relation references through when a <see cref="DataFrame"/> action
    /// analyzes its plan. Internal — the catalog seam is an engine implementation detail, not public
    /// API, until the reader/SQL doors (#158/#159) and a metastore-backed catalog land.
    /// </summary>
    internal LocalCatalog Catalog => _catalog;

    /// <summary>
    /// The execution backend this session drives a <see cref="DataFrame"/> action through — the
    /// dependency-inversion seam (<see cref="IQueryExecutor"/>) that lets <c>DeltaSharp.Core</c> execute
    /// without referencing the engine. It is created lazily from the registered factory (or
    /// <see cref="UnsupportedQueryExecutor"/> when none is registered) and can be overridden per session
    /// (used by tests and, later, by the executor lane / #174). Never null.
    /// </summary>
    internal IQueryExecutor QueryExecutor
    {
        get
        {
            // Publish the lazily-built executor atomically. SparkSession is explicitly multi-threaded
            // (see the _globalLock / Volatile / Interlocked discipline above), and a #174 factory may be
            // stateful, so a non-atomic `??=` could hand two threads two different executors. Read once;
            // if unset, build and CAS the field. Under a concurrent first-access race BOTH threads may
            // build (and so invoke the factory) — only PUBLICATION is single: the CAS loser discards its
            // own instance and adopts the winner's, so every caller observes the same executor. Never
            // null: the fail-closed default is UnsupportedQueryExecutor.
            IQueryExecutor? existing = Volatile.Read(ref _queryExecutor);
            if (existing is not null)
            {
                return existing;
            }

            IQueryExecutor created =
                _queryExecutorFactory?.Invoke(this) ?? UnsupportedQueryExecutor.Instance;
            return Interlocked.CompareExchange(ref _queryExecutor, created, null) ?? created;
        }

        set => Volatile.Write(ref _queryExecutor, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Registers (or clears, when <paramref name="factory"/> is <see langword="null"/>) the process-wide
    /// factory that builds the <see cref="IQueryExecutor"/> for every subsequently-resolved session.
    /// The executor lane (STORY-04.6.2 / #174) calls this once at startup so real query execution is
    /// wired without inverting Core ⟂ Engine independence; tests use it (or the per-session
    /// <see cref="QueryExecutor"/> setter) to install a double. Affects only sessions that have not yet
    /// resolved their executor.
    /// </summary>
    /// <param name="factory">The factory to install, or <see langword="null"/> to reset to the
    /// unsupported default.</param>
    internal static void RegisterQueryExecutorFactory(Func<SparkSession, IQueryExecutor>? factory) =>
        _queryExecutorFactory = factory;

    /// <summary>The read-door resolver (#499) the analyzer resolves a <c>delta</c> path scan through, or
    /// <see langword="null"/> when no storage backend is registered (a Core-only process).</summary>
    internal IFileRelationResolver? FileRelationResolver => _fileRelationResolver;

    /// <summary>The session's ANSI lens (#614): <c>spark.sql.ansi.enabled</c> parsed into an
    /// <see cref="AnsiMode"/> (unset -> <see cref="AnsiMode.Ansi"/>). Read live from the current
    /// <see cref="Conf"/> so a runtime <see cref="RuntimeConfig.Set(string, string)"/> is honored on the
    /// next analyze/action. Threaded into the analyzer so output-schema nullability derivation widens an
    /// overflow-capable arithmetic / lossy cast output column to nullable under Legacy.</summary>
    internal AnsiMode AnsiMode => ReadAnsiMode(_conf);

    /// <summary>
    /// Registers (or clears, when <paramref name="resolver"/> is <see langword="null"/>) the process-wide
    /// <see cref="IFileRelationResolver"/> the analyzer resolves <c>delta</c> path scans through (#499). The
    /// Executor lane calls this once at startup (alongside <see cref="RegisterQueryExecutorFactory"/>) so a
    /// <c>read.format("delta").load(path)</c> binds against a real Delta log; tests use it to install a
    /// double. Affects every subsequent analyze pass.
    /// </summary>
    /// <param name="resolver">The resolver to install, or <see langword="null"/> to reset (a Core-only
    /// process where a delta read fails closed).</param>
    internal static void RegisterFileRelationResolver(IFileRelationResolver? resolver) =>
        _fileRelationResolver = resolver;

    /// <summary>
    /// Gets a <see cref="DataFrameReader"/> for reading data into a <see cref="DataFrame"/> (Spark's
    /// <c>spark.read</c>). Each access returns a <b>fresh</b> reader (a new mutable builder), so staged
    /// schema/options on one reader never leak into another.
    /// </summary>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    public DataFrameReader Read
    {
        get
        {
            EnsureNotStopped(nameof(Read));
            return new DataFrameReader(this);
        }
    }

    /// <summary>
    /// Parses a SQL query and returns its result as a <see cref="DataFrame"/> (Spark's
    /// <c>spark.sql</c>), routing SQL through the <b>same</b> planning pipeline as the DataFrame API so
    /// the two converge after parsing/lowering (STORY-04.1.3 / #159).
    /// </summary>
    /// <remarks>
    /// This is a <b>transformation</b>, not an action: the SQL is lexed and lowered into an
    /// <b>unresolved</b> logical plan built from the same shared IR nodes (<c>Project</c>,
    /// <c>Filter</c>, <c>UnresolvedRelation</c>, …) the DataFrame API constructs (AC3), and
    /// <b>no</b> analysis, optimization, or execution runs until a later action (ADR-0001, AC1). The
    /// M1 door implements a small, well-scoped subset — <c>SELECT &lt;cols|*&gt; FROM &lt;relation&gt;
    /// [WHERE &lt;predicate&gt;]</c> with literals, column references, comparisons, arithmetic, and
    /// boolean combinators. Any construct outside it (joins, aggregates, subqueries, function calls,
    /// DDL/DML, set operations, …) raises a deterministic <see cref="SqlParseException"/> at parse
    /// time, before any execution (AC2). The full ANTLR4 frontend arrives in EPIC-07 (ADR-0007). See
    /// <c>docs/engineering/design/sql-door.md</c>.
    /// </remarks>
    /// <param name="sqlText">The SQL text to parse and lower.</param>
    /// <returns>A <see cref="DataFrame"/> backed by the query's unresolved logical plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sqlText"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    /// <exception cref="SqlParseException">
    /// <paramref name="sqlText"/> is malformed (<see cref="SqlParseErrorKind.SyntaxError"/>) or uses a
    /// construct outside the M1 subset (<see cref="SqlParseErrorKind.UnsupportedFeature"/>).
    /// </exception>
    public DataFrame Sql(string sqlText)
    {
        EnsureNotStopped(nameof(Sql));
        ArgumentNullException.ThrowIfNull(sqlText);
        LogicalPlan plan = SqlParser.Parse(sqlText);
        return new DataFrame(this, plan);
    }

    /// <summary>
    /// Creates a <see cref="DataFrame"/> from a local in-memory collection (Spark's
    /// <c>createDataFrame</c>).
    /// </summary>
    /// <param name="data">The local rows to materialize into a <see cref="DataFrame"/>.</param>
    /// <returns>A <see cref="DataFrame"/> backed by the supplied local data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    /// <exception cref="NotSupportedException">
    /// The session is active but this untyped overload requires schema inference, which is out of M1
    /// scope. Supply an explicit schema via
    /// <see cref="CreateDataFrame(IEnumerable{Row}, StructType)"/> instead (STORY-04.1.2 / #158).
    /// </exception>
    public DataFrame CreateDataFrame(IEnumerable data)
    {
        EnsureNotStopped(nameof(CreateDataFrame));
        ArgumentNullException.ThrowIfNull(data);
        throw new NotSupportedException(
            "SparkSession.CreateDataFrame(IEnumerable) requires schema inference, which is out of " +
            "scope for this milestone. Supply an explicit schema via " +
            "CreateDataFrame(IEnumerable<Row>, StructType) (STORY-04.1.2 / #158).");
    }

    /// <summary>
    /// Creates a <see cref="DataFrame"/> from an in-memory sequence of <see cref="Row"/>s with an
    /// explicit schema (Spark's <c>createDataFrame(rows, schema)</c>). The schema is authoritative and
    /// row values are read positionally by ordinal.
    /// </summary>
    /// <remarks>
    /// This is a <b>transformation</b>: it builds a scan (<c>LocalRelation</c>) logical plan and
    /// <b>materializes no rows</b> — the sequence is not enumerated until an action
    /// (<see cref="DataFrame.Collect()"/>, <see cref="DataFrame.Count()"/>, …) runs (STORY-04.1.2 / #158,
    /// AC1). The captured sequence is wrapped in a <b>memoizing snapshot</b>: the first action enumerates
    /// it once and caches an immutable snapshot, and every later action replays that same snapshot. So,
    /// like Spark's <c>createDataFrame(List, schema)</c>, all actions on the returned frame observe
    /// identical, stable rows — mutating the source after the first action, or passing a single-use
    /// iterator, does not change results. (Mutating the source <i>before</i> the first action is still
    /// observed, since laziness defers the snapshot to that first action.)
    /// </remarks>
    /// <param name="data">The in-memory rows. Each row's values are read positionally against
    /// <paramref name="schema"/>.</param>
    /// <param name="schema">The explicit, authoritative schema of the DataFrame.</param>
    /// <returns>A <see cref="DataFrame"/> backed by a <c>LocalRelation</c> scan over the supplied data.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> or <paramref name="schema"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="SessionStoppedException">The session has been stopped or disposed.</exception>
    public DataFrame CreateDataFrame(IEnumerable<Row> data, StructType schema)
    {
        EnsureNotStopped(nameof(CreateDataFrame));
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(schema);
        return new DataFrame(this, new LocalRelation(schema, data));
    }

    /// <summary>
    /// Stops this session, transitioning it to the terminal stopped state (Spark's <c>stop()</c>).
    /// Clears this thread's active session when it is this one, and the process-wide default when it
    /// is this one. Idempotent.
    /// </summary>
    public void Stop()
    {
        // Transition under _globalLock so a session being reused inside GetOrCreate's locked decision
        // cannot be concurrently flipped to stopped (B2 TOCTOU): GetOrCreate observes IsActive and Stop
        // mutates _state under the same lock, making the reuse decision and the lifecycle transition
        // mutually exclusive. Additionally, the state transition runs under the RuntimeConfig gate so it
        // is mutually exclusive with Conf.Set's in-gate stopped-check + mutation (F1 TOCTOU): a Set that
        // observes the session active under the gate cannot be stopped before it writes, and a Set
        // sequenced after this transition sees the stopped state under the gate and throws. Lock order
        // is strictly _globalLock -> _gate (never the inverse), so it is deadlock-free. The fast-path
        // readers (IsActive / EnsureNotStopped) stay lock-free via Volatile.Read; Interlocked.Exchange
        // keeps that write visible and idempotent.
        bool transitioned;
        lock (_globalLock)
        {
            lock (_conf.SyncRoot)
            {
                transitioned = Interlocked.Exchange(ref _state, StateStopped) != StateStopped;
            }

            if (transitioned && ReferenceEquals(_defaultSession, this))
            {
                _defaultSession = null;
            }
        }

        // Idempotent: only the first transition clears this thread's active slot (thread-local, so it
        // needs no global lock).
        if (transitioned && _activeSession.Value == this)
        {
            _activeSession.Value = null;
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
            // Derive the app name from the live conf (never a cached field) so the message stays
            // consistent with configuration even after a GetOrCreate reuse changed spark.app.name.
            throw SessionStoppedException.ForMember(memberName, _conf.Get(AppNameConfigKey, null));
        }
    }

    /// <summary>
    /// Implements <see cref="SparkSessionBuilder.GetOrCreate"/>: reuse the active then default
    /// session, applying <paramref name="options"/> to it, otherwise create and register a new one.
    /// </summary>
    internal static SparkSession GetOrCreate(IReadOnlyDictionary<string, string> options)
    {
        // Parse/validate backend selection first, BEFORE acquiring _globalLock and UNCONDITIONALLY
        // (whether a session will be reused or created), so an invalid value fails fast and
        // deterministically — including on the reuse path. No engine work happens here. The parsed
        // value is not cached on the session; SparkSession.ExecutionBackend is derived from conf.
        _ = ParseExecutionBackend(
            options.TryGetValue(ExecutionBackendConfigKey, out string? raw) ? raw : null);

        // #603: validate a builder-supplied spark.sql.ansi.enabled with the SAME fail-fast discipline as the
        // backend key (ApplyOptions -> SetInternal bypasses the Conf.Set-time validation), so a bad value fails
        // at session creation rather than being deferred to the first action's ReadAnsiMode.
        _ = ParseAnsiEnabled(
            options.TryGetValue(AnsiEnabledConfigKey, out string? ansiRaw) ? ansiRaw : null);

        lock (_globalLock)
        {
            SparkSession? active = _activeSession.Value;
            if (active is not null && active.IsActive)
            {
                ApplyOptions(active, options);
                // Test seam: fire at the reuse window — decision made under _globalLock, session still
                // held active by the lock, about to be returned. Inert (null) in production.
                active.ReuseRaceProbe?.Invoke();
                return active;
            }

            if (_defaultSession is not null && _defaultSession.IsActive)
            {
                ApplyOptions(_defaultSession, options);
                _activeSession.Value = _defaultSession;
                // Test seam: see the active-path note above.
                _defaultSession.ReuseRaceProbe?.Invoke();
                return _defaultSession;
            }

            SparkSession session = new(options);
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

    /// <summary>
    /// Validates a typed configuration value at <see cref="RuntimeConfig.Set(string, string)"/> time
    /// (fail-fast). For the execution-backend key the value is parsed eagerly so an invalid value
    /// throws the same deterministic <see cref="ArgumentException"/> at set time that
    /// <see cref="GetOrCreate"/> and the <see cref="ExecutionBackend"/> read raise, ensuring an invalid
    /// backend can never be stored. Unconstrained keys are accepted unchanged.
    /// </summary>
    internal static void ValidateTypedConfigValue(string key, string value)
    {
        if (string.Equals(key, ExecutionBackendConfigKey, StringComparison.Ordinal))
        {
            _ = ParseExecutionBackend(value);
        }
        else if (string.Equals(key, AnsiEnabledConfigKey, StringComparison.Ordinal))
        {
            _ = ParseAnsiEnabled(value);
        }
    }

    // Parses spark.sql.ansi.enabled (case-insensitive true/false). Unset/empty defaults to true — ANSI is
    // the DeltaSharp default (ADR-0007/0008). A non-boolean value is rejected fail-fast — at Conf.Set time
    // (ValidateTypedConfigValue) AND at builder GetOrCreate — with the same discipline as the backend key.
    private static bool ParseAnsiEnabled(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException(
                $"Unrecognized '{AnsiEnabledConfigKey}' value '{raw}'. Expected a boolean: true or false.",
                AnsiEnabledConfigKey),
        };
    }

    /// <summary>Reads the session's <c>spark.sql.ansi.enabled</c> into the planner's <see cref="AnsiMode"/>
    /// (unset -> <see cref="AnsiMode.Ansi"/>). Read per action (via <c>ExecutionOptions.From</c>) so a runtime
    /// <see cref="RuntimeConfig.Set(string, string)"/> is honored on the next query (#603).</summary>
    internal static AnsiMode ReadAnsiMode(RuntimeConfig conf)
    {
        ArgumentNullException.ThrowIfNull(conf);
        return ParseAnsiEnabled(conf.Get(AnsiEnabledConfigKey, null)) ? AnsiMode.Ansi : AnsiMode.Legacy;
    }

    private static ExecutionBackend ParseExecutionBackend(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
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
