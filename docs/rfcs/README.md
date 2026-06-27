# DeltaSharp RFCs

Substantial changes to DeltaSharp go through a **Request for Comments (RFC)**
process so the community can weigh in before significant implementation effort.

## When you need an RFC

Open an RFC for changes that are cross-cutting or hard to reverse, such as:

- new or breaking **public API** surface (e.g., `SparkSession`/`DataFrame` shape);
- **wire protocols** or on-disk formats (gRPC/Arrow Flight, plan serialization,
  Delta protocol features);
- new **engine subsystems** or major changes to execution, the optimizer/scheduler,
  shuffle, catalog, streaming, or the Kubernetes operator;
- anything that materially affects performance, compatibility, or governance.

Small, obvious, or easily-reversible changes (bug fixes, docs, incremental
features) do **not** need an RFC — just open a PR. If unsure, open a GitHub
Discussion or a draft RFC and ask.

## RFCs vs ADRs

- An **RFC** is a *proposal under discussion* — it captures motivation,
  alternatives, and design for community review.
- An [**ADR**](../adr/README.md) is the *recorded decision* (the source of truth).
  An accepted RFC frequently results in one or more ADRs.

## Process

1. **Draft.** Copy [`0000-template.md`](0000-template.md) to
   `docs/rfcs/0000-my-feature.md` (keep `0000` until you have a PR number).
2. **Open a PR.** Rename the file to your PR number (e.g.,
   `0042-my-feature.md`). The PR is where discussion happens.
3. **Discuss & revise.** Maintainers and the community comment; you iterate.
4. **Final Comment Period (FCP).** When discussion converges, a maintainer
   proposes to **accept** or **reject**, starting a ~7-day FCP.
5. **Disposition.** After FCP, the RFC is merged (accepted) or closed (rejected).
   Accepted RFCs get a **tracking issue** for implementation, and typically an ADR.

## Status values

`Draft` → `In FCP` → `Accepted` / `Rejected` / `Withdrawn` → `Superseded by RFC-XXXX`.

## Submitting

Sign off your commits (DCO; see [CONTRIBUTING](../../CONTRIBUTING.md)) and follow
the [Code of Conduct](../../CODE_OF_CONDUCT.md).
