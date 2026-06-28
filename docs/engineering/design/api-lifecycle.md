# API lifecycle: experimental and obsolete

> **Status:** living document. Created with FEAT-01.5
> ([STORY-01.5.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0153-experimental-and-obsolete-api-lifecycle)).
> Extends [api-governance.md](api-governance.md) (PublicApi/BannedApi baselines). Grounded in
> [ADR-0014](../../adr/0014-target-framework-aot.md) (TFM and AOT posture) and checklists
> [03a](../checklists/03a-dotnet-coding-standards.md),
> [20](../checklists/20-developer-experience-api-checklist.md), and
> [11](../checklists/11-documentation-support-checklist.md). Update it whenever the lifecycle
> conventions or the diagnostic-ID registry change.

DeltaSharp's public surface is governed by PublicApiAnalyzers baselines (see
[api-governance.md](api-governance.md)). On top of *what* is public, this document governs the
**stability** of public APIs as they evolve: how an API is marked **experimental** while it is
still being shaped, and how it is marked **obsolete** once it is being retired. Both states are
expressed with .NET's built-in attributes so that the compiler — not a convention reviewers must
remember — tells consumers about the risk at build time.

Every new public API has exactly one stability state:

| State | Marker | Compiler diagnostic | Consumer effect |
| --- | --- | --- | --- |
| Stable | *(none)* | — | Normal use; covered by SemVer compatibility once shipped. |
| Experimental (preview) | `[Experimental("DS####", UrlFormat = …)]` | `DS####` (a DeltaSharp ID) | Build **warning** until explicitly acknowledged; shape may change or be removed. |
| Obsolete (deprecated) | `[Obsolete(message, error)]` | `CS0618`/`CS0612` (built-in) | Build warning (or error) with replacement/removal guidance. |
| Internal | `internal` | — | Not part of the public surface; not in the PublicAPI baseline. |

## Experimental APIs

Mark an API experimental when it is published for feedback but its shape, semantics, or
Spark-parity promise is not yet settled. Use
[`System.Diagnostics.CodeAnalysis.ExperimentalAttribute`](https://learn.microsoft.com/dotnet/api/system.diagnostics.codeanalysis.experimentalattribute)
(available on `net8.0` and `net10.0`):

```csharp
[Experimental(DeltaSharpDiagnostics.PreviewMetadataApis, UrlFormat = DeltaSharpDiagnostics.UrlFormat)]
public static string PreviewReleaseChannel => /* inert preview value */;
```

The diagnostic ID and URL template live as `const` strings in the internal
[`DeltaSharpDiagnostics`](../../../src/DeltaSharp.Core/DeltaSharpDiagnostics.cs) class so every
experimental API draws from the **same registered values** (the constants are `internal`, so they
do not widen the public surface). `UrlFormat` is a `string.Format` template whose `{0}` is replaced
with the diagnostic ID, producing a per-diagnostic help link (for example
`…/api-lifecycle.md#DS0001`).

**Required documentation.** Every experimental member's XML doc records four things so a reader and
the generated reference both see the contract without opening the source:

1. **Diagnostic ID** — the `DS####` code consumers suppress.
2. **Documentation URL** — the registry entry (the same link `UrlFormat` produces).
3. **Owner** — the persona/role accountable for graduating or removing it.
4. **Expected review point** — the milestone or condition that triggers stabilize-or-remove.

The live reference implementation is `DeltaSharp.DeltaSharpInfo.PreviewReleaseChannel`
([DeltaSharpInfo.cs](../../../src/DeltaSharp.Core/DeltaSharpInfo.cs)).

**Consuming an experimental API.** Using a `[Experimental]` member raises the `DS####` warning,
which is build-breaking under the repository's `TreatWarningsAsErrors`. A consumer that accepts the
risk opts in explicitly, with justification:

```csharp
#pragma warning disable DS0001 // Preview metadata API — shape may change (api-lifecycle.md#DS0001)
string channel = DeltaSharpInfo.PreviewReleaseChannel;
#pragma warning restore DS0001
```

or project-wide for a sample or spike via `<NoWarn>DS0001</NoWarn>`. Prefer the narrowest scope.
Inside DeltaSharp's own `src/` we **avoid consuming** experimental members in shipping code paths;
if it is unavoidable, scope the `#pragma` to a single statement and state the invariant, the same
way [banned-API exceptions](api-governance.md#requesting-a-scoped-exception) are scoped.

**How PublicApiAnalyzers track experimental APIs.** An experimental member is still a public member,
so it appears in [`PublicAPI.Unshipped.txt`](../../../src/DeltaSharp.Core/PublicAPI.Unshipped.txt)
as an ordinary documentation-ID line — the `[Experimental]` attribute lives in code, not in the
baseline file:

```text
static DeltaSharp.DeltaSharpInfo.PreviewReleaseChannel.get -> string!
```

Keeping experimental APIs **unshipped** (not promoted into `PublicAPI.Shipped.txt`) reflects their
real status: removing or reshaping them is a low-compatibility-risk change because every consumer
was warned at build time. The lifecycle is therefore:

- **Graduate to stable:** remove `[Experimental]`, keep the baseline line, and (on the next release)
  promote it from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`. Mark the registry entry
  `Graduated`.
- **Remove:** delete the member and its `PublicAPI.Unshipped.txt` line; note it in the PR's
  *Public API & compatibility* section. Mark the registry entry `Removed`.

## Obsolete APIs

Mark an API obsolete when it is being retired in favor of a replacement. Use the built-in
[`System.ObsoleteAttribute`](https://learn.microsoft.com/dotnet/api/system.obsoleteattribute) with a
message that gives the consumer a next action:

```csharp
[Obsolete(DeltaSharpDiagnostics.ProductNameObsoleteMessage, error: false)]
public static string ProductName => Product;
```

**Message requirement (AC2).** The message must state **replacement guidance** (name the API to use
instead) and/or a **removal timeline** (the release the member is scheduled to disappear in). The
live reference implementation is `DeltaSharp.DeltaSharpInfo.ProductName`, whose message is:

> Use DeltaSharpInfo.Product instead. The ProductName alias is obsolete and is scheduled for
> removal in DeltaSharp v0.2.0.

**`error` flag.** Start with `error: false` so existing source keeps compiling with a `CS0618`
**warning** during the deprecation window (the "When a consumer builds, the diagnostic states
replacement guidance" outcome). Flip to `error: true` for a hard stop one release before removal, so
the break is loud before the member actually disappears.

**No `DS####` ID.** Obsolete APIs surface through the framework's built-in `CS0618` (with message)
or `CS0612` (without) diagnostics, so they **do not consume** a DeltaSharp `DS####` identifier. The
registry below is reserved for DeltaSharp-authored diagnostics (experimental IDs today, custom
analyzer rules later).

**Baseline tracking.** An obsolete member stays in the PublicAPI baseline until it is actually
removed; the `[Obsolete]` attribute does not change its documentation-ID line. Removing it later is a
breaking change that moves the line out of `PublicAPI.Shipped.txt` and requires a migration note
(checklist [11](../checklists/11-documentation-support-checklist.md)).

## Diagnostic-ID registry (`DS####`)

DeltaSharp reserves the **`DS####`** identifier namespace for its own compile-time diagnostics. Today
that is the IDs attached to `[Experimental]` APIs; custom Roslyn analyzer rules will draw from the
same namespace later. Reserving and recording the IDs here keeps each one **unique** and **traceable
to policy** (AC4): a maintainer adding an ID can see at a glance which numbers are taken and what each
one governs.

### Range allocation

| Range | Purpose |
| --- | --- |
| `DS0001`–`DS0099` | Experimental public-API feature areas (`[Experimental]` diagnostic IDs). |
| `DS0100`–`DS0899` | Reserved for future DeltaSharp Roslyn analyzer rules. |
| `DS0900`–`DS0999` | Reserved for build/packaging diagnostics. |

One ID identifies a **feature area**, not a single member: several related experimental APIs may
share an ID so a consumer can opt into a whole preview surface with one suppression.

### Adding a diagnostic ID

1. Pick the **next free** number in the appropriate range and add a row to the registry table below,
   plus a matching `<a id="DS####"></a>` detail anchor so the `UrlFormat` deep link resolves.
2. Add the `const` to [`DeltaSharpDiagnostics`](../../../src/DeltaSharp.Core/DeltaSharpDiagnostics.cs)
   and reference it from the attribute — never hard-code the literal at the call site.
3. For an experimental ID, write the member's XML doc with the four required facts (ID, URL, owner,
   review point) and update the PublicAPI baseline.
4. Confirm the ID is unique (it must not already appear in this table) and that the build emits the
   expected `DS####` diagnostic when the API is consumed without suppression.

### Registered diagnostic IDs

| ID | Name | Kind | Status | Owner | Documentation |
| --- | --- | --- | --- | --- | --- |
| [`DS0001`](#DS0001) | Preview metadata APIs | Experimental | Active | dotnet-library-platform-engineer | [DS0001](#DS0001) |

<a id="DS0001"></a>

#### DS0001 — Preview metadata APIs

- **Kind:** Experimental (`[Experimental]`).
- **Status:** Active (unshipped preview).
- **Owner:** `dotnet-library-platform-engineer`.
- **Applies to:** `DeltaSharp.DeltaSharpInfo.PreviewReleaseChannel` — preview build/version
  metadata accessors whose shape and taxonomy are not yet stable.
- **Expected review point:** when the release-channel taxonomy (preview/rc/stable) is finalized for
  the v0.1 release; graduate to stable or remove at that point.
- **Opt in with:** `#pragma warning disable DS0001` (per call site) or `<NoWarn>DS0001</NoWarn>`
  (project-wide, for samples/spikes), with justification.

## Samples

Samples may demonstrate experimental APIs before they stabilize. When they do, the sample names the
`DS####` diagnostic ID, links back to this registry, and shows the opt-in — see the **Preview status
and compatibility** section of [`samples/README.md`](../../../samples/README.md).

## References

- [API governance: public-API baselines and banned APIs](api-governance.md)
- [Repository layout and project conventions](repository-layout.md)
- [Samples README](../../../samples/README.md)
- [03a — .NET coding standards checklist](../checklists/03a-dotnet-coding-standards.md)
- [11 — Documentation support checklist](../checklists/11-documentation-support-checklist.md)
- [20 — Developer experience and API checklist](../checklists/20-developer-experience-api-checklist.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
- [.NET `ExperimentalAttribute`](https://learn.microsoft.com/dotnet/api/system.diagnostics.codeanalysis.experimentalattribute)
- [.NET `ObsoleteAttribute`](https://learn.microsoft.com/dotnet/api/system.obsoleteattribute)
- [Roslyn PublicApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md)
