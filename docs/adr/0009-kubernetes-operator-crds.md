# ADR-0009: Kubernetes Operator and CRD design

- **Status:** Accepted
- **Date:** 2026-06-27
- **Deciders:** @khaines
- **Related:** ADR-0003, ADR-0004, `docs/engineering/design/engine-architecture.md`

## Context

The Kubernetes Operator reconciles custom resources that declare DeltaSharp
applications/jobs and manages driver/executor (and shuffle-worker) lifecycle. We
must decide the CRD set and how the controller is built in .NET.

## Options under consideration

- **CRD set:** `DeltaSharpApplication` (one-shot job), `DeltaSharpSession`
  (interactive), `DeltaSharpCluster`/shuffle-service DaemonSet — which to ship.
- **Controller framework:** a .NET operator SDK (e.g. KubeOps / dotnet-operator
  patterns) vs a custom reconcile loop.
- **Admission/validation webhooks**, finalizers, status subresources, scaling.

## Decision

Build the Operator with **KubeOps** (the .NET operator SDK: CRD generation,
reconcile loops, admission webhooks) — consistent with native-.NET. Core CRDs:
**`DeltaSharpApplication`** (batch job) and **`DeltaSharpSession`** (interactive /
long-running), with shuffle workers managed as a **DaemonSet** (ADR-0004);
cluster/pool CRDs later. A dedicated **`kubernetes-operator-controller-engineer`**
seat owns the CRD schemas, reconcilers, webhooks, finalizers, status subresources,
and scaling — distinct from `dotnet-distributed-execution-engineer` (process
hosting) and `cloud-native-distributed-systems-architect` (topology design).

## Gating / dependencies

Gates the candidate **`kubernetes-operator-controller-engineer`** persona (a
possible split from `cloud-native-distributed-systems-architect` (design) and
`dotnet-distributed-execution-engineer` (hosting)).
