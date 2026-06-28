# API governance: public-API baselines and banned APIs

> **Status:** living document. Created with FEAT-01.5
> ([STORY-01.5.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0151-publicapianalyzers-baselines),
> [STORY-01.5.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-01-project-build-platform.md#story-0152-bannedapianalyzers-policy)).
> Grounded in [ADR-0001](../../adr/0001-execution-strategy.md) (codegen tier is optional
> and AOT-gated), [ADR-0014](../../adr/0014-target-framework-aot.md) (TFM and AOT posture),
> and checklists [03a](../checklists/03a-dotnet-coding-standards.md),
> [05](../checklists/05-security-checklist.md),
> [08](../checklists/08-performance-checklist.md), and
> [20](../checklists/20-developer-experience-api-checklist.md). Update it whenever the
> governed projects, baseline format, or banned categories change.

DeltaSharp gates two classes of build-time policy with Roslyn analyzers so that public
surface changes are intentional and so that AOT-, trim-, determinism-, and
security-unsafe APIs cannot enter production assemblies unnoticed. Both analyzers are
wired centrally in [`Directory.Build.props`](../../../Directory.Build.props) inside the
`lane:01.5` section and pinned in
[`Directory.Packages.props`](../../../Directory.Packages.props).

## What is governed

| Project | Public-API baseline (`PublicApiAnalyzers`) | Banned APIs (`BannedApiAnalyzers`) |
| --- | --- | --- |
| `DeltaSharp.Core` (packable, public) | yes | yes |
| `DeltaSharp.Engine` (production, internal) | no | yes |
| `DeltaSharp.Executor` (production, internal host) | no | yes |
| `tests/**` | no | no |

The scope follows intent: public-API baselines apply only to **packable public
libraries**, banned APIs apply to every **production assembly under `src/`**, and test
projects are exempt because they legitimately use reflection, clocks, and randomness.

Both analyzers are referenced with `PrivateAssets="all"`, so they are development-time
tools only and never flow to package consumers.

## Enforcement model

The relevant diagnostics (`RS0016`, `RS0017`, `RS0025` for public API; `RS0030` for
banned APIs) ship as warnings. Enforcement rides on the repository-wide
`TreatWarningsAsErrors=true` policy in `Directory.Build.props`, so a violation **fails the
build** with a clear, actionable diagnostic. This is the same policy that backs the .NET,
trim, and AOT analyzers, so relaxing warnings-as-errors would weaken all of them at once.

## Public-API baselines (STORY-01.5.1)

Each governed library owns two files next to its project:

- `PublicAPI.Shipped.txt` — the public surface already released in a shipped package. It
  is **empty** until the first release (DeltaSharp has not shipped `v0.1` yet).
- `PublicAPI.Unshipped.txt` — public surface added since the last release. It currently
  records the only public type, `DeltaSharp.DeltaSharpInfo`, and its members.

The files use the analyzer's documentation-ID format with `#nullable enable`, so
reference-type nullability is part of the recorded contract (for example
`static DeltaSharp.DeltaSharpInfo.Product.get -> string!`).

### How it catches changes

- **Adding** public API without recording it fails the build with `RS0016`
  (*Symbol is not part of the declared public API*).
- **Removing or changing** an entry already promoted to `PublicAPI.Shipped.txt` fails with
  `RS0017` (*Symbol is part of the declared API but could not be found*), which surfaces the
  compatibility break for review.

### Updating the baseline

1. Build the project. The `RS0016` diagnostic prints the exact line to add (including the
   `!`/`?` nullability suffix). The IDE code fix *Add to public API* does the same.
2. Add the line to `PublicAPI.Unshipped.txt` (keep entries sorted for clean diffs).
3. In the pull request, describe the public-API and compatibility impact in the
   **Public API & compatibility** section of the PR template.
4. On release, move the accumulated `PublicAPI.Unshipped.txt` entries into
   `PublicAPI.Shipped.txt` and clear the unshipped file.

Because `DeltaSharp.Core` multi-targets `net8.0;net10.0`, both target frameworks are
analyzed against the same baseline files; keep the public surface identical across targets
unless an ADR says otherwise.

## Banned APIs (STORY-01.5.2)

The shared ban list lives at [`BannedSymbols.txt`](../../../BannedSymbols.txt) in the
repository root and is applied to every production assembly. Each entry carries a
bracketed category tag plus remediation guidance, and links to the ADR that justifies the
ban. Lines are grouped into category sections with `//` comment banners, which
`BannedApiAnalyzers` supports. The analyzer **silently ignores any line it cannot resolve**
(no diagnostic), so a mistyped documentation-ID is a no-op — entries must use exact IDs and
new bans must be proven to fire.

| Category | Examples | Why it is banned | Safe alternative |
| --- | --- | --- | --- |
| dynamic-code / AOT | `Expression.Compile`, `System.Reflection.Emit.*` | Emit IL at runtime; NativeAOT-incompatible (ADR-0001, ADR-0014). | Interpreted vectorized backend; gate any compiled tier behind `RuntimeFeature.IsDynamicCodeSupported` with `[RequiresDynamicCode]`. |
| trim / AOT | `Activator.CreateInstance`, `Type.GetType(string)`, `Assembly.LoadFrom` | Reflection or dynamic loading that trimming/AOT cannot analyze (ADR-0014). | Known constructors, factory delegates, or DI; annotate unavoidable reflection with `[RequiresUnreferencedCode]` / `[DynamicallyAccessedMembers]`. |
| determinism | `DateTime.Now`, `DateTime.UtcNow`, `Guid.NewGuid`, `System.Random` | Nondeterministic ambient state breaks reproducible planning, caching, and tests. | Injected `TimeProvider`/clock and id/RNG abstractions; UTC. |
| security | `Process.Start`, `MD5`, `SHA1` | Command-execution surface and broken cryptography (checklist 05). | Validated argument lists with security review; SHA-256 or stronger. |

A banned call fails the build with `RS0030` and the entry's remediation message, so the
diagnostic tells the author both *why* it is banned and *what to use instead*.

A method documentation-ID with no parameter list matches only the **parameterless**
overload, so each dangerous overload of a banned method family is listed explicitly with its
parameter types. For the reflection-style APIs (`Activator.CreateInstance`, `Type.GetType`)
the trim/AOT analyzers (`EnableTrimAnalyzer` / `EnableAotAnalyzer`, already enabled on the
production projects) provide a second, dataflow-aware backstop, so the ban list and the
analyzers reinforce each other rather than relying on the ban list alone for completeness.

### Requesting a scoped exception

Some APIs are legitimately needed (for example, the optional compiled backend from
ADR-0001 deliberately uses dynamic code behind a feature switch). Exceptions must be
**narrow, justified, and linked to the ADR** that allows them:

```csharp
// Compiled fast-path tier — reachable only when RuntimeFeature.IsDynamicCodeSupported is
// true and elided from NativeAOT publishes. Justified by ADR-0001 (optional codegen tier).
#pragma warning disable RS0030 // Banned API: Expression.Compile (see ADR-0001)
var compiled = expression.Compile();
#pragma warning restore RS0030
```

Requirements for an exception:

- Scope the `#pragma warning disable`/`restore` to the smallest possible region (ideally a
  single statement), never a whole file or project.
- State the invariant that makes it safe and link to [ADR-0001](../../adr/0001-execution-strategy.md)
  or [ADR-0014](../../adr/0014-target-framework-aot.md).
- For security-sensitive bans, name the required owner/review (checklist
  [05](../checklists/05-security-checklist.md)).
- Reviewers reject unscoped or unjustified suppressions.

## Adding or changing governance

- **A new banned API:** add a line to `BannedSymbols.txt` as
  `<id>;[category] message with remediation and ADR link`, where `<id>` is the C#
  documentation-comment ID (`T:`, `M:`, `P:`, `F:`, `E:`, or `N:`). A method ID with **no
  parameter list matches only the parameterless overload**, so list each dangerous overload
  explicitly with its parameter types (the file does this for `Activator.CreateInstance`,
  `Type.GetType`, `Process.Start`, and the `Compile` overloads); generic methods use the
  doc-ID double-backtick arity form. Ban a whole namespace with `N:`. The analyzer silently
  ignores IDs it cannot resolve, so verify each new entry actually fires.
- **A new packable public library:** add its project name to the `DeltaSharpTracksPublicApi`
  condition in `Directory.Build.props` and create empty `PublicAPI.Shipped.txt` plus a
  populated `PublicAPI.Unshipped.txt` baseline.
- **A new production assembly under `src/`:** it is governed by the banned-API policy
  automatically through the path-based `DeltaSharpIsProductionAssembly` gate.

## References

- [ADR-0001: Execution strategy](../../adr/0001-execution-strategy.md)
- [ADR-0014: Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
- [03a — .NET coding standards checklist](../checklists/03a-dotnet-coding-standards.md)
- [05 — Security checklist](../checklists/05-security-checklist.md)
- [08 — Performance checklist](../checklists/08-performance-checklist.md)
- [20 — Developer experience and API checklist](../checklists/20-developer-experience-api-checklist.md)
- [Repository layout and project conventions](repository-layout.md)
- [Roslyn PublicApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md)
- [Roslyn BannedApiAnalyzers](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/BannedApiAnalyzers/BannedApiAnalyzers.Help.md)
