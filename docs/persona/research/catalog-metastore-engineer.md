# Catalog & Metastore Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The Catalog & Metastore Engineer is the discipline owner for DeltaSharp's pluggable catalog subsystem: the metadata layer that gives names, namespaces, tables, views, functions, and governance-ready objects stable meaning before the analyzer and execution engine can do useful work. ADR-0005 makes this role explicit: DeltaSharp will use a Spark V2-inspired `CatalogPlugin` / `TableCatalog` model, ship a native default catalog, provide a Hive Metastore-compatible first-party plugin, and leave Unity-Catalog-style governance as a later plugin.[^1]

The role exists because a Spark-compatible engine is not only a planner and executor. Users operate through names: `catalog.namespace.table`, current database, temporary views, routines, table properties, `SHOW TABLES`, `DESCRIBE TABLE`, and `information_schema`. If those names resolve incorrectly, every downstream component inherits ambiguity. If DDL is not transactional, concurrent jobs can bind to the wrong table. If the catalog cannot represent Delta locations and table capabilities cleanly, the storage layer and optimizer receive brittle, provider-specific metadata.[^2]

DeltaSharp's catalog has to be native to .NET while still interoperating with the lakehouse ecosystem. Spark's DataSource V2 catalog interfaces provide the right conceptual shape: plugins initialize with configuration, expose namespaced table operations, advertise capabilities, and return stable metadata objects to analysis and planning. Hive Metastore compatibility remains essential because many existing data lakes still store database and table metadata there; compatibility must be precise about what maps exactly, what is lossy, and what is intentionally unsupported.[^3]

Governance must be anticipated without being prematurely implemented. Unity Catalog demonstrates that modern catalogs become security, lineage, audit, sharing, and multi-engine governance planes, not just lookup maps. DeltaSharp should therefore design object identity, securable boundaries, audit hooks, and metadata APIs so a later Unity-Catalog-style plugin can enforce grants and lineage without rewriting core resolution rules.[^4]

---

## Evidence base

- ADR-0005, "Catalog / metastore" — DeltaSharp decision to build a pluggable Spark V2-style catalog with native default, Hive Metastore-compatible plugin, and later Unity-Catalog-style governance plugin.[^1]
- Apache Spark SQL DataSource V2 catalog API documentation and source concepts — `CatalogPlugin`, `TableCatalog`, `SupportsNamespaces`, `FunctionCatalog`, identifier and namespace abstractions, and table capability models.[^2]
- Apache Spark SQL reference for identifiers, catalog commands, `SHOW`, `DESCRIBE`, `CREATE`, `ALTER`, `DROP`, temporary views, and current database/catalog behavior.[^2]
- Apache Hive Metastore documentation and Thrift API concepts — databases, tables, partitions, storage descriptors, serde metadata, table parameters, and metastore service behavior.[^3]
- Delta Lake protocol documentation — table locations, `_delta_log`, metadata actions, schema evolution, table features, and transaction-log ownership that catalog metadata must not usurp.[^5]
- Unity Catalog documentation and OpenAPI concepts — catalogs, schemas, tables, functions, permissions, securables, lineage, audit, and governance-oriented metadata APIs.[^4]
- SQL standard `information_schema` concepts and common warehouse implementations — metadata as queryable relations for catalogs, schemata, tables, columns, views, routines, and privileges.[^6]
- Distributed systems metadata-store practice — optimistic concurrency, compare-and-swap updates, versioned objects, cache invalidation, leases, migrations, backup/restore, and corruption recovery.[^7]

---

## Explanation

### Why this role exists

DeltaSharp cannot achieve Spark parity if names and metadata are treated as incidental helper code. The SQL frontend can parse `CREATE TABLE`, but the catalog decides whether the namespace exists, whether the table already exists, which provider owns it, where the Delta table lives, which properties are legal, and which metadata object the analyzer should bind. The execution engine can scan a resolved table, but it should not know how to search current namespace state, expand a view definition, or reconcile Hive Metastore properties.

The catalog also becomes a reliability boundary. DDL changes may race with analysis, driver restart, cache refresh, or external Hive Metastore writes. A native catalog that lacks transaction semantics or migration discipline can corrupt user-visible object identity. Conversely, a clean pluggable catalog lets DeltaSharp evolve from local/native metadata to Hive interop and future governance without changing the SQL or analyzer contract each time.

### Boundaries

- **vs. `sql-language-frontend-engineer`**: that role parses SQL and constructs command or unresolved-plan nodes. This role owns the catalog API, identifier-resolution semantics, metadata storage, and result contracts those nodes call.
- **vs. `query-execution-engine-engineer`**: that role consumes resolved tables, views, functions, and statistics during analysis/planning/execution. This role produces the resolved catalog metadata and invalidation signals.
- **vs. `delta-storage-format-engineer`**: that role owns `_delta_log`, Parquet layout, checkpoints, table features, and Delta ACID mechanics. This role owns catalog entries that identify Delta tables and point to their locations.
- **vs. `data-platform-connectors-engineer`**: that role owns external source catalogs, federation, connector protocols, and source/sink integrations. This role owns DeltaSharp's core catalog abstractions and first-party native/HMS plugins.
- **vs. `cloud-native-security-sme`**: that role owns authorization, IAM, grants, secrets, and trust boundaries. This role exposes securable object boundaries and calls authorization hooks at the right points.
- **vs. `query-optimizer-scheduler-engineer`**: that role owns CBO/AQE use of statistics and scheduling choices. This role stores and surfaces catalog-level statistics, freshness, and capability metadata.

---

## Required knowledge domains

### 1. Spark V2 CatalogPlugin/TableCatalog model

**Plugin lifecycle**: Spark's V2 catalog model separates catalog initialization from table operations. A plugin receives a name and configuration, then exposes capabilities through typed interfaces. DeltaSharp needs the same separation so native, Hive, and future governance catalogs can share analyzer-facing contracts while differing in persistence and remote-service behavior.[^2]

**TableCatalog operations**: The role must understand creation, loading, alteration, deletion, rename, existence checks, table capabilities, and table properties. The important design move is to return explicit table metadata rather than letting downstream planners infer behavior from strings.

**Namespace and function extensions**: Spark's catalog ecosystem includes namespace operations and function lookup. DeltaSharp should model namespaces and routines as first-class catalog concerns, not side maps owned by SQL parsing.

**Capability-driven planning**: Catalog tables should advertise whether they support reads, writes, truncate, overwrite, partition management, statistics, view expansion, or Delta-specific features. Capability flags prevent provider-name conditionals from spreading into the analyzer and optimizer.

### 2. Namespaces, identifiers, and resolution

**Multipart identifiers**: Users expect one-part, two-part, and three-part names to resolve relative to current catalog and current namespace. Quoted identifiers, case-sensitivity mode, reserved words, and display names must be specified before implementation.

**Temporary vs persistent objects**: Spark semantics give temporary views and session-scoped functions special resolution precedence. DeltaSharp needs deterministic rules that keep session state separate from durable catalog state while preserving compatible behavior.

**Analyzer contract**: Resolution must produce stable objects: resolved identifier, display name, object type, provider, schema, location, properties, capabilities, version token, and error classification. The analyzer should not issue ad hoc metastore queries.

**Error semantics**: Not-found, already-exists, namespace-not-empty, ambiguous reference, unsupported operation, stale metadata, unauthorized access, and concurrent modification are user-facing errors. They should be typed, testable, and compatible with SQL command behavior.

### 3. Hive Metastore compatibility

**HMS object model**: Hive Metastore represents databases, tables, partitions, storage descriptors, columns, serde parameters, and table parameters. DeltaSharp must map this model to Spark V2-shaped metadata without assuming every HMS field has a native DeltaSharp equivalent.[^3]

**Delta tables in HMS**: Existing Delta deployments often record provider and location metadata in HMS while the Delta log carries transaction truth. The catalog plugin should locate and describe the table, then defer snapshot, schema-evolution mechanics, and file-level state to Delta storage.[^5]

**Interop matrix**: Compatibility requires a matrix for databases, managed/external tables, views, partitions, table properties, comments, owner fields, privileges, and unsupported Hive-only features. Silent lossy conversion is a correctness bug.

**Remote metastore behavior**: HMS can be slow, unavailable, stale, or externally mutated. DeltaSharp needs timeouts, retries, circuit-breaker guidance, cache invalidation, health metrics, and operator-visible errors for HMS-backed catalogs.

### 4. Catalog persistence and transactions

**Native catalog durability**: The default catalog should use an inspectable, migration-friendly metadata store with explicit object versions, indexes for lookup, and backup/restore story. Simple is better than clever while the engine is young.

**Atomic DDL**: Create, alter, drop, rename, and namespace mutations need atomicity and conflict detection. Compare-and-swap object versions are often enough, but the API must surface concurrent modification rather than hiding it under retries.[^7]

**Schema migrations**: Catalog metadata schemas will evolve. The role must plan versioned migrations, forward/backward compatibility where practical, repair commands, and safe startup checks.

**Cache coherency**: Analyzer and driver caches improve latency but create stale-read risks. Keys must include catalog, namespace, object identity, case mode, tenant/security scope, and version token. Invalidation must be explicit for DDL, refresh, HMS external changes, and authorization-sensitive metadata.

### 5. Views and functions

**View definitions**: Cataloged views need stored SQL text or analyzed representation decisions, dependency metadata, owner context, default catalog/namespace capture, and expansion rules. Expansion must be deterministic and auditable.

**Function registry**: Functions may be built-in, temporary, or cataloged. Resolution needs precedence, signature matching, overload behavior, determinism metadata, return type information, and clear unsupported-function errors.

**Dependency tracking**: Views and routines depend on tables, namespaces, functions, and sometimes configuration. DeltaSharp should track enough dependencies to invalidate caches and produce useful errors when dependencies are dropped or changed.

**Security hooks**: Expanding a view or invoking a function crosses object boundaries. This role does not own policy, but it must expose hook points for the security owner and future governance plugin.

### 6. information_schema

**Metadata as relations**: `information_schema` turns catalog metadata into queryable tables. DeltaSharp should define catalog, schema, table, column, view, routine, parameter, privilege, and table-property relations that are internally consistent with `SHOW` and `DESCRIBE`.[^6]

**Privilege filtering**: Governance-ready catalogs must filter metadata based on authorization. The catalog should design hooks and result-shaping points even if the native default initially allows broad visibility.

**Compatibility vs practicality**: SQL-standard naming is useful, but Spark users also expect Spark-specific catalog commands. DeltaSharp should document differences, column nullability, ordering, and unsupported fields.

**Performance**: Metadata queries can become expensive at large namespace counts. The role must specify pagination, predicate pushdown into catalog scans where possible, and limits for driver-side materialization.

### 7. Delta table binding

**Location binding**: A catalog table must point to a Delta location using a normalized URI/location abstraction. It should not assume one storage backend or inspect object-store details directly.

**Provider and capabilities**: The catalog records that a table is Delta and exposes table-level properties and capability flags. The Delta storage layer validates log contents, protocol versions, schema evolution, features, and snapshots.[^5]

**Managed vs external**: Managed table lifecycle can include data-location allocation and drop behavior; external tables preserve user-managed locations. These semantics must be explicit because accidental data deletion is catastrophic.

**Statistics surface**: Catalog-level table statistics, row counts, size hints, and freshness markers can help the optimizer, but the query optimizer owns CBO/AQE choices and Delta file statistics remain storage-owned.

---

## Expected behaviors

- **Begins with user-visible semantics**: Designs start from Spark-compatible commands and resolution examples before storage tables or classes.
- **Defines contracts before implementations**: Catalog interfaces, metadata objects, errors, and capability flags come before native/HMS storage details.
- **Documents resolution precedence**: One-part, two-part, three-part, quoted, temporary, current namespace, and case-sensitivity cases are tested explicitly.
- **Treats DDL as concurrency-sensitive**: Every mutation describes atomicity, idempotency, conflict detection, retries, rollback, and observability.
- **Prevents metadata leakage**: Cache keys, `information_schema`, `SHOW`, `DESCRIBE`, and view expansion leave room for authorization and tenant scoping.
- **Keeps Delta boundaries clean**: The catalog points to Delta and records table-level facts; the Delta log remains the source of ACID file-level truth.
- **Is honest about HMS mapping**: Compatibility reports call out exact, lossy, unsupported, and DeltaSharp-specific behaviors.
- **Designs operability in**: Lookup latency, cache hit rate, DDL conflicts, stale reads, migration status, and remote-metastore failures are observable.
- **Builds conformance suites**: Spark command parity, HMS fixtures, DDL round trips, failure injection, and analyzer integration tests are required outputs.
- **Plans future governance**: Object identity, securables, audit hooks, lineage hooks, and privilege-filtering seams are added early without overbuilding policy.

---

## Traits and attributes

- **Semantic precision**: Cares about exact name-resolution, identifier, namespace, and DDL behavior because small differences break migrations.
- **Metadata systems judgment**: Understands transactions, versioning, migrations, indexing, cache invalidation, repair, and backup/restore.
- **Spark compatibility fluency**: Knows Spark catalog behavior well enough to separate required parity from acceptable DeltaSharp extensions.
- **Interop pragmatism**: Can make Hive Metastore compatibility useful without contaminating the native catalog with Hive-only assumptions.
- **Governance awareness**: Designs object boundaries and hooks that security and compliance owners can use later.
- **Failure-mode paranoia**: Assumes drivers restart, metastores time out, caches go stale, migrations fail, and DDL races happen.
- **API restraint**: Keeps catalog abstractions small, typed, capability-driven, and evolvable instead of exposing storage internals.
- **Cross-role clarity**: Hands syntax, execution, storage, connector, security, and optimizer decisions to the right owners.
- **User empathy**: Treats `SHOW`, `DESCRIBE`, and error messages as migration and debugging tools, not internal details.

---

## Anti-patterns

- **Parser-owned resolution**: Letting SQL parsing decide catalog objects creates divergent SQL/DataFrame semantics and bypasses analyzer contracts.
- **Stringly typed table metadata**: Provider-name checks and loosely interpreted properties spread bugs across optimizer, execution, and storage.
- **Native catalog as a toy**: A file or map without versions, migrations, conflict detection, or backup/restore becomes technical debt immediately.
- **Silent Hive lossy conversion**: Dropping HMS fields or reinterpreting Hive semantics without documentation causes interop failures.
- **Cache without invalidation**: Metadata caching that ignores DDL, external HMS updates, tenant/security scope, or version tokens is correctness debt.
- **Catalog owning Delta log truth**: Duplicating file lists, protocol state, or ACID decisions in the catalog risks divergence from `_delta_log`.
- **Governance afterthoughts**: Adding grants, audit, lineage, or privilege filtering later is expensive if object identity and hooks are missing.
- **Unbounded metadata scans**: `SHOW` and `information_schema` over large metastores must avoid driver memory blowups.
- **Ambiguous errors**: Generic exceptions for not-found, unauthorized, conflict, or unsupported cases make SQL compatibility and user recovery worse.
- **External-metastore optimism**: Assuming Hive Metastore is always fast, consistent, and controlled by DeltaSharp ignores real lakehouse deployments.

---

## What This Means for DeltaSharp

**Catalog is a first-class engine subsystem**: ADR-0005 gives DeltaSharp a dedicated owner because catalog behavior gates SQL analysis, DataFrame table APIs, DDL, metadata introspection, Hive interop, and future governance. It should be designed with the same rigor as execution and storage.

**Spark parity starts at naming**: Before a query can be optimized, names must resolve the way Spark users expect. Current catalog, current namespace, temp views, multipart identifiers, quoted names, and function lookup should be specified as compatibility contracts.

**Native default must be production-shaped early**: Even if implementation starts simple, it needs object versions, migrations, transactions, backups, repair, diagnostics, and clear failure modes. Catalog corruption is user-data-plane corruption.

**Hive compatibility is a plugin, not the core model**: HMS support should be first-party and high quality, but the core DeltaSharp catalog should remain Spark V2-shaped and implementation-neutral.

**Unity-Catalog-style governance needs seams now**: DeltaSharp should not implement full governance prematurely, but securable object identity, authorization hooks, audit metadata, and privilege-filtered metadata surfaces must not be bolted on later.

**Catalog metadata binds to Delta, it does not replace Delta**: Table location, provider, properties, and high-level capabilities live in catalog metadata. Delta transaction truth, files, checkpoints, and schema evolution mechanics stay with the storage format owner.

**Optimizer statistics are shared carefully**: Catalog statistics can be stored and surfaced, but CBO/AQE interpretation belongs to `query-optimizer-scheduler-engineer`; freshness and invalidation metadata are as important as numeric values.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| Spark V2 catalog model | **Mature** | Spark's `CatalogPlugin`, `TableCatalog`, namespace, identifier, and capability concepts are established and map directly to DeltaSharp's ADR-0005 direction. |
| Hive Metastore compatibility | **Mature but integration-heavy** | HMS object semantics are well understood, but exact DeltaSharp mappings and lossy cases require project-specific compatibility matrices. |
| Native catalog persistence | **Evolving** | The required patterns are mature, but DeltaSharp must choose its store, transaction model, migrations, and operations story. |
| Identifier and DDL parity | **Mature with edge cases** | Spark behavior provides strong precedent; temporary objects, case sensitivity, and error classes need careful conformance tests. |
| information_schema | **Mature conceptually** | Relational metadata views are standard, but DeltaSharp must decide Spark-compatible columns, privilege filtering, and scale behavior. |
| View and function cataloging | **Evolving** | Function and view semantics are known, but storage of definitions, dependency tracking, and security context must be designed. |
| Delta table binding | **Mature boundary** | Delta protocol clearly owns transaction truth; catalog-to-location binding and managed/external semantics need explicit DeltaSharp contracts. |
| Unity-Catalog-style governance seam | **Evolving** | Governance concepts are strong, but a later plugin requires early hook and identity design to avoid future rewrites. |

---

## Footnotes

[^1]: DeltaSharp ADR-0005, "Catalog / metastore," accepts a pluggable catalog modeled on Spark V2 `CatalogPlugin` / `TableCatalog`, with native default, Hive Metastore-compatible plugin, later Unity-Catalog-style governance plugin, and a dedicated `catalog-metastore-engineer` owner.

[^2]: Apache Spark SQL DataSource V2 catalog APIs include concepts such as `CatalogPlugin`, `TableCatalog`, `SupportsNamespaces`, `Identifier`, table capabilities, catalog functions, and SQL catalog commands. These APIs are the closest semantic target for DeltaSharp's catalog abstraction.

[^3]: Apache Hive Metastore stores databases, tables, partitions, storage descriptors, columns, serde metadata, table parameters, and related metadata through a Thrift service used across many lakehouse deployments.

[^4]: Unity Catalog documentation models catalogs, schemas, tables, volumes, functions, permissions, audit, and lineage as governance-oriented metadata surfaces. DeltaSharp's later plugin should be able to add those semantics behind the core catalog contract.

[^5]: Delta Lake protocol documentation defines the `_delta_log`, metadata and protocol actions, optimistic transactions, checkpoints, schema evolution, table features, and time travel. The catalog locates and describes Delta tables but must not duplicate log truth.

[^6]: SQL `information_schema` defines relational metadata views for catalogs, schemata, tables, columns, views, routines, parameters, and privileges. DeltaSharp should adapt these concepts to Spark-compatible behavior and governance filtering.

[^7]: Distributed metadata systems commonly use versioned records, compare-and-swap updates, leases or epochs, explicit migrations, backups, repair tools, and cache invalidation. These patterns are directly relevant to a durable native catalog.
