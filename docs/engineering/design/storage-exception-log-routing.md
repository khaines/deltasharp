# Storage exception log routing: the `.Message`-only sink contract

> **Status:** living document (operator guidance). Created with
> [#689](https://github.com/khaines/deltasharp/issues/689) <!-- issue-state:closed --> as the operator-facing counterpart to the
> per-type XML documentation added by [#688](https://github.com/khaines/deltasharp/pull/688)
> ([#664](https://github.com/khaines/deltasharp/issues/664) <!-- issue-state:closed -->); substantially corrected by
> [#694](https://github.com/khaines/deltasharp/pull/694) — see
> [What #694 corrected](#what-694-corrected-one-defect-class-six-symptoms). Grounded in the
> message-hygiene work of #648/#651/#653/#667 and in `DiagnosticText.DescribeWithoutInner`
> (`src/DeltaSharp.Storage/Delta/DiagnosticText.cs`). Reviewed against checklists
> [05](../checklists/05-security-checklist.md),
> [09a](../checklists/09a-logging-checklist.md),
> [09c](../checklists/09c-distributed-tracing-checklist.md),
> [11](../checklists/11-documentation-support-checklist.md), and
> [14](../checklists/14-tenant-isolation-checklist.md). **Checklist [07](../checklists/07-privacy-checklist.md) is satisfied for the guidance this
> page provides**, with host-instantiation obligations delineated: the server-side-only diagnostic sink it
> recommends is the deploying host's to instantiate and operate, so this page's 07 obligation is to
> *define, require, name owners for, and ratify defaults for* the retention, audited access, ownership,
> collection, review, failure-signal, residency, and erasure governance below — which it now does. The
> `privacy-compliance-grc-lead` sign-off ratifying those default recommendations landed in #744; the host —
> not DeltaSharp — is the accountable party for the sink it deploys (controller or processor per the Owner
> row). See
> [What a storage `.Message` still retains](#what-a-storage-message-still-retains). Read with
> [observability-conventions.md](observability-conventions.md) (the repo-wide logging, metrics, tracing,
> and redaction rules) and [storage-delta-architecture.md](storage-delta-architecture.md) (the storage
> layer these exceptions come from). Update it whenever a storage exception type gains or loses a
> `ToString()` override, gains or loses a typed property, starts chaining an inner exception, or is
> marshalled across an executor/driver or control-plane status boundary. The machine-marked tables on this
> page are **parsed by a compiled test**, including every raw property path in the reflection-state table,
> and will fail the build if they drift.

This page is for the engineer wiring **log routing** for a host that embeds DeltaSharp's storage layer:
which exception state is safe to send to a tenant-visible sink, which is server-side-diagnostic only, and
which common .NET logging patterns quietly violate that split. It documents a **consumer-side
obligation** — the library cannot enforce it inside your log pipeline, so it is written down here.

## What #694 corrected: one defect class, six symptoms

The first version of this page was reviewed by seven seats and failed six ways. Every one of the six was
the same defect: **a proxy standing in for the property that actually matters.** Fixing them as six
independent edits would have left the class intact, so they are corrected here as one idea, and named so
the next reader can spot the seventh instance.

| The proxy that was used | The property that actually matters |
| --- | --- |
| A type's **constructor shape** (does it take an `Exception`?) | Does it retain **reflection-reachable unsanitized state** = `InnerException` ∪ typed properties |
| Does the sink **render or reflect**? | Does the sink **walk `.InnerException` itself** |
| Is a `ToString()` override **declared**, is a name **listed**? | Does the override **actually omit the inner** |
| Is the exception **constructed** in a test? | Is it **thrown** — the only shape that reaches production |
| Does the **test's hand-list** agree with itself? | Does the **doc table the claim names** agree with the code |
| Is `.Message` **sanitized**? | What does `.Message` **itself still retain** |

Each row below is written against the right-hand column. Where a claim is now executed by a compiled
test, the test is named inline.

## The contract

> **Contract.** Treat every storage `.Message` as untrusted tenant data. Fixed literals and tokens actually
> routed through `DiagnosticText.Sanitize` have the posture described below; the message-posture table
> below lists the **verified producers for call sites known as of `76d2c8e`; coverage is a manually maintained door list, not an automated inventory**,
> enforced by `StorageHygieneSweepTests` (a mutation turning a `Sanitize` call into an identity turns a
> sweep-test case red). Eight of the nine `DeltaStorageException` factories accept a fully-composed
> message and carry the hygiene obligation in the type-level XML `<remarks>` (only
> `ColumnNotPresentInFile` sanitizes internally at the factory); `DeltaProtocolException` carries the
> same obligation. The six `ToString()`-covered exception types can retain a **raw, unsanitized
> `.InnerException`**, and several storage exceptions retain **raw, unsanitized typed properties**. A
> tenant-visible sink MUST render `.Message` or `.ToString()`, and MUST NOT walk `.InnerException` or
> reflect over the exception object graph.
>
> **`Sanitized` means record-forgery-safe for tokens actually passed through `DiagnosticText.Sanitize`
> (line-break **and** `Cf` text-confusion classes — bidi/zero-width/TAG — neutralized to `U+FFFD`) and free
> of raw decoder text.** It does not mean safe from every text-spoofing class (homoglyph/confusable
> spoofing is deliberately **not** covered), and it does not mean personal-data-free. A sanitized `.Message` can still carry a caller-relative
> object path (including Hive partition `key=value` segments, which *are* column values) and a requested
> column name. Route it accordingly — see
> [What a storage `.Message` still retains](#what-a-storage-message-still-retains) and
> [Known gaps](#known-gaps-in-the-sanitizer-itself).

## Exception/diagnostic message culture convention (#764)

*This convention applies at the formatting layer; it is orthogonal to the sanitization and routing
obligations described above.*

Exception and diagnostic message formatting follows a split rule:

- **Bare interpolation (`$"..."`) is acceptable** for unsigned integral types (`byte`, `ushort`,
  `uint`, `ulong`, `nuint`) and round-trip `:O` timestamps — values whose rendered form is
  culture-invariant by type. Bare interpolation of signed `int`/`long` is also acceptable when the
  value is demonstrably non-negative at the call site — for example, bounded by a preceding
  `ArgumentOutOfRangeException.ThrowIfNegative` or similar guard, derived from an unsigned source and
  known to fit the signed range, or constrained by a validated constructor parameter. Note: on
  locales such as `ar-SA`, the sign-rendering path prepends a bidi-control character for *negative*
  values — not positive ones — so a verified non-negative call site carries no culture risk.
- **`string.Create(CultureInfo.InvariantCulture, ...)` is required** when interpolation is genuinely
  culture-sensitive — decimals/floats, or any value whose `ToString()` is locale-influenced through
  numeric separators or alternate digit forms. **Prefer** it for signed integers that do not satisfy
  any of the conditions above — i.e., where the non-negative guarantee is neither type-enforced nor
  explicitly guarded. Signed-integer *sign-mark* divergence (the `ar-SA` bidi-control prefix above) is
  deliberately placed in this lower **prefer** tier rather than **required**: it affects only the
  leading sign of already-negative values, a low-risk rendering difference, so it warrants a default
  toward invariant formatting without mandating churn at every unguarded signed-integer call site.

The goal is correctness and consistency, not blanket churn: this repository's dominant message style is
bare interpolation, so converting isolated safe call sites to invariant formatting is noise without
correctness benefit. Apply invariant formatting where culture can change behaviour; keep unsigned
integer and timestamp diagnostics simple.

**Analyzer policy (CA1305):** CA1305 (*SpecifyIFormatProvider*) is not enabled globally in this
repository, and it would not enforce this convention if it were. Modern C# lowers bare interpolation
(`$"..."`) to `DefaultInterpolatedStringHandler`, which CA1305 does **not** flag — verified on this
repo's toolchain (`net10.0`, `AnalysisLevel=latest`): with CA1305 raised to a warning, bare `int`,
`uint`, `long`, and `double` interpolations produce zero CA1305 diagnostics, while explicit
`int.ToString()` and `string.Format(...)` calls without an `IFormatProvider` are flagged. CA1305 is
therefore blind to exactly the interpolation call sites this convention governs, and fires only on the
comparatively rare explicit `ToString()`/`string.Format()` paths. Because the analyzer can neither
catch the cases we care about nor distinguish a guarded non-negative site from a hazardous one, this
convention is upheld by the split rule above and in code review rather than by a global CA1305 gate;
apply invariant formatting selectively on genuinely culture-sensitive paths.

## Why the contract exists: the surfaced-message / raw-state split

The storage decode and validation boundaries fail **closed** on malformed, foreign, or hostile input: a
corrupt Parquet footer, a crafted `_delta_log` commit line, a data file whose schema is narrower than the
table's. Each such failure has two audiences with opposite needs:

- The **caller / tenant** needs a message that says what went wrong without echoing the untrusted bytes
  that caused it. Message hygiene (#648/#651/#653/#667) therefore scrubs attacker-influenceable content —
  raw Parquet.Net text, data-file paths, foreign schema field names — out of the covered surfaced
  `Exception.Message` paths, and neutralizes log-injection line-break characters (C0/C1 controls plus
  `U+2028`/`U+2029`) through `DiagnosticText.Sanitize`. The reviewed raw examples and the non-exhaustive
  producer-audit boundary are scoped explicitly above and in
  [Known gaps](#known-gaps-in-the-sanitizer-itself), rather than being hidden by a blanket claim.
- The **operator debugging the node** needs the raw cause. So the raw framework exception is **retained**
  as `Exception.InnerException` rather than discarded, and some raw tokens are **retained on typed
  properties** so a caller can opt in and redact at its own sink.

One object, two trust levels. That works only if the sink respects the split.

> **A correction worth making in the safe direction.** Earlier revisions of this page listed "JSON parse
> fragments over crafted bytes" among the things the inner retains. That **overstates** it.
> `System.Text.Json`'s `JsonException.Message` echoes at most a **single offending character plus a byte
> offset** — measured against .NET 10: `'S' is an invalid start of a value. LineNumber: 0 |
> BytePositionInLine: 6.` — never a value or a fragment. The raw inner from a **Parquet.Net** decode is
> the value-bearing case; the JSON one is a position oracle, which is a real but much smaller signal. A
> page that overstates danger gets disbelieved, and then its true warnings are ignored too.

The obvious way to break the split is `Exception.ToString()`, whose default implementation appends
`" ---> " + innerException`, re-rendering the raw inner. #664/#688 closed that path: the covered types
override `ToString()` with `DiagnosticText.DescribeWithoutInner`, which renders
`{FullTypeName}: {Message}` (plus ` (Kind: {kind})` where the type carries a `Kind`) followed by the
exception's **own** stack trace, and **omits the `InnerException` chain entirely**. The type name is
namespace-qualified, matching the .NET default (`DescribeWithoutInner` uses `GetType().ToString()`).

Suppression is transitive, because `Exception.ToString()` recurses into an inner through the inner's own
virtual `ToString()`. A covered exception wrapped in a plain outer exception, or nested inside an
`AggregateException` (including after `Flatten()`), still suppresses its raw inner. This is locked by
`StorageExceptionToStringTests.CoveredException_WrappedInOuterOrAggregate_ToString_TransitivelyOmitsRawInner`
(`tests/DeltaSharp.Storage.Tests/StorageExceptionToStringTests.cs`).

**Transitivity is the whole mechanism, and it is also the whole limit.** It works because the renderer
delegates the recursion to `Exception.ToString()`. A sink that does its own `.InnerException` walk never
enters that recursion, so the override never gets the chance to cut it — which is the single fact behind
[the sink decision procedure](#the-decision-procedure-does-the-sink-walk-innerexception-itself) below.

For contrast, `LocalFileSystemBackend.SurfaceFailure`
(`src/DeltaSharp.Storage/Backends/LocalFileSystemBackend.cs`) applies the **stronger** RF-8b treatment:
it refuses to chain the raw path-bearing framework exception at all and attaches a *synthetic*
`IOException` instead, whose message is `{FrameworkExceptionTypeName}: {root-redacted message}`. That
inner is **root-redacted even under reflection** — a reflecting sink still sees the framework message,
but with the absolute mount/warehouse root replaced by `<table-root>`, so the internal layout never
discloses. The decode boundaries deliberately keep the raw, un-redacted inner, which is why they need
this contract.

## What a reflecting sink can reach

There are **two** kinds of retained raw state, and a sink reaches both by the same mechanism. Keying the
inventory on "does this type chain an inner" — the constructor-shape proxy — is what made the first
version of this page clear three types that leak.

### 1. Types that retain a raw `InnerException` (and therefore override `ToString()`)

<!-- BEGIN:covered-types — parsed by StorageExceptionToStringTests.LogRoutingDoc_CoveredAndRawStateTables_MatchTheCompiledInventories.
     The first cell of each data row must be the backticked type name; keep this table and CoveredTypeNames in sync. -->

| Type (namespace `DeltaSharp.Storage[.Delta]`) | Visibility | `ToString()` renders | Raw content on `.InnerException` |
| --- | --- | --- | --- |
| `DeltaStorageException` | `internal sealed` | message + `(Kind: {StorageErrorKind})` | Raw Parquet.Net / framework exception from a decode or I/O failure |
| `DeltaProtocolException` | `internal sealed` | message + `(Kind: {DeltaProtocolErrorKind})` | Raw JSON parse error over crafted commit/checkpoint bytes (a character + byte offset, not values) |
| `DeltaCommitUnknownStateException` | `internal sealed` | message | Raw framework/IO exception behind an unresolvable commit outcome |
| `OptimizeSchemaEvolutionException` | `internal sealed` | message | Originating `DeltaStorageException`, down to its own raw inner |
| `DeltaReadException` | `public sealed` | message | The whole internal chain, down to the raw decode exception |
| `DeltaReadSchemaEvolutionException` | `public sealed` | message | Originating storage exception, down to its own raw inner |

<!-- END:covered-types -->

Every row renders as `{FullTypeName}: {Message}` (plus the `Kind` suffix where shown) followed by the
exception's own stack trace, and nothing from the `InnerException` chain. All six are `sealed`, so the
inherited-override hazard (a subclass silently reverting to `Exception.ToString()`) is structurally
impossible.

**Four of the six are `internal`**, so their per-type XML documentation is invisible from outside the
assembly. `DeltaSharp.Storage` is also non-packable today (`IsPackable=false`), so an operator reaches
these types only through a host that references the assembly in-repo. That is the whole reason this page
exists rather than only the XML docs. (`IsPackable=false` is asserted by
`StorageExceptionToStringTests.StorageAssembly_IsNonPackable_AndReviewedProviderFamiliesAreAbsent`.)

### 2. Every type that retains reflection-reachable unsanitized state

This is the table that matters for a **reflecting** sink, and it is a **superset** of the one above: a
type with no inner at all can still hand a destructurer raw attacker text on a public typed property.
"Reachable" is `InnerException` ∪ the typed properties, at the accessibility shown. Public instance
fields are also public-member reflection state, so the compiled guard forbids storage exception types
from declaring them rather than letting a field bypass this inventory.

<!-- BEGIN:reflection-reachable-state — parsed by StorageExceptionToStringTests.LogRoutingDoc_CoveredAndRawStateTables_MatchTheCompiledInventories.
     The first cell of each data row must be the backticked type name. Every backticked path in the second
     cell is compared with the constructor-probed reflection inventory; keep both the type and path exact. -->

| Type | Raw state a reflecting sink reaches | Reachable at | In `.Message`? |
| --- | --- | --- | --- |
| `DeltaCommitUnknownStateException` | `.InnerException` | public | no |
| `DeltaConstraintDependentColumnException` | `.ColumnName`, `.Constraints[].Name`, `.Constraints[].Expression` | **public** | sanitized + list-capped copy |
| `DeltaConstraintViolationException` | `.Constraint.Name`, `.Constraint.Expression` | **public** | sanitized copy |
| `DeltaProtocolException` | `.InnerException` | public | no |
| `DeltaReadException` | `.InnerException` (the whole chain) | public | no |
| `DeltaReadSchemaEvolutionException` | `.InnerException`, `.FilePath` | **public** | no — path deliberately dropped |
| `DeltaSchemaMismatchException` | `.Path` | public (type is `internal`) | sanitized copy |
| `DeltaStorageException` | `.InnerException`, `.Path` | **public** | no — path is redacted by path-bearing producers |
| `OptimizeSchemaEvolutionException` | `.InnerException`, `.FilePath` | `.FilePath` is `internal` | no — path deliberately dropped |

<!-- END:reflection-reachable-state -->

Read the last two columns together. "Sanitized copy" means the message carries a **length-capped,
control-character-neutralized** rendering of the same token while the property keeps the original: a
destructurer therefore re-surfaces the CR/LF and `U+2028` that `Sanitize` removed, and removes the length
cap. Measured through a real `Serilog` `{@Ex}` destructurer against the shipped assembly, with a poison
token of `LEAK<CR><LF>INJECTED<U+2028>` + 100 filler characters:

| Type | `.Message` length | Destructured length | CR in `.Message` | CR in destructured output |
| --- | --- | --- | --- | --- |
| `DeltaConstraintViolationException` | 362 | 940 | no | **yes** |
| `DeltaConstraintDependentColumnException` | 868 † | 1490 † | no | **yes** |
| `DeltaSchemaMismatchException` | 261 | 671 | no | **yes** |

In all three the raw value lands in the **first key** of the emitted JSON object
(`"Ex":{"Constraint":{"Kind":"Check","Name":"LEAK\r\nINJECTED …`), and `InnerException` is `null` — which
is exactly why the constructor-shape proxy cleared them.

> † `DeltaConstraintDependentColumnException` was re-verified in-tree on 2026-08-02: #696 (`d314baa`) raised
> this producer's column-name cap from `ConfigTokenMaxLength` to `DefaultMaxLength`
> (`DeltaConstraintDependentColumnException.cs:106`), and the column is echoed twice, so the `.Message`
> length is now **868** (was 768 under the 2026-07-29 measurement). The **destructured** length was measured
> by the external Serilog harness on 2026-07-29 (not re-run here — those sink packages are not repo
> dependencies; see [§Verification](#verification)) and is therefore ~100 characters low for this row
> pending a re-measurement. The CR-in-output and first-key-leak conclusions are unchanged.

Every remaining storage exception type declares **only value-typed** properties (for example an enum
`Kind`, a `long` `Version`, or a `TimeSpan` retention) and chains no inner, so a destructurer reaches no
attacker-authored text on them. The exact population is classified in compiled code rather than repeated
as an unparsed prose list: `EveryStorageExceptionType_IsClassifiedAsCoveredOrInnerFree` requires every
new exception type to enter one of the reviewed buckets. The member shape is derived structurally and
asserted by
`StorageExceptionToStringTests.ReflectionReachableExceptionState_IsPinned_AndDerivesTheDocumentedRawStateSet`,
which also pins the full declared-property inventory so **adding a reference-typed property to any storage
exception type fails the build until it is classified in the table above**. The test constructs every
type with distinct probe values, recursively reads every declared reference-typed property, and compares
the exact observed paths with both the structural property inventory and the backticked paths in the
table; a type-level membership match cannot hide a missing property.

## What a storage `.Message` still retains

`Sanitized` is a **record-forgery and raw-decoder-text** property, not a personal-data property. This page
previously let the two be read as the same thing. The table below is a behavior-pinned set of
**verified producers for call sites known as of `76d2c8e`; coverage is a manually maintained door list, not an automated inventory**,
enforced by `StorageHygieneSweepTests` for LocalFileSystemBackend and ColumnNotPresentInFile producers,
`ParquetMessageHygieneTests` and `ParquetCorruptionTests` for Parquet `columnLabel`/`columnName` producers
(a mutation on any listed sanitizer turns the respective suite red; #749).
Every row surfaces through `.Message`, and therefore through every sink row this page marks Safe, including
`Activity.AddException`'s `exception.message` tag.

<!-- BEGIN:message-posture — parsed by StorageExceptionToStringTests.LogRoutingDoc_CoveredAndRawStateTables_MatchTheCompiledInventories.
     The first two cells are exact machine-readable identifiers and postures. -->

| Producer token | Posture | What `.Message` carries | Privacy classification |
| --- | --- | --- | --- |
| `LocalFileSystemBackend.SurfaceFailure.path` | `redacted` | Path shape through `DiagnosticText.DescribePath`: file/directory name plus partition column names, with Hive partition values dropped | Partition column names and file names can still be personal-data metadata under checklist 07 |
| `LocalFileSystemBackend.SurfaceFailure.detail` | `sanitized` | Root-redacted framework detail routed through `DiagnosticText.Sanitize` | Framework messages can repeat personal-data-bearing text outside the path renderer |
| `LocalFileSystemBackend.OpenReadAsync.missingPath` | `redacted` | Path shape through `DiagnosticText.DescribePath`: file/directory name plus partition column names, with Hive partition values dropped | Partition column names and file names can still be personal-data metadata under checklist 07 |
| `DeltaStorageException.ColumnNotPresentInFile.columnName` | `sanitized` | Requested column name through `DiagnosticText.Sanitize`, for example `hiv_status` | Column names can be personal-data metadata under checklist 07 |

<!-- END:message-posture -->

The hygiene posture differs by site, so do not generalize from one example. `MapWalkError`,
`OpenConfinedRead`, and `SurfaceFailure` route displayed paths through `DiagnosticText.DescribePath`,
which drops Hive partition values and keeps only the file/directory shape plus partition column names;
`SurfaceFailure`'s root-redacted framework detail is also line-break-sanitized.
`ColumnNotPresentInFile` routes the displayed column name through `DiagnosticText.Sanitize`, so line-break
and format controls are neutralized there too. Backend write/publish and Parquet schema producers known as of `76d2c8e` are covered by the sweep or
the Parquet-specific suites; new producers remain a reviewer obligation tracked by
[#749](https://github.com/khaines/deltasharp/issues/749) <!-- issue-state:open -->. None of these controls
removes the tenant's own data from a relative path or column name.

**Consequences for routing:**

- The DON'T at [Don't leak by construction](#dont) — "don't attach a raw storage path to a span or
  structured log" — and the MUST "render `.Message`" are in tension **by design**, because `.Message`
  *contains* a relative storage path on those paths. Resolve it the way the rest of this page resolves
  tenancy: `.Message` is safe to return **to the tenant that owns the table**, and is *not* automatically
  safe to broadcast to a shared, cross-tenant, or long-retention sink.
- If your sink is shared across tenants, or is retained beyond your table-data retention, treat
  `.Message` as tenant data: scope it, retain it no longer than the data it describes, and include it in
  erasure scope.
- If the sink is hosted or third-party, the provider is your **processor or sub-processor** depending on
  your own posture (see the **Owner** row below): its region, residency, support access, and transfer
  basis are part of the routing decision.
- The library-side minimum-disclosure improvement drops partition values from surfaced paths that use
  `DiagnosticText.DescribePath`; producers not yet routed through it remain a routing obligation rather
  than a library guarantee.

### If you route the raw inner to a server-side-only sink

The DO list below recommends a server-side-only diagnostics sink for `ex.InnerException?.Message`. That
sink is **yours**, not DeltaSharp's — DeltaSharp is a library and cannot instantiate or operate it.
Checklist 07's obligation on *this page* is therefore to define, require, name owners for, and ratify
defaults for the governance below; the third column gives the ratified default for each. These defaults
are **obligation floors** — the strictness you must meet or exceed — **not retention floors**: for
duration, shorter is always compliant, up to and including not retaining at all.

| Required | What to define | Ratified default (an obligation floor — meet or exceed; for duration, shorter is always compliant) |
| --- | --- | --- |
| **Retention period** | How long the raw inner text is kept | **Maximum 30 days, and never longer than the described table's own retention (whichever is shorter). No minimum — shorter, or not retaining, is always compliant.** Defensible only because the payload is *unsanitized*: it can carry absolute/caller-relative object paths, **undropped Hive partition values** (partition = column values = table data/PII), and value-bearing decoder text — none of it minimized. **Legal hold** overrides to block deletion as a documented, owned privacy exception carrying **basis, owner, expiry, compensating controls, and tenant/customer-communication requirements**. |
| **Access scope** | Who can read it and how access is audited | Named on-call/operations role only; never a tenant, never a shared/cross-tenant dashboard; every read logged (who, when, table id + time window). **07 gate:** the access path must first pass **checklist 05** (authenticated non-shared identity, least-privilege role binding, short-lived/workload credentials over static sink tokens, deny-by-default) and **checklist 14** (query surface cannot return another tenant's records) before you may call your sink compliant — this page's 05/14 review covers its routing *guidance*, not your sink. The **read-audit log itself** must be append-only/tamper-evident, stored where the reading role cannot delete or mutate it, **owned by someone other than the audited role**, retained ≥ the sink's retention, and in review-cadence + legal-hold scope. |
| **Owner** | Who is accountable, and the controller/processor posture | A named operations owner accountable for the sink and its reviews. **Determine the posture before executing the DPA:** the host is a **controller** for a diagnostics sink whose purpose and means it determines (even while it is a *processor* for the tenant's table data), and a hosted provider is then its **processor**; where the host runs the sink strictly on a customer's documented instructions it is a **processor** and the provider is a **sub-processor**. In-repo, `cloud-native-site-reliability-engineer` owns operational guidance and `privacy-compliance-grc-lead` owns data-classification. |
| **Collection path** | How records reach the sink and stay attributable | A documented, bounded route with **no ambient fan-out**. Attribution keys on every record: **table id + Delta version + timestamp**. Separately maintain an **owned, documented table-id → tenant + storage-location/region mapping**, retained ≥ the sink's retention **plus your breach-notification window** — table id alone resolves neither tenant nor region, both of which 07 breach-triage and residency require. |
| **Review cadence** | How often retention, access, residency, need are reassessed | At least annually, **and on-event** after any sink/provider, region/residency, or data-classification change (including a new producer entering the message-posture door list, or a `ToString()`/typed-property change). |
| **Failure signal** | How governance failures surface | An owned, **non-silent** alert / compliance-control failure across **five** classes: (1) collection, (2) retention/expiry, (3) access-audit-logging, (4) erasure failures → **operations owner**; and (5) **unauthorized/anomalous read or exposure** — a denied cross-tenant read, the sink surfaced on a shared or tenant-visible dashboard, or a routing change feeding the raw inner to a chain-walking provider → **security alert to the security/incident owner**, not only ops. |
| **Erasure path** | How a subject-erasure request reaches this sink | Table id + time window yields a **candidate set, not a subject** — the payload is unsanitized free text, so table+time resolves a *window*, not a person. The host either **deletes the whole candidate window (the defensible default)** or content-matches within it, and **must record which**. **Subject-level enumeration is out of scope** — it runs via the source table. Co-erasure scope with the source row: `_delta_log`, time-travel versions, backups, derived tables, **caches, and object-store versions**. Emit an **erasure completion/verification record**: tables, version range, time window, count of records deleted, verification result, and exceptions — carrying **no new PII**. |
| **Breach triage** | How affected parties are resolved on the clock | Use the **table-id → tenant + storage-location/region mapping** (Collection path) to resolve affected **tenants, storage locations, and regions** within the regulatory clock. Subject-level identification is out of scope for the sink and runs via the source table. |
| **Hosted / third-party sink** | Processor / sub-processor, residency, transfer basis | Treat the provider as a **processor / sub-processor** (per the Owner determination) receiving **unsanitized** tenant personal data. Before use, pin and bind: sub-processor status disclosed; the primary region; **and its cross-region DR/replication regions, lifecycle-tiering regions, and the provider's own sub-processors (fourth parties)** — a region pin that does not bind backup and DR is not a residency commitment. Document the transfer basis where data crosses borders: **DPA executed, SCCs in place, TIA completed, support-access geography reviewed.** |

`privacy-compliance-grc-lead` has ratified these default recommendations (#744). With them, **this page
satisfies checklist 07 for the guidance it provides** — you still own instantiating and operating a sink
that meets them, and, per the Access row, your sink is compliant only once its access path passes
checklists 05 and 14.

## The residual: reflection over the exception graph

`ToString()` is a rendering hook. It cannot constrain a sink that never calls it. A logger that
**serializes the exception object graph by reflection** walks the public `InnerException` property, and
the public typed properties, directly — the `ToString()` override is simply not on that code path.

This residual is **by design, not an oversight**: the raw inner is the server-side diagnostic the storage
layer intentionally preserves, and the raw typed properties exist so a caller can redact at its own sink.
Removing them would close the reflection path at the cost of the debugging signal — the trade-off #664
raised (its option 1: attach a synthetic sanitized inner instead) and #688 resolved in favor of keeping
the raw cause. What remains is a **sink-side encode-on-write obligation**, which is what the rest of this
page specifies.

## Sink rules

### The decision procedure: does the sink walk `.InnerException` itself?

> **Read this before the matrix.** The earlier version of this page told you to classify a sink as
> **rendering** (safe) or **reflecting** (leaks). **That procedure is false**, and it is false in the
> direction that gets people hurt: it clears sinks that leak.
>
> The suppression works only because `Exception.ToString()` **recurses into the inner via the inner's own
> virtual `ToString()`**, and the override cuts that recursion. So the question is not whether the sink
> renders — it is **whether the sink delegates the chain walk to `Exception.ToString()` or performs its
> own**. A sink that renders *each chain level it found itself* is a rendering sink that leaks.
>
> **NLog is exactly that sink.** With `${exception:maxInnerExceptionLevel=5}` it **calls the DeltaSharp
> override** — the render below contains `(Kind: CorruptData)`, and `grep -rn "Kind: "
> src/DeltaSharp.Storage/` has exactly one producer, `DiagnosticText.cs` — and leaks anyway, because NLog
> enumerated the chain before rendering it:
>
> ```text
> DeltaSharp.Storage.DeltaReadException: The table could not be read.
>    at …
> DeltaSharp.Storage.DeltaStorageException: Parquet footer is malformed. (Kind: CorruptData)
> System.InvalidOperationException: RAW-INNER-LEAK-parquet-footer-0xDEADBEEF
> ```
>
> Classify a sink by asking, in order:
>
> 1. **Does it render only `.Message`?** → safe from the inner; still carries what
>    [`.Message` retains](#what-a-storage-message-still-retains).
> 2. **Does it call `ex.ToString()` once and print the result?** → safe. The override cuts the recursion.
> 3. **Does it enumerate `.InnerException` itself** — a `maxInnerExceptionLevel`, an `ExceptionDetails`
>    list, a `while (e != null)` loop? → **leaks**, no matter how it renders each level.
> 4. **Does it reflect over the object graph?** → **leaks**, and reaches the raw typed properties too, not
>    just the inner.
>
> For `ILogger`, **the provider and its layout decide, not the `LogError(ex, …)` call.** The same call is
> safe on one provider and a leak on another, and safe on one NLog layout and a leak on the next. Audit
> every provider *and its configured layout* you register.

### The measured matrix

Every row was executed against the **shipped `DeltaSharp.Storage` assembly** — a real
`DeltaReadException` → `DeltaStorageException(CorruptData)` → `InvalidOperationException("RAW-INNER-LEAK…")`
chain, actually thrown. "Safe" means the raw inner text did not appear in the sink output; "leaks" means
it did. See [Verification](#verification) for versions and provenance.

| Sink configuration | Result | Which question above decides it |
| --- | --- | --- |
| **Microsoft.Extensions.Logging providers** | | |
| Console, simple formatter, `LogError(ex, …)` | Safe | (2) renders `ToString()` once |
| Console, JSON formatter, `LogError(ex, …)` | Safe | (2) renders `ToString()` once |
| Console, systemd formatter, `LogError(ex, …)` | Safe | (2) renders `ToString()` once |
| Any provider, `LogError("… {Reason}", tableId, ex.Message)` | Safe | (1) message only — the exception object never leaves the call |
| NLog `${exception}` (default `format=tostring,data`, `maxInnerExceptionLevel=0`) | Safe | (2) one `ToString()` call — safe because of the override |
| NLog `${exception:format=tostring}` | Safe | (2) one `ToString()` call |
| NLog `${exception:maxInnerExceptionLevel=5}` | **Leaks** | (3) NLog enumerates the chain itself |
| NLog `${exception:format=tostring:maxInnerExceptionLevel=5}` | **Leaks** | (3) enumerates, *then* renders each level |
| NLog `${exception:format=@}` (serialize all properties) | **Leaks** | (4) reflects the object graph |
| Azure Monitor / Application Insights `AddApplicationInsights()` + `LogError(ex, …)` | **Leaks** | (3) builds one `ExceptionDetails` entry **per chain level** |
| **Serilog** | | |
| Default text `{Exception}` token | Safe | (2) renders `ToString()` |
| `CompactJsonFormatter` (emits `@x`) | Safe | (2) renders `ToString()` |
| `Serilog.Formatting.Json.JsonFormatter` | Safe | (2) renders `ToString()` |
| `Error("… {Reason}", ex.Message)` | Safe | (1) message only |
| `Serilog.Exceptions` `Enrich.WithExceptionDetails()` | **Leaks** | (4) destructurer walks the graph |
| `{@Ex}` destructuring in the message template | **Leaks** | (4) default destructurer reflects public properties — and puts raw `FilePath` first |
| **Tracing** | | |
| `Activity.AddException(ex)` (.NET 9+) | Safe (chain-walk) — but **RS0030-banned in DeltaSharp** | (1)/(2) tags are `.Message`, `.ToString()`, type name. Safe for the `.InnerException`-walk axis, but the `.Message` tag can carry a tenant-bearing identifier, so it is banned here (#455); scrub/omit first. |
| OpenTelemetry `activity.RecordException(ex)` (obsolete in favor of `AddException`) | Safe (chain-walk) — but **RS0030-banned in DeltaSharp** | (1)/(2) same three tags; banned for the same tenant-identifier reason (#455, forward ban). |
| **APM SDKs** | | |
| `TelemetryClient.TrackException(ex)` | **Leaks** | (3) one `ExceptionDetails` entry per chain level |
| **Serializers and hand-rolled walks** | | |
| `JsonSerializer.Serialize<Exception>(ex)` on a **thrown** exception | Safe *by accident* | throws `NotSupportedException` on `TargetSite` — do not rely on this |
| `JsonSerializer.Serialize<Exception>(ex)` on an **unthrown** exception | **Leaks** | (4) `InnerException` is a public property |
| `Newtonsoft.Json` `JsonConvert.SerializeObject(ex)` | **Leaks** | (4) same |
| Hand-rolled `while (e is not null) { … e = e.InnerException; }` | **Leaks** | (3) by construction |
| **Direct** | | |
| `ex.ToString()` written to a sink | Safe | (2) the override |
| `ex.Message` written to a sink | Safe | (1) message only — safe from the raw inner; content remains untrusted |

The matrix above is a dated measurement, not a closed configuration inventory. Two families deserve
their own call-out:

- **Application Insights** is one of the most widely deployed .NET production sinks, and it leaks through
  the **same `LogError(ex, …)` call** that is safe on the console: its `ILogger` provider builds an
  `ExceptionTelemetry` whose `ExceptionDetails` list carries one entry **per exception in the chain**.
  Reproduced on both paths — `TelemetryClient.TrackException(ex)` and `AddApplicationInsights()` +
  `LogError(ex, …)`.
- **Serilog's `{@Ex}`** is **one character** away from the safe `{Reason}` shape recommended below. `@`
  switches Serilog from "call `ToString()`" to "destructure the object", which reaches `InnerException`
  *and* every raw typed property. Grep your templates for `{@` on anything exception-shaped.

### DO

- Prefer the shape that is safe under **every** provider and every layout: the exception object and its
  inner never leave the call, only the explicitly supplied structured fields and untrusted message string:

  ```csharp
  logger.LogError("Delta read failed for {Table}: {Reason}", tableId, ex.Message);
  ```

  Note the `@`-free `{Reason}`. `{@Reason}` would destructure.

- Log the exception object **only into a provider whose layout you have audited** against the four
  questions above, and let it render. The overrides do the work — but this is safe because of the
  provider *and its layout*, not because of the call:

  ```csharp
  logger.LogError(ex, "Delta read failed for {Table}.", tableId);
  ```

- On a span, the built-in exception recording (`activity?.AddException(ex)`) captures `exception.message`
  (`ex.Message`), `exception.stacktrace` (`ex.ToString()`), and `exception.type` — which is *safe on the
  chain-walk axis* but **RS0030-banned in DeltaSharp production code (#455)** because `ex.Message` can carry a
  tenant-bearing table/catalog identifier. Do not call it; instead scrub/omit the identifier and emit a
  **bounded structural tag** (the sanctioned form), or leave the exception off the span entirely:

  ```csharp
  // BANNED (RS0030, #455): activity?.AddException(ex);  // ex.Message may carry a tenant identifier
  activity?.SetTag("deltasharp.error.kind", classifiedKind);  // bounded, tenant-free
  ```

- Treat CRD status-condition messages and Kubernetes Events as durable `.Message`-only sinks (question
  1), not ephemeral console output. They remain subject to the personal-data, readership, retention, and
  erasure obligations above. Encode and bound the message before writing it — the sweep covers all
  producers known as of `76d2c8e`; any producer at a new guard is a reviewer obligation — and include
  these sinks: **every #744 governance row above applies except _Access scope_** — CRD status and
  Kubernetes Events are tenant-readable by design, which is why they are `.Message`-only sinks, so the
  access-scope row does not apply; retention, owner, collection, review cadence, failure signal, erasure,
  breach triage, and the hosted-sink row all do. See also the hygiene work in #749.

- If you need the raw inner for on-node debugging, route it to a **server-side-only** sink that no tenant
  can read, with the governance properties in
  [If you route the raw inner](#if-you-route-the-raw-inner-to-a-server-side-only-sink) defined, and treat
  its text as **untrusted**: it is not sanitized, so it can carry CR/LF and `U+2028`/`U+2029` line breaks
  into a shared log stream. Encode on write.

  ```csharp
  // Illustrative. Server-side-only sink, and your own encoder for untrusted text.
  diagnosticsOnlyLogger.LogDebug("cause: {Cause}", EncodeUntrusted(ex.InnerException?.Message));
  ```

### DON'T

- Don't pass the exception object to a provider or layout that enumerates the chain. The call looks
  identical to the safe one above — only the registered provider and its layout differ:

  ```csharp
  // DON'T at a tenant-visible sink: the Application Insights provider turns this into an
  // ExceptionTelemetry whose ExceptionDetails list has one entry per exception in the chain.
  builder.Logging.AddApplicationInsights();
  logger.LogError(ex, "Delta read failed for {Table}.", tableId);
  ```

  ```xml
  <!-- DON'T at a tenant-visible sink: NLog walks .InnerException itself, so the ToString()
       override is called per level and the raw inner is rendered anyway. -->
  <target name="app" xsi:type="Console" layout="${message} ${exception:maxInnerExceptionLevel=5}" />
  ```

- Don't enable a reflection-based exception destructurer on a tenant-visible sink, and don't destructure
  an exception into a message template:

  ```csharp
  // DON'T at a tenant-visible sink: walks InnerException and re-surfaces the raw cause.
  new LoggerConfiguration().Enrich.WithExceptionDetails();

  // DON'T: the '@' destructures. Raw FilePath becomes the first key of the emitted object.
  log.Error("Delta read failed: {@Ex}", ex);
  ```

- Don't serialize the exception object, and don't route it through an `ISerializable` sink.
  `Exception.GetObjectData` is **public on all six covered types** — a binary/`ISerializable`-based
  formatter, a remoting-style channel, or any serializer that honours `ISerializable` reaches
  `InnerException` through it without ever touching a property getter:

  ```csharp
  // DON'T: InnerException is a public property, and is also written by GetObjectData.
  logger.LogError("failed: {Payload}", JsonSerializer.Serialize<Exception>(ex));
  ```

- Don't walk the chain into a tenant-visible message, and don't reflect over the typed properties that
  hold deliberately-unsanitized values — every entry in
  [the reflection-reachable table](#2-every-type-that-retains-reflection-reachable-unsanitized-state), not
  just the public `DeltaReadSchemaEvolutionException.FilePath`.

- Don't attach `ex.Data`, a full plan/EXPLAIN string, or a raw storage path to a span or structured log —
  see the redaction rules in
  [observability-conventions.md](observability-conventions.md#redaction-never-log-secrets-credential-bearing-paths-sql-literals-or-row-values).
  And note that on the paths listed in
  [What a storage `.Message` still retains](#what-a-storage-message-still-retains), `.Message`
  itself carries a relative storage path — so this DON'T constrains *where* `.Message` may go, not just
  what you may add alongside it.

### How DeltaSharp's own log sites behave

As of **2026-08-02**, the storage layer's `LoggerMessage` source-generated log sites
(`src/DeltaSharp.Storage/Diagnostics/DeltaCommitLog.cs`, `DeltaDeleteLog.cs`, `DeltaOptimizeLog.cs`,
`DeltaVacuumLog.cs`) are the in-repo reference for this contract, and they are **stricter than it
requires**: on a failure they log only the exception's **type name** into an `{ExceptionType}` field —
for example `DeltaCommitLog.CommitFailed(_logger, version, attempts, ex.GetType().Name)` — and never pass
the exception object, its message, or its inner. That shape is **provider- and layout-independent** — it
is safe no matter what a host registers — which is exactly why it is the one to copy. Fall back to
`LogError(ex, …)` only when you need the stack trace *and* you have audited every registered provider's
layout. `StorageLogSites_NeverAcceptAnExceptionObject` pins the **exact method-and-parameter
signatures**, including each `String exceptionType` field and the absence of any `Exception` parameter.
The example call expression above is a dated current-tree observation; a method signature cannot prove
where a caller obtained an arbitrary string.

This reference recommendation is scoped to the **failure sites** described above; it does not classify
every string field on every storage event as tenant-safe. `DeltaVacuumLog.VacuumCandidateDecision`
(event 4102) previously emitted a listing-derived relative path in an unbounded `{Path}` field at Debug
level, which could carry Hive partition values or line-break characters. **That was closed by #696** (now
on `main`; [#750](https://github.com/khaines/deltasharp/issues/750) <!-- issue-state:closed --> resolved): event 4102 now carries a
`{CandidateDescription}` — a bounded, partition-value-free rendering via `DiagnosticText.DescribePath` —
so there is no raw `Path` field to copy. **Operator action (do not skip):** erasure/retention evidence
that projected the `Path` field of event 4102 now resolves **null** and MUST be repointed at
`CandidateDescription`; where the exact object key is required as durable proof-of-erasure evidence,
persist `VacuumAuditEntry.Path` from the in-process `VacuumResult` rather than the log field.

"Never accept the exception object" is executed, not asserted: the test discovers every
`[LoggerMessage]` method in the compiled storage assembly, compares the exact population and signatures,
and fails if any takes an `Exception`.

## Enforcement, and why there is no analyzer

#664 floated a Roslyn analyzer that would flag reflection-based exception destructuring, or
`LogError(ex, …)` on these types, at tenant-facing sinks. **DeltaSharp does not ship that analyzer, and
this is a decision rather than a deferral.** The reasoning:

- **The sink is outside our compilation.** An analyzer sees only the compilation it runs in. The
  offending call site lives in the operator's host application and its logging configuration — a
  different compilation that this repository does not build. As of **2026-08-02**, current-tree inspection
  finds `Microsoft.Extensions.Logging.Abstractions` only and no provider, formatter, sink, or exporter in
  the built projects ([observability-conventions.md](observability-conventions.md#scope-and-status)); the
  repo's own storage log sites never pass an exception object in the first place. An in-repo analyzer would
  therefore have **zero call sites to flag** — zero true positives by construction. *(The decidable halves are
  executed: `StorageAssembly_IsNonPackable_AndReviewedProviderFamiliesAreAbsent` parses the storage
  project, asserts that `.Abstractions` is the only `Microsoft.Extensions.Logging` package, and rejects
  the reviewed provider/sink/exporter families named in the guard across the exact root solution-project
  graph, its inherited build manifests, and committed NuGet lock files. This is deliberately not a
  universal package-name classifier; the broader
  current-tree statement is dated and remains a review obligation.
  `StorageLogSites_NeverAcceptAnExceptionObject` fails if a log site takes an `Exception`.)*
- **There is no shipping vehicle to reach the operator.** `DeltaSharp.Storage` is non-packable
  (`IsPackable=false` — asserted by the same test), so there is no NuGet package through which an
  analyzer could flow to a consumer's build. Shipping one would mean creating and publishing a new
  analyzer package for a Low-severity, forward-looking guard with no consumer today.
- **The predicate is not statically decidable.** The rule is not "don't call this API" — it is "don't
  send raw retained state to a sink a *tenant* can read". Sink tenancy is a deployment property, not a
  source-level one — and so is provider registration *and layout configuration*: the very same
  `LogError(ex, …)` is safe under one NLog layout and a leak under the next (measured above), so no
  source-level rule can classify the call site at all. `JsonSerializer.Serialize` and Serilog enrichers
  are likewise legitimate almost everywhere else. Any analyzer would be a heuristic whose dominant output
  is false positives, and the normal response to those — blanket suppression — actively erodes the
  contract it is supposed to protect.
- **The enforceable half is enforced, in code.** What *is* inside our scope is the inventory this page
  publishes, guarded by the tests below in `StorageExceptionToStringTests`:

  | Test | What it makes impossible |
  | --- | --- |
  | `EveryExceptionType_IsConstructed_Thrown_AndIfItCarriesAnInner_RendersNeitherItsMessageNorItsTrace` | An override that is *declared* but does not *omit* the inner; a type that starts chaining a synthetic inner; a leak planted in the stack-trace branch. Builds and **throws** an instance of every exception type in the assembly, with no hand-list. |
  | `ReflectionReachableExceptionState_IsPinned_AndDerivesTheDocumentedRawStateSet` | A new/renamed/re-scoped typed property escaping classification; constructor probes must expose every recursively reachable reference-state path. |
  | `LogRoutingDoc_CoveredAndRawStateTables_MatchTheCompiledInventories` | This page going stale. Both type populations, every raw path in the reflection-state table, and the representative message-posture rows are **parsed** and compared to compiled inventories. |
  | `DocumentedMessagePostureExamples_AgreeWithRuntime` | A representative posture label disagreeing with what the real producer emits; the runtime observations are compared with the parsed row identities and postures. |
  | `ReferenceStateClassification_RequiresDisjointBuckets` | A raw path being laundered into the sanitized/derived bucket or the classification overlap check becoming inert. |
  | `InnerChainingExceptionTypes_AreExactlyTheDocumentedCoveredSet_AndOverrideToString` | An inner-chaining type shipping without a `ToString()` override. |
  | `EveryStorageExceptionType_IsClassifiedAsCoveredOrInnerFree` | A new exception type of **any** constructor shape shipping unclassified; a type self-classifying as covered by declaring an override without an `Exception` parameter. |

  Together, a seventh exception type or a new raw property on an existing type fails the build until it
  is classified **and** this page is updated — and that second clause is now true, which it was not before
  #694. The doc tables were the last unexecuted half of this claim; making them executable rather than
  deleting them was the deliberate choice, because after the re-keying above **the reflection-state
  table is a safety control**, not decoration: it is the only place an operator can read which typed
  properties are unsafe to destructure.

  **Known blind spot, stated rather than glossed.** "Can chain an inner" is a *dataflow* property, and
  no reflection predicate decides it. A type can chain a **synthetic** inner while declaring no
  `Exception` parameter at all — `base(message, new IOException(detail))`, which is precisely the
  `SurfaceFailure` / RF-8b shape described above. The behavioural oracle now closes most of this: it
  builds every type from its own constructors and checks the render against whatever inner actually
  materialises, so a synthetic inner attached in a constructor that the harness can call **is** caught.
  What remains uncaught is a synthetic inner attached only on a code path the constructor does not take
  — for example one built by a static factory the harness does not invoke. That case is a review
  obligation, listed in [Ownership and review](#ownership-and-review).

Net: the part we can enforce is enforced by compiled tests; the part we cannot is a documented consumer
obligation, backed by the shipped `ToString()` override that makes a sink safe **if and only if it lets
`Exception.ToString()` do the chain walk**. It does **not** make every sink safe — a chain-walking layout,
a reflecting provider, or a destructurer still leaks, which is why the sink rules above are normative
rather than advisory. Revisit this decision if DeltaSharp starts shipping a packable storage surface
**and** a supported logging integration — at that point the sink moves inside a compilation we own, and an
analyzer becomes both possible and useful.

## Known gaps in the sanitizer itself

`DiagnosticText.Sanitize` closes the **line-break** class of log injection (the one that lets an
attacker forge a whole log record) and, since #683/#685/#686 (PR #696), the **text-confusion /
invisible-smuggling** class as well:

- **Closed (line-break):** C0/C1 controls (category `Cc` — CR, LF, NUL, tab, NEL) and `U+2028`/`U+2029`
  (`Zl`/`Zp`), each replaced with `U+FFFD`; plus a length cap and lone-surrogate neutralization.
- **Closed (text-confusion, since PR #696):** bidirectional overrides (`U+202E`, `U+200F`, `U+2066`…),
  zero-width characters (`U+200B`), and Unicode TAG characters (`U+E0000`–`U+E007F`). The sanitizer now
  neutralizes every `UnicodeCategory.Format` (`Cf`) code point to `U+FFFD`, read at the whole **code
  point** so astral TAG characters are caught too. This closed the gap this page originally tracked as open
  in [#746](https://github.com/khaines/deltasharp/issues/746) <!-- issue-state:closed --> (now resolved): the crafted
  `protocol.readerFeatures` → `SanitizeAndJoin` → `DeltaReadException` spoofing path is neutralized. The
  neutralization lives in the shared `DeltaSharp.Abstractions` primitive that both Storage and the Core SQL
  parser forward to, so the two recognizers cannot drift.
- **Operator obligation for unreviewed producers.** An operator rendering a storage `.Message` from a
  producer that has not been reviewed for hygiene into a shared log stream should still encode on write
  to guard against line-break injection. The sanitizer covers all producers known as of `76d2c8e`;
  any future producer at a new guard is a reviewer obligation until it is added to the sweep.
- **All producers known as of `76d2c8e` are covered ([#747](https://github.com/khaines/deltasharp/issues/747) <!-- issue-state:closed -->, completed — closes on PR merge).**
  All `DeltaStorageException` and `DeltaProtocolException` factories that accept fully-composed messages
  carry an explicit hygiene obligation in the type-level XML `<remarks>`; their call sites pre-sanitize
  attacker-influenceable tokens before interpolation — verified by `StorageHygieneSweepTests` and
  `StorageMessageHygieneTests` for known call sites (both suites carry `DeltaProtocolException`
  producers). New producers remain a manual reviewer obligation (#749, open).

## Verification

Every "safe"/"leaks" claim in [The measured matrix](#the-measured-matrix) was executed, not assumed.
**Measured 2026-07-29** against .NET 10, using the **shipped `DeltaSharp.Storage` assembly** (loaded from
the Release build output, so the types, overrides, and message text are the real ones) with:

| Package | Version |
| --- | --- |
| `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Console` | 9.0.0 |
| `Serilog` | 4.2.0 |
| `Serilog.Sinks.Console`, `Serilog.Sinks.TextWriter`, `Serilog.Formatting.Compact` | 6.0.0 / 3.0.0 / 3.0.0 |
| `Serilog.Extensions.Logging` | 9.0.0 |
| `Serilog.Exceptions` | 8.4.0 |
| `NLog`, `NLog.Extensions.Logging` | 5.3.4 / 5.3.14 |
| `OpenTelemetry.Api` | 1.11.2 |
| `Newtonsoft.Json` | 13.0.3 |
| `Microsoft.ApplicationInsights`, `Microsoft.Extensions.Logging.ApplicationInsights` | 2.23.0 |

The Application Insights result was captured through an in-memory `ITelemetryChannel` on **both** paths —
`TelemetryClient.TrackException(ex)` and an `ILogger.LogError(ex, …)` through `AddApplicationInsights()` —
and both produced an `ExceptionTelemetry` whose `ExceptionDetails` list contained the raw inner's message
alongside the sanitized one. The destructuring measurements in
[What a reflecting sink can reach](#2-every-type-that-retains-reflection-reachable-unsanitized-state) used
Serilog's default destructurer via `{@Ex}` with `CompactJsonFormatter`.

The in-tree behavior the tables depend on — the override itself, the omission of the chain at every
covered site, the transitive suppression through an outer exception and an `AggregateException`, the
render of a **thrown** exception, the reachable-state inventory, and the doc tables themselves — is locked
by `tests/DeltaSharp.Storage.Tests/StorageExceptionToStringTests.cs`.

None of those sink packages is a DeltaSharp dependency, so **no test pins them** and the matrix can age:
a destructurer or a default layout can change behavior in a minor version without anything here failing.
A pinned test would assert a third party's behavior that this repo could not remediate, so the date and
versions above are deliberately provenance rather than enforcement — re-measure before relying on a row
for a sink whose major version has moved on. The NLog rows are the standing reminder that a *layout*
change, not just a version change, moves a row.

## Ownership and review

The cloud-native-site-reliability-engineer owns the log-routing posture and the sinks these rules
constrain; the delta-storage-format-engineer owns which exception types retain raw state; the
privacy-compliance-grc-lead owns what counts as personal data in a message and the retention/erasure
obligations on a diagnostics sink; the technical-writer owns this document's accuracy and clarity. A PR
that adds a storage exception type, adds or re-scopes a typed property, introduces a public instance
field, changes a `ToString()` override, or changes what a message retains is reviewed against
[05](../checklists/05-security-checklist.md), [07](../checklists/07-privacy-checklist.md),
[09a](../checklists/09a-logging-checklist.md), and
[14](../checklists/14-tenant-isolation-checklist.md), and updates the tables above — which the compiled
guards will insist on anyway.

The compiled guards cannot catch every future integration, so a reviewer must check:

- **A synthetic inner attached outside a constructor the test harness can call** — for example one built
  only inside a static factory. The behavioural oracle constructs each type from its own constructors; an
  inner materialised only on some other path stays invisible to it.
- **A new logging provider, a new layout on an existing provider, or a new non-log sink such as a CRD
  status condition or Kubernetes Event** — the
  provider *and its layout* decide whether `LogError(ex, …)` renders once or walks the chain, and that
  choice and the sink's readership are invisible to this repository.
- **A message that starts carrying a new class of tenant data.** `Sanitize` is an injection control, not
  a classification control; only a reviewer decides whether a newly echoed token is personal data.
- **The full exception-message producer inventory.** The posture table above covers all call sites
  known as of `76d2c8e`; coverage is a manually maintained door list, not an automated inventory.
  Verified by `StorageHygieneSweepTests` (each entry has a live behavior pin; the sweep door-matrix is hand-enumerated, not auto-generated). #749 tracks
  additional producers. The sweep door-matrix catches new raw echoes *inside* already-covered guards; a producer at a wholly new guard is not automatically detected and remains a reviewer obligation.
- **Inherited `Exception.Data`.** Current storage code does not populate it, but layouts such as NLog's
  default `format=tostring,data` and object-graph destructurers can render it. A change that writes
  attacker- or tenant-derived values there requires table and sink review.

## References

- [observability-conventions.md](observability-conventions.md) — repo-wide logging, metrics, tracing, and
  redaction conventions
- [storage-delta-architecture.md](storage-delta-architecture.md) — the storage layer these exceptions
  come from
- [05 — Security Checklist](../checklists/05-security-checklist.md)
- [07 — Privacy Checklist](../checklists/07-privacy-checklist.md)
- [09a — Logging Checklist](../checklists/09a-logging-checklist.md)
- [09c — Distributed Tracing Checklist](../checklists/09c-distributed-tracing-checklist.md)
- [11 — Documentation Support Checklist](../checklists/11-documentation-support-checklist.md)
- [14 — Tenant Isolation Checklist](../checklists/14-tenant-isolation-checklist.md)
- `src/DeltaSharp.Storage/Delta/DiagnosticText.cs` — `DescribeWithoutInner` and `Sanitize`
- `src/DeltaSharp.Storage/Diagnostics/` — the `LoggerMessage` log sites that model this contract in-repo
- `src/DeltaSharp.Storage/Backends/LocalFileSystemBackend.cs` — `SurfaceFailure`, the stronger RF-8b
  synthetic-inner treatment
- `tests/DeltaSharp.Storage.Tests/StorageExceptionToStringTests.cs` — the compiled guards behind this page
