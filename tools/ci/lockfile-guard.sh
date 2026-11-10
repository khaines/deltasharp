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
# NO SIDE EFFECTS: the guard never writes to the index or the working tree. A
# brand-new (untracked) lock file is rendered with `git diff --no-index` against
# /dev/null rather than with a `git add --intent-to-add` marker, so nothing can
# clobber a developer's staged work (a staged `git rm --cached`, a `git add -p`
# partial stage) and no cleanup step is needed.
#
set -euo pipefail

# One pathspec for detection, diffing and the printed remediation
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

# Render every byte that a LINE READER or a MARKDOWN RENDERER might treat as a
# line break as a visible escape, so untrusted bytes cannot start a new line.
# This closes two sinks at once:
#
#   * GitHub workflow commands — the Actions runner scans stdout AND stderr for
#     `::error::…`, `::add-mask::…`, `::stop-commands::…`. A PR-controlled path
#     or manifest entry carrying `benign<CR>::error::…` would otherwise begin a
#     fresh line at column 0.
#   * The step-summary code fence — cmark-gfm treats a bare CR as a line ending,
#     so lock-file CONTENT containing one could close the fence early and inject
#     markdown.
#
# Rather than argue about which readers honour which terminator, the whole class
# is escaped: CR, the other C0 controls, and the Unicode line breaks NEL
# (U+0085), LS (U+2028) and PS (U+2029). LF and TAB are left alone — LF is the
# record separator these filters work on, and TAB breaks nothing.
ESCAPE_ARGS=(
  -e "s/$(printf '\r')/\\\\r/g"
  -e "s/$(printf '\013')/\\\\v/g"
  -e "s/$(printf '\014')/\\\\f/g"
  -e "s/$(printf '\033')/\\\\e/g"
  -e "s/$(printf '\302\205')/\\\\u0085/g"
  -e "s/$(printf '\342\200\250')/\\\\u2028/g"
  -e "s/$(printf '\342\200\251')/\\\\u2029/g"
  -e "s/[$(printf '\001-\010\016-\037\177')]/?/g"
)
readonly ESCAPE_ARGS

escape_untrusted() {
  LC_ALL=C sed "${ESCAPE_ARGS[@]}"
}

# Escaped AND bulleted: a NON-whitespace marker before the text, because the
# runner tolerates indentation, so leading spaces alone would not stop a forged
# `::` command. (Diff hunks are exempt from the bullet: unified-diff output
# always begins with `+`, `-`, ` `, `@`, `d` or `i`, never `:` — but it is still
# escaped, since its CONTENT can carry line breaks.)
bullet_untrusted() {
  sed 's/^/  - /'
}

mark_untrusted() {
  escape_untrusted | bullet_untrusted
}

# ---------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------

# Lock files that changed, appeared, or were deleted after the restore.
#
# Two commands, because neither alone is both complete and precise:
#   * `git status --porcelain -uall` sees modified, deleted and new (untracked)
#     lock files. It does NOT see .gitignore'd files. `-uall` is kept
#     defensively: a pathspec-limited status on current git already reports the
#     lock file inside a brand-new directory individually rather than collapsing
#     it to `?? src/New/`, but `-uall` guarantees that on older versions too.
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
# `|| true`: `grep` exits 1 on an empty (or all-comment) manifest, which under
# `set -e` + `pipefail` would abort the guard with no output at all — a red check
# with an empty log. run_guard reports that case explicitly instead.
manifest_lockfiles() {
  { sed -e 's/#.*//' -e 's/[[:space:]]*$//' "$MANIFEST_REL" |
    grep -v '^$' || true; } | LC_ALL=C sort -u
}

# Lock files that exist on disk but are not tracked, including .gitignore'd ones
# (no `--exclude-standard`), NUL-delimited.
untracked_lockfiles_z() {
  git ls-files -z --others -- "$LOCKFILE_PATHSPEC"
}

# The full diff of every lock file, WITHOUT touching the index: tracked files
# through the ordinary diff, untracked (and ignored) ones through
# `git diff --no-index` against /dev/null, which renders them as additions the
# same way an intent-to-add marker would — but is read-only, so it can never
# disturb a developer's staged work. `$1` is `--stat` or `--patch`.
lockfile_diff() {
  local mode="$1" p
  git -c core.quotePath=false --no-pager diff "$mode" -- "$LOCKFILE_PATHSPEC" 2>/dev/null || true
  while IFS= read -r -d '' p; do
    git -c core.quotePath=false --no-pager diff --no-index "$mode" -- /dev/null "$p" 2>/dev/null || true
  done < <(untracked_lockfiles_z)
}

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

# Render a bulleted, fence-sanitized block of untrusted lines inside a fenced
# code block.
emit_block() {
  printf '%s\n' "$FENCE"
  printf '%s\n' "$1" | escape_untrusted | sanitize_fence | bullet_untrusted
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
    diff_stat="$(lockfile_diff --stat)"
    if [ -n "$diff_stat" ]; then
      printf '### Diff stat\n\n'
      emit_block "$diff_stat"
    fi

    # Bounded: the patch is PR-controlled content. `head -c` closes the pipe
    # early, so pipefail is disabled inside this subshell only. `escape_untrusted`
    # runs BEFORE `sanitize_fence` as belt and braces, not because the order is
    # load-bearing: the two filters commute (escaping never emits a backtick, and
    # the sanitizer never emits a control byte), which a differential fuzz over
    # 4 000 lines confirmed byte-for-byte.
    diff_patch="$(
      set +o pipefail
      lockfile_diff --patch | escape_untrusted | sanitize_fence | head -c "$DIFF_BYTE_LIMIT"
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
  repo_root="$(git rev-parse --show-toplevel)"
  cd "$repo_root"

  if [ ! -f "$MANIFEST_REL" ]; then
    echo "lockfile guard FAILED: expected manifest '${MANIFEST_REL}' is missing." >&2
    return 1
  fi

  drift="$(detect_drift)"

  manifest="$(manifest_lockfiles)"
  if [ -z "$manifest" ]; then
    echo "lockfile guard FAILED: '${MANIFEST_REL}' lists no lock files. Every tracked packages.lock.json must be listed there." >&2
    return 1
  fi
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
    missing="$({ printf '%s\n%s' "$missing" "${on_disk_missing%$'\n'}" | grep -v '^$' || true; } | LC_ALL=C sort -u)"
  fi

  if [ -z "$drift" ] && [ -z "$missing" ] && [ -z "$extra" ]; then
    echo "lockfile guard OK: $(printf '%s\n' "$manifest" | grep -c '^') tracked packages.lock.json file(s) present and in sync with the project files."
    return 0
  fi

  write_summary "$drift" "$missing" "$extra"

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
  shift
  (
    cd "$dir" &&
      env GITHUB_STEP_SUMMARY="$dir/summary.md" GITHUB_ACTIONS='' "$@" \
        bash "$SELFTEST_SCRIPT" >"$dir/stdout.txt" 2>&1
  ) || rc=$?
  printf '%s' "$rc"
}

# Every byte a line reader or markdown renderer may treat as a line break is
# turned into a real LF, so an assertion sees exactly what such a reader would.
st_split_lines() {
  LC_ALL=C awk \
    -v nel="$(printf '\302\205')" \
    -v ls="$(printf '\342\200\250')" \
    -v ps="$(printf '\342\200\251')" \
    '{ gsub(nel, "\n"); gsub(ls, "\n"); gsub(ps, "\n"); print }' "$1" |
    tr '\r\013\014' '[\n*]'
}

# No forged workflow command may appear at column 0 of ANY line, in EITHER
# stream, under EITHER line-break convention.
st_assert_no_forged_command() {
  local d="$1" name="$2" f
  for f in stdout.txt summary.md; do
    [ -f "$d/$f" ] || continue
    if st_split_lines "$d/$f" | grep -qE '^[[:space:]]*::'; then
      st_fail "${name}: a ::workflow-command reached column 0 of ${f}"
      return
    fi
  done
  st_pass "$name"
}

# No raw control byte may reach EITHER sink. This oracle covers the single-byte
# classes only: CR, VT and FF by name, plus the ENTIRE catch-all range
# `\001-\010\016-\037\177` as a character class, so narrowing that range at any
# byte — an interior one such as `\010`, or either boundary — leaks a raw byte
# here. (ESC falls inside the range as well as having its own rule. NEL, LS and
# PS are multi-byte UTF-8, not control bytes; they are pinned by their
# escaped-form assertions and by st_assert_no_forged_command instead.)
st_assert_no_raw_control() {
  local d="$1" name="$2" f byte label leaked=''
  local c0_class
  c0_class="[$(printf '\001-\010\016-\037\177')]"
  for f in stdout.txt summary.md; do
    [ -f "$d/$f" ] || continue
    if LC_ALL=C grep -q -- "$c0_class" "$d/$f"; then
      leaked="${leaked} C0-catch-all-range(${f})"
    fi
    while IFS='|' read -r byte label; do
      [ -n "$byte" ] || continue
      if LC_ALL=C grep -qF -- "$(printf '%b' "$byte")" "$d/$f"; then
        leaked="${leaked} ${label}(${f})"
      fi
    done <<'BYTES'
\r|CR
\013|VT
\014|FF
BYTES
  done
  if [ -z "$leaked" ]; then
    st_pass "$name"
  else
    st_fail "${name}: raw control bytes survived:${leaked}"
  fi
}

# STRUCTURAL fence oracle. Everything the guard prints from untrusted input
# lives strictly INSIDE a five-backtick block, so walking the rendered lines the
# way a markdown reader would must end balanced AND must never see a payload
# sentinel outside a fence. A parity count would not do: an EVEN number of
# smuggled fences also breaks out.
#
# The toggle models the complete CommonMark closing-fence rule rather than an
# exact string match. A close has exactly three degrees of freedom: it may be
# indented by up to three spaces, its run may be LONGER than the opener, and it
# may be followed by trailing spaces OR TABS. All three matter here, because a
# diff CONTEXT line is space-prefixed: an unchanged ````` line inside a lock file
# reaches the summary as ` `````` , an unchanged INDENTED one as `   ````` `, and
# an unchanged tab-padded one as ` `````<TAB>` — each of which really does close
# the guard's block. An exact-match toggle would be blind to exactly the
# geometry the sanitizer exists to stop.
# Requiring at least as many backticks as the guard's own delimiter also keeps
# the ```bash remediation block from confusing the walk, and an addition line
# (`+`````` ) cannot close a fence, so it must not toggle either.
st_assert_fence_structure() {
  local d="$1" file="$2" name="$3" sentinels="$4" verdict
  verdict="$(st_split_lines "$d/$file" |
    LC_ALL=C awk -v fence="$FENCE" -v sentinels="$sentinels" '
      $0 ~ ("^ {0,3}`{" length(fence) ",}[ \t]*$") { inside = !inside; fences++; next }
      !inside && $0 ~ ("^(" sentinels ")") { leak = leak " | " $0 }
      END {
        if (fences == 0) { print "no fenced block was emitted at all"; exit }
        if (inside) { print "the rendering ends INSIDE a code fence"; exit }
        if (leak != "") print "untrusted content was rendered OUTSIDE a fence:" leak
      }')"
  if [ -z "$verdict" ]; then
    st_pass "$name"
  else
    st_fail "${name}: ${verdict} (${file})"
  fi
}

# The guard is read-only: it must never leave the index (or a file's tracked
# state) different from how it found it.
st_assert_index_clean() {
  local d="$1" name="$2"
  if [ -z "$(git -C "$d" diff-index --cached --name-only HEAD)" ]; then
    st_pass "$name"
  else
    st_fail "${name}: the guard mutated the index"
  fi
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
  assert_contains "$d/summary.md" '+{"version": 1' 'new lock file renders as an addition (diff --no-index)'
  # The `--no-index` render must honour the mode it is given: the stat section
  # is a stat, not a second copy of the patch.
  local stat_block
  stat_block="$(awk '/^### Diff stat/ { in_stat = 1; next } /^### / { in_stat = 0 } in_stat' "$d/summary.md")"
  case "$stat_block" in
    *'1 file changed'*) st_pass 'the untracked file contributes a diff STAT line' ;;
    *) st_fail "the untracked file produced no diff stat: ${stat_block}" ;;
  esac
  case "$stat_block" in
    *'@@'*) st_fail 'the diff-stat section contains patch hunks (mode was ignored)' ;;
    *) st_pass 'the diff-stat section carries no patch hunk' ;;
  esac
  # `git diff-index --cached HEAD`, not `git diff --cached`: the latter does not
  # list intent-to-add entries, so this would pass even if a future change
  # reintroduced an index write and left a marker behind.
  st_assert_index_clean "$d" 'rendering an untracked lock file leaves the index untouched'
  if git -C "$d" status --porcelain -- 'src/New/packages.lock.json' | grep -q '^??'; then
    st_pass 'the new lock file is still untracked afterwards'
  else
    st_fail 'the guard changed the tracked state of an untracked lock file'
  fi
}

# A developer who follows the guard's own remediation (`git add -- <pathspec>`)
# must not have that work disturbed by the next run. The guard never writes the
# index or the working tree, so there is nothing to undo — this pins that.
st_case_prestaged_survives() {
  local d="$SELFTEST_ROOT/prestaged"
  local staged='{"version": 1, "dependencies": {"net10.0": {"deliberately": "staged"}}}'
  st_make_repo "$d"
  printf '%s\n' "$staged" >"$d/src/Proj/packages.lock.json"
  git -C "$d" add -- 'src/Proj/packages.lock.json'
  # A second, untracked lock file: the guard renders it with `git diff
  # --no-index`, so it must end up staging nothing of its own.
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
    st_fail 'the guard staged an untracked lock file'
  else
    st_pass 'the guard staged nothing of its own'
  fi
}

# A developer who has STAGED A REMOVAL (`git rm --cached`) must keep it: an
# index-writing guard would resurrect the entry from HEAD.
st_case_staged_removal_survives() {
  local d="$SELFTEST_ROOT/staged-removal"
  st_make_repo "$d"
  git -C "$d" rm -q --cached 'src/Proj/packages.lock.json'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'a staged lock-file removal still fails the guard'
  if git -C "$d" ls-files --error-unmatch -- 'src/Proj/packages.lock.json' >/dev/null 2>&1; then
    st_fail 'the guard resurrected a staged removal into the index'
  else
    st_pass 'a staged removal survives the guard run'
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
  st_assert_index_clean "$d" 'a worktree deletion is not staged by the guard'
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
  # Pins the `--no-index` rendering of untracked files: without it a reviewer
  # gets an empty patch for an ignored path instead of an actionable one.
  assert_contains "$d/summary.md" '+{"version": 1}' 'hidden lock file renders as an addition (diff --no-index)'
  st_assert_index_clean "$d" 'an ignored lock file is not staged by the guard'
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
  # Catches a `comm -23`/`comm -13` swap: an unlisted file is NOT a missing one.
  assert_not_contains "$d/summary.md" '**missing**' 'an unlisted lock file is not reported as missing'
  assert_contains "$d/summary.md" '**not listed**' 'an unlisted lock file is reported as unlisted'
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
  # The five-backtick lines are UNCHANGED, so they reach the summary as diff
  # CONTEXT — space-prefixed — and each covers one degree of freedom of
  # CommonMark's closing-fence rule, which is exactly what `sanitize_fence`
  # exists to neutralize:
  #   `````        -> ` `````` : a plain close.
  #   <2 spaces>   -> `   `````` : three leading spaces in total, the maximum
  #                   indent a close may carry (this is what exercises the
  #                   sanitizer's own ` \{0,3\}` indent group).
  #   <trailing \t> -> ` `````<TAB>`: a close may be followed by spaces OR tabs.
  # The ``` line and the ## heading ride along as further payload.
  # shellcheck disable=SC2016 # backticks are payload data, not a substitution
  local payload='`````\n  `````\n`````\t\n{"version": %s}\n```\n## INJHEADING\n'
  # shellcheck disable=SC2059 # $payload is the format string on purpose
  printf "$payload" 1 >"$d/src/Proj/packages.lock.json"
  git -C "$d" commit -qam 'lock file whose content contains markdown fences'
  # shellcheck disable=SC2059 # $payload is the format string on purpose
  printf "$payload" 2 >"$d/src/Proj/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'lock file containing a markdown fence fails'
  assert_contains "$d/summary.md" "'''" 'leading backtick run is neutralized in the summary'
  st_assert_fence_structure "$d" summary.md \
    'a context-line fence in lock-file content cannot close the summary block' \
    '::error::|[[:space:]#+-]*INJHEADING'
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
  st_assert_no_forged_command "$d" 'no reporting branch forges a ::workflow-command annotation'
  st_assert_index_clean "$d" 'a lock file under a magic-pathspec path is not staged'
}

# The whole line-terminator class, in a MANIFEST ENTRY and in lock-file
# CONTENT: neither may start a line for a workflow-command reader or for the
# markdown renderer of the step summary.
st_case_line_terminator_injection() {
  local d="$SELFTEST_ROOT/line-terminators"
  st_make_repo "$d"
  # Manifest entry (the `missing` branch, rendered through emit_block and
  # printed to stderr): TWO terminator + five-backtick runs, so an EVEN number
  # of fences is smuggled in — a parity count would not notice — plus one
  # forged `::error::` behind each escape class.
  # shellcheck disable=SC2016 # backticks are payload data, not a substitution
  printf 'src/Proj/packages.lock.json\nbenign\r`````\rINJHEADING::error::cr\013`````\014::error::ff\033::error::esc\001::error::soh\177::error::del\002\010::error::c0lo\016\037::error::c0hi/packages.lock.json\n' \
    >"$d/tools/ci/expected-lockfiles.txt"
  # Lock-file CONTENT (the diff-patch sink): the same classes, each immediately
  # before a forged command, plus fence runs behind a terminator, plus EVERY
  # byte of the catch-all range so narrowing it anywhere leaks a raw byte.
  printf '{"v": 1}\n' >"$d/src/Proj/packages.lock.json"
  git -C "$d" commit -qam 'hostile manifest entry'
  # shellcheck disable=SC2016 # backticks are payload data, not a substitution
  printf '{"v": 2, "x": "A\r`````\n## INJHEADING\rB\302\205::error::nel\342\200\250::error::ls\342\200\251::error::ps\013::error::vt\014::error::ff\033::error::esc\001::error::soh\177::error::del\r`````\rINJHEADING::error::tail C0[\001\002\003\004\005\006\007\010\016\017\020\021\022\023\024\025\026\027\030\031\032\033\034\035\036\037\177]"}\n' \
    >"$d/src/Proj/packages.lock.json"
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'hostile manifest entry and lock-file content fail'
  st_assert_no_forged_command "$d" 'no line terminator can forge a ::workflow-command (either stream)'
  # Per-class, per-sink: the escaped form must be present and the raw byte gone.
  # stdout carries the manifest entry, the summary carries both it and the diff.
  assert_contains "$d/stdout.txt" 'benign\r`````\r' 'CR in a manifest entry renders as a literal \\r (stdout)'
  assert_contains "$d/stdout.txt" '\v`````\f::error::ff' 'VT and FF in a manifest entry render as \\v and \\f (stdout)'
  assert_contains "$d/stdout.txt" '\e::error::esc' 'ESC in a manifest entry renders as \\e (stdout)'
  assert_contains "$d/stdout.txt" '?::error::soh' 'SOH in a manifest entry is caught by the C0 rule (stdout)'
  assert_contains "$d/stdout.txt" '?::error::del' 'DEL in a manifest entry is caught by the C0 rule (stdout)'
  # The manifest entry also reaches the SUMMARY through emit_block, whose escape
  # runs before the fence sanitizer: a terminator there could otherwise smuggle
  # a fence out of the block.
  assert_contains "$d/summary.md" 'benign\r`````\r' 'CR in a manifest entry is escaped in the summary too'
  assert_contains "$d/summary.md" '\u0085::error::nel' 'NEL in lock-file content renders as a literal \\u0085'
  assert_contains "$d/summary.md" '\u2028::error::ls' 'LS in lock-file content renders as a literal \\u2028'
  assert_contains "$d/summary.md" '\u2029::error::ps' 'PS in lock-file content renders as a literal \\u2029'
  assert_contains "$d/summary.md" '\v::error::vt' 'VT in lock-file content renders as a literal \\v'
  assert_contains "$d/summary.md" '\f::error::ff' 'FF in lock-file content renders as a literal \\f'
  assert_contains "$d/summary.md" '\e::error::esc' 'ESC in lock-file content renders as a literal \\e'
  assert_contains "$d/summary.md" '?::error::soh' 'SOH in lock-file content is caught by the C0 rule'
  assert_contains "$d/summary.md" '?::error::del' 'DEL in lock-file content is caught by the C0 rule'
  assert_contains "$d/stdout.txt" '??::error::c0lo' 'a low C0 pair in a manifest entry is caught by the catch-all (stdout)'
  assert_contains "$d/stdout.txt" '??::error::c0hi' 'a high C0 pair in a manifest entry is caught by the catch-all (stdout)'
  # 27 bytes: \001-\010 and \016-\037 and \177, each rendered `?` by the
  # catch-all except ESC (\033), which its own rule renders `\\e` first.
  assert_contains "$d/summary.md" 'C0[?????????????????????\e?????]' 'every byte of the catch-all range is escaped in lock-file content'
  st_assert_no_raw_control "$d" 'no raw control byte survives into either sink'
  st_assert_fence_structure "$d" summary.md \
    'neither an odd nor an even fence smuggle breaks the summary block' \
    '::error::|[[:space:]#+-]*INJHEADING'
  st_assert_index_clean "$d" 'a hostile manifest entry does not make the guard write the index'
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

st_case_empty_manifest() {
  local d="$SELFTEST_ROOT/empty-manifest"
  st_make_repo "$d"
  printf '# every entry commented out\n\n' >"$d/tools/ci/expected-lockfiles.txt"
  git -C "$d" commit -qam 'empty the manifest'
  local rc
  rc="$(st_run_guard "$d")"
  assert_exit 1 "$rc" 'an empty manifest fails closed instead of dying on set -e'
  assert_contains "$d/stdout.txt" 'lists no lock files' 'an empty manifest is explained'
}

st_case_diff_byte_limit() {
  local d="$SELFTEST_ROOT/diff-limit" bytes
  st_make_repo "$d"
  local pad
  # `head` upstream of `tr`, so nothing receives SIGPIPE under `pipefail`.
  pad="$(head -c 4000 /dev/zero | LC_ALL=C tr '\0' 'a')"
  printf '{"version": 1, "pad": "%s"}\n' "$pad" >"$d/src/Proj/packages.lock.json"
  local rc
  rc="$(LOCKFILE_GUARD_DIFF_BYTES=512 st_run_guard "$d" LOCKFILE_GUARD_DIFF_BYTES=512)"
  assert_exit 1 "$rc" 'a large drift diff still fails'
  assert_contains "$d/summary.md" 'truncated to 512 bytes' 'the summary states the byte limit in force'
  bytes="$(wc -c <"$d/summary.md")"
  if [ "$bytes" -lt 4000 ]; then
    st_pass "the patch block is bounded by the byte limit (summary ${bytes} bytes)"
  else
    st_fail "the patch block was not bounded (summary ${bytes} bytes)"
  fi
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
  st_case_staged_removal_survives
  st_case_deleted_worktree
  st_case_deleted_committed
  st_case_gitignored
  st_case_ignored_build_dirs
  st_case_manifest_lag
  st_case_manifest_duplicates
  st_case_skip_worktree
  st_case_workflow_command_injection
  st_case_line_terminator_injection
  st_case_unicode_path
  st_case_fence_breakout
  st_case_remediation_pathspec
  st_case_missing_manifest
  st_case_empty_manifest
  st_case_diff_byte_limit

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
