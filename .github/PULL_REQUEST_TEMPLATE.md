## Summary

<!-- What does this PR change, and why? -->

<!-- Link the issues this PR resolves, e.g. "Closes #123" (one per line). -->
Closes #

## Type of change

- [ ] Build / CI / tooling
- [ ] New feature
- [ ] Bug fix
- [ ] Documentation
- [ ] Refactor (no behavior change)

## Public API & compatibility

<!--
  Required by STORY-01.5.1. Public surface changes must update
  `src/DeltaSharp.Core/PublicAPI.Unshipped.txt`; see
  docs/engineering/design/api-governance.md. Replace "None" where this PR has impact.
-->

- Public API change: None — or describe the public members added, removed, or changed.
- Compatibility / release-note impact: None — or describe breaking changes, migration
  steps, and the release note this needs.

## Checklist

- [ ] `dotnet build -c Release` passes locally
- [ ] `dotnet test` passes locally
- [ ] `dotnet format --verify-no-changes` passes locally
- [ ] Relevant Definition-of-Done checklist(s) in `docs/engineering/checklists/` are satisfied
- [ ] Public API / compatibility impact is described above and `PublicAPI.Unshipped.txt` is updated for any public surface change
- [ ] Docs updated if public API or behavior changed

## Developer Certificate of Origin (DCO)

By submitting this PR I certify the [Developer Certificate of Origin](https://developercertificate.org/).
**Every commit must include a `Signed-off-by:` trailer.**

- [ ] All commits are signed off (`git commit -s`)

> If the **DCO** check fails, sign off your existing commits and re-push:
>
> ```bash
> git rebase --signoff origin/main
> git push --force-with-lease
> ```
