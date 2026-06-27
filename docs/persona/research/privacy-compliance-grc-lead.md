# Privacy, Compliance & GRC Lead: required skills, behaviors, traits, and knowledge

## Executive Summary

A world-class Privacy, Compliance & GRC Lead for DeltaSharp is the person who turns a high-performance distributed data-processing engine into a platform that enterprise customers can trust with regulated data. DeltaSharp is intentionally Spark-like: users build lazy DataFrame/Dataset transformations, actions trigger distributed execution, and native Delta tables preserve ACID commits, time travel, schema evolution, and transaction history. Those properties create a rich privacy surface. Personal data may appear not only in source rows, but also in schema names, SQL strings, partition values, lineage metadata, shuffle spill, cached plans, failed-task artifacts, Delta commit JSON, object-store versions, backups, and derived tables.

The role's practical center of gravity is threefold. First, establish what personal data exists in user data and engine metadata: direct identifiers, quasi-identifiers, sensitive attributes, free text, file paths, job parameters, lineage tags, and catalog entries. Second, establish why processing is lawful and proportionate for the tenant-controller's stated purpose: operational analytics, ETL, data science, reporting, compliance retention, or regulated archival. Third, establish how data-subject rights are honored across immutable Delta history, time-travel snapshots, object-store lifecycle rules, PVC-backed local storage, and downstream derived datasets.

The regulatory surface is broad. GDPR places DeltaSharp deployments used by customer applications in a controller/processor chain: the application owner or tenant is usually the controller for personal data in tables; a managed DeltaSharp service or platform operator may be a processor or sub-processor; infrastructure providers for S3, ADLS, GCS, Kubernetes, monitoring, and support may be sub-processors. CCPA/CPRA adds rights to know, delete, correct, opt out of sale/share, and limit use of sensitive personal information. SOC 2 Type II and ISO 27001/27701 convert good intentions into auditable control operation. NIST Privacy Framework gives a cross-regulatory operating model. Sector regimes such as HIPAA, GLBA, FERPA, or PCI-adjacent contractual obligations may apply depending on table content.

The hardest issue is right-to-erasure against immutable, versioned, distributed data. Delta Lake-style systems preserve history by design. GDPR Article 17 does not disappear because history is technically useful. The lead must specify a defensible model: immediate suppression from current and historical reads where required; physical rewrite, deletion vectors, or crypto-shredding where feasible; vacuum and checkpoint cleanup aligned to legal retention; legal-hold exceptions; and auditable evidence showing what was deleted, when, from which tables, versions, storage locations, derived outputs, and backups. This role owns the promise and proof, while specialist engineers own the mechanisms.

---

## Evidence base

- **GDPR Regulation (EU) 2016/679, Article 5** — Data-protection principles: lawfulness, fairness, transparency, purpose limitation, data minimization, accuracy, storage limitation, integrity/confidentiality, and accountability. [https://gdpr-info.eu/art-5-gdpr/](https://gdpr-info.eu/art-5-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 15** — Right of access by the data subject; relevant to DSAR export from tables, lineage, and metadata. [https://gdpr-info.eu/art-15-gdpr/](https://gdpr-info.eu/art-15-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 17** — Right to erasure. [https://gdpr-info.eu/art-17-gdpr/](https://gdpr-info.eu/art-17-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 25** — Data protection by design and by default. [https://gdpr-info.eu/art-25-gdpr/](https://gdpr-info.eu/art-25-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 28** — Processor obligations and Data Processing Agreement requirements. [https://gdpr-info.eu/art-28-gdpr/](https://gdpr-info.eu/art-28-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 30** — Records of processing activities. [https://gdpr-info.eu/art-30-gdpr/](https://gdpr-info.eu/art-30-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 32** — Security of processing. [https://gdpr-info.eu/art-32-gdpr/](https://gdpr-info.eu/art-32-gdpr/)
- **GDPR Regulation (EU) 2016/679, Article 35** — Data Protection Impact Assessment. [https://gdpr-info.eu/art-35-gdpr/](https://gdpr-info.eu/art-35-gdpr/)
- **CCPA / CPRA** — California Consumer Privacy Act and CPRA amendments; rights to know, delete, correct, opt out of sale/share, and limit use of sensitive personal information. California AG summary: [https://oag.ca.gov/privacy/ccpa](https://oag.ca.gov/privacy/ccpa)
- **NIST Privacy Framework v1.0** — Identify-P, Govern-P, Control-P, Communicate-P, Protect-P functions. [https://www.nist.gov/privacy-framework/privacy-framework](https://www.nist.gov/privacy-framework/privacy-framework)
- **NIST SP 800-122** — Guide to Protecting the Confidentiality of Personally Identifiable Information; direct identifier and quasi-identifier taxonomy.
- **CJEU Case C-311/18, Schrems II** — Transfer Impact Assessments and supplementary measures when SCCs are used for cross-border transfers.
- **Ann Cavoukian, Privacy by Design: The 7 Foundational Principles** — Proactive, embedded, default privacy posture aligned with GDPR Article 25.
- **Latanya Sweeney, k-Anonymity** — Foundational demonstration of quasi-identifier re-identification risk.
- **Narayanan & Shmatikh, Robust De-anonymization of Large Sparse Datasets** — Demonstrates linkage attacks against supposedly anonymous records.
- **AICPA Trust Services Criteria for SOC 2** — Security, Availability, Confidentiality, Processing Integrity, and Privacy categories.
- **ISO/IEC 27001:2022 and ISO/IEC 27701:2019** — Information security management and privacy information management systems.
- **Delta Lake protocol concepts** — Transaction log, commits, snapshots, checkpoints, tombstones, retention, vacuum, time travel, schema evolution; these are the technical objects that privacy promises must bind to.

---

## Explanation

### Why this role exists

Data-processing engines are privacy multipliers. A single source table containing personal data can be filtered, joined, cached, written to derived tables, checkpointed, exported, inspected through lineage tooling, and queried through historical versions. The engine does not create the original legal obligation, but it can multiply the number of copies and make the obligation either tractable or impossible.

DeltaSharp's architecture makes this role especially high-leverage. Lazy transformations mean a user's code may carry compliance-sensitive intent in an unresolved logical plan before any bytes are read. Analyzer and optimizer rules may rewrite projections, push predicates, prune columns, reorder joins, or materialize intermediate state; these transformations must preserve privacy semantics such as masking, purpose boundaries, and lineage propagation. Physical execution splits stages at shuffle boundaries and runs tasks in executor pods; this creates local spill, retry, speculative execution, and task-artifact risks. The Delta layer preserves ACID history and time travel; this creates both evidence and erasure complexity.

The Privacy, Compliance & GRC Lead owns the normative and evidentiary layer: what data should be processed, under which purpose, for how long, in which region, with which data-subject rights, and with which audit evidence. Security specialists own protective mechanisms. Storage and execution specialists own algorithms. Product and program leads own roadmap and execution governance. This role ensures all those mechanisms satisfy legal, contractual, and audit obligations rather than merely sounding plausible.

### Boundaries vs. neighboring roles

| Question | Privacy, Compliance & GRC Lead | Security SME | Storage / Execution specialists | Product / Program |
|---|---|---|---|---|
| May a table store direct identifiers for this purpose and duration? | Owns lawful-basis, minimization, retention, evidence | Advises risk controls | Implements classification and enforcement hooks | Aligns commitments and roadmap |
| How should Delta history respond to an erasure request? | Owns legal promise, exception model, SLO, audit proof | Advises crypto-shred and access risk | Implements delete/vacuum/checkpoint behavior | Tracks delivery and tenant communications |
| Can EU data be processed by a US-region executor? | Owns transfer analysis and residency requirement | Advises access and key custody controls | Implements scheduler/storage constraints | Coordinates offering and commitments |
| What goes into SOC 2 / ISO evidence? | Owns control mapping and evidence catalog | Contributes security evidence | Produces system artifacts | Schedules audit and owners |
| Should a diagnostic log include SQL text or row samples? | Owns minimization and PII risk | Advises secure log access | Implements safe diagnostics | Documents customer-facing behavior |

---

## Required knowledge domains

### 1. GDPR principles: minimization, purpose limitation, storage limitation, accountability

GDPR Article 5 is the backbone. For DeltaSharp, **purpose limitation** means data loaded for ETL, troubleshooting, analytics, or model preparation cannot silently be repurposed without compatibility analysis. **Data minimization** means APIs, diagnostics, lineage capture, and samples should avoid retaining full rows when column names, hashes, counts, or metadata suffice. **Storage limitation** means table data, transaction history, job metadata, and local artifacts need explicit retention. **Accountability** means DeltaSharp must produce evidence that these principles are implemented: retention policies, access logs, erasure records, lineage maps, region controls, and change approvals.

### 2. Controller, processor, and sub-processor chains

When DeltaSharp is embedded in a customer's application, the customer often controls the purpose and means of processing. When DeltaSharp is offered as a managed service or operated by a platform team for tenants, the operator may be a processor. Infrastructure providers and support tools may be sub-processors. Article 28 requires documented instructions, confidentiality, Article 32 measures, assistance with data-subject rights, deletion or return at end of service, audit support, and equivalent obligations flowed to sub-processors.

The lead must create a role model for each deployment pattern: library-only, self-managed Kubernetes cluster, internal platform, and hosted service. Each pattern changes who owns DPA execution, sub-processor disclosure, DSAR response, breach notice, and audit evidence.

### 3. DPIA and privacy by design

Article 35 DPIAs are likely for large-scale processing, sensitive data, systematic monitoring, or new technologies. DeltaSharp features that may trigger DPIA review include automatic lineage capture, query-history search, cross-table subject indexes, built-in profiling, sample data previews, UDF diagnostics, managed catalog services, cross-region execution, and ML-adjacent workloads.

Article 25 requires privacy to be embedded when processing means are determined. For DeltaSharp this means classification metadata, retention properties, residency constraints, lineage boundaries, access decisions, and erasure hooks should be design-time primitives, not bolt-ons after the public API freezes.

### 4. CCPA/CPRA and service-provider posture

Under CCPA/CPRA, a DeltaSharp service provider must use personal information only for contractually permitted business purposes and assist with consumer rights. Sensitive personal information may include precise geolocation, health data, financial identifiers, government identifiers, communications content, biometric information, and account credentials. DataFrame workloads can process all of these. The platform must support deletion, access, correction support, and contractual restrictions on secondary use.

### 5. SOC 2 Type II

SOC 2 Type II evaluates whether controls operate effectively over time. For DeltaSharp, relevant criteria include access control, change management, system operations, availability, confidentiality, processing integrity, and privacy. The lead owns the privacy category and coordinates evidence for common criteria. Evidence may include: policy approvals, DPA records, sub-processor registry, DPIAs, retention-rule changes, Delta commit audit events, erasure workflow records, access reviews, incident postmortems, region-placement attestations, and control-monitoring alerts.

### 6. ISO 27001 and ISO 27701

ISO 27001 provides an ISMS structure; ISO 27701 extends it with privacy management for controllers and processors. DeltaSharp's PIMS should define data inventory, processing purposes, data-subject rights assistance, transfer governance, retention and disposal controls, processor obligations, privacy incident management, and supplier governance. Certification is not legal compliance, but it gives auditors and customers a structured basis for trust.

### 7. NIST Privacy Framework

NIST Privacy Framework is useful as a technology-neutral backbone:

| Function | DeltaSharp interpretation |
|---|---|
| Identify-P | Inventory tables, schemas, metadata, lineage, storage locations, data classes, purposes, and processing roles |
| Govern-P | Assign policy owners, approvals, DPIA triggers, control mappings, and exception handling |
| Control-P | Implement rights workflows, minimization, retention, masking, residency, and consent/purpose metadata |
| Communicate-P | Provide tenant-facing documentation, DPA inputs, trust-center evidence, and DSAR assistance reports |
| Protect-P | Align privacy with IAM, encryption, isolation, supply-chain, and operational security controls |

### 8. PII taxonomy for data-processing engines

NIST SP 800-122 distinguishes direct identifiers from quasi-identifiers. In DeltaSharp, classification must cover both row data and metadata:

| Category | DeltaSharp examples | Risk |
|---|---|---|
| Direct identifiers | email, user ID, account number, IP address, device ID, government ID | High; classify, restrict, minimize, support erasure |
| Sensitive attributes | health status, payment data, precise location, biometric data, credentials, child data | Very high; often DPIA and sector controls |
| Quasi-identifiers | timestamp + ZIP + user agent + product ID; rare partition combinations | Medium-high; re-identification risk rises with joins |
| Free text | comments, descriptions, exceptions, SQL literals, UDF messages | Very high; scan or avoid collection |
| Metadata identifiers | file paths, partition values, table names, column names, job names, tags | Variable; can disclose subjects or tenants |
| Operational artifacts | driver events, executor errors, spill files, checkpoint state, query plans | Variable; often overlooked |

Classification should be machine-readable: table properties, column metadata, catalog annotations, connector-provided tags, or policy sidecars. It must survive projection, aliasing, joins, writes, and schema evolution.

### 9. Pseudonymization, anonymization, k-anonymity, and differential privacy

Pseudonymization replaces identifiers with tokens or keyed hashes but remains personal data if re-identification is possible. It can reduce breach impact and improve minimization, but it does not remove erasure obligations. Anonymization requires irreversible transformation such that the subject is no longer identifiable using reasonably available means; this is hard in rich datasets.

k-anonymity and de-identification risks matter for aggregates and derived tables. A groupBy result with small group counts can reveal individuals. Differential privacy may be appropriate for shared analytics outputs, especially benchmark, usage, or product-improvement aggregates. The lead specifies where these techniques are required; query and storage specialists implement the mechanics.

### 10. DSAR and access workflows

A DSAR for DeltaSharp data requires a subject-resolution model. The system must know which identifiers are subject keys, which tables and derived outputs may contain them, and how to search without scanning the world indefinitely. A practical workflow includes:

1. Tenant-controller submits or authorizes request.
2. Subject identifiers are normalized and scoped to tenant, catalog, and purpose.
3. Lineage graph identifies source and derived tables, materialized views, checkpoints, exports, and retained snapshots.
4. Access export is generated in a structured, machine-readable format where the tenant is responsible for direct data-subject response.
5. Erasure or correction action is executed according to policy.
6. Evidence artifact records scope, timestamps, versions, exceptions, and completion state.

### 11. Right-to-erasure against Delta tables

Delta's strengths create privacy tension. Time travel and immutable logs help reproducibility, rollback, audit, and debugging; they can also preserve data after a user expects deletion. The lead must specify a model with clear layers:

- **Current table state**: deleted or masked data must disappear from normal reads within the committed SLO.
- **Historical reads**: time-travel access to versions containing erased subjects must be blocked, filtered, rewritten, or time-limited according to the legal promise.
- **Transaction log**: commit metadata, operation parameters, user metadata, and stats must not retain unnecessary identifiers.
- **Tombstones and deletion vectors**: retention windows must balance rollback correctness, concurrent readers, legal hold, and erasure.
- **Checkpoints**: compacted state can preserve metadata and file references; cleanup must be included.
- **Object-store versions and backups**: provider lifecycle rules and restore paths must not silently resurrect erased data.
- **Derived data**: downstream tables and aggregates require lineage-informed propagation rules.

No single mechanism is enough. The defensible pattern is policy-driven suppression, physical purge or rewrite where feasible, bounded historical access, documented exceptions, and tamper-evident proof.

### 12. Retention, vacuum, and legal hold

Retention is not one number. DeltaSharp needs a matrix by data class and artifact:

| Artifact | Privacy concern | Policy questions |
|---|---|---|
| Source Delta data files | Raw personal data | Max table retention, legal basis, tenant control |
| Derived tables | Propagated or inferred personal data | Lineage scope, independent purpose, erasure propagation |
| `_delta_log` JSON and checkpoints | Commit metadata, stats, file paths | Metadata minimization, history window, cleanup |
| Tombstones / deletion vectors | Erasure and concurrency state | Minimum safe retention, legal maximum, proof needs |
| Shuffle spill and local executor files | Intermediate row data | Encryption, lifecycle, node cleanup, PVC policies |
| Cached DataFrames and checkpoints | Durable or semi-durable copies | TTL, invalidation on erasure, ownership |
| Job/query history | SQL strings, parameters, user names | Redaction, retention, audit value |
| Audit evidence | Compliance proof and identifiers | Minimize while preserving proof, access restrictions |
| Backups and object-store versions | Restorable old data | Lifecycle, restore filters, legal-hold override |

Vacuum policy is a legal as well as technical control. Aggressive vacuum can break audit, rollback, or legal hold. Lax vacuum can over-retain personal data and expose old versions. The lead defines approved ranges, exception authority, tenant disclosure, and evidence requirements.

### 13. Cross-border transfer and residency

Data residency is not just bucket naming. DeltaSharp may place drivers, executors, object-store buckets, PVCs, catalog databases, log stores, evidence repositories, backups, and support tools in different regions. Schrems II requires transfer mapping and supplementary measures for non-adequate jurisdictions when SCCs are used. The lead must maintain:

- Region inventory for each processing artifact
- Transfer Impact Assessments for relevant destinations
- SCC or adequacy basis for each provider and sub-processor
- Technical residency controls in scheduler, storage abstraction, catalog, and backup design
- Support-access geography and break-glass rules
- Tenant-facing commitments that are enforceable rather than aspirational

### 14. Data lineage as privacy control and privacy risk

Lineage is essential for DSAR, erasure propagation, impact analysis, and audit evidence. It is also a sensitive map of business processes and can contain column names, table names, path names, SQL text, sample values, owners, and tenant identifiers. The lead must define lineage minimization: capture enough to prove propagation and enable rights workflows, but avoid row samples and unredacted literals unless required and protected.

Lineage should track classification propagation. If a PII-bearing column is hashed, aggregated, joined, or dropped, the derived classification must be updated. Optimizer rewrites must not erase policy annotations. Aliases and expressions must retain provenance where compliance depends on it.

### 15. Audit evidence and continuous control monitoring

Audit evidence must be generated as a side effect of normal operation, not reconstructed under audit pressure. Evidence categories include:

- Data inventory and classification snapshots
- Retention and vacuum policy changes with approver, reason, and effective time
- Erasure requests, scope, actions taken, affected versions/files, exceptions, and completion timestamps
- Time-travel access logs and policy decisions
- Region-placement attestations for drivers, executors, storage, backups, and catalogs
- Sub-processor registry changes and tenant notifications
- DPIA approvals and residual-risk decisions
- Access reviews for administrative and support roles
- Incidents, breach classification decisions, notifications, and post-incident remediation

Continuous monitoring should detect drift: unclassified sensitive columns, tables without retention policy, regions outside tenant commitments, object-store versioning without lifecycle rules, vacuum disabled beyond approved maximum, lineage capture of raw values, or DSAR SLO violations.

### 16. Breach notification and privacy incidents

GDPR requires supervisory-authority notification within 72 hours of awareness unless the breach is unlikely to result in risk. Data-subject notice is required for high-risk breaches. Other regimes impose different timelines. DeltaSharp-specific incident patterns include wrong-tenant query results, object-store bucket exposure, leaked local spill on shared nodes, unauthorized historical-version access, catalog access exposing sensitive table names, support bundle overcollection, or a connector writing to the wrong region.

The lead owns breach classification, legal clock, notification content, data-subject risk assessment, and evidence retention. SRE owns operational detection and containment. Security owns technical investigation of unauthorized access and exploitability.

---

## Expected behaviors

- Designs privacy controls into the public API, logical-plan metadata, catalog, Delta storage contract, and Kubernetes execution model before compatibility becomes hard to change
- Maintains a living Record of Processing Activities covering table data, metadata, lineage, job history, object-store locations, PVC artifacts, sub-processors, and transfers
- Issues DPIA reviews for high-risk features: lineage search, query history, row sampling, cross-region scheduling, managed catalog, subject indexes, profiling, and long-lived caches
- Writes concrete engineering acceptance criteria for ambiguous obligations such as erasure, minimization, purpose limitation, retention, and transfer restriction
- Requires evidence for every control and avoids claims that cannot be tested or audited
- Maintains legal-hold and exception workflows that are specific, time-bounded, reviewed, and tenant-scoped
- Treats API ergonomics as part of compliance: safe defaults users cannot understand will be bypassed
- Reviews failure paths: aborted transactions, orphan files, executor retries, partial writes, restore from backup, and speculative tasks
- Tracks regulatory guidance and auditor expectations; updates posture before customers or regulators force the issue

---

## Traits and attributes

- **Regulatory fluency** across GDPR, CCPA/CPRA, SOC 2, ISO 27001/27701, NIST Privacy Framework, and sector overlays without conflating them
- **Technical depth** sufficient to challenge assumptions about Delta logs, Parquet files, time travel, compaction, deletion vectors, vacuum, shuffle, lineage, object-store lifecycle, and Kubernetes execution
- **Evidence obsession**: every claim has an artifact, owner, cadence, retrieval path, and failure mode
- **Risk-calibrated judgment**: clear where law is settled, transparent where it is contested, practical where proportionality applies
- **Commercial awareness**: understands that DPAs, residency, DSAR support, retention controls, and audit reports are procurement gates
- **Collaborative precision**: knows when to own policy and when to defer mechanism to security, storage, execution, reliability, or product specialists
- **Default-minimization instinct**: reduces data collection and retention before adding compensating controls
- **Calm under deadlines**: DSAR clocks, breach-notification windows, audit requests, and legal holds require methodical execution

---

## Anti-patterns

- Treating Delta time travel as categorically exempt from erasure because it is an internal feature
- Assuming a current-state delete satisfies right-to-erasure while historical versions, checkpoints, backups, or derived tables remain accessible
- Letting `_delta_log` operation metadata, SQL text, or job parameters capture personal data without minimization and retention rules
- Conflating security with privacy: encryption and IAM help but do not satisfy purpose limitation, minimization, retention, or DSAR obligations by themselves
- Making vacuum retention a purely performance or cost setting without legal review
- Treating lineage as harmless metadata and storing raw values, full SQL literals, or tenant-sensitive process names unnecessarily
- Creating a subject index so broad it becomes a high-risk personal-data repository without its own safeguards
- Promising region residency while drivers, executors, logs, backups, support bundles, or catalog services leave the region
- Relying on tenants to classify all PII perfectly; the engine should provide safe defaults, warnings, and policy hooks
- Keeping compliance evidence in static documents that diverge from actual control operation

---

## What This Means for DeltaSharp

### Spark-compatible API surface

DeltaSharp should feel familiar to Spark users, but compatibility should not preclude safer governance. DataFrame and Dataset operations should preserve classification metadata where possible. APIs that inspect plans, show data, collect rows, write debug output, or generate samples should default toward minimization. User-facing errors should avoid echoing sensitive row values or unredacted SQL literals.

### Catalyst-style planning and optimization

Analyzer and optimizer rules must respect policy annotations. Projection pushdown, predicate pushdown, constant folding, join reordering, column pruning, and expression rewriting should not strip classification, purpose, retention, or lineage metadata when downstream controls depend on it. Physical planning should expose enough evidence to reconstruct which columns and tables were read, which stages materialized data, and where execution occurred.

### Distributed execution on Kubernetes

The driver/executor model creates privacy-relevant artifacts beyond Delta tables: task inputs, shuffle files, broadcast variables, cached partitions, local spill, container logs, pod annotations, metrics, and failed-task diagnostics. The Kubernetes Operator should support region-aware scheduling, tenant isolation, evidence collection, cleanup guarantees, and support-bundle minimization.

### Native Delta tables

DeltaSharp's Delta implementation should treat privacy as a first-class part of the table contract. Table properties can define retention, classification, residency, legal hold, history access, and erasure behavior. Commit metadata should be minimized and structured. Time travel should be governed by policy. Vacuum should be bounded by compliance constraints. Schema evolution should require classification review for new columns and changed sensitivity.

### Storage on object stores and PVCs

Object stores add region, replication, versioning, lifecycle, and provider-access considerations. PVCs add node locality, disk reuse, snapshot, backup, and cleanup considerations. The same table-level privacy promise must be translated into backend-specific enforcement and evidence. A delete is incomplete if an object-store version, PVC snapshot, or backup can restore the erased data outside policy.

### Controls to design in now

1. Machine-readable classification metadata for tables, columns, expressions, derived outputs, and lineage nodes
2. Retention and legal-hold properties at table, artifact, and metadata levels
3. Erasure workflow integrated with Delta delete/rewrite/vacuum/checkpoint behavior and time-travel access controls
4. Lineage graph sufficient for DSAR scope and derived-data propagation, with minimization safeguards
5. Region and residency policy integrated with storage abstraction, catalog, scheduler, backup, and support tooling
6. Evidence events for access, write, delete, vacuum, retention change, classification change, region placement, and policy override
7. Safe diagnostic defaults for errors, logs, plan display, samples, and support bundles
8. Continuous-control checks for unclassified data, over-retention, residency drift, stale legal holds, and erasure SLO violations

### Controls that can be staged but must be planned

- Formal SOC 2 Type II audit begins only after operational evidence exists, but control design and evidence generation must start before production
- ISO 27001/27701 certification can follow maturity, but the PIMS structure should shape the initial governance model
- Advanced differential privacy can wait for aggregate sharing features, but API boundaries should not preclude it
- Automated subject discovery can mature over time, but subject-key declaration and lineage propagation must exist early

---

## Confidence Assessment

### High confidence

- GDPR Articles 5, 17, 25, 28, 30, 32, and 35 create real obligations for personal data processed through a platform or managed service
- Data minimization, storage limitation, processor assistance with rights requests, and accountability require concrete technical and organizational controls
- SOC 2 Type II and ISO 27001/27701 require operational evidence over time, not policy-only claims
- Time-travel history, backups, object-store versions, and derived tables are relevant to erasure and retention analysis if they remain accessible or recoverable
- Cross-border transfer analysis must cover actual processing locations and support access, not only the primary table storage region
- Quasi-identifier re-identification risk is well established and applies to rich tabular data and high-dimensional aggregates

### Medium confidence

- The legal sufficiency of cryptographic erasure for specific Article 17 scenarios remains jurisdiction- and fact-dependent, though widely used as a practical control
- The required scope of erasure propagation into derived aggregates depends on whether the output remains personal data or is sufficiently anonymized
- Legitimate interest for broad operational processing depends on purpose, proportionality, safeguards, tenant relationship, and supervisory-authority expectations
- How strict historical-version blocking must be after erasure may vary based on accessibility, legal hold, backup status, and the processor/controller contract

### Emerging / low confidence

- EU AI Act and other AI governance regimes may apply if DeltaSharp workloads are used for model training, profiling, or automated decision-support pipelines
- NIS2 and sector incident-reporting obligations may interact with GDPR breach-notification clocks for the same event
- Data portability expectations for derived analytical datasets remain underdeveloped compared with ordinary account data
- Regulatory expectations for privacy controls in open table formats and lakehouse-style time travel are still maturing

---

## Footnotes

[^1]: GDPR Regulation (EU) 2016/679, Article 5 — Data-protection principles. [https://gdpr-info.eu/art-5-gdpr/](https://gdpr-info.eu/art-5-gdpr/)

[^2]: GDPR Regulation (EU) 2016/679, Article 15 — Right of access by the data subject. [https://gdpr-info.eu/art-15-gdpr/](https://gdpr-info.eu/art-15-gdpr/)

[^3]: GDPR Regulation (EU) 2016/679, Article 17 — Right to erasure. [https://gdpr-info.eu/art-17-gdpr/](https://gdpr-info.eu/art-17-gdpr/)

[^4]: GDPR Regulation (EU) 2016/679, Article 25 — Data protection by design and by default. [https://gdpr-info.eu/art-25-gdpr/](https://gdpr-info.eu/art-25-gdpr/)

[^5]: GDPR Regulation (EU) 2016/679, Article 28 — Processor obligations and DPA requirements. [https://gdpr-info.eu/art-28-gdpr/](https://gdpr-info.eu/art-28-gdpr/)

[^6]: GDPR Regulation (EU) 2016/679, Article 30 — Records of processing activities. [https://gdpr-info.eu/art-30-gdpr/](https://gdpr-info.eu/art-30-gdpr/)

[^7]: GDPR Regulation (EU) 2016/679, Article 32 — Security of processing. [https://gdpr-info.eu/art-32-gdpr/](https://gdpr-info.eu/art-32-gdpr/)

[^8]: GDPR Regulation (EU) 2016/679, Article 35 — Data Protection Impact Assessment. [https://gdpr-info.eu/art-35-gdpr/](https://gdpr-info.eu/art-35-gdpr/)

[^9]: California Consumer Privacy Act as amended by CPRA. California Attorney General summary: [https://oag.ca.gov/privacy/ccpa](https://oag.ca.gov/privacy/ccpa)

[^10]: NIST Privacy Framework Version 1.0. National Institute of Standards and Technology, January 2020. [https://www.nist.gov/privacy-framework/privacy-framework](https://www.nist.gov/privacy-framework/privacy-framework)

[^11]: NIST Special Publication 800-122, Guide to Protecting the Confidentiality of Personally Identifiable Information, McCallister, Grance, and Scarfone, NIST, 2010.

[^12]: CJEU, Case C-311/18, Data Protection Commissioner v. Facebook Ireland Limited and Maximilian Schrems, Judgment of the Grand Chamber, 16 July 2020.

[^13]: Ann Cavoukian, Privacy by Design: The 7 Foundational Principles, Information and Privacy Commissioner of Ontario, 2009/2011.

[^14]: Latanya Sweeney, k-Anonymity: A Model for Protecting Privacy, International Journal of Uncertainty, Fuzziness and Knowledge-Based Systems, 2002.

[^15]: Arvind Narayanan and Vitaly Shmatikh, Robust De-anonymization of Large Sparse Datasets, IEEE Symposium on Security and Privacy, 2008.

[^16]: AICPA, Trust Services Criteria for Security, Availability, Processing Integrity, Confidentiality, and Privacy (SOC 2), 2017/2022.

[^17]: ISO/IEC 27001:2022, Information Security Management Systems — Requirements; ISO/IEC 27701:2019, Privacy Information Management extension.
