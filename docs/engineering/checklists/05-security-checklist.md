# 05 — Security Checklist

> **Scope:** Authentication, authorization, secrets, driver/executor transport, storage access, SQL input handling, supply chain, telemetry, incident response, and any code or manifests crossing a trust boundary.
> **Priority:** CRITICAL.
> **Owners:** cloud-native-security-sme, kubernetes-operator-controller-engineer, dotnet-distributed-execution-engineer. **Grounded in:** `.github/copilot-instructions.md`, `SECURITY.md`, `review-pr/rating-rubric.md`, ADR-0003, ADR-0004, ADR-0009, ADR-0014.

## How to use
Apply this checklist to every change that can affect credentials, identities, network paths, storage paths, SQL/query text, container artifacts, CRDs, or observability. Treat injection, auth bypass, credential exposure, and cross-tenant access as Critical; cross-check 14 for isolation and 18 for operator controls.

## Checklist
### Trust boundaries and identity
- [ ] The change identifies callers, users, tenants, operator, driver, executors, shuffle workers, catalogs, object stores, PVCs, and external services that cross trust boundaries.
- [ ] Job submission, action execution, session creation, and administrative operations require authenticated identities with auditable authorization decisions.
- [ ] Driver, executor, and shuffle-worker identities are per job, session, namespace, or tenant; shared cluster-wide identities are rejected unless explicitly justified and constrained.
- [ ] Workload identity or short-lived scoped tokens are preferred over static cloud keys for S3, ADLS, GCS, catalogs, and Kubernetes APIs.
- [ ] Operator service-account privileges are least-privilege and namespace-aware; see 18 for RBAC and webhook enforcement.
- [ ] User-provided dependencies, UDFs, connectors, and executor code are treated as untrusted workload surfaces.

### Zero-trust driver/executor transport
- [ ] Driver ↔ executor control traffic uses gRPC over HTTP/2 with mutual TLS; plaintext or cluster-internal-trust-only RPC is not accepted.
- [ ] Arrow Flight data-plane traffic for shuffle, result fetch, and broadcast uses authenticated, encrypted channels per ADR-0003.
- [ ] Certificates, SPIFFE/SPIRE IDs, projected service-account tokens, or equivalent workload identities bind peers to the expected tenant/job/session.
- [ ] gRPC and Arrow Flight authorization verifies task, stage, shuffle block, broadcast, and result ownership before serving data.
- [ ] Replay, downgrade, unknown-version, and cross-job registration attempts fail closed and produce security-safe audit events.
- [ ] Health probes and metrics endpoints do not expose privileged RPC methods or bypass mTLS requirements for sensitive data.

### Authorization and storage access
- [ ] Object-store paths are authorized through a storage abstraction that checks tenant, catalog, table, prefix/container/bucket, and operation.
- [ ] PVC mounts, hostPath access, shuffle directories, spill directories, and cache directories cannot be shared across tenants unless explicitly isolated by policy and identity.
- [ ] Delta `_delta_log` reads and writes are protected as sensitive operations; metadata access is not treated as harmless.
- [ ] Broadcast, cache, shuffle, and spill reads validate producer and consumer job/tenant context before returning bytes.
- [ ] Catalog access, table namespaces, and storage credential scopes align so a user cannot bypass catalog policy with a raw object-store URI.
- [ ] Authorization failures are denied by default, distinguishable from transient storage failures, and safe to surface without leaking secrets or row data.

### Secrets and credentials
- [ ] S3, ADLS, GCS, catalog, encryption-key, registry, and webhook credentials never appear in logs, metrics, traces, status, events, `EXPLAIN`, physical plans, generated code, or exception messages.
- [ ] Secret references in CRDs, Helm values, Terraform variables, and manifests point to externally managed secrets; plaintext secret values are not committed.
- [ ] Credential lifetime, rotation, revocation, and propagation are defined for drivers, executors, shuffle workers, and operator webhooks.
- [ ] Environment variables, mounted files, command-line args, runtimeconfig, and diagnostic dumps are reviewed for accidental secret exposure.
- [ ] Redaction is tested for connection strings, SAS tokens, access keys, bearer tokens, signed URLs, and cloud-provider credential formats.
- [ ] Credentials are not embedded in logical plans, serialized task descriptors, shuffle metadata, Delta commit info, lineage records, or cost-attribution metadata.

### Input validation and injection defense
- [ ] SQL frontend parsing, identifier handling, string literal handling, function resolution, and catalog lookup are parser-driven and never assembled by string concatenation.
- [ ] SQL parameters, user expressions, UDF metadata, and connector options are validated before they reach catalogs, storage clients, or generated commands.
- [ ] Object-store and PVC paths reject traversal, ambiguous normalization, absolute-path escape, encoded separators, credential-bearing URIs, and tenant-prefix bypasses.
- [ ] CRD fields, labels, annotations, resource names, image references, and service-account references are validated by schema and admission controls.
- [ ] Deserialization of task descriptors, plan fragments, shuffle metadata, and Arrow/Flight messages is bounded, versioned, and fails closed on malformed input.
- [ ] Error messages identify invalid input without echoing complete SQL text, raw paths, credentials, or sensitive row values.

### Supply-chain integrity
- [ ] NuGet, container base image, GitHub Action, Helm chart, Terraform provider, and code-generation dependencies are pinned with documented update policy.
- [ ] Release builds are deterministic where practical, include SourceLink, and preserve reproducible provenance for packages and images.
- [ ] Packages, containers, and release artifacts are signed; cluster admission can verify signatures before running operator, driver, executor, and shuffle images.
- [ ] SBOMs are generated for .NET packages and container images, retained with releases, and scanned against vulnerability policy.
- [ ] NativeAOT executor images from ADR-0014 use minimal trusted bases and do not smuggle build tools, package managers, or unused shells into runtime layers.
- [ ] CI workflow permissions are minimal, secrets are scoped to protected branches/environments, and pull-request workflows cannot exfiltrate release credentials.

### Observability, incident response, and disclosure
- [ ] Security-relevant audit events cover job submission, action authorization, credential issuance, storage access denial, Delta log mutation, image admission, and cross-tenant access attempts.
- [ ] Logs, metrics, traces, profiles, minidumps, and support bundles are classified and redacted before collection or export.
- [ ] Security alerts distinguish auth bypass, credential leakage, suspicious egress, unauthorized Delta mutation, image-policy violation, and repeated denied cross-tenant access.
- [ ] Incident response preserves evidence, rotates affected credentials, verifies Delta table integrity, and documents blast radius.
- [ ] Vulnerability reports follow `SECURITY.md`: private GitHub Security Advisories or the monitored security email, 3-business-day acknowledgement target, 10-business-day initial assessment, and coordinated disclosure.
- [ ] Security exceptions include owner, expiration, compensating control, and a testable condition for removal.

## Anti-patterns (red flags)
- SQL injection, path traversal, command injection, unsafe deserialization, or parser bypass in user-controlled inputs.
- Any auth bypass for job submission, action execution, operator APIs, driver/executor RPC, shuffle fetch, catalog access, or Delta commits.
- Credentials, signed URLs, bearer tokens, object-store keys, or tenant secrets appear in logs, plans, `EXPLAIN`, metrics, traces, status, or telemetry.
- Driver/executor or Arrow Flight traffic trusts the cluster network instead of using mTLS and authorization.
- Broad operator RBAC or cloud IAM lets one tenant, job, executor, or namespace access another tenant's data.
- Unsigned or mutable release artifacts, unpinned CI actions, or images that cannot produce SBOM/provenance evidence.
- Public GitHub issue, PR, or discussion used to disclose an unpatched vulnerability.

## References
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- [18 — Kubernetes Operator Checklist](18-kubernetes-operator-checklist.md)
- [10 — Runtime Environment Checklist](10-runtime-environment-checklist.md)
- [13 — Infrastructure as Code Checklist](13-infrastructure-as-code-checklist.md)
- `SECURITY.md`
- `.github/skills/review-pr/rating-rubric.md`
- ADR-0003: Data-plane transport
- ADR-0004: Shuffle architecture
- ADR-0009: Kubernetes Operator and CRD design
- ADR-0014: Target framework and AOT posture
- `docs/persona/agents/cloud-native-security-sme-agent.md`
