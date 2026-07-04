namespace DeltaSharp.Diagnostics;

/// <summary>
/// The single source of truth for DeltaSharp's telemetry <b>names</b>: the shared root prefix used by
/// every <c>Meter</c>, <c>ActivitySource</c>, and <c>Microsoft.Extensions.Logging.ILogger</c>
/// category, plus the canonical low-cardinality attribute keys shared verbatim across structured logs,
/// metric tags, and trace-span attributes. The prose conventions that govern how these are used live in
/// <c>docs/engineering/design/observability-conventions.md</c> (FEAT-00.4, #110/#111); this type keeps the
/// literal strings in one compiled, testable place so future driver/executor/operator instrumentation
/// cannot drift from the documented vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// This type holds <b>names only</b> — no <c>Meter</c> or <c>ActivitySource</c> instance is created here.
/// A component owns its own instrument/source instances (created once per component, per checklist
/// <c>09b</c>) and names them <c>DeltaSharp.&lt;Component&gt;</c> using <see cref="RootName"/> as the
/// prefix. Because <c>System.Diagnostics.Metrics</c> and <c>System.Diagnostics.ActivitySource</c> are
/// inherently no-ops until a <c>MeterListener</c>/<c>ActivityListener</c> or OpenTelemetry exporter
/// subscribes, referencing these names never requires a collector — instrumentation is a safe no-op when
/// telemetry export is disabled (this mirrors the ambient <see cref="ExecutionAudit"/> forwarders in this
/// same namespace, which do nothing when no sink is installed).
/// </para>
/// <para>
/// The attribute keys use the OpenTelemetry-style dotted, lowercase, <c>deltasharp.</c>-prefixed form so
/// the identical string is valid as a metric tag key, an <c>Activity</c> tag key, and an
/// <c>Microsoft.Extensions.Logging.ILogger</c> scope key. They split into two disjoint groups the prose
/// keeps distinct: <b>metric-label-safe</b> keys name closed, bounded-at-any-instant dimensions
/// (component, operation, outcome, stage); <b>correlation/exemplar-only</b> keys (job/task/executor id,
/// correlation id, attempt, partition, table version) belong on logs, spans, and exemplars but are
/// <b>never</b> metric labels because they are unbounded over a run's lifetime. High-cardinality values
/// (raw storage paths, SQL text, row values) are never telemetry keys or values at all
/// (checklists <c>09a</c>/<c>09b</c>/<c>09c</c>).
/// </para>
/// <para>
/// The type is deliberately <see langword="internal"/>: it seeds a convention, not a public contract, so
/// it adds nothing to the Abstractions PublicAPI baseline. It lives in <c>DeltaSharp.Abstractions</c> (the
/// shared Core+Engine diagnostics layer, beside <see cref="ExecutionAudit"/>) so both siblings can bind
/// the same names; a host that must publish these names to configure an OpenTelemetry provider can be
/// granted access, or the vocabulary can be promoted to public through a reviewed PublicAPI change when a
/// consumer genuinely needs it.
/// </para>
/// </remarks>
internal static class DeltaSharpTelemetry
{
    /// <summary>
    /// The shared root prefix for every DeltaSharp <c>Meter</c> name, <c>ActivitySource</c> name, and
    /// <c>Microsoft.Extensions.Logging.ILogger</c> category. Component telemetry identities follow
    /// the pattern <c>DeltaSharp.&lt;Component&gt;</c> (for example <c>DeltaSharp.Engine</c>,
    /// <c>DeltaSharp.Executor</c>); logger categories are the fully-qualified type name of an
    /// <c>ILogger&lt;T&gt;</c>, which already begins with this root because every type lives under the
    /// <c>DeltaSharp</c> namespace.
    /// </summary>
    internal const string RootName = "DeltaSharp";

    /// <summary>Bounded component identifier — which subsystem emitted the signal (for example
    /// <c>engine</c>, <c>executor</c>, <c>catalog</c>). A closed, low-cardinality set.</summary>
    internal const string ComponentKey = "deltasharp.component";

    /// <summary>Bounded operation identifier — the logical operation within a component (for example
    /// <c>collect</c>, <c>plan</c>, <c>commit</c>). A closed, low-cardinality set, not free text.</summary>
    internal const string OperationKey = "deltasharp.operation";

    /// <summary>Bounded terminal outcome — one of a closed set such as <c>success</c>, <c>cancelled</c>,
    /// <c>timeout</c>, <c>conflict</c>, <c>failure</c> — so success, cancellation, timeout, and error are
    /// never collapsed into one ambiguous count (checklist <c>09b</c>).</summary>
    internal const string OutcomeKey = "deltasharp.outcome";

    /// <summary>Correlates every signal for one logical job/application. An opaque identifier — never a
    /// user, tenant, or path value. A <b>correlation/exemplar-only</b> field: valid on structured logs, span
    /// attributes, and metric exemplars, but <b>never</b> a metric label — job ids are unbounded across runs
    /// and would multiply a metric's time-series cardinality without bound.</summary>
    internal const string JobIdKey = "deltasharp.job.id";

    /// <summary>The pipeline stage a signal belongs to. A bounded enum-like value drawn from the
    /// execution stage vocabulary (for example <c>analyze</c>, <c>plan</c>, <c>scan</c>, <c>backend</c>,
    /// <c>materialize</c>), never free text.</summary>
    internal const string StageKey = "deltasharp.stage";

    /// <summary>Identifies a task within a stage. Opaque; bounded per run but unbounded across runs, so it is
    /// a <b>correlation/exemplar-only</b> field (structured logs, span attributes, metric exemplars) and
    /// <b>never</b> a metric label. Used on task-scoped signals once the distributed executor exists.</summary>
    internal const string TaskIdKey = "deltasharp.task.id";

    /// <summary>Identifies the executor that produced the signal — an executor ordinal/slot, never a pod UID
    /// or other unbounded identity. Bounded per run but unbounded across runs, so it is a
    /// <b>correlation/exemplar-only</b> field (structured logs, span attributes, metric exemplars) and
    /// <b>never</b> a metric label.</summary>
    internal const string ExecutorIdKey = "deltasharp.executor.id";

    /// <summary>The logical table identity a signal concerns (catalog-qualified name), used instead of a
    /// raw, credential-bearing storage path (which is neither low-cardinality nor safe). A
    /// <b>correlation/exemplar-only</b> field: valid on structured logs and span attributes, but
    /// <b>never</b> a metric label (the set of tables is workload-dependent). A catalog-qualified name may
    /// itself embed a tenant id, so it must be scrubbed or omitted when it reveals a tenant boundary or
    /// regulated dataset.</summary>
    internal const string TableKey = "deltasharp.table";

    /// <summary>The Delta table version (commit version) a signal concerns. Small at any instant but
    /// <b>unbounded over a table's commit history</b>, so it is a <b>correlation/exemplar-only</b> field
    /// (structured logs, span attributes, metric exemplars) and <b>never</b> a metric label — a per-commit
    /// label would multiply a metric's time-series cardinality without bound.</summary>
    internal const string TableVersionKey = "deltasharp.table.version";

    /// <summary>An explicit request/action correlation identifier for paths that predate an ambient
    /// <c>Activity</c> trace context, or that must carry correlation across a boundary where trace context
    /// is not propagated. Opaque — never a user or tenant identity. A <b>correlation/exemplar-only</b> field:
    /// valid on structured logs, span attributes, and metric exemplars, but <b>never</b> a metric label,
    /// because correlation ids are unbounded.</summary>
    internal const string CorrelationIdKey = "deltasharp.correlation.id";

    /// <summary>The task/stage <b>attempt</b> number a signal belongs to (the first try, then incremented
    /// on each retry / shuffle re-resolution, per ADR-0004). A <b>correlation/exemplar-only</b> field:
    /// valid on structured logs, span attributes, and metric exemplars, but <b>never</b> a metric label,
    /// because attempts grow per retry and would multiply a metric's time-series cardinality. Bounded per
    /// run; populated once the retry/re-resolution paths exist.</summary>
    internal const string AttemptKey = "deltasharp.attempt";

    /// <summary>The <b>partition</b> (split) index a task-scoped signal processes. A
    /// <b>correlation/exemplar-only</b> field: valid on structured logs and span attributes, but
    /// <b>never</b> a metric label, because partition counts are workload-dependent and unbounded across
    /// jobs. Populated once the distributed executor exists.</summary>
    internal const string PartitionKey = "deltasharp.partition";
}
