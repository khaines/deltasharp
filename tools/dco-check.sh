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

failed=0
checked=0

while IFS= read -r sha; do
  [ -z "$sha" ] && continue
  checked=$((checked + 1))
  subject="$(git show -s --format='%s' "$sha")"
  if git show -s --format='%B' "$sha" | grep -Eiq "$sign_off_re"; then
    echo "  ok    ${sha}  ${subject}"
  else
    echo "  FAIL  ${sha}  ${subject}"
    failed=1
  fi
done < <(git rev-list --no-merges "$range")

if [ "$checked" -eq 0 ]; then
  echo "DCO: no commits to check in range '${range}'."
  exit 0
fi

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

echo "DCO check passed: all ${checked} commit(s) are signed off."
