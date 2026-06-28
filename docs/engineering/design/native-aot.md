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
