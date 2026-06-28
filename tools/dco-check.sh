#!/usr/bin/env bash
#
# DCO (Developer Certificate of Origin) check.
#
# Verifies that every non-merge commit introduced by a pull request carries a
# `Signed-off-by:` trailer. Invoked by .github/workflows/dco.yml.
#
# Run locally against your branch:
#
#     BASE_SHA=origin/main HEAD_SHA=HEAD ./tools/dco-check.sh
#
set -euo pipefail

base="${BASE_SHA:-}"
head="${HEAD_SHA:-HEAD}"

if [ -n "$base" ]; then
  range="${base}..${head}"
else
  # No base provided: check only the tip commit.
  range="${head}~1..${head}"
fi

# A valid sign-off looks like: "Signed-off-by: Real Name <email@example.com>".
sign_off_re='^Signed-off-by: .+ <[^[:space:]]+@[^[:space:]]+>[[:space:]]*$'

# Enumerate the commits to check. Capture to a variable (not a pipe or process
# substitution) so a `git rev-list` failure is DETECTED and the gate fails closed,
# instead of silently treating a broken/unknown range as "nothing to check". Every
# commit is checked (no --no-merges): main requires linear history, so a stray merge
# commit must be rebased away and should fail this gate early.
if ! commits="$(git rev-list "$range")"; then
  echo "DCO check FAILED: could not enumerate commits for range '${range}'." >&2
  exit 1
fi

failed=0
checked=0

while IFS= read -r sha; do
  [ -z "$sha" ] && continue
  # Strip control characters before logging the untrusted commit subject.
  subject="$(git show -s --format='%s' "$sha" | LC_ALL=C tr -d '\000-\037')"
  # Exempt automated bot commits (e.g. dependabot[bot]): DCO is a human attestation
  # and dependency-bump PRs cannot add a Signed-off-by trailer. GitHub bot accounts
  # author commits from a *[bot]@users.noreply.github.com email.
  author_email="$(git show -s --format='%ae' "$sha")"
  if [[ "$author_email" == *'[bot]@users.noreply.github.com' ]]; then
    echo "  skip  ${sha}  ${subject} (automated bot commit, DCO-exempt)"
    continue
  fi
  checked=$((checked + 1))
  # Read the full message into a variable first, so the match below is a here-string
  # rather than a `git ... | grep -q` pipeline (which can surface SIGPIPE under
  # `pipefail` on very large messages and misreport a signed commit as failed).
  message="$(git show -s --format='%B' "$sha")"
  if grep -Eiq "$sign_off_re" <<<"$message"; then
    echo "  ok    ${sha}  ${subject}"
  else
    echo "  FAIL  ${sha}  ${subject}"
    failed=1
  fi
done <<<"$commits"

if [ "$failed" -ne 0 ]; then
  cat <<'EOF'

DCO check failed: one or more commits are missing a `Signed-off-by` trailer.

Every commit must certify the Developer Certificate of Origin
(https://developercertificate.org/). Sign new commits with:

    git commit -s

To add sign-off to existing commits on this branch:

    git rebase --signoff origin/main
    git push --force-with-lease
EOF
  exit 1
fi

if [ "$checked" -eq 0 ]; then
  echo "DCO check passed: no commits to verify in range '${range}'."
  exit 0
fi

echo "DCO check passed: all ${checked} commit(s) are signed off."
