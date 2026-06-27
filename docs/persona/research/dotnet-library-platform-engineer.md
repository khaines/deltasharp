# .NET Library & Package Platform Engineer: required skills, behaviors, traits, and knowledge

## Executive Summary

The .NET Library & Package Platform Engineer owns the physical delivery and enforcement layer for DeltaSharp: NuGet packages, multi-targeting, deterministic builds, repository build governance, public API baselines, analyzer policy, source-generator packaging, strong naming, assembly-version stability, and trim/Native-AOT readiness. DeltaSharp is a native .NET Spark-equivalent shipped as C# libraries, so its public API is not just a set of method names; it is a long-lived assembly and package contract that downstream applications, connectors, executor images, and CI systems must consume safely.[^1]

This role is distinct from public API design. `developer-experience-api-engineer` owns Spark-parity API shape, overloads, names, docs, migration, and samples. The library platform role owns the physical and enforcement layer that makes that shape buildable, packageable, versionable, analyzer-governed, and AOT-ready. It is also distinct from runtime performance: `dotnet-runtime-performance-engineer` owns GC/JIT/allocation/hot-path behavior, while this role owns the build, package, analyzer, versioning, and AOT annotation controls around those implementations.[^2]

DeltaSharp's ADR-0001 makes this role especially important. The engine has an AOT-clean vectorized interpreter as the default and reference backend, plus an optional JIT codegen tier that may use `Expression.Compile()` or later IL generation only when dynamic code is supported. Native AOT cannot rely on runtime code generation: `DynamicMethod` and ref-emit paths are annotated as requiring dynamic code, and `Expression.Compile()` cannot be treated as a universal AOT mechanism. The library platform engineer owns the feature switches, package boundaries, annotations, analyzer gates, and `PublishAot` verification that keep the optional compiled backend cleanly dead-code-eliminated from AOT executor publishes.[^3]

The role's day-to-day output is practical governance: `Directory.Build.props`, `Directory.Build.targets`, `global.json`, `Directory.Packages.props`, `.editorconfig`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `BannedSymbols.txt`, package validation, SourceLink, symbols, strong-name and `InternalsVisibleTo` policy, `AssemblyVersion` rules, and upgrade checklists. Without this discipline, a native .NET engine drifts into non-reproducible builds, accidental public API breaks, floating dependencies, trim warnings that nobody owns, and packages that cannot be safely consumed or AOT-published.[^4]

---

## Evidence base

- Microsoft .NET library guidance for cross-platform targeting and NuGet package authoring defines multi-targeting, dependency exposure, and compatibility considerations for reusable libraries.[^1]
- NuGet Central Package Management documents repository-wide package version governance through `Directory.Packages.props`, reducing dependency drift and making upgrades reviewable.[^4]
- MSBuild `Directory.Build.props` and `Directory.Build.targets` provide repository-wide build policy hooks for shared project settings, analyzer configuration, signing, and deterministic build behavior.[^5]
- `global.json` SDK selection pins the .NET SDK feature band used by local and CI builds, preventing accidental SDK drift.[^6]
- SourceLink and deterministic build documentation describe the symbol/source reproducibility expected from world-class NuGet packages.[^7]
- Microsoft package validation and `Microsoft.DotNet.ApiCompat` document assembly/package compatibility checks for public libraries.[^8]
- Roslyn analyzers include `PublicApiAnalyzers` rules such as RS0016/RS0017 for public API tracking and `BannedApiAnalyzers` rule RS0030 for forbidden API usage.[^9]
- .NET attributes such as `[Experimental]`, `[Obsolete]` with diagnostic metadata, and `[EditorBrowsable]` support explicit preview, deprecation, and discoverability policies.[^10]
- Roslyn source generator documentation recommends `IIncrementalGenerator` for efficient, cacheable, deterministic generation pipelines.[^11]
- Native AOT and trimming documentation define the annotation model for dynamic code and reflection: `[DynamicallyAccessedMembers]`, `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]`, and targeted suppressions.[^12]
- `dotnet/runtime` expression-tree implementation marks IL compilation paths with dynamic-code feature guards and `RequiresDynamicCode`; dynamic methods are not a safe Native AOT primitive.[^3]
- DeltaSharp ADR-0001 requires the optional compiled backend to be gated and elided under AOT while the interpreted vectorized backend remains the correctness reference.[^13]

---

## Explanation

### Why this role exists

DeltaSharp is not an application hidden behind one deployment artifact. It is a library and package ecosystem: public APIs, internal engine assemblies, connector packages, analyzer packages, source generators, executor images, test assemblies, and downstream applications all consume its build outputs. That ecosystem needs a platform owner who thinks in assembly identity, NuGet compatibility, analyzer governance, target frameworks, deterministic builds, and upgrade paths.

A native .NET Spark equivalent also has a wider compatibility surface than a typical service. It must remain usable in normal JIT-hosted applications, Kubernetes executor processes, connector libraries, and Native AOT executor images. The same API surface may need to satisfy Spark-like ergonomics, optimizer internals, distributed execution, Delta/Parquet storage, and package consumers. Without a dedicated library platform owner, build settings and package boundaries become accidental architecture.

The role exists because many failures in .NET libraries are not algorithmic failures. A package can compile locally but fail under a different SDK, leak unstable transitive dependencies, break binary compatibility through an assembly-version mistake, ship symbols without SourceLink, expose internal types as public API, use reflection that breaks trimming, or accidentally keep a JIT-only codegen assembly reachable from an AOT publish. These are product-quality failures for DeltaSharp.

### Boundaries

- **vs. `developer-experience-api-engineer`**: Developer Experience owns public Spark-parity API shape: names, overloads, XML docs, migration, samples, and user-facing ergonomics. Library Platform owns physical packaging, analyzers, public API baselines, versioning, assembly identity, and AOT annotation hygiene that enforce and preserve that shape.
- **vs. `dotnet-runtime-performance-engineer`**: Runtime Performance owns GC/JIT behavior, allocation, SIMD performance, unsafe hot paths, and runtime diagnostics. Library Platform owns package boundaries, analyzer gates, build settings, and AOT/trim annotations that keep those implementations consumable and governable.
- **vs. `dotnet-distributed-execution-engineer`**: Distributed Execution owns process hosting, Kestrel/gRPC, `IHostedService`, Channels, shutdown, and executor composition. Library Platform collaborates on package composition, `PublishAot` profiles, feature switches, and dependency closure for those hosts.
- **vs. `cloud-native-security-sme`**: Security owns supply-chain policy, signing trust, provenance, SBOM requirements, and secrets. Library Platform implements compatible package signing/build mechanics and dependency governance in collaboration.
- **vs. `query-execution-engine-engineer`**: Query Execution owns logical/physical semantics and backend selection behavior. Library Platform enforces assembly boundaries and ADR-0001 dynamic-code/AOT hygiene around those decisions.

---

## Required knowledge domains

### 1. NuGet packaging, multi-targeting, SourceLink, deterministic builds, and package validation

**Package boundaries**: DeltaSharp should have intentional package IDs, assembly names, dependency surfaces, and analyzer/source-generator packages. Public API packages, engine-internal packages, connector packages, and executor-hosting packages should not leak unstable dependencies or force consumers to reference implementation-only assemblies.

**Multi-targeting**: The engineer must understand `<TargetFramework>` vs. `<TargetFrameworks>`, target-specific dependencies, conditional compilation, reference assemblies, and API availability. DeltaSharp may need to support modern targets such as `net8.0`, `net9.0`, or `net10.0`, and possibly compatibility targets where justified. Multi-targeting should exist to serve consumers, not to create untested combinatorial drift.[^1]

**Dependency exposure**: NuGet dependencies are part of the consumer contract. Package references should be centralized, versioned intentionally, and split so optional features do not drag heavyweight or JIT-only dependencies into AOT-safe packages. Transitive dependencies need compatibility review just like public types.

**Symbols and SourceLink**: World-class packages ship usable symbols and SourceLink so downstream developers can debug into DeltaSharp without source mismatches. Deterministic builds ensure that package artifacts can be reproduced and audited.[^7]

**Package validation**: `ApiCompat` and package validation detect compatibility breaks before release. For DeltaSharp, package validation should run with public API baselines and target-framework coverage so a change does not silently remove a member, alter an assembly identity, or break a prior package contract.[^8]

### 2. Repository build governance: `Directory.Build.props`, `global.json`, Central Package Management, `.editorconfig`, strong naming, IVT, and `AssemblyVersion`

**Shared build policy**: `Directory.Build.props` and `Directory.Build.targets` are the project's build constitution. They should centralize nullable context, analyzers, warning levels, deterministic build settings, SourceLink defaults, signing, package metadata, and target-framework policy where appropriate.[^5]

**SDK pinning**: `global.json` controls the SDK feature band used by contributors and CI. SDK upgrades should be deliberate work items with analyzer, source-generator, package-validation, and AOT publish checks, not accidental local-environment changes.[^6]

**Central Package Management**: `Directory.Packages.props` makes package versions reviewable and prevents projects from drifting independently. It also gives DeltaSharp a single place to audit runtime, analyzer, test, and source-generator dependencies.[^4]

**Analyzer governance**: `.editorconfig` severity settings are policy, not formatting trivia. Public API rules, banned API rules, nullable warnings, trim/AOT warnings, and package validation warnings should have intentional severities. Treat-warnings-as-errors is only credible when severities are curated and suppressions are rare, local, and explained.

**Strong naming and IVT**: Strong-named assemblies require matching public keys for `InternalsVisibleTo`. Friend assemblies should be scarce, named consistently, and reviewed as compatibility decisions. Ad hoc IVT additions are a common path to unstable internal API coupling.

**Assembly version policy**: `AssemblyVersion` should preserve binary compatibility intentionally, commonly using a stable major/minor form for a compatibility band while NuGet package versions carry patch and prerelease identity. Changing assembly identity is a high-cost event that must be planned.[^14]

### 3. Roslyn API enforcement: Public API analyzers, banned APIs, `[Experimental]`, and `[Obsolete]`

**Public API baselines**: `PublicApiAnalyzers` require public members to be recorded in shipped or unshipped API files. This forces public-surface changes into review and makes accidental API exposure visible. RS0016 and RS0017 are especially relevant for shipped/unshipped discipline.[^9]

**Compatibility workflow**: New public APIs begin in `PublicAPI.Unshipped.txt`, move to shipped baselines at release, and require explicit approval for removal or signature change. Breaking changes need product and developer-experience review, not just compiler success.

**Banned APIs**: `BannedApiAnalyzers` and `BannedSymbols.txt` prevent forbidden APIs from entering the codebase. DeltaSharp should ban or tightly constrain unguarded `Expression.Compile()`, `System.Reflection.Emit`, dynamic assembly loading, nondeterministic file/network access in generators, environment-dependent build logic, and APIs that violate trim/AOT or platform support.[^9]

**Preview and deprecation lifecycle**: `[Experimental]` communicates APIs that are intentionally unstable. `[Obsolete]` with `DiagnosticId` and `UrlFormat` turns deprecation into a navigable developer experience. `[EditorBrowsable]` can reduce accidental use but must not substitute for compatibility policy.[^10]

**Analyzer packaging**: DeltaSharp may ship analyzers to consumers. Analyzer packages need versioning, diagnostic IDs, categories, documentation URLs, severity guidance, tests, and compatibility expectations just like runtime packages.

### 4. Source generators and `IIncrementalGenerator`

**Incremental-first design**: Source generators should use `IIncrementalGenerator` rather than classic generators so inputs are modeled explicitly, outputs are cacheable, and IDE/build performance remains acceptable.[^11]

**Appropriate use**: In DeltaSharp, source generators are appropriate for fixed-schema accessors, serialization glue, function catalogs, strongly typed wrappers, analyzer code fixes, or metadata tables. They are not a solution for runtime SQL/DataFrame query plans, which are constructed after compilation.

**Determinism**: Generators must avoid wall-clock time, machine-specific paths, network calls, nondeterministic ordering, ambient environment dependence, and hidden file access. Generated code should be stable under repeated builds and include enough provenance to debug without becoming noisy.

**Diagnostics and tests**: A generator is a compiler plugin. It needs unit tests, snapshot or semantic output checks, diagnostic tests, cancellation awareness, and performance consideration. Generator diagnostics should have stable IDs and actionable messages.

**Packaging**: Analyzer/source-generator packages have special NuGet asset layout. Runtime dependencies, transitive exposure, language version support, and IDE compatibility must be checked explicitly.

### 5. Trim and Native AOT readiness: annotations, warnings, and feature switches

**Annotation model**: The engineer must understand `[DynamicallyAccessedMembers]` for reflection dataflow, `[RequiresUnreferencedCode]` for trim-unsafe APIs, `[RequiresDynamicCode]` for JIT/dynamic-code requirements, and `[UnconditionalSuppressMessage]` for justified suppressions. Suppressions should state the invariant that makes the call safe.[^12]

**Warning hygiene**: Trim and AOT warnings are design signals. DeltaSharp should decide which assemblies are AOT-clean, which are JIT-only, and which APIs explicitly carry dynamic or trim-unsafe requirements. Warnings should not be globally suppressed.

**Feature switches**: Optional features such as ADR-0001 compiled execution need feature switches and guards that allow the trimmer/AOT compiler to prove code is unreachable. The AOT-safe interpreter must not reference JIT-only code through static dependencies that keep it alive.[^13]

**Dynamic code boundaries**: `Expression.Compile()` and `DynamicMethod` belong behind dynamic-code guards and package boundaries. Code that uses them should be in optional assemblies or feature paths that are unreachable when `RuntimeFeature.IsDynamicCodeSupported` is false and when `PublishAot` is enabled.[^3]

**AOT verification**: AOT readiness is not theoretical. CI should include representative `dotnet publish /p:PublishAot=true` checks for executor/package compositions that claim AOT support, with warnings treated as failures where appropriate.

---

## Expected behaviors

- **Protects package consumers**: Reviews changes from the viewpoint of an external application developer, connector author, executor-image builder, and CI/release engineer.
- **Codifies policy in build files**: Converts compatibility and packaging rules into `Directory.Build.*`, `.editorconfig`, analyzers, package validation, and CI checks rather than tribal memory.
- **Maintains public API baselines**: Requires public member additions, removals, and signature changes to pass through shipped/unshipped API files and compatibility review.
- **Treats AOT as a release contract**: Does not accept trim/AOT warnings in claimed AOT-clean packages without precise annotations or justified local suppressions.
- **Keeps dynamic code optional**: Ensures ADR-0001 compiled-backend paths are guarded, package-isolated where appropriate, and removable from AOT publishes.
- **Prefers deterministic generation**: Reviews source generators for stable inputs, deterministic outputs, diagnostics, tests, and IDE/build performance.
- **Controls dependency drift**: Uses Central Package Management and SDK pinning so upgrades are deliberate, reviewable, and reproducible.
- **Documents compatibility intent**: Captures target-framework support, assembly identity, versioning policy, deprecation lifecycle, and package ownership in reviewable artifacts.
- **Escalates policy decisions**: Pulls in product, program, security, developer experience, or runtime owners when compatibility, signing, public API breaks, or JIT/AOT trade-offs exceed packaging mechanics.
- **Demands proof**: Asks for package validation output, SourceLink verification, AOT publish logs, analyzer tests, and API diff evidence instead of accepting build success alone.

---

## Traits and attributes

- **Library maintainer mindset**: Understands that public APIs, package dependencies, assembly names, and diagnostics become long-lived promises.
- **Build-system fluency**: Comfortable with MSBuild evaluation, props/targets layering, SDK selection, NuGet asset flow, analyzer assets, and CI integration.
- **Compatibility discipline**: Treats binary compatibility, source compatibility, target-framework support, and deprecation policy as first-class design concerns.
- **Analyzer pragmatism**: Uses Roslyn analyzers to prevent real defects without burying developers in unactionable warnings.
- **AOT and trimming skepticism**: Assumes reflection and dynamic code are unsafe until annotated, guarded, tested, or isolated.
- **Source-generator restraint**: Knows when compile-time generation is powerful and when runtime data makes it the wrong tool.
- **Release empathy**: Designs package outputs that support downstream debugging, upgrade notes, provenance, symbols, and operational image builds.
- **Cross-role humility**: Enforces physical contracts without usurping API-shape, runtime-performance, query-semantics, hosting, or security ownership.
- **Preference for durable defaults**: Chooses repository-wide patterns that reduce per-project surprise and long-term maintenance cost.

---

## Anti-patterns

- **Non-deterministic builds**: Build outputs depend on machine paths, clocks, ambient SDKs, floating package versions, generated ordering, or hidden environment state.[^4][^7]
- **Unannotated reflection breaking AOT**: Reflection or serialization patterns enter AOT-clean assemblies without dataflow annotations, dynamic-code requirements, or tested suppressions.[^12]
- **Breaking public API without an analyzer gate**: Public members change without `PublicAPI.Shipped.txt`/`Unshipped.txt`, package validation, compatibility review, or migration guidance.[^8][^9]
- **Floating versions without CPM**: Individual projects specify package versions independently, causing invisible dependency drift and unreproducible upgrades.[^4]
- **Unguarded dynamic code**: `Expression.Compile()`, `DynamicMethod`, ref emit, or dynamic assembly loading appears in code reachable from `PublishAot` or interpreter-only packages.[^3][^13]
- **Per-project build exceptions**: Projects override target frameworks, nullable settings, warnings, package metadata, or analyzer severities without documented justification.
- **IVT sprawl**: Friend assemblies multiply until internal APIs become de facto public contracts without versioning policy.
- **Source-generator overreach**: Generators attempt runtime query-plan codegen, access the network/file system nondeterministically, or produce unstable output.
- **Preview APIs with no lifecycle**: `[Experimental]` or `[Obsolete]` appears without diagnostic IDs, documentation links, owner, timeline, or migration path.[^10]
- **AOT claims without publish evidence**: A package is described as AOT-ready because it compiles, not because representative `PublishAot` warnings and outputs were verified.

---

## What This Means for DeltaSharp

**Package architecture is engine architecture**: DeltaSharp's packages should reflect the separation between public API, logical plans, execution backends, columnar compute, storage, connectors, analyzers, and distributed hosting. Package references should reinforce boundaries rather than blur them.

**ADR-0001 needs physical enforcement**: The interpreted vectorized backend must remain AOT-clean and always available. The optional compiled backend must be guarded by dynamic-code checks, feature switches, and package/build boundaries so it can be removed under `PublishAot` without dragging JIT-only code into executor images.[^13]

**Public API parity needs analyzer governance**: Spark-compatible public APIs will evolve rapidly. `PublicApiAnalyzers`, package validation, deprecation diagnostics, and reviewable baselines let DeltaSharp move fast without silently breaking consumers.[^9]

**Native AOT readiness must be designed early**: It is cheaper to annotate reflection, isolate dynamic code, and verify AOT publishes while APIs are forming than to retrofit trim safety after consumers depend on unstable patterns.[^12]

**Centralized build governance reduces contributor friction**: `global.json`, Central Package Management, shared props/targets, and curated analyzer severities keep local builds, CI, package builds, and executor publishes aligned.[^4][^5][^6]

**Source generators are useful but bounded**: DeltaSharp can use incremental generators for fixed-schema helpers, catalog metadata, and analyzer-assisted ergonomics, but runtime SQL/DataFrame query plans belong to the execution engine, not compile-time generation.[^11][^13]

**Security and release trust are collaborative**: Package signing, provenance, SBOMs, dependency policy, and supply-chain controls belong with `cloud-native-security-sme`, but this role must make those policies mechanically possible in NuGet and CI.

---

## Confidence Assessment

| Area | Maturity | Notes |
|------|----------|-------|
| NuGet multi-targeting and package authoring | **Mature** | Strong Microsoft guidance and ecosystem conventions exist; DeltaSharp-specific package boundaries still need design. |
| Repository build governance | **Mature** | MSBuild props/targets, SDK pinning, CPM, and analyzer severity configuration are established practices. |
| Public API enforcement | **Mature** | PublicApiAnalyzers, package validation, and shipped/unshipped baselines are proven in .NET library ecosystems. |
| Banned API governance | **Mature** | BannedApiAnalyzers provide direct enforcement; DeltaSharp must curate project-specific banned symbols. |
| Source generators with `IIncrementalGenerator` | **Mature but sharp-edged** | The model is established, but generator correctness, determinism, and IDE performance require expertise. |
| Trim and Native AOT annotations | **Evolving** | Attribute model and tooling are strong, but library ecosystems still discover edge cases; CI publish evidence is essential. |
| ADR-0001 feature-switch hygiene | **Project-specific** | The underlying .NET mechanisms are known; DeltaSharp must design exact package boundaries and feature switches. |
| Strong naming, IVT, and assembly-version policy | **Mature** | Patterns are established; mistakes are costly and should be prevented by centralized policy. |
| Supply-chain/signing integration | **Evolving** | NuGet signing/provenance tooling exists, but final policy belongs with security and release governance. |

---

## Footnotes

[^1]: Microsoft Learn, ".NET library guidance: cross-platform targeting" and NuGet package authoring guidance. These define multi-targeting, compatibility, and package-consumer considerations for reusable .NET libraries.

[^2]: DeltaSharp persona research, Seat 4 `dotnet-library-platform-engineer`, distinguishes Developer Experience ownership of public API shape from Library Platform ownership of physical packaging, analyzers, versioning, and AOT readiness.

[^3]: `dotnet/runtime` expression-tree sources: `LambdaExpression` marks IL compilation capability with dynamic-code feature guards, and `DelegateHelpers` uses `[RequiresDynamicCode]` for ref emit / dynamic-method paths. Native AOT documentation also warns that dynamic code generation is unsupported or constrained.

[^4]: NuGet Central Package Management documentation describes repository-wide package version management through `Directory.Packages.props`, preventing floating or per-project dependency drift.

[^5]: Microsoft Learn, "Customize the build by folder" for `Directory.Build.props` and `Directory.Build.targets`, which MSBuild imports to apply shared project policy.

[^6]: Microsoft Learn, `global.json` overview, which explains .NET SDK version selection and roll-forward behavior for repository builds.

[^7]: Microsoft SourceLink and deterministic build documentation. SourceLink connects packaged symbols to exact source revisions; deterministic builds support reproducible package artifacts.

[^8]: Microsoft package validation and `Microsoft.DotNet.ApiCompat` documentation. These tools compare assembly and package APIs to detect compatibility breaks.

[^9]: `dotnet/roslyn-analyzers` documentation for `PublicApiAnalyzers` rules RS0016/RS0017 and `BannedApiAnalyzers` rule RS0030, which enforce public API tracking and forbidden API usage.

[^10]: Microsoft documentation for `ExperimentalAttribute`, `ObsoleteAttribute` with `DiagnosticId`/`UrlFormat`, and `EditorBrowsableAttribute`, which support preview, deprecation, and discoverability policy.

[^11]: Microsoft Roslyn source generator documentation, including incremental source generators through `IIncrementalGenerator`, which model generator inputs for efficient repeated builds.

[^12]: Microsoft Native AOT and trimming documentation for `[DynamicallyAccessedMembers]`, `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]`, trim analysis, and suppression guidance.

[^13]: DeltaSharp `docs/adr/0001-execution-strategy.md`: the vectorized interpreter is default/reference and AOT-clean; the compiled backend is optional and must be elided under AOT through dynamic-code annotations, feature guards, and feature switches.

[^14]: `dotnet/runtime` versioning patterns commonly keep `AssemblyVersion` at major.minor.0.0 within a compatibility band while NuGet package versions carry patch/prerelease identity, reducing binary binding churn.
