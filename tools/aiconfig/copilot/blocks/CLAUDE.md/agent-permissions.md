## Agent permissions

GitHub Copilot has no repository permission file — there is no `.github` equivalent of
Claude Code's `settings.json`, no allow/deny list, and no `defaultMode`. What Copilot *does*
share with Claude Code is the part that actually matters for trust: **the configuration in
this repository is read and executed on the machine that checks it out**, so a change to
`.github/agents/`, `.github/skills/` or this file is a change to what runs on your laptop.
The rules below are therefore the same rules, minus the settings surface that does not exist
here.

**The C7 trust boundary.** `dotnet restore`/`build`/`test`/`format` execute PR-supplied
MSBuild targets, analyzers, source generators, and `nuget.config` sources in-process. On
another author's branch, run them from a throwaway copy outside the worktree, per
`.github/skills/review-pr/rigor-battery.md` (C7). Reviewing someone else's branch means
their persona wrappers and skill manifests are the ones on disk, so they sit inside the same
boundary as the build itself.

**Front matter is policed.** The `reconcile` workflow's `copilot-frontmatter` check walks
every tracked `.github/agents/*.agent.md` and `.github/skills/**/SKILL.md` with a strict
reader. A wrapper may carry only `name`, `description` and `tools`; a skill manifest only
`name` and `description`. `allowed-tools` is rejected outright — it grants unprompted tool
use — and so are `permissionMode`, `hooks`, `mcpServers`, `env`, `isolation`, `model`, and
any key outside those lists. Front matter the strict reader cannot parse (a quoted key,
`key :`, a flow or indented mapping, a duplicate key) fails the same way, and a skills tree
with no manifest is drift, not a pass. A wrapper whose filename stem does not match its
`name:` is an integrity error.

**No links.** Any *tracked* symlink **or submodule** (git mode `120000`/`160000`) at
`.github` itself, or anywhere under `.github/agents/`, `.github/skills/`, or at
`.github/copilot-instructions.md`, fails the gate. The loader follows such a link; the
gate's walks deliberately do not, so a filesystem-only check would ship unreviewed
configuration. The rule is decided by the **git index mode**, not by what the path resolves
to on your machine, so neither shape can hide: a **dangling** link (one pointing at `bin/`
or `obj/`) is absent in a checkout-only CI job and resolves to real configuration on any
machine that has built, and a **submodule** is checked out *empty* by CI while
`git submodule update --init` loads whatever it contains on a developer machine.

**Spelling is policed too.** The index is listed once from the work-tree root, and every
entry that **case- or normalization-folds** onto a policed path is compared with the
canonical spelling: on an APFS/NTFS checkout a tracked `.GitHub/agents/x.agent.md`,
`.github/agents/x.AGENT.MD` or `.github/skills/<x>/ſkill.md` (U+017F) materializes at the
path the loader reads, so it fails as "rename it" whatever its index mode. The same fold
decides file *names* in the walks as well as the index, and a tracked path whose bytes are
not valid UTF-8 is reported rather than decoded.

**Scope.** Only the three AI-config children are policed here — `.github/agents`,
`.github/skills` and `.github/copilot-instructions.md` — plus the `.github` root entry
itself, so a link or submodule *at* `.github` cannot hide the tree behind it.
`.github/workflows/`, `CODEOWNERS`, `ISSUE_TEMPLATE/` and `dependabot.yml` are deliberately
**out** of this gate's scope: they are reviewed on their own path, and policing them here
would red unrelated pull requests on a rule that was never about them.

**Coverage is not assumed.** A `.github` the checkout's index does not own — an export
dropped inside an enclosing checkout, whose index tracks nothing at or under any policed
child — is reported as *unverified*, not clean. An untracked subtree inside a `.github` the
checkout *does* own is ordinary work in progress, so it is walked, policed and noted rather
than skipped. Non-git checkouts skip with a reason, and the run's success line is qualified
accordingly.
