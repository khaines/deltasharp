# 07 — Privacy Checklist

> **Scope:** PII and regulated data in DataFrames, Datasets, SQL, Delta tables, lineage, logs, metrics, traces, catalogs, caches, shuffle, spill, storage backends, and compliance evidence.
> **Priority:** STANDARD.
> **Owners:** privacy-compliance-grc-lead, cloud-native-security-sme, cloud-native-site-reliability-engineer. **Grounded in:** `.github/copilot-instructions.md`, `review-pr/rating-rubric.md`, ADR-0003, ADR-0004, ADR-0009.

## How to use
Use this checklist for features that ingest, derive, persist, expose, retain, delete, audit, or move data that may contain personal information. Pair it with 05 for protective controls and 14 for tenant boundaries whenever privacy depends on security or isolation.

## Checklist
### Data inventory and classification
- [ ] The change identifies PII or regulated data that may appear in DataFrame/Dataset columns, SQL text, schemas, partition values, file paths, UDF inputs, and connector options.
- [ ] Data classification propagates through projections, filters, joins, aggregations, UDFs, broadcasts, caches, shuffle, checkpoints, and Delta writes.
- [ ] New schemas, generated columns, nested fields, partition columns, constraints, and catalog metadata are reviewed for classification changes.
- [ ] Derived outputs, aggregates, materialized tables, samples, previews, and failed-job artifacts are classified instead of assuming only source data is sensitive.
- [ ] Column names, table names, job names, labels, annotations, and status messages are considered possible personal-data metadata.
- [ ] The data controller/processor responsibility boundary is clear for framework behavior, deployment configuration, and tenant application logic.

### Minimization and privacy-safe defaults
- [ ] APIs, examples, default configs, and templates avoid collecting direct identifiers, full SQL text, raw row payloads, or unbounded metadata unless required.
- [ ] Logs, metrics, traces, plans, `EXPLAIN`, errors, and support bundles use redaction, hashing, sampling, or structural summaries instead of raw personal data.
- [ ] Lineage and audit records capture the minimum evidence needed: job/table/version IDs, policy decisions, counts, hashes, and timestamps rather than row values.
- [ ] Temporary data in shuffle, spill, cache, broadcast, and executor-local disks has bounded lifetime and clear cleanup semantics.
- [ ] Diagnostic toggles that may expose data are disabled by default, access-controlled, time-limited, and documented as privacy-sensitive.
- [ ] Data minimization is validated in tests for common failure paths, not only successful jobs.

### Lineage, governance, and evidence
- [ ] Logical and physical plan lineage preserves enough source, transformation, sink, table-version, and tenant context to support audits without overcollecting data.
- [ ] Optimizer rewrites and predicate pushdown do not break lineage attribution for PII-bearing columns or derived outputs.
- [ ] Job metadata links user identity, tenant identity, action, driver/executor pods, storage locations, Delta table versions, and retention policy where needed for evidence.
- [ ] Audit evidence is immutable or tamper-evident enough for SOC 2 / ISO evidence while still respecting privacy retention limits.
- [ ] Compliance claims map to verifiable artifacts for GDPR, CCPA/CPRA, SOC 2, ISO 27001/27701, and tenant responsibility matrices.
- [ ] Evidence collection has an owner, collection path, retention period, review cadence, and failure signal.

### Retention, erasure, and Delta history
- [ ] Right-to-erasure workflows account for Delta `_delta_log`, Parquet files, deletion vectors, tombstones, checkpoints, time travel, object-store versions, backups, caches, and derived tables.
- [ ] VACUUM and retention settings are explicit table or catalog policy, not undocumented defaults, and legal hold can block deletion when required.
- [ ] Time-travel access to historical versions containing erased personal data is disabled, shortened, rewritten, or otherwise made compliant according to documented policy.
- [ ] Deletion vectors, compaction, rewrite, crypto-shredding, and VACUUM choices include proof artifacts and known limitations.
- [ ] Failed deletes, aborted commits, orphan files, speculative task outputs, and retry leftovers are discoverable and cleanable.
- [ ] Erasure completion records identify affected subjects, tables, version ranges, derived outputs, exceptions, and verification result without retaining unnecessary PII.

### Residency and cross-border transfer
- [ ] S3, ADLS, GCS, and PVC-backed storage choices specify region, replication, backup, support-access geography, and data-residency commitments.
- [ ] Driver, executor, shuffle-worker, and object-store placement cannot silently move regulated data across prohibited regions or clusters.
- [ ] Region-aware scheduling, storage references, catalog policies, and network egress controls align with residency requirements.
- [ ] Cross-region replication, lifecycle tiering, disaster recovery, and object-store fallback are reviewed for transfer impact.
- [ ] Tenant-facing configuration explains what DeltaSharp enforces, what the platform operator configures, and what the tenant must decide.
- [ ] SCC/TIA, DPA, sub-processor, and support-access evidence is updated when infrastructure or provider choices change.

### Access, breach readiness, and operations
- [ ] Privacy-sensitive access paths are covered by 05 security controls and 14 tenant-isolation controls before compliance claims are made.
- [ ] Access to historical versions, lineage, catalog metadata, audit logs, and support bundles is role-scoped and logged.
- [ ] Incident response covers wrong-tenant reads, object-store misconfiguration, stale time-travel exposure, leaked shuffle spill, and catalog/lineage disclosure.
- [ ] Breach triage can identify affected tenants, tables, versions, time windows, storage locations, and subjects fast enough to support regulatory clocks.
- [ ] Operational runbooks avoid copying raw personal data into tickets, chat, dashboards, or long-lived debug artifacts.
- [ ] Privacy exceptions include legal basis, owner, expiry, compensating controls, and tenant/customer communication requirements.

## Anti-patterns (red flags)
- Treating immutable Delta history, time travel, backups, or `_delta_log` metadata as exempt from erasure and retention obligations.
- Capturing full SQL text, row samples, object paths, or plan literals in logs, traces, metrics, audit records, or `EXPLAIN` without redaction and access control.
- Lineage that is too incomplete to prove where PII flowed or too detailed because it stores personal data unnecessarily.
- VACUUM, deletion vectors, compaction, or crypto-shredding described as compliant without proof artifacts and exception handling.
- Region, replication, backup, or support-access changes that silently alter data-residency commitments.
- Assuming namespace isolation alone satisfies privacy obligations without the controls in 05 and 14.

## References
- [05 — Security Checklist](05-security-checklist.md)
- [14 — Tenant Isolation Checklist](14-tenant-isolation-checklist.md)
- [17 — Delta Storage Format Checklist](17-delta-storage-format-checklist.md)
- [09a — Logging Checklist](09a-logging-checklist.md)
- [09b — Metrics Checklist](09b-metrics-checklist.md)
- [09c — Distributed Tracing Checklist](09c-distributed-tracing-checklist.md)
- ADR-0003: Data-plane transport
- ADR-0004: Shuffle architecture
- ADR-0009: Kubernetes Operator and CRD design
- `docs/persona/agents/privacy-compliance-grc-lead-agent.md`
