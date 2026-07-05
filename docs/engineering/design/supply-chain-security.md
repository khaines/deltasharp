# Supply-chain security: scanning, SBOM, deterministic build, and policy

> **Status:** living document. Created with FEAT-00.3 —
> [STORY-00.3.1](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0031-secret-scanning-and-dependencysca-scanning)
> (secret + dependency/SCA scanning, #107),
> [STORY-00.3.2](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0032-sbom-and-deterministic-build-evidence)
> (SBOM + deterministic build, #108), and
> [STORY-00.3.3](https://github.com/khaines/deltasharp/blob/main/docs/planning/epics/EPIC-00-engineering-foundations.md#story-0033-signing-and-branch-protection-policy)
> (signing + branch-protection, #109). Grounded in
> [ADR-0014](../../adr/0014-target-framework-aot.md) (TFM/AOT),
> [ADR-0015](../../adr/0015-open-source-positioning.md) (Apache-2.0 / community trust), and
> checklists [03a](../checklists/03a-dotnet-coding-standards.md),
> [05](../checklists/05-security-checklist.md),
> [07](../checklists/07-privacy-checklist.md),
> [11](../checklists/11-documentation-support-checklist.md), and
> [13](../checklists/13-infrastructure-as-code-checklist.md). It is the source of truth for
> DeltaSharp's supply-chain controls — update it whenever a scan, threshold, SBOM setting,
> build/provenance setting, signing posture, or branch-protection setting changes.

DeltaSharp is an open-source data platform ([ADR-0015](../../adr/0015-open-source-positioning.md)),
so its supply chain is part of its public trust surface. This document ties together the
first layer of controls — secret detection, dependency-vulnerability visibility and gating,
SBOM output, deterministic-build provenance, artifact-signing posture, and protected-merge
requirements — and records exactly what is enforced today versus documented for later. The
controls satisfy the "Supply-chain integrity" section of the
[security checklist (05)](../checklists/05-security-checklist.md) and prepare later
Kubernetes and storage work for secure defaults.

## Scope and threat model

The controls here defend against a specific set of supply-chain risks:

- **Leaked credentials** committed to the repository (checklist 05 "Secrets and
  credentials").
- **Vulnerable dependencies** entering via NuGet, directly or transitively (checklist 05
  "Supply-chain integrity").
- **Opaque artifacts** — a consumer cannot tell what a package contains or whether it was
  built reproducibly from this source (checklist 05, ADR-0015 community trust).
- **Unprotected merges** — unreviewed or unverified changes reaching `main` (checklist 05
  "CI workflow permissions").

Out of scope for M1 (documented as future posture below): cryptographic package/image
signing automation, build-provenance attestations, and runtime admission verification.
Driver/executor mTLS, tenant isolation, and storage authorization are owned by later
features and their checklists (05, 14, 18); this document does not restate them.

## Control map

| # | Control | Mechanism | Enforcement today | Config / code |
| --- | --- | --- | --- | --- |
| 107a | Secret scanning | GitHub-native secret scanning + push protection (primary); in-repo tripwire (testable) | Enabled; `secret-scan` CI job fails on un-allowlisted match | repo settings; [`security.yml`](../../../.github/workflows/security.yml), [`tools/security/secret-scan.py`](../../../tools/security/secret-scan.py) |
| 107b/c | Dependency / SCA scanning | Build-time NuGet audit + `sca` gate + PR `dependency-review` | HIGH/CRITICAL fail the build **and** the `sca` gate; `dependency-review` fails on high | [`Directory.Build.props`](../../../Directory.Build.props), [`security.yml`](../../../.github/workflows/security.yml), [`dependency-review.yml`](../../../.github/workflows/dependency-review.yml), [`tools/security/sca-gate.py`](../../../tools/security/sca-gate.py) |
| 107d | Suppression hygiene | Allowlists carrying scope/reason/owner/expiry, expiry-enforced | Malformed/expired waivers fail closed | [`sca-policy.json`](../../../tools/security/sca-policy.json), [`secret-scan-allowlist.json`](../../../tools/security/secret-scan-allowlist.json) |
| 108a/d | SBOM | CycloneDX per packable project, retained artifact | `sbom` CI job generates + uploads | [`security.yml`](../../../.github/workflows/security.yml), [`.config/dotnet-tools.json`](../../../.config/dotnet-tools.json) |
| 108b | Deterministic build evidence | Deterministic compile + double-build hash comparison | `sbom` job fails on non-deterministic output | [`Directory.Build.props`](../../../Directory.Build.props), [`security.yml`](../../../.github/workflows/security.yml) |
| 108c | Source provenance | In-box SourceLink + repository metadata | `pack.yml` asserts SourceLink resolves to the repo | [`Directory.Build.props`](../../../Directory.Build.props), [`pack.yml`](../../../.github/workflows/pack.yml) |
| 109a | Required checks | Branch protection on `main` | `build-test-format`, `dco`, `coverage` required; security scans documented for post-merge wiring | GitHub branch protection (see below) |
| 109b | Signing posture | DCO today; package/image signing documented future | DCO enforced via `dco` check | [`dco.yml`](../../../.github/workflows/dco.yml), this doc |
| 109c | Release traceability | This baseline referenced by a future release workflow | Documented | this doc |
| 109d | Bypass control | `enforce_admins=true`; protection editable only by repo admins | No merge bypass; admin change limited to maintainers | GitHub branch protection, [`GOVERNANCE.md`](../../../GOVERNANCE.md), [`.github/CODEOWNERS`](../../../.github/CODEOWNERS) |

## 1. Secret scanning (STORY-00.3.1, #107a)

### Primary control: GitHub-native secret scanning + push protection

The authoritative control is **GitHub secret scanning with push protection**, enabled on the
repository (`security_and_analysis.secret_scanning = enabled`,
`secret_scanning_push_protection = enabled`). It is org-managed, ships a broad provider
pattern set, and **blocks a push** that contains a recognized credential before it reaches
`main`. This is the control that protects real secrets; it needs no code in this repository.

### In-repo tripwire: `tools/security/secret-scan.py`

GitHub-native scanning results are not surfaced as a *check* on forks/clones, and the
acceptance criterion requires that a **safe fixture be reported by a scanner** on every run.
So we add a small, self-contained CI tripwire — [`tools/security/secret-scan.py`](../../../tools/security/secret-scan.py),
run by the `secret-scan` job in [`security.yml`](../../../.github/workflows/security.yml) —
that:

- scans every git-tracked file for a **small set of high-signal provider patterns** (AWS
  access-key ids, PEM private-key blocks, GitHub tokens, Slack tokens, Google API keys) plus
  a **project canary** (`DELTASHARP_TEST_SECRET_…`);
- **masks** every match in its output, so it never echoes a credential into CI logs
  (checklist 05: secrets must not appear in logs);
- fails the job on any **un-allowlisted** match; and
- ships a `--selftest` mode that proves every detector still fires (against fragment-built
  in-memory samples) and that the committed fixture is detected — failing closed if a regex
  regresses or the fixture is removed.

**Why a stdlib scanner and not a third-party action (e.g. gitleaks).** The primary control
is already GitHub-native; the tripwire only needs to be *testable, offline, and
zero-dependency*. A Python-3 standard-library script matches the repository's established
self-contained-tooling convention — [`tools/dco-check.sh`](../../../tools/dco-check.sh),
[`tools/coverage/coverage-gate.py`](../../../tools/coverage/coverage-gate.py), and
[`dco.yml`](../../../.github/workflows/dco.yml) which "uses no third-party action so there is
no extra supply-chain surface to review" — and adds no new action to pin or audit. It is
intentionally narrow (near-zero false positives); broad and entropy-based detection remains
GitHub-native's job.

### Safe fixture

[`tools/security/testdata/secret-scan-fixture.txt`](../../../tools/security/testdata/secret-scan-fixture.txt)
holds **non-functional, obviously-fake** values: the canonical AWS-documentation example
access-key id (`AKIA…EXAMPLE`, recognized broadly as a non-secret so it does not trip push
protection) and the project canary. These grant access to nothing. On a normal scan the
tripwire **reports** the fixture as an expected, masked match; `--selftest` additionally
asserts the fixture is detected. This is how the AC ("a safe fixture is reported by a secret
scanner without exposing real creds") is proven on every run.

### Suppression / allowlist format (AC #107d)

A match is suppressed only by an entry in
[`secret-scan-allowlist.json`](../../../tools/security/secret-scan-allowlist.json). Every
entry **must** carry `path` (an fnmatch glob, repo-relative), `scope`, `reason`, `owner`, and
`expiry` (`YYYY-MM-DD`); `rules` (detector ids, or `["*"]`) and `reviewCriteria` are
optional. A **malformed** entry fails the scan closed, and an **expired** entry stops
suppressing — the match re-surfaces and fails the scan — so waivers cannot silently rot. A
real leaked secret is **rotated, never allowlisted**.

## 2. Dependency / SCA scanning (STORY-00.3.1, #107b, #107c)

DeltaSharp uses **Central Package Management** ([`Directory.Packages.props`](../../../Directory.Packages.props))
so every dependency version is visible to review and tooling. Three layers report and gate
vulnerabilities.

### Layer 1 — build-time NuGet audit

The .NET SDK's NuGet audit runs during restore/build and emits `NU1901` (low), `NU1902`
(moderate), `NU1903` (high), and `NU1904` (critical) warnings for known-vulnerable packages.
[`Directory.Build.props`](../../../Directory.Build.props) sets `TreatWarningsAsErrors=true`
and lists `NU1901;NU1902` in `WarningsNotAsErrors`, so **LOW/MODERATE advisories are
non-blocking warnings while HIGH/CRITICAL advisories break the build** in the
`build-test-format` job.

### Layer 2 — the `sca` gate

The `sca` job in [`security.yml`](../../../.github/workflows/security.yml) runs
`dotnet list package --vulnerable --include-transitive --format json` and pipes the report to
[`tools/security/sca-gate.py`](../../../tools/security/sca-gate.py). `dotnet list package
--vulnerable` always exits 0 (it is a reporting command), so **the gate — not the CLI — is
what turns a finding at or above the threshold into a red check**, with an explicit report
carrying **severity and package identity** for each finding (AC #107b) plus the advisory URL.
It complements Layer 1 by producing the reviewable report and enforcing the threshold
independently, and it runs a `--selftest` first (mirroring the coverage gate) to prove its
own threshold/suppression/expiry logic before trusting it on real data.

**Runs on push, PR, and nightly.** Besides every push to `main` and every PR, the `sca` job
also runs on a **nightly `schedule`** (cron `27 6 * * *`). Because vulnerability data is
external and time-varying, a CVE can be disclosed against a **pinned, unchanged** dependency
between pushes; the nightly re-audit surfaces it within a day instead of waiting for the next
commit. (`secret-scan` and `sbom` are skipped on the schedule — they are pure functions of
committed source, so a nightly run would be identical.)

**Fail-closed on an empty/partial report (provenance).** `dotnet list package --vulnerable`
exits 0 with an empty finding set for BOTH a genuinely clean run and an unreachable advisory
DB / dropped project / truncated JSON, so "no findings" alone is fail-**open**. Mirroring the
coverage gate's `expectedAssemblies` check, the SCA gate asserts **report provenance**: a
healthy report lists a `path` for every solution project even when clean, so an empty/absent
project list fails closed, and — using the `expectedProjects` allowlist in
[`sca-policy.json`](../../../tools/security/sca-policy.json) — a report **missing** any
expected project (truncated/partial/dropped) also fails closed, naming the missing project.
Only a report with all expected projects present and zero blocking findings passes.
`expectedProjects` is kept in sync with the solution (adding a project is a deliberate
governance event, like the coverage `expectedAssemblies` allowlist).

### Layer 3 — PR `dependency-review`

[`dependency-review.yml`](../../../.github/workflows/dependency-review.yml) runs GitHub's
`dependency-review-action` (pinned by SHA) on pull requests. Using the dependency graph it
diffs base↔head and **fails on newly introduced high-severity vulnerabilities**
(`fail-on-severity: high`), catching risk at the moment a dependency is added. It also
**surfaces license information** for the changed dependencies in the job summary, but does
**not gate** on licenses: no `deny-licenses`/`allow-licenses` policy is configured, so a
license change is reported for review, not blocked. (If a license *gate* is wanted later,
add a `deny-licenses`/`allow-licenses` list to the action and update this section.)
The repository is public, so dependency review needs no GitHub Advanced Security licence; it
does require the repository **dependency graph**, which is **enabled** on this repo (turned on
together with **Dependabot alerts** — `PUT /repos/khaines/deltasharp/vulnerability-alerts`).
Without it the dependency-review API returns `403` and the job fails, so the graph must stay
enabled.

### Threshold (AC #107c)

The documented failure threshold is **High and above** (High, Critical). It is set in
[`sca-policy.json`](../../../tools/security/sca-policy.json) (`failOnSeverity: "high"`),
matched by `dependency-review`'s `fail-on-severity: high`, and consistent with the build-time
audit posture (HIGH/CRITICAL break the build; LOW/MODERATE are warnings). An **unknown**
severity ranks above Critical, so an un-triaged advisory fails closed.

### SCA suppression format (AC #107d)

An accepted or false-positive finding is waived in the `suppressions` array of
[`sca-policy.json`](../../../tools/security/sca-policy.json). Every entry **must** carry
`package`, `scope`, `reason`, `owner`, and `expiry`; `versions` (`"*"` or a comma-separated
exact list), `advisory` (pin to one URL), and `reviewCriteria` are optional. As with the
secret allowlist, malformed entries fail closed and **expired suppressions stop suppressing**.
Example:

```jsonc
{
  "package": "Some.Transitive.Package",
  "versions": "1.2.3",
  "advisory": "https://github.com/advisories/GHSA-xxxx-xxxx-xxxx",
  "scope": "DeltaSharp.Core (transitive via Foo)",
  "reason": "Not reachable from DeltaSharp code paths; upstream fix pending.",
  "owner": "@khaines",
  "expiry": "2026-01-31",
  "reviewCriteria": "Remove when Foo >= 2.0 (fixed) is adopted."
}
```

## 3. SBOM (STORY-00.3.2, #108a, #108d)

The `sbom` job in [`security.yml`](../../../.github/workflows/security.yml) generates a
**CycloneDX** software bill of materials for each **packable** project — `DeltaSharp.Core`
and `DeltaSharp.Abstractions` (both `IsPackable=true`; `DeltaSharp.Engine` and
`DeltaSharp.Executor` are not packaged).

**Tool choice.** We use the OWASP **CycloneDX .NET tool** (`dotnet CycloneDX`), pinned to
`6.2.0` in [`.config/dotnet-tools.json`](../../../.config/dotnet-tools.json) and restored
with `dotnet tool restore`. It is the standard SBOM generator for .NET, integrates directly
with the SDK, and reads the resolved dependency graph (emitting SHA-512 package hashes) — so
no third-party GitHub Action is added to the supply-chain surface, consistent with the
tooling convention noted above. It emits CycloneDX spec **1.7** JSON.

**Naming convention (AC #108d).** One SBOM per packable project, named
`<PackageId>-<Version>.cdx.json` (version derived from `<VersionPrefix>` in
`Directory.Build.props`, so the SBOM name tracks the real package version):

- `DeltaSharp.Core-0.1.0.cdx.json`
- `DeltaSharp.Abstractions-0.1.0.cdx.json`

They are uploaded together as the **`sbom-cyclonedx`** workflow artifact and **retained for
90 days** (`retention-days: 90`), alongside `reproducible-build-evidence.md` (below). The
`sbom` job asserts each file is a valid CycloneDX BOM with a components list before upload,
so a broken generator fails the job rather than shipping an empty SBOM.

## 4. Deterministic build and provenance (STORY-00.3.2, #108b, #108c)

DeltaSharp's deterministic-build and SourceLink settings were established with FEAT-01.6
(see [packaging.md](packaging.md)); this section records the **evidence** that satisfies
#108 and does not re-plumb settings that already exist.

### Settings (already configured, in `Directory.Build.props`)

- `Deterministic=true` (always on) and `ContinuousIntegrationBuild=true` when
  `GITHUB_ACTIONS=true` — the compiler normalizes source paths and emits a hash-derived MVID.
- `PublishRepositoryUrl=true`, `EmbedUntrackedSources=true`.
- `IncludeSymbols=true` with `SymbolPackageFormat=snupkg`.
- `RepositoryUrl`/`RepositoryType` and `PackageLicenseExpression=Apache-2.0` metadata.

### Reproducible-build evidence (AC #108b)

The `sbom` job compiles each packable assembly **twice** from the same commit
(`--no-incremental`, separate output dirs) for both target frameworks (`net8.0`, `net10.0`)
and compares the **SHA-256** of each produced DLL. Because the build is deterministic,
identical inputs produce **byte-identical** assemblies; a mismatch fails the job. The hashes
and the build settings are written to `reproducible-build-evidence.md` and retained in the
`sbom-cyclonedx` artifact, providing the "documented evidence of reproducible assembly
metadata inputs" the AC asks for. (Verified locally on both TFMs: repeated builds are
byte-identical.)

### Source provenance / SourceLink (AC #108c)

**SourceLink is provided in-box by the .NET 8+ SDK**, so DeltaSharp references **no**
`Microsoft.SourceLink.*` package — a deliberate decision (see the `lane:01.6` comment in
[`Directory.Build.props`](../../../Directory.Build.props) and
[packaging.md](packaging.md)) that avoids the SDK-version-tied build-package coupling the
repository already hit with `Microsoft.NET.ILLink.Tasks`, keeping the SDK pinned in one place
([`global.json`](../../../global.json)). A CI build emits a SourceLink map resolving source to
`https://raw.githubusercontent.com/khaines/deltasharp/<commit>/…`, and
[`pack.yml`](../../../.github/workflows/pack.yml) already **asserts** (in its pack build) that
the generated `*.sourcelink.json` map resolves to the repository. Combined with
`PublishRepositoryUrl` and the repository metadata, the artifacts carry the source-provenance
fields the AC expects.

## 5. Artifact-signing posture (STORY-00.3.3, #109b)

Nothing is published yet, so signing is intentionally staged. The interim posture, owner, and
activation criteria (AC #109b):

| Layer | Today (interim) | Future posture | Owner | Activation criterion |
| --- | --- | --- | --- | --- |
| **Commit provenance** | **DCO sign-off** enforced by the `dco` check ([`dco.yml`](../../../.github/workflows/dco.yml), [`tools/dco-check.sh`](../../../tools/dco-check.sh)) | Optionally require signed commits (`required_signatures`, currently **false**) | @khaines (founding maintainer / TSC) | A second maintainer joins and key management is agreed |
| **NuGet package signing** | Unsigned; built deterministically with SourceLink + SBOM | Author-sign `.nupkg`/`.snupkg` in a release workflow (repository signature added by NuGet.org on publish) | @khaines (release steward) | First public release to NuGet.org; signing certificate/Trusted Signing provisioned |
| **Container image signing** | No images published | Sign operator/driver/executor images (cosign/Sigstore); cluster admission verifies signatures (checklist 05) | @khaines | First container image publish (ADR-0014 NativeAOT executor images) |
| **Build provenance** | Reproducible-build evidence + SBOM (this doc) | SLSA build-provenance attestations (e.g. `actions/attest-build-provenance`) | @khaines | Release workflow introduced |

Until automation lands, the trust baseline is: **DCO-signed commits**, **deterministic
builds with SourceLink**, and a **retained SBOM** per package.

## 6. Branch-protection policy (STORY-00.3.3, #109a, #109d)

Branch protection on `main` (verified **2026-07-04** via
`gh api repos/khaines/deltasharp/branches/main/protection`):

| Setting | Value |
| --- | --- |
| Required status checks | `build-test-format`, `dco`, `coverage` (`strict: false`) |
| `enforce_admins` | **true** (rules apply to admins; no merge bypass) |
| `required_linear_history` | true |
| `required_conversation_resolution` | true |
| `allow_force_pushes` / `allow_deletions` | false / false |
| `required_pull_request_reviews` | `dismiss_stale_reviews: true`, `required_approving_review_count: 0`, `require_code_owner_reviews: false` |
| `required_signatures` | false (DCO is the commit attestation today) |
| Merge methods (repo) | **squash-only** (`allow_squash_merge: true`; merge-commit and rebase disabled), `delete_branch_on_merge: true` |

**Required checks (AC #109a).** CI (`build-test-format`) and DCO (`dco`) are required, and
`coverage` was added to the required set (FEAT-00.2 post-merge wiring, #456). The FEAT-00.3
**security scans** are **not yet required checks**. Their triggers differ: `sca`,
`secret-scan`, and `sbom` (in `security.yml`) run on **every PR and every push to `main`**
(and `sca` also on a nightly `schedule`), while `dependency-review` (in
`dependency-review.yml`) is **PR-only** — it needs a base↔head range to diff. A status check
can only be added to branch protection **after it has first run on `main`**, which happens
once this change merges. Promoting them is therefore a **documented post-merge step** (the
same pattern used for `coverage`):

```bash
gh api -X PATCH repos/khaines/deltasharp/branches/main/protection/required_status_checks \
  --input - <<'JSON'
{ "strict": false, "checks": [
  {"context": "build-test-format"}, {"context": "dco"}, {"context": "coverage"},
  {"context": "sca"}, {"context": "secret-scan"}, {"context": "sbom"},
  {"context": "dependency-review"} ] }
JSON
```

**Code-owner review.** [`.github/CODEOWNERS`](../../../.github/CODEOWNERS) routes review
requests (default owner `@khaines`), but branch protection does **not** currently require
code-owner approval (`required_approving_review_count: 0`) — an intentional single-maintainer
exception documented in
[label-taxonomy.md](../../planning/label-taxonomy.md#branch-protection-and-required-review),
to be tightened when a second maintainer joins.

**Bypass control (AC #109d).** With `enforce_admins=true` there is **no merge bypass** of
required checks — the rules apply even to administrators. The only actors who can *modify or
disable* branch protection are **repository administrators**, which today is the founding
maintainer **@khaines** (the documented owner in [`.github/CODEOWNERS`](../../../.github/CODEOWNERS)
and the TSC role in [`GOVERNANCE.md`](../../../GOVERNANCE.md)). Bypass/administration is thus
limited to a documented maintainer, and expands only through GOVERNANCE's maintainer process.

## 7. Traceability for a future release workflow (STORY-00.3.3, #109c)

When a release workflow is introduced, it references this baseline and must:

1. **SBOM** — generate and attach the CycloneDX SBOM per released package using the naming
   convention in §3, retained with the release.
2. **Deterministic build + provenance** — build with the settings in §4, verify the
   reproducible-build evidence, and confirm SourceLink resolves to the release commit.
3. **Signing** — sign `.nupkg`/`.snupkg` (and any container images) per the activation
   criteria in §5, and publish build-provenance attestations.
4. **Gating** — run the same `sca`, `secret-scan`, `dependency-review`, and `build-test-format`
   checks against the release ref before publishing.

Each release artifact is thereby traceable to a commit, an SBOM, reproducibility evidence,
and (once activated) a signature.

## Local reproduction

Every gate runs locally with no network beyond NuGet:

```bash
# Dependency / SCA gate (High+ fails):
dotnet restore DeltaSharp.sln --locked-mode
dotnet list DeltaSharp.sln package --vulnerable --include-transitive --format json > sca-report.json
python3 tools/security/sca-gate.py --selftest
python3 tools/security/sca-gate.py --input sca-report.json

# Secret scan (un-allowlisted match fails; the safe fixture is reported, masked):
python3 tools/security/secret-scan.py --selftest
python3 tools/security/secret-scan.py

# SBOM (per packable project):
dotnet tool restore
dotnet CycloneDX src/DeltaSharp.Core/DeltaSharp.Core.csproj \
  --output sbom --filename DeltaSharp.Core-0.1.0.cdx.json --output-format Json \
  --set-name DeltaSharp.Core --set-version 0.1.0 --set-type Library \
  --exclude-test-projects --configuration Release

# Reproducible-build check (byte-identical DLLs across repeated builds):
dotnet build src/DeltaSharp.Core/DeltaSharp.Core.csproj -c Release -f net8.0 --no-restore --no-incremental -o repro/a
dotnet build src/DeltaSharp.Core/DeltaSharp.Core.csproj -c Release -f net8.0 --no-restore --no-incremental -o repro/b
sha256sum repro/a/DeltaSharp.Core.dll repro/b/DeltaSharp.Core.dll   # the two hashes must match
```

## References

- [ADR-0014 — Target framework and AOT posture](../../adr/0014-target-framework-aot.md)
- [ADR-0015 — Open-source positioning](../../adr/0015-open-source-positioning.md)
- [05 — Security Checklist](../checklists/05-security-checklist.md) ("Supply-chain integrity")
- [07 — Privacy Checklist](../checklists/07-privacy-checklist.md),
  [11 — Documentation Support Checklist](../checklists/11-documentation-support-checklist.md),
  [13 — Infrastructure as Code Checklist](../checklists/13-infrastructure-as-code-checklist.md),
  [03a — .NET Coding Standards](../checklists/03a-dotnet-coding-standards.md)
- [packaging.md](packaging.md) — NuGet packaging, SourceLink, deterministic builds
- [quality-gates.md](quality-gates.md) — CI gates and required checks
- [`SECURITY.md`](../../../SECURITY.md), [`GOVERNANCE.md`](../../../GOVERNANCE.md),
  [`.github/CODEOWNERS`](../../../.github/CODEOWNERS)
- [`.github/workflows/security.yml`](../../../.github/workflows/security.yml),
  [`.github/workflows/dependency-review.yml`](../../../.github/workflows/dependency-review.yml),
  [`.github/workflows/pack.yml`](../../../.github/workflows/pack.yml),
  [`.github/workflows/dco.yml`](../../../.github/workflows/dco.yml)
