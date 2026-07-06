# NativeAOT executor publish profile

> **Status:** M1 representative profile for STORY-01.4.1 (#125). Grounded in
> [ADR-0014](../../adr/0014-target-framework-aot.md) and
> [ADR-0001](../../adr/0001-execution-strategy.md).

DeltaSharp's executable executor host lives in `src/DeltaSharp.Executor` and targets
`net10.0`. Its `PublishAot=true` setting is local to that executable project so public
libraries keep their library-oriented trim/AOT posture and are not forced into an
executable NativeAOT publish profile.

## CI verification

The supported CI runner publishes the representative Linux executor with:

```bash
dotnet publish src/DeltaSharp.Executor -c Release -r linux-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=true -warnaserror
```

The expected executable path is:

```text
src/DeltaSharp.Executor/bin/Release/net10.0/linux-x64/publish/DeltaSharp.Executor
```

The `aot.yml` workflow treats warnings as errors, so trim and AOT warnings (`IL2xxx` /
`IL3xxx`) fail the publish unless a future PR adds a narrow, documented suppression with
an ADR-backed justification.

## Supply chain — why the publish restore is not locked-mode

The AOT publish step intentionally does **not** pass `--locked-mode`. A `PublishAot` publish
pulls the RID-specific NativeAOT toolchain (`Microsoft.DotNet.ILCompiler` and
`runtime.linux-x64.Microsoft.DotNet.ILCompiler`), which are SDK-band-pinned and
Microsoft-signed and resolved through the `global.json` SDK pin. Forcing them into a
`packages.lock.json` would reintroduce the SDK-version coupling the repo deliberately avoids
for build-time toolchain packages (the same SDK-band coupling that makes `DeltaSharp.Engine`
/ `DeltaSharp.Storage` pin the analyzer-injected `Microsoft.NET.ILLink.Tasks` via
`VersionOverride` rather than let its SDK-tied version float in their committed lock files —
[#468](https://github.com/khaines/deltasharp/issues/468)). Third-party dependency pinning is
already enforced on the same commit by the required `build-test-format` job, which restores
the solution with `--locked-mode`. This NativeAOT job is a discard-only trim/AOT-regression
gate — the produced binary is asserted to exist and then thrown away, never signed,
distributed, or used as a release artifact.

## Local macOS smoke test

On an Apple Silicon development machine with the native toolchain installed, run:

```bash
dotnet publish src/DeltaSharp.Executor -c Release -r osx-arm64 -p:PublishAot=true -p:TreatWarningsAsErrors=true -warnaserror
```

The expected local executable path is:

```text
src/DeltaSharp.Executor/bin/Release/net10.0/osx-arm64/publish/DeltaSharp.Executor
```

NativeAOT requires the platform native linker and SDK. If a local machine lacks that
toolchain, use the Linux `aot.yml` workflow as the authoritative supported-environment
check.

## Follow-up

STORY-01.4.2 (#126), the dynamic-code feature switch and codegen elision work, is
deferred until the execution-backend code from ADR-0001 exists.
