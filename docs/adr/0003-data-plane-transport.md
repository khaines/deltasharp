# ADR-0003: Data-plane transport — gRPC control plane + Arrow Flight data plane

- **Status:** Accepted
- **Date:** 2026-06-26
- **Deciders:** @khaines
- **Related:** ADR-0002 (columnar format), ADR-0004 (shuffle), `docs/engineering/design/engine-architecture.md`

## Context

DeltaSharp's distributed runtime moves two very different kinds of traffic:

- **Control plane** (driver ↔ executor): task assignment, status, heartbeats,
  lifecycle, metrics — small request/response and streaming messages.
- **Data plane**: bulk columnar data — shuffle blocks, result fetch, broadcast —
  large `RecordBatch`-shaped payloads.

ADR-0002 commits to keeping **Arrow at the edges**, which makes Arrow Flight (a
gRPC-based protocol purpose-built to stream Arrow `RecordBatch`es) a natural data
transport.

## Decision

Split transport by plane, behind an abstraction:

- **Control plane: gRPC (`grpc-dotnet` / `Grpc.AspNetCore`)** — task RPC,
  heartbeats, status, lifecycle, hosted on Kestrel HTTP/2 with gRPC health-check
  probes.
- **Data plane: Arrow Flight** — stream Arrow batches for shuffle exchange, result
  fetch, and broadcast, reusing the same HTTP/2 hosting stack.
- Wrap the data plane behind an **`IDataExchange`** abstraction so a raw
  `System.IO.Pipelines`/socket path can replace Arrow Flight on the hottest shuffle
  paths later without changing callers (same swappable-impl pattern as ADR-0001/0002).

## Consequences

### Positive

- One hosting stack (Kestrel HTTP/2) for both gRPC control and Arrow Flight data.
- Arrow-consistent: Flight streams our columnar batches with minimal custom framing.
- `IDataExchange` preserves the option to drop to raw Pipelines for peak throughput.

### Negative / costs

- Arrow Flight throughput trails raw sockets at the extreme; acceptable initially
  given the abstraction escape hatch.
- Two protocols to operate (mitigated: both are HTTP/2-based and co-hosted).

### Follow-ups and sequencing

- Start with gRPC control + Arrow Flight data behind `IDataExchange`.
- Add a raw `System.IO.Pipelines` data-exchange implementation only if/when
  profiling shows Flight is the bottleneck on hot shuffle.

## Alternatives considered

- **gRPC for both control and bulk data (no Flight):** one protocol, but re-invents
  batch framing that Flight already standardizes. Rejected.
- **gRPC control + raw `System.IO.Pipelines` data from day one:** peak performance,
  highest up-front build cost. Deferred behind `IDataExchange`.
- **All Arrow Flight (control + data):** awkward for tiny control messages. Rejected.

## References

- `grpc/grpc-dotnet`; MS Learn — gRPC services with ASP.NET Core and
  "Performance best practices with gRPC" (channel reuse, HTTP/2 multiplexing).
- Apache Arrow Flight (gRPC streaming of `RecordBatch`); `Apache.Arrow.Flight`.
- MS Learn — `System.IO.Pipelines`.
