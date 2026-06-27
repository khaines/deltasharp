# EPIC-06: Catalog & Data Sources

- **Roadmap milestone:** M2 (link to ../../../ROADMAP.md#milestone-2--storage--sql-v0x)
- **Primary persona(s):** `catalog-metastore-engineer`, `data-platform-connectors-engineer`
- **Related ADRs:** ADR-0005
- **Depends on:** EPIC-04, EPIC-05
- **Status:** draft
- **Size:** XL

## Objective

Provide Spark V2-style catalog and DataSource contracts so DeltaSharp can register, resolve, scan, and write tables through public extension points. This epic connects the SQL/analyzer layer to native and external metadata, and connects scan/write planning to built-in and third-party-style data sources without compromising Delta ACID guarantees from EPIC-05.

## Scope

**In scope**
- Pluggable catalog abstractions modeled on Spark V2 `CatalogPlugin` and `TableCatalog`, including capabilities, namespaces, tables, views, functions, errors, and analyzer-facing resolution contracts.
- Native DeltaSharp catalog persistence for namespaces, tables, views, functions, `information_schema`, and DDL operations including `CREATE`, `ALTER`, `DROP`, `SHOW`, and `DESCRIBE`.
- Hive Metastore-compatible first-party catalog plugin for lakehouse interoperability.
- DataSource V2-style connector API for scan builders, partition discovery, projection/filter pushdown, splits, statistics, write modes, and commit coordination.
- Built-in Parquet, CSV, JSON, and Delta source/sink wiring to EPIC-05 storage behavior.

**Out of scope** (and where it lives instead)
- Delta transaction log, Parquet internals, ACID commit implementation, deletion vectors, and VACUUM mechanics → EPIC-05 / persona `delta-storage-format-engineer`.
- SQL grammar, parser ASTs, and user-facing command syntax beyond catalog semantics → EPIC-07 / persona `sql-language-frontend-engineer`.
- Physical operator execution, scheduler behavior, and optimizer rule implementation beyond declared connector contracts → EPIC-08 and EPIC-11 / personas `query-execution-engine-engineer`, `query-optimizer-scheduler-engineer`.
- Authorization policy, IAM integration, grants enforcement, and secret distribution → EPIC-00 / persona `cloud-native-security-sme`.
- Streaming-specific source offsets and micro-batch semantics → EPIC-12 / persona `structured-streaming-engine-engineer`.

## Exit criteria

- [ ] Tables, views, functions, and namespaces can be registered and resolved through the native catalog using stable identifiers and transactional DDL.
- [ ] Identifier resolution serves the analyzer and SQL frontend for multipart names, current namespace, temporary objects, case sensitivity, `SHOW`, `DESCRIBE`, and `information_schema`.
- [ ] A Hive Metastore-compatible plugin reads and writes external metastore metadata with documented mappings, unsupported cases, cache invalidation, and failure modes.
- [ ] A third-party-style DataSource V2 connector works through the public API, including schema reporting, split planning, filter/projection pushdown with residuals, and writes.
- [ ] Built-in Parquet, CSV, JSON, and Delta sources read and write correctly through the connector API, with Delta source/sink operations preserving EPIC-05 ACID semantics.
- [ ] Catalog and connector paths enforce tenant/path/credential isolation where external locations, object stores, or PVCs are involved.

## Features

### FEAT-06.1: Pluggable catalog abstraction

- **Objective:** Define Spark V2-compatible catalog interfaces and resolution contracts consumed by SQL, analyzer, optimizer, and connector layers.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `sql-language-frontend-engineer`, `query-execution-engine-engineer`.
- **Depends on:** EPIC-04.

#### Stories

##### STORY-06.1.1: Catalog plugin lifecycle and capabilities

- **As a** platform integrator **I want** `CatalogPlugin` and `TableCatalog`-style interfaces **so that** native, Hive, and future catalogs can be registered consistently.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `dotnet-library-platform-engineer`.
- **Size:** M. **Depends on:** EPIC-04.
- **Acceptance criteria:**
  - [ ] Given catalog configuration When the session starts Then named catalog plugins initialize with options, capability flags, and cancellation-aware async APIs.
  - [ ] Given a plugin that does not implement an operation When called Then it returns a typed unsupported-operation error rather than a provider-specific exception.
  - [ ] Given native and test plugin implementations When registered side by side Then identifier resolution can target either catalog by name.
  - [ ] Given public interface changes When packages are built Then APIs remain trimming/NativeAOT-aware and versionable.
- **Definition of done:** builds/tests/format pass; checklists `19`, `15`, `03a`, `04a` satisfied; docs updated if public API changes.

##### STORY-06.1.2: Identifier resolution contract for analyzer integration

- **As an** analyzer owner **I want** deterministic catalog resolution **so that** unresolved plans bind tables, views, and functions with Spark-compatible semantics.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `sql-language-frontend-engineer`, `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-06.1.1.
- **Acceptance criteria:**
  - [ ] Given one-, two-, and three-part identifiers When resolved Then catalog, namespace, and object precedence follow documented Spark-compatible rules.
  - [ ] Given quoted names, case sensitivity settings, current catalog, and current namespace When lookup occurs Then returned identifiers have stable canonical and display forms.
  - [ ] Given missing, ambiguous, temporary, or unauthorized objects When resolution fails Then the error class is deterministic and usable by SQL diagnostics.
  - [ ] Given a DDL mutation or catalog refresh When cached analyzer metadata exists Then invalidation prevents silently stale resolution.
- **Definition of done:** builds/tests/format pass; checklists `19`, `15`, `16`, `03a`, `04a` satisfied; docs updated if public API changes.

##### STORY-06.1.3: Catalog metadata and table capability model

- **As a** planner **I want** resolved metadata with capabilities **so that** scans, writes, and DDL are planned without provider-name special cases.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `data-platform-connectors-engineer`, `query-execution-engine-engineer`.
- **Size:** M. **Depends on:** STORY-06.1.2.
- **Acceptance criteria:**
  - [ ] Given a resolved table When metadata is requested Then provider, schema, location, partitioning, properties, statistics token, and capability flags are present.
  - [ ] Given a Delta table entry When resolved Then the metadata contains enough location and property information for EPIC-05 snapshot loading without interpreting Delta log internals in the catalog.
  - [ ] Given connector-backed tables When capabilities are inspected Then read, write, truncate, overwrite, pushdown, and streaming support are explicit.
  - [ ] Given metadata serialization and deserialization When round-tripped Then stable table identity and version tokens are preserved.
- **Definition of done:** builds/tests/format pass; checklists `19`, `15`, `03a`, `04a` satisfied; docs updated if public API changes.

### FEAT-06.2: Native catalog persistence and DDL

- **Objective:** Ship the default DeltaSharp catalog with durable metadata, transactional DDL, `information_schema`, and Spark-compatible metadata commands.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `sql-language-frontend-engineer`.
- **Depends on:** FEAT-06.1.

#### Stories

##### STORY-06.2.1: Native catalog storage model and migrations

- **As a** catalog operator **I want** durable native catalog persistence **so that** namespaces, tables, views, and functions survive process restarts and schema upgrades.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `dotnet-framework-runtime-engineer`.
- **Size:** L. **Depends on:** FEAT-06.1.
- **Acceptance criteria:**
  - [ ] Given persisted native catalog metadata When the driver restarts Then all committed namespaces, tables, views, and functions load with stable identities.
  - [ ] Given concurrent DDL attempts When metadata commits Then conflicts are detected and only valid transactional changes become visible.
  - [ ] Given a catalog schema migration When applied Then versioned migrations are idempotent and preserve backup/restore evidence.
  - [ ] Given corrupt or partially written metadata When opened Then the catalog reports diagnosable corruption and does not silently drop objects.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.2.2: Namespace, table, view, and function DDL semantics

- **As a** SQL user **I want** catalog DDL operations **so that** I can create, alter, drop, show, and describe DeltaSharp objects predictably.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `sql-language-frontend-engineer`.
- **Size:** L. **Depends on:** STORY-06.2.1.
- **Acceptance criteria:**
  - [ ] Given `CREATE`, `ALTER`, `DROP`, `RENAME`, `USE`, `SHOW`, and `DESCRIBE` catalog requests When executed Then metadata changes and output rows match documented semantics.
  - [ ] Given managed and external table definitions When dropped Then managed lifecycle and external non-destructive behavior are enforced.
  - [ ] Given `IF EXISTS` and `IF NOT EXISTS` options When repeated Then operations are idempotent according to their command semantics.
  - [ ] Given invalid locations, duplicate names, incompatible providers, or stale version tokens When DDL runs Then no partial metadata mutation is committed.
- **Definition of done:** builds/tests/format pass; checklists `19`, `15`, `14`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.2.3: `information_schema` and metadata command outputs

- **As a** migration tool author **I want** queryable catalog metadata **so that** tools can inspect namespaces, tables, columns, views, routines, and properties consistently.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `sql-language-frontend-engineer`.
- **Size:** M. **Depends on:** STORY-06.2.2.
- **Acceptance criteria:**
  - [ ] Given catalog objects When `information_schema` views are queried Then columns, types, nullability, ordering, and filtering match documented contracts.
  - [ ] Given `SHOW` and `DESCRIBE` commands When executed Then outputs are derived from the same metadata model as `information_schema`.
  - [ ] Given privilege-filtering hooks When enabled by a security plugin Then metadata rows can be filtered without changing object identities.
  - [ ] Given table properties and provider metadata When inspected Then Delta locations and connector options are visible only through safe, non-secret fields.
- **Definition of done:** builds/tests/format pass; checklists `19`, `15`, `14`, `03a`, `04a` satisfied; docs updated if public API changes.

### FEAT-06.3: Hive Metastore-compatible plugin

- **Objective:** Provide first-party Hive Metastore interoperability while keeping the core catalog abstraction Spark V2-shaped and provider-neutral.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Depends on:** FEAT-06.1, FEAT-06.2.

#### Stories

##### STORY-06.3.1: Hive Metastore object mapping and client contract

- **As a** lakehouse operator **I want** Hive Metastore databases and tables mapped into DeltaSharp catalog metadata **so that** existing metastore assets can be queried.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `data-platform-connectors-engineer`.
- **Size:** L. **Depends on:** STORY-06.1.3.
- **Acceptance criteria:**
  - [ ] Given HMS databases, external tables, managed tables, partitions, properties, and storage descriptors When imported or resolved Then mappings to DeltaSharp metadata are documented and tested.
  - [ ] Given HMS metadata that cannot be represented safely When resolved Then DeltaSharp returns explicit unsupported or lossy-mapping diagnostics.
  - [ ] Given Delta tables registered in HMS When resolved Then provider, schema, partitioning, and location are sufficient for EPIC-05 snapshot loading.
  - [ ] Given metastore credentials and URIs When configured Then secrets are not exposed through errors, logs, or metadata outputs.
- **Definition of done:** builds/tests/format pass; checklists `19`, `14`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.3.2: Hive Metastore DDL and cache invalidation

- **As a** multi-engine user **I want** Hive-backed DDL and refresh behavior **so that** DeltaSharp coexists with external engines changing the same metastore.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `cloud-native-site-reliability-engineer`.
- **Size:** M. **Depends on:** STORY-06.3.1.
- **Acceptance criteria:**
  - [ ] Given supported create, alter, drop, and repair operations When executed through the HMS plugin Then HMS state and DeltaSharp resolved metadata stay consistent.
  - [ ] Given an external HMS change When cache TTL expires or refresh is requested Then subsequent resolution observes the new metadata.
  - [ ] Given metastore outage, timeout, or stale version conflict When DDL or lookup runs Then the plugin returns typed retryable or terminal errors.
  - [ ] Given concurrent DeltaSharp and external DDL When conflicts occur Then no local cache invents success for a failed HMS mutation.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-06.4: DataSource V2 connector API

- **Objective:** Define the public source/sink API for third-party-style connectors, including scans, pushdown, splits, statistics, and writes.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `query-execution-engine-engineer`.
- **Depends on:** EPIC-04, FEAT-06.1.

#### Stories

##### STORY-06.4.1: Scan builder, schema, and pushdown contracts

- **As a** connector author **I want** a scan builder API **so that** sources can declare schema, capabilities, accepted pushdowns, residual filters, and statistics.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-06.1.3.
- **Acceptance criteria:**
  - [ ] Given a connector scan builder When projection and filters are pushed Then it returns accepted predicates, residual predicates, output schema, and correctness caveats.
  - [ ] Given null semantics, timestamps, nested fields, and unsupported expressions When pushdown is attempted Then unsupported filters remain residual and are not marked enforced.
  - [ ] Given source statistics When reported Then row count, size, partitioning, and freshness are optional but typed and versioned.
  - [ ] Given lazy DataFrame transformations When constructing scans Then no data rows are read until an action triggers execution.
- **Definition of done:** builds/tests/format pass; checklists `19`, `16`, `21`, `03a`, `04a` satisfied; docs updated if public API changes.

##### STORY-06.4.2: Partition discovery, splits, and reader factories

- **As an** execution planner **I want** connector splits and reader factories **so that** distributed scans can be planned without source-specific execution logic.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `query-execution-engine-engineer`.
- **Size:** L. **Depends on:** STORY-06.4.1.
- **Acceptance criteria:**
  - [ ] Given file and non-file sources When splits are planned Then split metadata includes size, partition values, locality hints, and serialization-safe reader configuration.
  - [ ] Given partition pruning predicates When planning files Then only matching partitions are enumerated and residual filters remain available for execution.
  - [ ] Given object-store throttling or listing errors When planning splits Then errors are retryable or terminal according to documented connector semantics.
  - [ ] Given executor-side reader creation When a split is consumed Then only that split's scoped credentials and paths are accessible.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `14`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.4.3: Write API, commit coordination, and write modes

- **As a** connector author **I want** DataSource V2 write contracts **so that** append, overwrite, truncate, and create-or-replace modes are safe under retries and task failures.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `query-execution-engine-engineer`, `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-06.4.2, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given append, overwrite, dynamic partition overwrite, truncate, and create-or-replace modes When a connector declares support Then unsupported modes fail before tasks write data.
  - [ ] Given task retry or speculative execution When writer commit messages are aggregated Then duplicate task output is rejected or deduplicated according to connector guarantees.
  - [ ] Given a Delta sink implementation When commits are coordinated Then final visibility is delegated to EPIC-05 Delta commit protocol and remains ACID.
  - [ ] Given a failed job after partial writes When abort runs Then connector cleanup is idempotent and does not delete committed data.
- **Definition of done:** builds/tests/format pass; checklists `19`, `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-06.5: Built-in file and Delta sources

- **Objective:** Ship first-party Parquet, CSV, JSON, and Delta sources/sinks implemented on the connector API and integrated with catalog metadata.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `delta-storage-format-engineer`.
- **Depends on:** FEAT-06.4, EPIC-05.

#### Stories

##### STORY-06.5.1: Built-in Parquet source and sink wiring

- **As a** DataFrame user **I want** Parquet reads and writes through the connector API **so that** Parquet files work consistently inside catalog and path-based flows.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** M. **Depends on:** STORY-06.4.3, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given a path-based Parquet read When schema is provided or inferred Then the connector produces `ColumnVector` batches through EPIC-05 Parquet IO.
  - [ ] Given projection, partition pruning, and filter pushdown When planning a Parquet scan Then row-group/file pruning is used where safe and residual filters are preserved.
  - [ ] Given Parquet writes through DataSource V2 When output completes Then files are readable by the Parquet source and external standards-compliant readers.
  - [ ] Given object-store or PVC paths When reading and writing Then path scoping and credential isolation are enforced.
- **Definition of done:** builds/tests/format pass; checklists `19`, `17`, `14`, `03a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.5.2: Built-in CSV and JSON sources

- **As a** data engineer **I want** CSV and JSON sources with deterministic schema and corrupt-record handling **so that** common file ingestion works without custom connectors.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators none.
- **Size:** L. **Depends on:** STORY-06.4.2.
- **Acceptance criteria:**
  - [ ] Given CSV and JSON files with provided schemas When read Then values, nullability, malformed records, and type conversions follow documented options.
  - [ ] Given schema inference When enabled Then sampling bounds, case sensitivity, numeric/timestamp inference, and nullable behavior are deterministic.
  - [ ] Given corrupt records When permissive, drop-malformed, fail-fast, or quarantine modes are selected Then results and diagnostics match the selected mode.
  - [ ] Given partitioned file layouts When planned Then partition columns are discovered consistently with Parquet source behavior.
- **Definition of done:** builds/tests/format pass; checklists `19`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.5.3: Delta source and sink integration

- **As a** DeltaSharp user **I want** Delta tables exposed as a source and sink **so that** catalog and path-based reads/writes preserve Delta ACID semantics.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `delta-storage-format-engineer`.
- **Size:** L. **Depends on:** STORY-06.4.3, EPIC-05.
- **Acceptance criteria:**
  - [ ] Given a catalog Delta table or path When read Then the connector obtains an EPIC-05 snapshot and scans only active files for that version.
  - [ ] Given append and overwrite writes through DataSource V2 When committed Then visibility is controlled exclusively by the Delta log commit protocol.
  - [ ] Given time-travel options When reading Delta Then version or timestamp selection is passed to EPIC-05 and reflected in the resolved scan metadata.
  - [ ] Given schema merge, overwrite schema, and column mapping options When writing Then connector validation delegates storage correctness to EPIC-05 and surfaces actionable errors.
- **Definition of done:** builds/tests/format pass; checklists `19`, `17`, `21`, `03a`, `04b` satisfied; docs updated if public API changes.

### FEAT-06.6: Connector/catalog conformance and integration fixtures

- **Objective:** Provide conformance tests and fixtures proving catalog resolution and connector behavior work together through public APIs.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `catalog-metastore-engineer`, `reliability-test-chaos-engineer`.
- **Depends on:** FEAT-06.1, FEAT-06.2, FEAT-06.4, FEAT-06.5.

#### Stories

##### STORY-06.6.1: Third-party-style connector conformance suite

- **As a** connector maintainer **I want** reusable conformance tests **so that** external connectors can prove they obey DeltaSharp DataSource V2 contracts.
- **Implementer persona(s):** Primary `data-platform-connectors-engineer`; Collaborators `reliability-test-chaos-engineer`.
- **Size:** M. **Depends on:** FEAT-06.4.
- **Acceptance criteria:**
  - [ ] Given a sample connector implementing the public API When conformance tests run Then schema, pushdown, splits, reads, writes, aborts, and errors are validated.
  - [ ] Given intentionally incorrect pushdown claims When tests execute Then missing residual filters and false enforcement are detected.
  - [ ] Given task retry, abort, and duplicate commit-message scenarios When tests run Then connector idempotency failures are surfaced.
  - [ ] Given API documentation examples When compiled Then sample connector code builds against the public interfaces.
- **Definition of done:** builds/tests/format pass; checklists `19`, `21`, `03a`, `04a`, `04b` satisfied; docs updated if public API changes.

##### STORY-06.6.2: End-to-end catalog-to-source integration fixtures

- **As a** program owner **I want** fixtures that exercise catalog resolution into scans and writes **so that** EPIC-06 exit criteria are objectively verifiable.
- **Implementer persona(s):** Primary `catalog-metastore-engineer`; Collaborators `data-platform-connectors-engineer`, `delta-storage-format-engineer`.
- **Size:** M. **Depends on:** FEAT-06.2, FEAT-06.5.
- **Acceptance criteria:**
  - [ ] Given native catalog tables for Delta, Parquet, CSV, and JSON When DataFrame and SQL flows resolve them Then scans use the expected connector and schema.
  - [ ] Given catalog DDL followed by reads and writes When operations complete Then analyzer resolution, connector planning, and storage commits observe consistent metadata versions.
  - [ ] Given object-store and PVC locations in catalog entries When integration tests run Then tenant/path/credential isolation checks pass.
  - [ ] Given a Delta table updated outside the catalog metadata When refresh is requested Then subsequent reads observe the new Delta snapshot without stale catalog state.
- **Definition of done:** builds/tests/format pass; checklists `19`, `17`, `21`, `14`, `03a`, `04b` satisfied; docs updated if public API changes.

## Open questions

- Should native catalog persistence be embedded in the driver process for v1 or exposed as a small service to simplify concurrent multi-driver deployments?
- Which subset of Hive Metastore SerDe/table-property semantics should be hard errors versus lossy metadata fields in v1 compatibility mode?
- What minimum connector API surface should be frozen for third-party preview before streaming sources from EPIC-12 extend the same contracts?
