#!/usr/bin/env bash
#
# NuGet lockfile guard (issue #831).
#
# Detection + reporting half of the `lockfile-guard` CI gate
# (.github/workflows/nuget-lockfile-guard.yml). The workflow runs the restore
#
#     dotnet restore DeltaSharp.sln --force-evaluate
#
# FIRST; this script assumes that already happened and only inspects the result.
# It never restores, never builds, and never commits.
#
# It fails (exit 1) when any of the following is true:
#
#   1. drift        — a committed `packages.lock.json` changed (or a new one
#                     appeared) after the force-evaluate restore. This is the
#                     Dependabot multi-TFM bug: the updater regenerates a
#                     multi-targeted project's lock file for a SINGLE target
#                     framework and silently drops the other TFM section.
#   2. removal      — a lock file listed in tools/ci/expected-lockfiles.txt is no
#                     longer tracked, or is missing from the working tree.
#   3. manifest lag — a tracked lock file is NOT listed in the manifest.
#
# Checks 2 and 3 make drift detection FAIL CLOSED: without them a pull request
# could delete every `packages.lock.json` (and/or .gitignore them), leave
# `git status` clean, and turn the check green while removing the very pinning
# it exists to protect.
#
# OUT OF SCOPE — disabling pinning through the build definition itself (for
# example `RestorePackagesWithLockFile=false`). An earlier revision tried to
# detect it, first by grepping the XML and then by evaluating the resolved
# MSBuild property, and neither is robust: MSBuild evaluates properties
# differently during `restore` than during `-getProperty`, so an opt-out
# conditioned on the restore session passes the probe yet still disables
# pinning at restore time. Rather than ship a check that gives a false sense of
# security, this guard covers only what it covers robustly; a build-definition
# change that disables pinning is visible in the pull-request diff and is left
# to human code review. The guard is advisory.
#
# Run locally (from anywhere in the repo):
#
#     dotnet restore DeltaSharp.sln --force-evaluate
#     tools/ci/lockfile-guard.sh
#
# Regression-test the guard itself (git only — no dotnet, no network):
#
#     tools/ci/lockfile-guard.sh --selftest
#
# Side effect: before rendering the diff the guard runs `git add
# --intent-to-add` on the lock-file pathspec so a brand-new (untracked) lock
# file shows up as an addition in `git diff` instead of an empty patch.
# Intent-to-add stages no content, and outside GitHub Actions the guard undoes
# exactly the markers it created (`git reset` on the paths that were untracked
# beforehand — never a path the developer had staged), so a local run leaves the
# developer's index as it found it.
#
set -euo pipefail

# One pathspec for detection, diffing, staging and the printed remediation
# command, so they can never disagree. `glob` makes `**` match across
# directories; `top` anchors at the repository root, so the guard behaves the
# same from a subdirectory and does not miss a root-level lock file.
readonly LOCKFILE_PATHSPEC=':(glob,top)**/packages.lock.json'
readonly MANIFEST_REL='tools/ci/expected-lockfiles.txt'
readonly RESTORE_CMD='dotnet restore DeltaSharp.sln --force-evaluate'

# Markdown fence for untrusted content. Five backticks (plus the sanitizer
# below) so lock-file content or a path containing a ``` run cannot terminate
# the block and inject markup into the job summary.
readonly FENCE='`````'

# GitHub truncates a step summary at 1 MiB. Keep the (unbounded, PR-controlled)
# patch well under that; `--stat` is emitted first and is always small.
DIFF_BYTE_LIMIT="${LOCKFILE_GUARD_DIFF_BYTES:-60000}"

usage() {
  cat <<EOF
Usage: tools/ci/lockfile-guard.sh [--selftest] [--help]

  (no args)   Verify committed packages.lock.json files, assuming a
              '${RESTORE_CMD}' has already run. Exit 0 = in sync.
  --selftest  Run the guard's own regression tests against throwaway git
              repositories. Exit 0 = all tests pass.
EOF
}

# Neutralize a markdown-fence breakout: a line whose first non-space characters
# are a run of three or more backticks could close the code block that wraps
# untrusted content. Only a LEADING run (after at most three spaces, per
# CommonMark) can close a fence; the optional single leading character absorbs a
# unified-diff marker (' ', '+', '-') so a fence inside diff context is rewritten
# too. Combined with the five-backtick fence above, injected markup cannot escape.
sanitize_fence() {
  sed -e "s/^\\([-+ ]\\{0,1\\}\\)\\( \\{0,3\\}\\)\`\\{3,\\}/\\1\\2'''/"
}

# Neutralize GitHub workflow-command injection: the Actions runner parses lines
# such as `::error::…`, `::add-mask::…` or `::stop-commands::…` on BOTH stdout
# and stderr, and it tolerates leading whitespace — so a PR-controlled path (a
# directory literally named `::error::pwned`) or manifest entry could forge the
# annotations reviewers rely on. Every untrusted line is therefore printed
# behind a NON-whitespace bullet, which makes it unparseable as a command.
#
# The runner's line reader also splits on a bare CR, and neither `git ls-files`
# nor the manifest C-quotes control bytes (`git status` does), so a path
# containing `benign<CR>::error::…` would otherwise start a fresh line at column
# 0 behind the bullet. CR is therefore rendered as a literal `\r`, the way git
# quotes it itself.
#
# (Diff hunks are exempt: unified-diff output always begins with `+`, `-`, ` `,
# `@`, `d` or `i`, so their first column is never `:`.)
mark_untrusted() {
  LC_ALL=C sed -e "s/$(printf '\r')/\\\\r/g" -e 's/^/  - /'
}

# ---------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------

# Lock files that changed, appeared, or were deleted after the restore.
#
# Two commands, because neither alone is both complete and precise:
#   * `git status --porcelain -uall` sees modified, deleted and new (untracked)
#     lock files. `-uall` matters: without it a brand-new directory is reported
#     collapsed (`?? src/New/`) and the lock file inside it would be missed.
#     It does NOT see .gitignore'd files.
#   * `git ls-files --others --ignored` enumerates lock files hidden by
#     .gitignore, so an ignore rule cannot make the guard pass. (`git status
#     --ignored=matching` is unusable here: it reports whole ignored DIRECTORIES
#     such as `obj/` that merely *could* contain a match, which is a false
#     positive on every built tree.)
# `core.quotePath=false` keeps non-ASCII paths copy-pasteable in both.
detect_drift() {
  local changed ignored
  changed="$(git -c core.quotePath=false status --porcelain --untracked-files=all -- "$LOCKFILE_PATHSPEC")"
  ignored="$(git -c core.quotePath=false ls-files --others --ignored --exclude-standard -- "$LOCKFILE_PATHSPEC" | sed 's|^|!! |')"
  printf '%s\n%s\n' "$changed" "$ignored" | grep -v '^[[:space:]]*$' || true
}

# Lock files currently tracked by git, one per line, NUL-read so no path needs
# quoting.
tracked_lockfiles() {
  git ls-files -z -- "$LOCKFILE_PATHSPEC" | tr '\0' '\n' | LC_ALL=C sort
}

# The committed expectation: which lock files MUST exist. Comments and blank
# lines are ignored. `sort -u`, because `comm` compares sorted UNIQUE sets: a
# duplicated manifest line would otherwise be reported as missing/unpinned.
manifest_lockfiles() {
  sed -e 's/#.*//' -e 's/[[:space:]]*$//' "$MANIFEST_REL" |
    grep -v '^$' | LC_ALL=C sort -u
}

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

# Render a bulleted, fence-sanitized block of untrusted lines inside a fenced
# code block.
emit_block() {
  printf '%s\n' "$FENCE"
  printf '%s\n' "$1" | sanitize_fence | mark_untrusted
  printf '%s\n' "$FENCE"
  printf '\n'
}

# Backticks below are MARKDOWN code spans inside single-quoted printf format
# strings, never command substitution.
# shellcheck disable=SC2016
emit_summary() {
  local drift="$1" missing="$2" extra="$3"
  local changed diff_stat diff_patch sdk

  printf '## NuGet lockfile guard failed\n\n'

  if [ -n "$drift" ]; then
    changed="$(printf '%s\n' "$drift" | sed 's/^...//')"
    printf 'The following `packages.lock.json` file(s) are **out of sync** with the project files:\n\n'
    emit_block "$changed"
    printf 'This commonly happens when Dependabot bumps a package on a multi-targeted\n'
    printf 'project and regenerates the lock file for only one target framework\n'
    printf '(dropping the other TFM section) — see issue #831.\n\n'
  fi

  if [ -n "$missing" ]; then
    printf 'The following `packages.lock.json` file(s) are **missing** — they are listed in\n'
    printf '`%s` but are no longer tracked by git (or not present in the\n' "$MANIFEST_REL"
    printf 'working tree). Removing a lock file removes dependency pinning:\n\n'
    emit_block "$missing"
    printf 'If a project was legitimately removed, delete its entry from `%s` in the same pull request.\n\n' "$MANIFEST_REL"
  fi

  if [ -n "$extra" ]; then
    printf 'The following tracked `packages.lock.json` file(s) are **not listed** in\n'
    printf '`%s`. Add them (sorted) in the same pull request so the\n' "$MANIFEST_REL"
    printf 'guard keeps protecting them:\n\n'
    emit_block "$extra"
  fi

  printf '**To fix, run locally and commit the result:**\n\n'
  printf '```bash\n'
  printf '%s\n' "$RESTORE_CMD"
  printf "git add -- '%s'\\n" "$LOCKFILE_PATHSPEC"
  printf "git commit -s -m 'deps: regenerate NuGet lock files'\\n"
  printf '```\n\n'

  # SDK breadcrumb: lock content is SDK-band sensitive, so a local repro that
  # disagrees with CI usually differs here first.
  if command -v dotnet >/dev/null 2>&1; then
    sdk="$(dotnet --version 2>/dev/null || echo 'unknown')"
  else
    sdk='not installed'
  fi
  printf '_.NET SDK used by this run: `%s` (CI pins it via `global.json`)._\n\n' "$sdk"

  if [ -n "$drift" ]; then
    diff_stat="$(git -c core.quotePath=false --no-pager diff --stat -- "$LOCKFILE_PATHSPEC" 2>/dev/null || true)"
    if [ -n "$diff_stat" ]; then
      printf '### Diff stat\n\n'
      emit_block "$diff_stat"
    fi

    # Bounded: the patch is PR-controlled content. `head -c` closes the pipe
    # early, so pipefail is disabled inside this subshell only.
    diff_patch="$(
      set +o pipefail
      git -c core.quotePath=false --no-pager diff -- "$LOCKFILE_PATHSPEC" 2>/dev/null | sanitize_fence | head -c "$DIFF_BYTE_LIMIT"
    )"
    if [ -n "$diff_patch" ]; then
      printf '### Diff (truncated to %s bytes)\n\n' "$DIFF_BYTE_LIMIT"
      printf '%s\n' "$FENCE"
      printf '%s\n' "$diff_patch"
      printf '%s\n\n' "$FENCE"
    fi
  fi
}

write_summary() {
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    emit_summary "$@" >>"$GITHUB_STEP_SUMMARY"
  else
    emit_summary "$@"
  fi
}

# ---------------------------------------------------------------------------
# Gate
# ---------------------------------------------------------------------------

run_guard() {
  local repo_root drift missing extra on_disk_missing manifest tracked p
  local -a ia_added=()
  repo_root="$(git rev-parse --show-toplevel)"
  cd "$repo_root"

  if [ ! -f "$MANIFEST_REL" ]; then
    echo "lockfile guard FAILED: expected manifest '${MANIFEST_REL}' is missing." >&2
    return 1
  fi

  # Detect BEFORE touching the index, so the intent-to-add below cannot mask a
  # real result.
  drift="$(detect_drift)"

  manifest="$(manifest_lockfiles)"
  tracked="$(tracked_lockfiles)"

  missing="$(LC_ALL=C comm -23 <(printf '%s\n' "$manifest") <(printf '%s\n' "$tracked"))"
  extra="$(LC_ALL=C comm -13 <(printf '%s\n' "$manifest") <(printf '%s\n' "$tracked"))"

  # A tracked-but-deleted file still appears in `git ls-files`, so also assert
  # presence on disk. Deliberate redundancy over detect_drift: it stays true
  # even if git is told to ignore the working tree (`--skip-worktree`,
  # `--assume-unchanged`), where `git status` reports nothing.
  on_disk_missing=''
  while IFS= read -r p; do
    [ -n "$p" ] || continue
    [ -f "$p" ] || on_disk_missing="${on_disk_missing}${p}"$'\n'
  done <<<"$manifest"
  if [ -n "$on_disk_missing" ]; then
    missing="$(printf '%s\n%s' "$missing" "${on_disk_missing%$'\n'}" | grep -v '^$' | LC_ALL=C sort -u)"
  fi

  if [ -z "$drift" ] && [ -z "$missing" ] && [ -z "$extra" ]; then
    echo "lockfile guard OK: $(printf '%s\n' "$manifest" | grep -c '^') tracked packages.lock.json file(s) present and in sync with the project files."
    return 0
  fi

  # Make a brand-new lock file render as an addition in `git diff` rather than
  # an empty patch. `--force` covers a lock file hidden by .gitignore. No
  # content is staged and this job never commits.
  #
  # Outside CI the markers are undone afterwards, so first record exactly which
  # paths the guard is about to add: everything matching the pathspec that git
  # does not track yet. A path the developer staged deliberately is TRACKED in
  # the index and therefore never appears here — the cleanup must not unstage
  # it, least of all the `git add` the remediation message asks for.
  if [ -z "${GITHUB_ACTIONS:-}" ]; then
    while IFS= read -r -d '' p; do
      ia_added+=("$p")
    done < <(git ls-files -z --others -- "$LOCKFILE_PATHSPEC")
  fi

  git add --intent-to-add --force -- "$LOCKFILE_PATHSPEC" >/dev/null 2>&1 || true

  write_summary "$drift" "$missing" "$extra"

  # CI checkouts are throwaway, a developer's index is not. `:(literal)` because
  # a path may itself look like pathspec magic (`::error::…`).
  if [ -z "${GITHUB_ACTIONS:-}" ] && [ "${#ia_added[@]}" -gt 0 ]; then
    for p in "${ia_added[@]}"; do
      git reset -q -- ":(literal)${p}" >/dev/null 2>&1 || true
    done
  fi

  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    echo "::error title=NuGet lockfile guard::packages.lock.json is out of sync or missing. See the job summary for the affected files and the fix."
  fi

  if [ -n "$drift" ]; then
    printf 'Out-of-sync lock file(s):\n' >&2
    printf '%s\n' "$drift" | sed 's/^...//' | mark_untrusted >&2
  fi
  if [ -n "$missing" ]; then
    printf 'Missing (unpinned) lock file(s):\n' >&2
    printf '%s\n' "$missing" | mark_untrusted >&2
  fi
  if [ -n "$extra" ]; then
    printf 'Tracked lock file(s) absent from %s:\n' "$MANIFEST_REL" >&2
    printf '%s\n' "$extra" | mark_untrusted >&2
  fi
  echo "lockfile guard FAILED. Run '${RESTORE_CMD}' and commit the result." >&2
  return 1
}

# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------
#
# Regression-tests the guard's own exit-code and reporting contract against
# throwaway git repositories, mirroring tools/coverage/coverage-gate-selftest.py.
# CI runs it BEFORE the real check, so a broken guard fails loudly instead of
# failing open.

SELFTEST_FAILURES=0
SELFTEST_ROOT=''

selftest_cleanup() {
  [ -n "$SELFTEST_ROOT" ] && [ -d "$SELFTEST_ROOT" ] && rm -rf "$SELFTEST_ROOT"
}

st_pass() { printf '  PASS %s\n' "$1"; }
st_fail() {
  printf '  FAIL %s\n' "$1"
  SELFTEST_FAILURES=$((SELFTEST_FAILURES + 1))
}

assert_exit() {
  local expected="$1" actual="$2" name="$3"
  if [ "$expected" = "$actual" ]; then
    st_pass "$name (exit $actual)"
  else
    st_fail "$name: expected exit $expected, got $actual"
  fi
}

assert_contains() {
  local file="$1" needle="$2" name="$3"
  if grep -qF -- "$needle" "$file"; then
    st_pass "$name"
  else
    st_fail "$name: '${needle}' not found in ${file}"
  fi
}

assert_not_contains() {
  local file="$1" needle="$2" name="$3"
  if grep -qF -- "$needle" "$file"; then
    st_fail "$name: unexpected '${needle}' in ${file}"
  else
    st_pass "$name"
  fi
}

# A minimal repo: one project file, one tracked lock file, and a manifest
# listing it. Extra lock-file paths may be passed as additional arguments; each
# gets a sibling project.
st_make_repo() {
  local dir="$1"
  shift
  local p name
  git -c init.defaultBranch=main init -q "$dir"
  git -C "$dir" config user.email 'lockfile-guard@example.invalid'
  git -C "$dir" config user.name 'Lockfile Guard Selftest'
  git -C "$dir" config commit.gpgsign false
  mkdir -p "$dir/tools/ci"
  cat >"$dir/Directory.Build.props" <<'EOF'
<Project>
  <PropertyGroup>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
EOF
  : >"$dir/tools/ci/expected-lockfiles.txt"
  for p in "src/Proj/packages.lock.json" "$@"; do
    mkdir -p "$dir/$(dirname "$p")"
    name="$(basename "$(dirname "$p")")"
    st_write_csproj "$dir/$(dirname "$p")/${name}.csproj"
    printf '{"version": 1, "dependencies": {"net8.0": {}}}\n' >"$dir/$p"
    printf '%s\n' "$p" >>"$dir/tools/ci/expected-lockfiles.txt"
  done
  LC_ALL=C sort -o "$dir/tools/ci/expected-lockfiles.txt" "$dir/tools/ci/expected-lockfiles.txt"
  git -C "$dir" add -A
  git -C "$dir" commit -qm 'fixture'
}

# A project MSBuild can evaluate. The body (if given on stdin via $2) replaces
# the default lock-file-enabled PropertyGroup.
# A project file beside each fixture lock file, so the fixture tree looks like a
# real repository.
st_write_csproj() {
  printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n' >"$1"
}

# Run the guard inside a fixture repo; echo its exit code. Output lands in
# <dir>/stdout.txt and the step summary in <dir>/summary.md.
st_run_guard() {
  local dir="$1" rc=0
  (
    cd "$dir" &&
      GITHUB_STEP_SUMMARY="$dir/summary.md" GITHUB_ACTIONS='' \
        bash "$SELFTEST_SCRIPT" >"$dir/stdout.txt" 2>&1
  ) || rc=$?
  printf '%s' "$rc"
}

st_case_in_sync() {
  local d="$SELFTEST_ROOT/in-sync"
  st_make_repo "$d"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 0 "$rc" 'in-sync tree passes'
  assert_contains "$d/stdout.txt" 'lockfile guard OK' 'in-sync tree reports OK'
  if [ -f "$d/summary.md" ]; then
    st_fail 'in-sync tree writes no step summary'
  else
    st_pass 'in-sync tree writes no step summary'
  fi
}

st_case_modified() {
  local d="$SELFTEST_ROOT/modified"
  st_make_repo "$d"
  # Simulate the Dependabot bug: the net10.0 section is dropped.
  printf '{"version": 1, "dependencies": {}}\n' >"$d/src/Proj/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'modified tracked lock file fails'
  assert_contains "$d/summary.md" 'src/Proj/packages.lock.json' 'modified lock file is listed'
  assert_contains "$d/summary.md" '### Diff stat' 'modified lock file emits a diff stat'
  assert_contains "$d/summary.md" '-{"version": 1' 'modified lock file emits a non-empty patch'
}

st_case_untracked() {
  local d="$SELFTEST_ROOT/untracked"
  st_make_repo "$d"
  mkdir -p "$d/src/New"
  printf '<Project Sdk="Microsoft.NET.Sdk"></Project>\n' >"$d/src/New/New.csproj"
  printf '{"version": 1, "dependencies": {"net10.0": {}}}\n' >"$d/src/New/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'new untracked lock file fails'
  assert_contains "$d/summary.md" 'src/New/packages.lock.json' 'new lock file is listed'
  assert_contains "$d/summary.md" '+{"version": 1' 'new lock file renders as an addition (intent-to-add)'
  # `git diff-index --cached HEAD`, not `git diff --cached`: the latter does not
  # list intent-to-add entries, so it would pass whether or not the guard
  # cleaned up after itself.
  if [ -z "$(git -C "$d" diff-index --cached --name-only HEAD)" ]; then
    st_pass 'local run leaves no intent-to-add entries in the index'
  else
    st_fail 'local run left intent-to-add entries staged'
  fi
}

# A developer who follows the guard's own remediation (`git add -- <pathspec>`)
# must not have that work unstaged by the next run: the cleanup may only undo
# the markers the guard itself created.
st_case_prestaged_survives() {
  local d="$SELFTEST_ROOT/prestaged"
  local staged='{"version": 1, "dependencies": {"net10.0": {"deliberately": "staged"}}}'
  st_make_repo "$d"
  printf '%s\n' "$staged" >"$d/src/Proj/packages.lock.json"
  git -C "$d" add -- 'src/Proj/packages.lock.json'
  # A second, untracked lock file so the guard also intent-to-adds something.
  mkdir -p "$d/src/New"
  printf '{"version": 1, "dependencies": {}}\n' >"$d/src/New/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'guard still fails with a deliberately staged lock file'
  if git -C "$d" diff-index --cached --name-only HEAD | grep -qxF 'src/Proj/packages.lock.json'; then
    st_pass 'a deliberately staged lock file is still staged afterwards'
  else
    st_fail 'the guard unstaged a deliberately staged lock file'
  fi
  if [ "$(git -C "$d" show ':src/Proj/packages.lock.json')" = "$staged" ]; then
    st_pass 'staged lock-file CONTENT survives the guard run'
  else
    st_fail 'staged lock-file content was discarded (reset to HEAD)'
  fi
  if git -C "$d" diff-index --cached --name-only HEAD | grep -qxF 'src/New/packages.lock.json'; then
    st_fail "the guard's own intent-to-add marker was left behind"
  else
    st_pass "the guard's own intent-to-add marker is removed"
  fi
}

st_case_deleted_worktree() {
  local d="$SELFTEST_ROOT/deleted-worktree"
  st_make_repo "$d"
  rm "$d/src/Proj/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'lock file deleted from the working tree fails'
  assert_contains "$d/summary.md" 'src/Proj/packages.lock.json' 'deleted lock file is listed'
}

st_case_deleted_committed() {
  local d="$SELFTEST_ROOT/deleted-committed"
  st_make_repo "$d"
  git -C "$d" rm -q 'src/Proj/packages.lock.json'
  git -C "$d" commit -qm 'remove pinning'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'committed lock-file deletion fails (fail-closed)'
  assert_contains "$d/summary.md" '**missing**' 'committed deletion reports a missing lock file'
  assert_contains "$d/summary.md" 'src/Proj/packages.lock.json' 'committed deletion lists the removed path'
}

st_case_gitignored() {
  local d="$SELFTEST_ROOT/gitignored"
  st_make_repo "$d"
  printf 'packages.lock.json\nhidden/\n' >"$d/.gitignore"
  git -C "$d" add .gitignore
  git -C "$d" commit -qm 'ignore lock files'
  mkdir -p "$d/src/Hidden" "$d/hidden/Proj"
  printf '{"version": 1}\n' >"$d/src/Hidden/packages.lock.json"
  printf '{"version": 1}\n' >"$d/hidden/Proj/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'gitignored regenerated lock file fails'
  assert_contains "$d/summary.md" 'src/Hidden/packages.lock.json' 'lock file hidden by an ignore rule is listed'
  assert_contains "$d/summary.md" 'hidden/Proj/packages.lock.json' 'lock file inside an ignored directory is listed'
  # Pins `--force` on the intent-to-add: without it an ignored path is not added
  # and the reviewer gets an empty patch instead of an actionable one.
  assert_contains "$d/summary.md" '+{"version": 1}' 'hidden lock file renders as an addition (intent-to-add --force)'
}

# A built tree carries ignored `obj/` directories. They must NOT be reported:
# `git status --ignored=matching` lists such directories as pathspec matches
# even though they contain no lock file, which would fail every real run.
st_case_ignored_build_dirs() {
  local d="$SELFTEST_ROOT/ignored-build-dirs"
  st_make_repo "$d"
  printf 'obj/\nbin/\n' >"$d/.gitignore"
  git -C "$d" add .gitignore
  git -C "$d" commit -qm 'ignore build output'
  mkdir -p "$d/src/Proj/obj" "$d/src/Proj/bin"
  printf '{}\n' >"$d/src/Proj/obj/project.assets.json"
  printf 'binary\n' >"$d/src/Proj/bin/Proj.dll"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 0 "$rc" 'ignored build directories are not false positives'
  assert_contains "$d/stdout.txt" 'lockfile guard OK' 'built tree still reports OK'
}

st_case_manifest_lag() {
  local d="$SELFTEST_ROOT/manifest-lag"
  st_make_repo "$d"
  mkdir -p "$d/src/Extra"
  printf '{"version": 1, "dependencies": {"net8.0": {}}}\n' >"$d/src/Extra/packages.lock.json"
  git -C "$d" add -A
  git -C "$d" commit -qm 'add project without updating the manifest'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'tracked lock file missing from the manifest fails'
  assert_contains "$d/summary.md" 'src/Extra/packages.lock.json' 'unlisted lock file is named'
}

st_case_unicode_path() {
  local d="$SELFTEST_ROOT/unicode"
  local weird='src/pröjekt spaced/packages.lock.json'
  st_make_repo "$d" "$weird"
  printf '{"version": 1, "dependencies": {}}\n' >"$d/$weird"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'path with spaces and non-ASCII fails on drift'
  assert_contains "$d/summary.md" 'src/pröjekt spaced/packages.lock.json' 'unicode path is printed unquoted (core.quotePath=false)'
  assert_not_contains "$d/summary.md" '\303\266' 'unicode path is not octal-escaped'
}

# The literal backticks here are lock-file CONTENT used to attempt a markdown
# fence breakout, not command substitution.
# shellcheck disable=SC2016
st_case_fence_breakout() {
  local d="$SELFTEST_ROOT/fence-breakout"
  st_make_repo "$d"
  printf '```\n## injected heading\n{"version": 1}\n' >"$d/src/Proj/packages.lock.json"
  git -C "$d" commit -qam 'lock file whose content contains a markdown fence'
  # After this edit the ``` line is UNCHANGED, so it reaches the summary as diff
  # context (" ```"), while the five-backtick line arrives as an addition.
  printf '```\n## injected heading\n{"version": 2}\n`````\n' >"$d/src/Proj/packages.lock.json"
  local rc fences
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'lock file containing a markdown fence fails'
  assert_contains "$d/summary.md" "'''" 'leading backtick run is neutralized in the summary'
  fences="$(grep -c '^`\{5,\}$' "$d/summary.md" || true)"
  if [ "$((fences % 2))" -eq 0 ] && [ "$fences" -gt 0 ]; then
    st_pass "code fences stay balanced (${fences} fence lines)"
  else
    st_fail "code fences unbalanced (${fences} fence lines) — content escaped its block"
  fi
}

st_case_remediation_pathspec() {
  local d="$SELFTEST_ROOT/remediation"
  st_make_repo "$d"
  printf '{"version": 1, "dependencies": {}}\n' >"$d/src/Proj/packages.lock.json"
  st_run_guard "$d" >/dev/null
  assert_contains "$d/summary.md" "git add -- ':(glob,top)**/packages.lock.json'" 'remediation uses the magic pathspec'
  assert_contains "$d/summary.md" '.NET SDK used by this run' 'summary carries the SDK breadcrumb'
}

# A duplicated (or unsorted) manifest line must not be reported as missing:
# `comm` compares sorted UNIQUE sets, so manifest_lockfiles uses `sort -u`.
st_case_manifest_duplicates() {
  local d="$SELFTEST_ROOT/manifest-duplicates"
  st_make_repo "$d" "src/Second/packages.lock.json"
  printf 'src/Second/packages.lock.json\nsrc/Proj/packages.lock.json\nsrc/Proj/packages.lock.json\n' \
    >"$d/tools/ci/expected-lockfiles.txt"
  git -C "$d" commit -qam 'duplicated, unsorted manifest'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 0 "$rc" 'duplicated/unsorted manifest entries do not false-fail'
  assert_contains "$d/stdout.txt" 'lockfile guard OK' 'duplicated manifest still reports OK'
}

# `--skip-worktree` tells git to ignore the working-tree copy, so `git status`
# stays clean after the file is deleted. The on-disk presence loop is the check
# that still catches it.
st_case_skip_worktree() {
  local d="$SELFTEST_ROOT/skip-worktree"
  st_make_repo "$d"
  git -C "$d" update-index --skip-worktree 'src/Proj/packages.lock.json'
  rm "$d/src/Proj/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'skip-worktree deletion still fails (on-disk presence check)'
  assert_contains "$d/summary.md" '**missing**' 'skip-worktree deletion reports a missing lock file'
}

# A PR-controlled path must never reach column 0 of stdout/stderr in GitHub's
# `::workflow-command` syntax, or a pull request could forge annotations.
# Drives ALL THREE reporting branches (drift, missing, extra): each prints
# untrusted text through its own `mark_untrusted` call, so a fixture that only
# produces drift would let a regression in the other two go unnoticed.
st_case_workflow_command_injection() {
  local d="$SELFTEST_ROOT/command-injection"
  local drift_path='::error::pwned/packages.lock.json'
  local extra_path='::warning::extra/packages.lock.json'
  local manifest_entry='::stop-commands::deadbeef/packages.lock.json'
  st_make_repo "$d" "$drift_path"

  # drift: a tracked lock file under a hostile path changed after the restore.
  printf '{"version": 1, "dependencies": {}}\n' >"$d/$drift_path"

  # extra: a tracked lock file under a hostile path that the manifest omits.
  mkdir -p "$d/$(dirname "$extra_path")"
  st_write_csproj "$d/$(dirname "$extra_path")/Extra.csproj"
  printf '{"version": 1}\n' >"$d/$extra_path"
  # The `./` prefix matters: `git add -- '::warning::extra/…'` is parsed as
  # MAGIC pathspec, not as a path.
  git -C "$d" add -- "./$(dirname "$extra_path")"
  git -C "$d" commit -qm 'unlisted lock file under a hostile path'

  # missing: a hostile manifest entry that is not tracked.
  printf '%s\n' "$manifest_entry" >>"$d/tools/ci/expected-lockfiles.txt"
  git -C "$d" add -- tools/ci/expected-lockfiles.txt
  git -C "$d" commit -qm 'hostile manifest entry'

  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'lock files under ::workflow-command paths fail'
  assert_contains "$d/summary.md" "$drift_path" 'hostile drift path is still reported'
  assert_contains "$d/summary.md" "$manifest_entry" 'hostile manifest entry is still reported'
  assert_contains "$d/summary.md" "$extra_path" 'hostile unlisted path is still reported'
  # Split on CR as well as LF, because the runner's line reader does too.
  if tr '\r' '\n' <"$d/stdout.txt" | grep -qE '^[[:space:]]*::'; then
    st_fail 'a ::workflow-command line reached column 0 of stdout/stderr'
  else
    st_pass 'no reporting branch forges a ::workflow-command annotation (stdout/stderr)'
  fi
  if tr '\r' '\n' <"$d/summary.md" | grep -qE '^[[:space:]]*::'; then
    st_fail 'a ::workflow-command line reached column 0 of the step summary'
  else
    st_pass 'no reporting branch forges a ::workflow-command annotation (summary)'
  fi
}

# The runner's line reader also splits on a bare CR, and neither `git ls-files`
# nor the manifest C-quotes control bytes — so the assertions here read the
# CR-SPLIT view, not just the LF view.
st_case_cr_injection() {
  local d="$SELFTEST_ROOT/cr-injection"
  st_make_repo "$d"
  printf 'src/Proj/packages.lock.json\nbenign\r::error::pwned/packages.lock.json\n' \
    >"$d/tools/ci/expected-lockfiles.txt"
  git -C "$d" commit -qam 'manifest entry carrying a bare CR'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'manifest entry with a CR fails (path is missing)'
  if tr '\r' '\n' <"$d/stdout.txt" | grep -qE '^[[:space:]]*::'; then
    st_fail 'a CR in an untrusted line forged a ::workflow-command on stdout/stderr'
  else
    st_pass 'a CR is escaped, so it cannot start a ::workflow-command line'
  fi
  assert_contains "$d/stdout.txt" 'benign\r::error::pwned/packages.lock.json' 'CR renders as a literal \r'
}

st_case_missing_manifest() {
  local d="$SELFTEST_ROOT/no-manifest"
  st_make_repo "$d"
  git -C "$d" rm -q 'tools/ci/expected-lockfiles.txt'
  git -C "$d" commit -qm 'drop the manifest'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'deleted manifest fails closed'
  assert_contains "$d/stdout.txt" 'expected manifest' 'missing manifest is explained'
}

run_selftest() {
  SELFTEST_SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  readonly SELFTEST_SCRIPT
  SELFTEST_ROOT="$(mktemp -d)"
  trap selftest_cleanup EXIT INT TERM

  echo "lockfile guard self-test (fixtures under ${SELFTEST_ROOT})"
  st_case_in_sync
  st_case_modified
  st_case_untracked
  st_case_prestaged_survives
  st_case_deleted_worktree
  st_case_deleted_committed
  st_case_gitignored
  st_case_ignored_build_dirs
  st_case_manifest_lag
  st_case_manifest_duplicates
  st_case_skip_worktree
  st_case_workflow_command_injection
  st_case_cr_injection
  st_case_unicode_path
  st_case_fence_breakout
  st_case_remediation_pathspec
  st_case_missing_manifest

  if [ "$SELFTEST_FAILURES" -eq 0 ]; then
    echo 'lockfile guard self-test: all assertions passed.'
    return 0
  fi
  echo "lockfile guard self-test: ${SELFTEST_FAILURES} assertion(s) FAILED." >&2
  return 1
}

main() {
  case "${1:-}" in
    '')
      run_guard
      ;;
    --selftest)
      run_selftest
      ;;
    -h | --help)
      usage
      ;;
    *)
      echo "lockfile guard: unknown argument '$1'." >&2
      usage >&2
      return 2
      ;;
  esac
}

main "$@"
