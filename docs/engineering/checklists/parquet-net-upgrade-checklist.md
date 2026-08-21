# Parquet.Net upgrade checklist

Bumping the pinned `Parquet.Net` version (`Directory.Packages.props`) is a **boundary-dependency change on
the attacker-facing decoder/encoder**. A minor bump has already shipped a breaking change within DeltaSharp's
used surface (6.0.3 → 6.1.0, #832/#837), and a bump can silently change the **emitted bytes** (compression,
encoding, `created_by`, footer metadata) even when the read path looks unaffected. Follow this checklist on
every bump.

## 1 · Before the bump

- [ ] Read the Parquet.Net release notes for every version between the current pin and the target, looking
      for changes to `DataField.ClrType`, logical-type annotations, the low-level `DataColumn` API, footer /
      `key_value_metadata` emission, default compression/encoding, and `created_by`.
- [ ] Note any change to a type DeltaSharp maps (string/binary → `ReadOnlyMemory<…>`, TIME, decimal, temporal
      annotations). A fail-open shape match (e.g. TIME shape-matching `int`/`bigint`, #832) is the highest-risk
      class — confirm DeltaSharp rejects on its **own** annotation, not on an unmapped CLR type.

## 2 · Run the gates

- [ ] `dotnet restore` then `dotnet build -c Release` (warnings-as-errors).
- [ ] `dotnet test` — in particular the storage codec suites. The **byte-level SHA-256 goldens**
      (`NestedParquetWriteGoldenTests`, #843) fail on any emitted-byte drift; the read-path suites
      (`NestedParquetReadTests`, `NestedParquetLeafTypeTests`, `ParquetSchemaMappingTests`, …) fail on any
      decode change.
- [ ] `dotnet format --verify-no-changes`.
- [ ] Update `packages.lock.json` (`dotnet restore --force-evaluate`) and review the transitive closure diff —
      confirm no native/unmanaged codec binary entered the closure (the managed-only decoder-RCE-surface
      invariant in `Directory.Packages.props`), and that the SBOM/SCA `expectedProjects` still covers the new
      transitive set.

## 3 · If the write goldens changed

A golden failure means the emitted bytes drifted. This is **expected** on a legitimate encoding change and
**not** an auto-heal:

- [ ] Confirm the drift is intentional (an encoding/compression/`created_by` change, not a correctness
      regression) by round-tripping the affected shapes through the reader (the #841 round-trip oracle).
- [ ] Regenerate the goldens deliberately: run the golden tests with `DELTASHARP_REGEN_WRITE_GOLDENS=1` (regen
      mode **emits the fresh constants and then fails** — it is never green), paste the emitted
      `(shape → sha256)` constants into `NestedParquetWriteGoldenTests.Goldens`, unset the env var, and commit
      them in a **reviewed** commit that explicitly states the encoding change and the Parquet.Net versions
      involved.
- [ ] The goldens are **version-coupled**: the hashed bytes embed the Parquet.Net `created_by` version string
      **and** DeltaSharp's own writer metadata, so regenerate against the version that actually ships. Never
      carry goldens generated at one pin onto a branch that lands a different pin — that is a guaranteed (and
      correct) golden failure; regenerate in the same commit that changes the pin.
- [ ] The goldens are a **cross-platform** byte commitment. Parquet.Net's default codec (managed Snappy) and
      metadata emission are expected to be platform/arch-independent; confirm the first multi-OS/arch CI run is
      green. A genuine platform divergence would itself be a legitimate catch by this gate — escalate it as a
      Parquet.Net determinism finding rather than papering over it with a per-platform golden.

## 4 · Sign-off

- [ ] All gates green on both TFMs (`net8.0;net10.0` for libraries).
- [ ] The bump commit references the Parquet.Net changelog entries that justify any golden regeneration.
- [ ] `Directory.Packages.props`' migration note updated with the new breaking changes (if any) for the next
      upgrader.

_Origin: #843 (nested Parquet write golden + version-bump gate), following the 6.0.3 → 6.1.0 read-path break
(#832/#837)._
