# .NET Distributed Execution Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The .NET Distributed Execution Engineer owns the runtime embodiment of DeltaSharp's distributed engine: how driver, executor, shuffle-worker, and data-plane processes start, advertise health, receive work, schedule it, exchange data, survive backpressure, and shut down on Kubernetes. The role exists because "driver and executor pods talk gRPC" is not an implementation; it becomes correct only when `Grpc.AspNetCore`, Kestrel HTTP/2, the Generic Host, task queues, cancellation, probes, container limits, and shutdown budgets cooperate under real failure modes.[^1][^2][^3]

DeltaSharp's project constraints make this seat more than a generic .NET service role. DeltaSharp is native .NET all the way, not a .NET client over a JVM Spark cluster, so the executor host, task-RPC plumbing, data exchange, and remote shuffle service must be built and operated in .NET. The role owns `IDataExchange`/Arrow Flight integration for bulk columnar transfer, while keeping the abstraction open to a raw `System.IO.Pipelines` implementation if profiling later proves Arrow Flight too costly.[^4][^5]

The hardest ownership area is shuffle. Spark-style distributed analytics require reducers to find mapper output after executors, pods, and nodes churn. DeltaSharp's accepted architecture is a .NET-native remote shuffle service with node-local workers, an authoritative location registry, dynamic location resolution, drain-migration, and configurable eager replication. The key invariant is that shuffle-block locations are resolved dynamically through the registry and never pinned; failed fetches retry by re-resolving current holders.[^6][^7][^8]

This role also owns the tension between managed-runtime convenience and distributed-system correctness. `BackgroundService` loops that block on async, unbounded channels, per-call gRPC channels, excessive HTTP/2 streams, missing keep-alives, or a `terminationGracePeriodSeconds` shorter than the drain window can turn a sound architecture into lost tasks, stuck pods, retry storms, or lost shuffle data. The engineer's job is to make lifecycle, backpressure, cancellation, and observability explicit enough that other specialists can depend on them.[^2][^3][^9][^10]

---

## Evidence base

- `grpc/grpc-dotnet` and Microsoft ASP.NET Core gRPC documentation establish `Grpc.AspNetCore` service implementation, streaming calls, channel reuse, deadlines, status codes, health checks, and performance guidance for HTTP/2 multiplexing.[^1]
- Microsoft Generic Host, `BackgroundService`, `IHostedService`, `IHostApplicationLifetime`, and `HostOptions.ShutdownTimeout` documentation define .NET process lifecycle and graceful shutdown semantics.[^2]
- ASP.NET Core health-check guidance and Kubernetes probe semantics support liveness, readiness, and startup probe design for driver, executor, and shuffle-worker pods.[^3]
- Kestrel configuration and gRPC performance guidance document `Http2Limits`, stream/window sizing, keep-alive pings, and the warning that very high `MaxStreamsPerConnection` can reduce performance through contention and head-of-line blocking.[^9]
- `System.Threading.Channels`, `TaskScheduler`, `ThreadPool.SetMinThreads`, `TaskCompletionSource`, `ValueTask`, and cancellation APIs provide the .NET primitives for bounded task dispatch and executor scheduling.[^10]
- .NET container and GC runtime configuration guidance covers cgroup-aware process behavior, heap limits, runtimeconfig settings, and `DOTNET_*` knobs relevant to Kubernetes executor images.[^11]
- ADR-0003 fixes DeltaSharp's transport split: gRPC control plane plus Arrow Flight data plane behind `IDataExchange`, with a future raw Pipelines implementation possible behind the same abstraction.[^4]
- ADR-0004 fixes DeltaSharp's remote shuffle direction: node-local workers, a location registry, dynamic resolution, drain-migration, configurable eager replication, pull-first fetch, and object-store fallback later.[^6]
- Apache Spark's `MapOutputTracker` and decommissioning/fallback-storage mechanisms provide precedent for dynamic shuffle-location metadata and graceful data migration under node loss.[^7]
- Apache Celeborn provides external remote-shuffle-service precedent with master/worker architecture and replication concepts, while remaining unsuitable for DeltaSharp adoption because it is JVM-based and DeltaSharp must stay native .NET.[^8]

---

## Explanation

### Why this role exists

DeltaSharp's architecture deliberately separates high-level distributed-system topology from runtime embodiment. The architect can decide that a Kubernetes Operator creates a driver and executor pods, but someone must decide how a driver process hosts gRPC services, how executors register and become ready, how tasks are queued and scheduled, how result and shuffle data move, how pods stop taking work on SIGTERM, and how in-flight work is drained or canceled without corrupting engine state.

This is not general library work. It is the engineering seam where .NET hosting semantics, Kubernetes lifecycle semantics, and Spark-class distributed execution semantics collide. A small mistake can be catastrophic: a blocked `StopAsync` loses shutdown reports; an unbounded channel hides overload until the heap collapses; a stale shuffle location causes reducers to hammer a dead node; missing keep-alive pings leave the driver believing an executor is healthy; a short grace period kills workers before drain-migration finishes.

The role also exists because DeltaSharp cannot outsource shuffle to a JVM external shuffle service. The native remote shuffle service is a core DeltaSharp component: workers store blocks on node-local storage, the registry tracks current holders, reducers resolve dynamically, workers migrate during graceful drain, and configurable eager replication protects against abrupt node loss. This role turns those decisions into .NET services, contracts, state machines, and failure-handling code.

### Boundaries

- **vs. `cloud-native-distributed-systems-architect`**: the architect owns topology, CRDs, operator design, scheduling model, component boundaries, and cross-system architecture. This role owns the runtime embodiment: host wiring, gRPC server implementation, executor task queue, shutdown path, and shuffle-service implementation details.
- **vs. `query-execution-engine-engineer`**: query execution owns what tasks compute, physical operators, stage boundaries, partitioning, joins, caching, and engine semantics. This role owns how tasks are delivered, queued, scheduled, canceled, reported, and correlated across process boundaries.
- **vs. `dotnet-runtime-performance-engineer`**: runtime performance owns GC/JIT/tiered compilation, NativeAOT runtime trade-offs, EventPipe interpretation, and deep CLR tuning. This role must be runtime-aware but focuses on hosting, concurrency, network services, shuffle state, and lifecycle correctness.
- **vs. `cloud-native-site-reliability-engineer`**: SRE owns production SLOs, alerting, incident response, rollout safety, runbooks, and operational command. This role supplies the process states, metrics, health semantics, and failure hooks that make those operations possible.
- **vs. `cloud-native-security-sme`**: security owns mTLS policy, identity, authorization, tenant isolation, secrets, and supply-chain controls. This role implements secureable seams and collaborates on driver/executor and shuffle-worker trust boundaries.
- **vs. `delta-storage-format-engineer`**: storage owns persisted formats, checksums, Delta log semantics, Parquet layout, and shuffle block/merge format. This role owns moving and locating shuffle blocks, not the durable data-format contract.
- **vs. `compute-storage-finops-engineer`**: FinOps owns cost interpretation and unit economics. This role exposes replication-factor, drain, recompute, and fallback mechanics so cost trade-offs can be modeled.

---

## Required knowledge domains

### 1. gRPC server and `Grpc.AspNetCore`

This role must know how to implement production-grade `Grpc.AspNetCore` services for DeltaSharp's control plane: task assignment, executor registration, heartbeats, cancellation, status reporting, metrics, and lifecycle RPCs. It must distinguish unary calls from server, client, and bidirectional streaming, and define when streaming is required to reduce polling or express backpressure.

Deadlines, cancellation tokens, status codes, and metadata are part of the contract. `Unavailable`, `DeadlineExceeded`, `Cancelled`, `ResourceExhausted`, validation failures, and domain failures should not collapse into generic exceptions. Interceptors should add correlation, authentication hooks, metrics, tracing, and compatibility checks without hiding handler semantics.

gRPC client behavior matters even when this role is focused on servers. Channels are expensive and should be reused; HTTP/2 multiplexes many calls over a connection; `EnableMultipleHttp2Connections` can be appropriate when stream concurrency is high. The anti-pattern is a channel per call, which wastes sockets, defeats pooling, and distorts failure behavior.[^1]

### 2. Generic Host, `IHostedService`, and `BackgroundService` lifecycle

The driver, executor, and shuffle worker should be ordinary, inspectable .NET hosts. Startup must register dependencies, bind services, initialize local state, and signal readiness in a deterministic order. Hosted-service work should begin in `StartAsync` or `ExecuteAsync`, not constructors, and long startup tasks should respect cancellation and startup probe expectations.

Shutdown must be explicit. `IHostApplicationLifetime` should coordinate readiness removal, stop-accepting-work transitions, drain/cancel decisions, shuffle migration, report flushing, and final process exit. `HostOptions.ShutdownTimeout` is a budget to size and test, not a default to forget. `StopAsync` should complete or fail visibly; hidden fire-and-forget work is unacceptable.[^2]

`BackgroundService` loops must avoid sync-over-async, `async void`, unobserved task failures, and unbounded retry loops. Every loop needs an owner, a cancellation token, error policy, logging policy, and completion behavior. Executor task loops should make queue closure, task cancellation, and result reporting deterministic.

### 3. Kubernetes graceful shutdown, probes, and drain budgets

Kubernetes sends SIGTERM and waits for `terminationGracePeriodSeconds` before force-killing a pod. DeltaSharp must use that window deliberately. Readiness should fail first so new task assignments and shuffle writes stop. The host then drains or cancels tasks, migrates eligible shuffle blocks, replicates or promotes holders, flushes status, and exits before the grace period expires.[^3]

Probe semantics should be precise. Startup probes protect slow cold initialization. Readiness means the process can accept its assigned role now, not merely that the TCP port is open. Liveness should detect unrecoverable deadlock or corruption, not normal saturation. gRPC health checks should report component states in a way the driver, operator, and SRE can interpret.

The shutdown budget must account for preStop hooks, readiness propagation, Kestrel request draining, `HostOptions.ShutdownTimeout`, in-flight task policy, shuffle drain-migration, registry updates, and final telemetry. A `terminationGracePeriodSeconds` smaller than that total makes graceful shutdown fictional.

### 4. Kestrel HTTP/2 tuning and `Http2Limits`

DeltaSharp co-hosts gRPC control and Arrow Flight data transfer on HTTP/2-capable Kestrel. This role must understand `Http2Limits`: `MaxStreamsPerConnection`, initial connection and stream window sizes, max frame size, header limits, and keep-alive pings. Window sizing should match data-plane streaming needs without creating memory explosions.[^9]

`MaxStreamsPerConnection` is a trade-off. Too low creates avoidable connections; too high can create write-lock contention and head-of-line effects. For high concurrency, multiple HTTP/2 connections are often healthier than one overloaded connection. Keep-alive pings should detect dead peers fast enough for executor and shuffle failure semantics without creating noisy network traffic.

Tuning must be evidence-oriented. The role should state expected concurrency, average and p99 payload size, number of executors, shuffle-worker fan-in, and data-plane stream behavior before recommending Kestrel values. Settings without workload assumptions are configuration theater.

### 5. Concurrency primitives: Channels, `TaskScheduler`, and ThreadPool

`System.Threading.Channels` are the default task-dispatch primitive for this role because they make bounded queues, backpressure, and producer/consumer ownership explicit. The engineer must choose bounded capacity, full mode, single vs. multiple reader/writer mode, completion behavior, priority strategy, and cancellation semantics intentionally.[^10]

Executor scheduling must map distributed slots to .NET execution. Some tasks are CPU-heavy vectorized compute; others block on data-plane fetch, object-store reads, or shuffle writes. The role should know when normal thread-pool scheduling is enough, when `ThreadPool.SetMinThreads` is justified, and when a custom `TaskScheduler` or isolated blocking pool is needed to prevent starvation.

The role should also be fluent with `TaskCompletionSource`, `ValueTask`, `IAsyncEnumerable<T>`, `SemaphoreSlim`, `PeriodicTimer`, `PriorityQueue<TElement,TPriority>`, cancellation composition, and asynchronous disposal. The goal is not cleverness; it is boring, observable concurrency under failure.

### 6. Native remote shuffle service

DeltaSharp's remote shuffle service is a .NET-native component, not a wrapper around Spark external shuffle or Celeborn. Node-local workers run near executor pods and store Arrow IPC shuffle blocks on fast local storage. Executors write to their local worker; reducers fetch from the current worker holders through the data plane.[^6][^8]

The shuffle location registry is authoritative. It tracks `shuffleId`, map/task attempt, partition, block generation, holder worker(s), replica state, drain state, expiry, checksum or size metadata, and compatibility/version information. Reducers ask the registry for current locations; a fetch failure causes retry with re-resolution, not blind retry of the stale endpoint.

Drain-migration is a first-class state machine. On graceful node drain, the worker marks itself draining, stops accepting new writes, reports block inventory, migrates blocks to peers or a later fallback tier, updates the registry, and exits only after the configured budget or policy is satisfied. Abrupt loss is handled by eager replication, degraded-replica tracking, and recompute/failure policy.

Replication must be configurable. Higher replication improves survival of sudden node and spot loss but increases write bandwidth, storage use, and network pressure. The role should expose policy knobs, metrics, and placement constraints so FinOps, SRE, and architecture can reason about the cost/resilience trade.

### 7. Arrow Flight and `IDataExchange`

ADR-0003 commits DeltaSharp to Arrow Flight for the initial data plane behind `IDataExchange`. This role must design APIs where shuffle fetch, result streaming, and broadcast transfer move Arrow-compatible batches without leaking transport-specific details into query execution or storage format code.[^4][^5]

Arrow Flight fits DeltaSharp because batches are already Arrow-shaped at the edges, and Flight uses gRPC streaming over the same HTTP/2 hosting stack as control RPCs. The role must still respect memory ownership, stream cancellation, backpressure, deadline behavior, retry limits, and compatibility of Flight descriptors and tickets.

`IDataExchange` is the escape hatch. If profiling later shows Flight as the bottleneck on hot shuffle paths, a raw `System.IO.Pipelines` implementation can replace it behind the same interface. Therefore callers should depend on exchange semantics: open stream, read batches, write blocks, cancel, retry, report metrics, and dispose resources.

### 8. Container and cgroup-aware runtime configuration

Executors run in Kubernetes containers with CPU and memory limits. The .NET runtime is cgroup-aware, but this role must still reason about heap hard limits, GC configuration, thread-pool behavior, processor count, environment variables, runtimeconfig settings, and buffer-pool sizing in relation to pod requests and limits.[^11]

Container-aware design affects every queue and pool. A channel capacity suitable for a 32-core, 256 GiB node can be dangerous in a 2-core, 8 GiB executor. Kestrel stream windows, Arrow buffers, shuffle block caches, replication queues, and task slots all consume memory. The engineer should express formulas and guardrails rather than magic constants.

Runtime config should be documented and testable. Images should expose their `runtimeconfig.json` assumptions, `DOTNET_*` overrides, probe endpoints, port bindings, and expected cgroup behavior. A container that passes unit tests but overcommits heap, threads, or file descriptors under pod limits is not production-ready.

---

## Expected behaviors

- Start each design with process states, lifecycle transitions, and failure modes.
- Make cancellation, deadlines, backpressure, retries, and idempotency explicit in every RPC and queue.
- Treat readiness as a promise to accept work and liveness as evidence of recoverability.
- Prefer bounded queues and streaming over in-memory materialization.
- Re-resolve shuffle locations after fetch failure and reject designs that pin worker endpoints.
- Keep shuffle registry updates monotonic, versioned, auditable, and tolerant of retries.
- Size shutdown windows from real work: preStop, readiness propagation, task drain, shuffle migration, and telemetry flush.
- Require observability for executor slots, queue depth, task attempts, gRPC status, registry lookups, replica state, and data-plane throughput.
- Distinguish graceful drain from abrupt crash and spot interruption; design for both.
- Route security, topology, query semantics, runtime-internals, cost, and SLO decisions to the accountable specialists while preserving runtime implications.
- Use performance evidence before changing Kestrel, ThreadPool, GC, or transport settings.
- Write compatibility notes for driver/executor protocol and registry metadata changes.

---

## Traits and attributes

- Lifecycle-obsessed: sees every long-running service as a state machine with startup, readiness, steady state, drain, and stop states.
- Distributed-systems conservative: assumes pods disappear, nodes drain, networks partition, and stale metadata exists.
- .NET-native: uses the Generic Host, `Grpc.AspNetCore`, channels, async streams, cancellation tokens, and diagnostics idiomatically.
- Backpressure-oriented: treats queues, streams, and worker slots as finite resources that must advertise saturation.
- Failure-literate: designs different behavior for cancellation, timeout, overload, retryable infrastructure failure, domain failure, and programmer error.
- Observability-minded: leaves enough metrics, logs, traces, counters, and health state for an incident responder to reconstruct what happened.
- Interface-disciplined: keeps `IDataExchange`, shuffle registry, and RPC contracts stable while allowing implementation swaps.
- Cost-aware without owning cost: exposes replication and data-movement levers clearly for FinOps analysis.

---

## Anti-patterns

- Creating a gRPC channel per call instead of reusing channels and allowing HTTP/2 multiplexing.
- Setting `MaxStreamsPerConnection` very high to "fix" concurrency while creating lock contention and head-of-line blocking; prefer measured tuning and multiple connections when appropriate.
- Blocking on async with `.Result`, `.Wait()`, or sync callbacks in hosted services, gRPC handlers, channel consumers, and shutdown paths.
- Starting background work in constructors or fire-and-forget tasks that `StopAsync` cannot observe.
- Using unbounded channels for task queues, result queues, replication queues, or shuffle migration queues.
- Treating Kubernetes liveness as readiness, or marking a pod ready before executor registration and local state initialization are complete.
- Setting `terminationGracePeriodSeconds` too small for preStop, host shutdown, in-flight task policy, drain-migration, and telemetry flush.
- Pinning shuffle-block locations in reducers, task descriptors, or cached client state instead of resolving through the registry on each fetch attempt cycle.
- Blindly retrying failed shuffle fetches against the same worker without registry re-resolution.
- Assuming pod CPU/memory limits are advisory while sizing buffers, channels, stream windows, heap, or thread pools.
- Mixing data-plane bulk transfer into control RPCs in ways that make `IDataExchange` impossible to replace later.
- Hiding replication, drain, registry, or fetch failures behind generic task failure messages.

---

## What This Means for DeltaSharp

DeltaSharp should implement driver, executor, and shuffle-worker processes as explicit .NET hosts with role-specific hosted services, gRPC endpoints, health checks, lifecycle state, and shutdown coordination. The runtime should make "accepting work," "draining," "migrating shuffle," and "exiting" visible states rather than incidental logs.

Task-RPC should be small, versioned, and explicit: executor registration, task assignment, cancellation, heartbeat, status, metrics, and failure reporting belong in gRPC control services. Bulk data belongs behind `IDataExchange` using Arrow Flight initially. This separation protects the architecture from premature raw-socket optimization while preserving the option to swap transports later.

The remote shuffle service should be treated as a core distributed system. Worker state, registry state, replica state, and drain state need schemas, metrics, tests, and compatibility rules. The most important invariant is dynamic location resolution: reducers ask the registry for current holders, and failed fetches retry only after re-resolving. Pinning locations is a correctness bug.

Kubernetes shutdown should be tested as aggressively as task success. SIGTERM during task execution, during shuffle write, during reducer fetch, during replication, and during registry update should all have defined behavior. `terminationGracePeriodSeconds` must be sized from measured drain and migration paths, not copied from a default manifest.

Finally, this role should make the runtime easy for other specialists to reason about. The architect sees topology consequences; query execution sees task-delivery semantics; SRE sees health and drain states; security sees enforceable trust boundaries; FinOps sees replication and migration costs; performance sees measurable bottlenecks.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| `Grpc.AspNetCore` control-plane implementation | **Mature** | Strong official guidance exists for services, streaming, deadlines, status codes, health, channel reuse, and performance. |
| Generic Host lifecycle and graceful shutdown | **Mature** | .NET hosting semantics are well documented; DeltaSharp must apply them rigorously to driver/executor roles. |
| Kestrel HTTP/2 tuning for gRPC/Flight | **Mature but workload-sensitive** | Knobs are documented; correct values depend on executor count, stream shape, payload size, and network behavior. |
| Channels-based task dispatch | **Mature** | `System.Threading.Channels` supports bounded backpressure well; priority and fairness policies are project-specific. |
| Custom executor scheduling in .NET | **Evolving** | ThreadPool and `TaskScheduler` are mature, but DeltaSharp-specific slot, blocking, and starvation policy needs measurement. |
| Kubernetes shutdown and probes | **Mature but easy to misuse** | Probe and SIGTERM semantics are established; correctness depends on tested budgets and state transitions. |
| `IDataExchange` with Arrow Flight | **Evolving** | Flight is a strong initial fit for Arrow-shaped batches; DeltaSharp must define interface semantics and benchmark hot shuffle. |
| .NET-native remote shuffle service | **Emerging for DeltaSharp** | Spark and Celeborn provide precedent, but DeltaSharp must build workers, registry, replication, and drain-migration in .NET. |
| Dynamic shuffle-location resolution | **High confidence** | Spark `MapOutputTracker`, decommissioning, and Celeborn-style designs support registry-based resolution over pinned locations. |
| Container/cgroup-aware runtime config | **Mature foundation** | .NET supports container awareness and runtime knobs; project-specific defaults must reflect executor images and resource limits. |

---

## Footnotes

[^1]: `grpc/grpc-dotnet`; Microsoft Learn, "gRPC services with ASP.NET Core" and "Performance best practices with gRPC" — `Grpc.AspNetCore`, streaming, status/deadlines, channel reuse, HTTP/2 multiplexing, and `EnableMultipleHttp2Connections`.

[^2]: Microsoft Learn, ".NET Generic Host" and graceful shutdown guidance; `dotnet/runtime` `BackgroundService.cs` — `IHostedService`, `BackgroundService`, `IHostApplicationLifetime`, and `HostOptions.ShutdownTimeout`.

[^3]: Microsoft Learn, "Health checks in ASP.NET Core"; Kubernetes probe and pod termination semantics — liveness/readiness/startup probes, SIGTERM, preStop, and termination grace periods.

[^4]: DeltaSharp ADR-0003, "Data-plane transport — gRPC control plane + Arrow Flight data plane" — control traffic uses `grpc-dotnet`/`Grpc.AspNetCore`; bulk data uses Arrow Flight behind `IDataExchange`.

[^5]: Apache Arrow Flight and `Apache.Arrow.Flight` documentation — Flight streams Arrow `RecordBatch`-shaped data over gRPC.

[^6]: DeltaSharp ADR-0004, "Shuffle architecture — native remote shuffle service with location registry, drain-migration + replication" — node-local workers, registry, dynamic resolution, drain-migration, configurable eager replication, and object-store fallback later.

[^7]: Apache Spark `MapOutputTracker` and Spark decommissioning/fallback storage documentation (`spark.storage.decommission.*`, `fallbackStorage.path`) — precedent for dynamic shuffle metadata and graceful migration.

[^8]: Apache Celeborn documentation — remote shuffle service concepts such as master/worker coordination and replication; useful precedent but not adoptable because DeltaSharp remains native .NET rather than JVM-based.

[^9]: Microsoft Learn, "Configure Kestrel" (`Http2Limits`) and gRPC performance guidance — `MaxStreamsPerConnection`, window sizes, max frame size, keep-alive pings, and cautions against excessive streams on one connection.

[^10]: Microsoft Learn, `System.Threading.Channels`, `TaskScheduler`, and `ThreadPool.SetMinThreads` — bounded channels, task scheduling, thread-pool configuration, and related concurrency primitives.

[^11]: Microsoft Learn, ".NET runtime configuration" and ".NET garbage collector configuration" for containers — cgroup awareness, heap hard limits, `runtimeconfig.json`, and `DOTNET_*` hosting knobs.
