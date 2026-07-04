## Summary

<!-- New here? See CONTRIBUTING.md and the Definition-of-Done checklists in docs/engineering/checklists/ for the full contribution funnel. -->

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

## Testing and evidence

<!--
  Show how this change is verified. Note the tests you added or updated, then paste
  the commands you ran and their results. "Not applicable" is only valid for
  docs- or tooling-only changes — say which and why.
-->

- Tests added or updated:
- Verification (commands run + result):

```text
$ dotnet build -c Release
$ dotnet test -c Release
$ dotnet format --verify-no-changes
```

## Documentation impact

<!-- Pick one. If docs changed, link the files updated in this PR. -->

- [ ] No documentation changes required (behavior and public API are unchanged)
- [ ] Documentation updated in this PR (list the files)

## Checklist

- [ ] `dotnet build -c Release` passes locally
- [ ] `dotnet test` passes locally
- [ ] `dotnet format --verify-no-changes` passes locally
- [ ] Relevant Definition-of-Done checklist(s) in `docs/engineering/checklists/` are satisfied (name them in the summary, e.g. `03a`, `04a`, `05`, `11`)
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
