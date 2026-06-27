# ADR-0009: Kubernetes Operator and CRD design

- **Status:** Proposed
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

TBD — to be resolved during backlog work.

## Gating / dependencies

Gates the candidate **`kubernetes-operator-controller-engineer`** persona (a
possible split from `cloud-native-distributed-systems-architect` (design) and
`dotnet-distributed-execution-engineer` (hosting)).
