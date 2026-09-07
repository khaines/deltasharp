#!/usr/bin/env python3
"""Generate the GitHub Copilot AI-config tree from the canonical Claude Code tree.

`.claude/**` is CANONICAL. `.github/agents`, `.github/skills` and
`.github/copilot-instructions.md` are GENERATED from it and committed, so both runtimes read
the same conventions and neither tree can quietly rot into disagreement with the other.

That rot is not hypothetical: before this generator existed the two trees were both
hand-maintained, and 16 of 25 persona wrappers had drifted apart (one by 55 lines), as had
every skill. Whoever edited one tree had no way to know the other existed.

Three transforms, in this order:

1. **Front matter** is projected, never copied. Claude's schema is a strict superset
   (`model`, `permissionMode`, `disallowedTools` have no Copilot antecedent), so the mapping
   is lossy BY DESIGN in one direction only. Keys that express a RESTRICTION are a hard error
   rather than a silent drop — dropping one would widen the generated wrapper.
2. **Body substitutions** rewrite the mechanical Claude-only spellings (paths, tool names)
   listed in `copilot/substitutions.json`. A `leakage` post-check fails the run if any of
   those spellings survives, which is what catches a NEW `.claude/...` reference added to a
   canonical file by someone who never ran this script.
3. **`ai:block` overrides** carry the text that must genuinely DIFFER between runtimes, not
   merely be spelled differently. The canonical file marks a span with
   `<!-- ai:block <id> -->` … `<!-- ai:endblock <id> -->` (HTML comments: invisible when
   rendered, inert to both loaders, greppable), and the override text lives at
   `copilot/blocks/<canonical-relpath>/<id>.md`. Substitutions are NOT applied inside an
   override; it is authored already-Copilot-shaped.

The one thing overrides exist for today is the review council. Claude Code dispatches Claude
models only, so its red-team gate runs on a tier no voting seat uses. Copilot can still
dispatch a different VENDOR, so the Copilot `review-pr` is documented as the
vendor-decorrelated re-run for protected-domain changes — the escape hatch the Claude skill
says does not exist yet.

Exit codes match `tools/reconcile/roster-labels.py`: 0 in sync, 1 stale (drift to fix by
re-running with `--write`), 2 could not verify (a missing canonical tree, an unreadable
override — never reported as "in sync").
"""

from __future__ import annotations

import argparse
import difflib
import json
import os
import posixpath
import re
import sys
import tempfile
import unicodedata

REPO_ROOT_MARKER = "DeltaSharp.sln"

CLAUDE_DIR_NAME = ".claude"
GITHUB_DIR_NAME = ".github"

DEFAULT_OVERRIDES_DIR = os.path.join("tools", "aiconfig", "copilot")
SUBSTITUTIONS_NAME = "substitutions.json"
BLOCKS_DIR_NAME = "blocks"

CLAUDE_INSTRUCTIONS = "CLAUDE.md"
COPILOT_INSTRUCTIONS = "copilot-instructions.md"

SKILL_MANIFEST_NAME = "SKILL.md"
MARKDOWN_SUFFIX = ".md"
COPILOT_AGENT_SUFFIX = ".agent.md"

# The canonical Copilot capability list. Copilot's wrapper schema names capabilities, not
# tools, so several Claude tools collapse onto one capability. Emitted in this fixed order so
# the generated front matter is stable regardless of how the canonical list was written.
CAPABILITY_ORDER = ("read", "edit", "search", "shell")
TOOL_CAPABILITIES = {
    "Read": "read",
    "Grep": "search",
    "Glob": "search",
    "Edit": "edit",
    "Write": "edit",
    "Bash": "shell",
    "NotebookEdit": "edit",
    "WebFetch": "read",
    "WebSearch": "search",
}

# Front-matter keys that survive into the Copilot wrapper, in emission order.
COPILOT_AGENT_KEYS = ("name", "description", "tools")
# Keys that are dropped because Copilot has no antecedent for them and their absence does not
# widen anything.
DROPPED_AGENT_KEYS = ("model",)
# Keys that express a RESTRICTION. Dropping one silently would make the generated wrapper
# MORE capable than the canonical one, so they are a hard error instead. None are present
# today, which is exactly why this is cheap to enforce now.
RESTRICTION_AGENT_KEYS = ("permissionMode", "disallowedTools")

COPILOT_SKILL_KEYS = ("name", "description")
DROPPED_SKILL_KEYS = ("model", "argument-hint")

BLOCK_OPEN = re.compile(r"^[ \t]*<!--[ \t]*ai:block[ \t]+([A-Za-z0-9][A-Za-z0-9_-]*)[ \t]*-->[ \t]*$")
BLOCK_CLOSE = re.compile(r"^[ \t]*<!--[ \t]*ai:endblock[ \t]+([A-Za-z0-9][A-Za-z0-9_-]*)[ \t]*-->[ \t]*$")

PROVENANCE = (
    "<!-- GENERATED from {source} by tools/aiconfig/generate-copilot.py — do not edit; "
    "edit the source and re-run with --write. -->"
)

# A markdown link target, used by the instructions link rebase. Inline links only; reference
# definitions are handled by the same routine because they are matched separately.
INLINE_LINK = re.compile(r"(?<!\!)\[([^\]]*)\]\(([^)\s]+)(\s+\"[^\"]*\")?\)")
LINK_DEFINITION = re.compile(r"^(\[[^\]]+\]:\s*)(\S+)(.*)$")
HAS_SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")


class GeneratorError(Exception):
    """A condition the generator refuses to guess about (exit 2)."""


def _fold(value: str) -> str:
    return unicodedata.normalize("NFC", value).casefold()


def _log(message: str) -> None:
    print(message)


# --------------------------------------------------------------------------------------
# Front matter
# --------------------------------------------------------------------------------------


def read_frontmatter(text: str, source: str) -> "tuple[dict[str, str] | None, str]":
    """`(mapping, body)` for a file opening with a `---` fence, else `(None, text)`.

    Deliberately small and strict, matching the reader in `tools/reconcile/roster-labels.py`:
    top-level `key: value` lines only. A shape it cannot read is an ERROR, never a silent
    skip — a wrapper whose front matter this cannot parse is one whose `tools:` it cannot
    project, and emitting it unprojected would be worse than failing.
    """
    lines = text.split("\n")
    if not lines or lines[0].strip() != "---":
        return None, text
    mapping: "dict[str, str]" = {}
    for index in range(1, len(lines)):
        line = lines[index]
        if line.strip() == "---":
            return mapping, "\n".join(lines[index + 1 :])
        if not line.strip():
            continue
        match = re.match(r"^([A-Za-z_][A-Za-z0-9_-]*):(?:[ \t]+(.*))?$", line)
        if match is None:
            if line[:1] in (" ", "\t") and mapping:
                # A folded/continued scalar belonging to the previous key.
                last = next(reversed(mapping))
                mapping[last] = (mapping[last] + " " + line.strip()).strip()
                continue
            raise GeneratorError(
                f"{source}: front-matter line {index + 1} is not a top-level `key: value` "
                f"pair this generator can project: {line!r}"
            )
        key, value = match.group(1), (match.group(2) or "").strip()
        if value in (">", ">-", ">+", "|", "|-", "|+"):
            # A block-scalar indicator, not the value. The continuation lines below carry the
            # text; both trees fold these to a single line, so the indicator itself is noise.
            value = ""
        if key in mapping:
            raise GeneratorError(
                f"{source}: duplicate front-matter key {key!r} — the loader takes the LAST "
                f"and this reader took the first, so the file must not carry both"
            )
        mapping[key] = value
    raise GeneratorError(f"{source}: front matter opens with `---` but is never closed")


def parse_tools(value: str, source: str) -> "list[str]":
    """The tool names in a `tools:` value, accepting `[A, B]` and `A, B` spellings."""
    stripped = value.strip()
    if stripped.startswith("[") and stripped.endswith("]"):
        stripped = stripped[1:-1]
    names = [item.strip().strip("\"'") for item in stripped.split(",")]
    names = [name for name in names if name]
    if not names:
        raise GeneratorError(f"{source}: `tools:` is present but lists no tools")
    return names


def project_tools(names: "list[str]", source: str) -> "list[str]":
    """Claude tool names projected onto Copilot capabilities, deduped, in canonical order."""
    capabilities = set()
    for name in names:
        capability = TOOL_CAPABILITIES.get(name)
        if capability is None:
            raise GeneratorError(
                f"{source}: no Copilot capability is known for the tool {name!r} — add it to "
                f"TOOL_CAPABILITIES with a deliberate mapping rather than dropping it"
            )
        capabilities.add(capability)
    return [item for item in CAPABILITY_ORDER if item in capabilities]


def project_agent_frontmatter(mapping: "dict[str, str]", source: str) -> str:
    """The Copilot wrapper front-matter block (including both fences)."""
    for key in RESTRICTION_AGENT_KEYS:
        if key in mapping:
            raise GeneratorError(
                f"{source}: `{key}:` expresses a RESTRICTION that the Copilot wrapper schema "
                f"cannot carry. Dropping it would make the generated wrapper MORE capable "
                f"than the canonical one, so this generator refuses to guess — decide "
                f"explicitly how the restriction should be expressed for Copilot"
            )
    known = set(COPILOT_AGENT_KEYS) | set(DROPPED_AGENT_KEYS) | set(RESTRICTION_AGENT_KEYS)
    unknown = sorted(set(mapping) - known)
    if unknown:
        raise GeneratorError(
            f"{source}: unrecognized front-matter key(s) {unknown} — this generator fails "
            f"closed rather than emitting a Copilot wrapper it does not understand"
        )
    for required in ("name", "description"):
        if not mapping.get(required):
            raise GeneratorError(f"{source}: front matter has no `{required}:`")
    lines = ["---", f"name: {mapping['name']}", f"description: {mapping['description']}"]
    if "tools" in mapping:
        capabilities = project_tools(parse_tools(mapping["tools"], source), source)
        rendered = ", ".join(f'"{item}"' for item in capabilities)
        lines.append(f"tools: [{rendered}]")
    lines.append("---")
    return "\n".join(lines)


def project_skill_frontmatter(mapping: "dict[str, str]", source: str) -> str:
    """The Copilot skill manifest front-matter block (including both fences)."""
    known = set(COPILOT_SKILL_KEYS) | set(DROPPED_SKILL_KEYS)
    unknown = sorted(set(mapping) - known)
    if unknown:
        raise GeneratorError(
            f"{source}: unrecognized skill front-matter key(s) {unknown} — fail closed"
        )
    for required in ("name", "description"):
        if not mapping.get(required):
            raise GeneratorError(f"{source}: skill front matter has no `{required}:`")
    return "\n".join(
        ["---", f"name: {mapping['name']}", f"description: {mapping['description']}", "---"]
    )


# --------------------------------------------------------------------------------------
# Blocks and substitutions
# --------------------------------------------------------------------------------------


def split_blocks(text: str, source: str) -> "list[tuple[str | None, str]]":
    """`text` as `(block_id_or_None, segment)` parts, in order.

    A `None` id is passthrough text (substituted); a named id is a span an override may
    replace verbatim.
    """
    parts: "list[tuple[str | None, str]]" = []
    buffer: "list[str]" = []
    open_id: "str | None" = None
    for number, line in enumerate(text.split("\n"), start=1):
        opened = BLOCK_OPEN.match(line)
        closed = BLOCK_CLOSE.match(line)
        if opened:
            if open_id is not None:
                raise GeneratorError(
                    f"{source}:{number}: ai:block {opened.group(1)!r} opens inside still-open "
                    f"block {open_id!r}; blocks do not nest"
                )
            parts.append((None, "\n".join(buffer)))
            buffer = []
            open_id = opened.group(1)
            continue
        if closed:
            if open_id is None:
                raise GeneratorError(
                    f"{source}:{number}: ai:endblock {closed.group(1)!r} with no open block"
                )
            if closed.group(1) != open_id:
                raise GeneratorError(
                    f"{source}:{number}: ai:endblock {closed.group(1)!r} closes block "
                    f"{open_id!r}"
                )
            parts.append((open_id, "\n".join(buffer)))
            buffer = []
            open_id = None
            continue
        buffer.append(line)
    if open_id is not None:
        raise GeneratorError(f"{source}: ai:block {open_id!r} is never closed")
    parts.append((None, "\n".join(buffer)))
    return parts


def apply_substitutions(text: str, table: "list[dict[str, str]]") -> str:
    for entry in table:
        if entry["kind"] == "literal":
            text = text.replace(entry["pattern"], entry["replace"])
        else:
            text = re.sub(entry["pattern"], entry["replace"], text)
    return text


def check_leakage(text: str, tokens: "list[str]", source: str, target: str) -> None:
    """Fail if a Claude-only spelling survived substitution into passthrough output.

    The patterns match a PATH rather than a bare mention. `.claude/commands` in a Copilot file
    points the reader at a tree Copilot does not own and is a bug; prose naming `.claude/` as
    a directory — a ledger row about the config-trust surface — is honest cross-reference and
    stays. Anything genuinely runtime-specific belongs in an `ai:block` override, which is not
    leakage-checked at all.
    """
    hits = sorted({token for token in tokens if re.search(token, text)})
    if hits:
        raise GeneratorError(
            f"{target}: Claude-only spelling(s) {hits} survived into generated output from "
            f"passthrough text in {source}. Either add a substitution to "
            f"{SUBSTITUTIONS_NAME}, or wrap the span in an `ai:block` and write a Copilot "
            f"override for it — do NOT hand-edit the generated file"
        )


# --------------------------------------------------------------------------------------
# Link rebasing (canonical lives at the repo root, the mirror lives in `.github/`)
# --------------------------------------------------------------------------------------


def rebase_target(target: str, from_dir: str, to_dir: str) -> str:
    """A repo-relative markdown link re-expressed from `from_dir` to `to_dir`."""
    if not target or HAS_SCHEME.match(target) or target.startswith(("#", "/", "<")):
        return target
    anchor = ""
    if "#" in target:
        target, _, fragment = target.partition("#")
        anchor = "#" + fragment
        if not target:
            return anchor
    resolved = posixpath.normpath(posixpath.join(from_dir, target)) if from_dir else target
    rebased = posixpath.relpath(resolved, to_dir or ".")
    return rebased + anchor


def rebase_links(text: str, from_dir: str, to_dir: str) -> str:
    """Rebase every inline link and link definition, skipping fenced and inline code.

    Structural, not a regex over `](docs/`: the canonical file links to `docs/`, `tools/`
    AND `.github/workflows/`, and only a real resolve-then-relativize gets all three right
    (`.github/workflows/reconcile.yml` must become `workflows/reconcile.yml`, not
    `../.github/workflows/reconcile.yml`).
    """
    out: "list[str]" = []
    fenced = False
    for line in text.split("\n"):
        if line.lstrip().startswith("```"):
            fenced = not fenced
            out.append(line)
            continue
        if fenced:
            out.append(line)
            continue
        definition = LINK_DEFINITION.match(line)
        if definition:
            out.append(
                definition.group(1)
                + rebase_target(definition.group(2), from_dir, to_dir)
                + definition.group(3)
            )
            continue
        out.append(_rebase_inline(line, from_dir, to_dir))
    return "\n".join(out)


def _rebase_inline(line: str, from_dir: str, to_dir: str) -> str:
    """Rebase inline links in `line`, leaving spans inside backticks untouched."""
    pieces = line.split("`")
    for index in range(0, len(pieces), 2):  # even pieces are outside inline code
        pieces[index] = INLINE_LINK.sub(
            lambda m: "[{}]({}{})".format(
                m.group(1), rebase_target(m.group(2), from_dir, to_dir), m.group(3) or ""
            ),
            pieces[index],
        )
    return "`".join(pieces)


# --------------------------------------------------------------------------------------
# Rendering
# --------------------------------------------------------------------------------------


class Generator:
    def __init__(self, claude_root: str, github_root: str, overrides_dir: str) -> None:
        self.claude_root = claude_root
        self.github_root = github_root
        self.overrides_dir = overrides_dir
        self.blocks_dir = os.path.join(overrides_dir, BLOCKS_DIR_NAME)
        config_path = os.path.join(overrides_dir, SUBSTITUTIONS_NAME)
        try:
            with open(config_path, encoding="utf-8") as handle:
                config = json.load(handle)
        except OSError as exc:
            raise GeneratorError(f"cannot read {config_path}: {exc}") from exc
        except json.JSONDecodeError as exc:
            raise GeneratorError(f"{config_path} is not valid JSON: {exc}") from exc
        self.substitutions = config["substitutions"]
        self.leakage = config["leakage"]
        self.used_overrides: "set[str]" = set()

    # -- overrides -------------------------------------------------------------------
    def override_path(self, relpath: str, block_id: str) -> str:
        return os.path.join(self.blocks_dir, relpath.replace("/", os.sep), block_id + ".md")

    def load_override(self, relpath: str, block_id: str) -> "str | None":
        path = self.override_path(relpath, block_id)
        if not os.path.isfile(path):
            return None
        self.used_overrides.add(os.path.normpath(path))
        try:
            with open(path, encoding="utf-8") as handle:
                return handle.read().rstrip("\n")
        except OSError as exc:
            raise GeneratorError(f"cannot read override {path}: {exc}") from exc

    def unused_overrides(self) -> "list[str]":
        """Override files no canonical `ai:block` marker claimed — stale, and an error.

        This is what stops overrides rotting silently. Restructure a canonical file and
        rename or delete a block, and the override that used to feed it becomes dead text
        nobody reads while the generated file quietly reverts to substituted passthrough.
        """
        found: "list[str]" = []
        if not os.path.isdir(self.blocks_dir):
            return found
        for dirpath, _dirnames, filenames in os.walk(self.blocks_dir):
            for filename in filenames:
                if not filename.endswith(MARKDOWN_SUFFIX):
                    continue
                path = os.path.normpath(os.path.join(dirpath, filename))
                if path not in self.used_overrides:
                    found.append(os.path.relpath(path, self.overrides_dir))
        return sorted(found)

    # -- body ------------------------------------------------------------------------
    def render_body(self, body: str, relpath: str, target: str) -> str:
        rendered: "list[str]" = []
        for block_id, segment in split_blocks(body, relpath):
            if block_id is None:
                substituted = apply_substitutions(segment, self.substitutions)
                check_leakage(substituted, self.leakage, relpath, target)
                rendered.append(substituted)
                continue
            override = self.load_override(relpath, block_id)
            # An override is authored already-Copilot-shaped, so it is neither substituted
            # nor leakage-checked. A marker with NO override is ordinary passthrough: the
            # span is runtime-neutral today and may grow an override later.
            rendered.append(
                override if override is not None
                else apply_substitutions(segment, self.substitutions)
            )
        return "\n".join(rendered)

    # -- files -----------------------------------------------------------------------
    def project_values(self, mapping: "dict[str, str]", relpath: str, target: str) -> "dict[str, str]":
        """Front-matter VALUES are passthrough text too, so they are substituted and checked.

        The `description:` of a skill manifest names the council's models, which is exactly the
        kind of Claude-only spelling the substitution table exists to rewrite. Skipping front
        matter would have let it through silently — the one place no reader would look for it.
        """
        out = dict(mapping)
        for key in ("description",):
            if key in out:
                value = apply_substitutions(out[key], self.substitutions)
                check_leakage(value, self.leakage, f"{relpath} ({key}:)", target)
                out[key] = value
        return out

    def render_agent(self, path: str, relpath: str) -> "tuple[str, str]":
        text = _read(path)
        mapping, body = read_frontmatter(text, relpath)
        if mapping is None:
            raise GeneratorError(f"{relpath}: a persona wrapper must open with `---`")
        target_name = mapping["name"] + COPILOT_AGENT_SUFFIX
        target = posixpath.join(GITHUB_DIR_NAME, "agents", target_name)
        front = project_agent_frontmatter(self.project_values(mapping, relpath, target), relpath)
        stem = os.path.basename(path)[: -len(MARKDOWN_SUFFIX)]
        if stem != mapping["name"]:
            raise GeneratorError(
                f"{relpath}: filename stem {stem!r} does not match `name: {mapping['name']}`"
            )
        rendered = self.render_body(body, relpath, target)
        return target, _assemble(front, relpath, rendered)

    def render_skill_file(self, path: str, relpath: str, target: str) -> str:
        text = _read(path)
        if _fold(os.path.basename(path)) == _fold(SKILL_MANIFEST_NAME):
            mapping, body = read_frontmatter(text, relpath)
            if mapping is None:
                raise GeneratorError(f"{relpath}: a skill manifest must open with `---`")
            front = project_skill_frontmatter(self.project_values(mapping, relpath, target), relpath)
            return _assemble(front, relpath, self.render_body(body, relpath, target))
        return _assemble(None, relpath, self.render_body(text, relpath, target))

    def render_instructions(self, path: str, relpath: str, target: str) -> str:
        body = self.render_body(_read(path), relpath, target)
        body = rebase_links(body, posixpath.dirname(relpath), GITHUB_DIR_NAME)
        body = body.replace(
            "# DeltaSharp — Claude Code Instructions", "# DeltaSharp — Copilot Instructions"
        )
        return _assemble(None, relpath, body)

    # -- the whole tree ---------------------------------------------------------------
    def build(self) -> "dict[str, str]":
        """Every generated file as `{repo-relative target: content}`."""
        out: "dict[str, str]" = {}
        agents_dir = os.path.join(self.claude_root, CLAUDE_DIR_NAME, "agents")
        if not os.path.isdir(agents_dir):
            raise GeneratorError(f"canonical agents directory not found: {agents_dir}")
        names = sorted(
            name for name in os.listdir(agents_dir)
            if name.endswith(MARKDOWN_SUFFIX) and os.path.isfile(os.path.join(agents_dir, name))
        )
        if not names:
            raise GeneratorError(f"no persona wrappers under {agents_dir}")
        for name in names:
            relpath = posixpath.join(CLAUDE_DIR_NAME, "agents", name)
            target, content = self.render_agent(os.path.join(agents_dir, name), relpath)
            out[target] = content

        skills_dir = os.path.join(self.claude_root, CLAUDE_DIR_NAME, "skills")
        if not os.path.isdir(skills_dir):
            raise GeneratorError(f"canonical skills directory not found: {skills_dir}")
        manifests = 0
        for dirpath, dirnames, filenames in os.walk(skills_dir):
            dirnames.sort()
            for filename in sorted(filenames):
                if not filename.endswith(MARKDOWN_SUFFIX):
                    continue
                path = os.path.join(dirpath, filename)
                inner = os.path.relpath(path, skills_dir).replace(os.sep, "/")
                relpath = posixpath.join(CLAUDE_DIR_NAME, "skills", inner)
                target = posixpath.join(GITHUB_DIR_NAME, "skills", inner)
                out[target] = self.render_skill_file(path, relpath, target)
                if _fold(filename) == _fold(SKILL_MANIFEST_NAME):
                    manifests += 1
        if manifests == 0:
            raise GeneratorError(f"no {SKILL_MANIFEST_NAME} manifests under {skills_dir}")

        instructions = os.path.join(self.claude_root, CLAUDE_INSTRUCTIONS)
        if not os.path.isfile(instructions):
            raise GeneratorError(f"canonical instructions not found: {instructions}")
        target = posixpath.join(GITHUB_DIR_NAME, COPILOT_INSTRUCTIONS)
        out[target] = self.render_instructions(instructions, CLAUDE_INSTRUCTIONS, target)

        stale = self.unused_overrides()
        if stale:
            raise GeneratorError(
                "override file(s) no canonical `ai:block` marker claims: "
                + ", ".join(stale)
                + " — the marker was renamed or removed, so this text reaches nobody while "
                "the generated file silently reverts to substituted passthrough. Restore the "
                "marker or delete the override."
            )
        return out


def _read(path: str) -> str:
    try:
        with open(path, encoding="utf-8") as handle:
            return handle.read()
    except OSError as exc:
        raise GeneratorError(f"cannot read {path}: {exc}") from exc
    except UnicodeDecodeError as exc:
        raise GeneratorError(f"{path} is not valid UTF-8: {exc}") from exc


def _assemble(front: "str | None", source: str, body: str) -> str:
    """Front matter, provenance, body — with the fence still at byte 0.

    The provenance comment goes AFTER the closing `---` on purpose: the reconcile gate's
    strict reader requires the fence at line 0, so a comment above it would fail the very
    check this tree exists to satisfy.
    """
    pieces = []
    if front is not None:
        pieces.append(front)
    pieces.append(PROVENANCE.format(source=source))
    pieces.append("")
    pieces.append(body.lstrip("\n"))
    return "\n".join(pieces).rstrip("\n") + "\n"


# --------------------------------------------------------------------------------------
# check / write
# --------------------------------------------------------------------------------------


def existing_tree(github_root: str) -> "dict[str, str]":
    """Every file currently in the generated locations, as `{target: content}`."""
    out: "dict[str, str]" = {}
    for sub in ("agents", "skills"):
        base = os.path.join(github_root, GITHUB_DIR_NAME, sub)
        for dirpath, _dirnames, filenames in os.walk(base):
            for filename in filenames:
                if not filename.endswith(MARKDOWN_SUFFIX):
                    continue
                path = os.path.join(dirpath, filename)
                rel = os.path.relpath(path, os.path.join(github_root, GITHUB_DIR_NAME))
                out[posixpath.join(GITHUB_DIR_NAME, rel.replace(os.sep, "/"))] = _read(path)
    instructions = os.path.join(github_root, GITHUB_DIR_NAME, COPILOT_INSTRUCTIONS)
    if os.path.isfile(instructions):
        out[posixpath.join(GITHUB_DIR_NAME, COPILOT_INSTRUCTIONS)] = _read(instructions)
    return out


def diff_trees(expected: "dict[str, str]", actual: "dict[str, str]") -> "list[str]":
    """Human-readable drift lines; empty when the generated tree is in sync."""
    lines: "list[str]" = []
    for target in sorted(set(expected) | set(actual)):
        want = expected.get(target)
        have = actual.get(target)
        if want == have:
            continue
        if want is None:
            lines.append(f"{target}: present but NOT generated by the canonical tree (delete it)")
            continue
        if have is None:
            lines.append(f"{target}: MISSING (the canonical tree generates it)")
            continue
        lines.append(f"{target}: STALE")
        lines.extend(
            "    " + item.rstrip("\n")
            for item in difflib.unified_diff(
                have.splitlines(True), want.splitlines(True),
                fromfile="committed", tofile="generated", n=2,
            )
        )
    return lines


def write_tree(github_root: str, expected: "dict[str, str]", actual: "dict[str, str]") -> int:
    changed = 0
    for target, content in sorted(expected.items()):
        path = os.path.join(github_root, target.replace("/", os.sep))
        if actual.get(target) == content:
            continue
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(content)
        changed += 1
    for target in sorted(set(actual) - set(expected)):
        os.remove(os.path.join(github_root, target.replace("/", os.sep)))
        changed += 1
    return changed


# --------------------------------------------------------------------------------------
# selftest
# --------------------------------------------------------------------------------------

SELFTEST_ASSERTIONS = 41


def _selftest(strict: bool) -> int:
    passed = 0
    failed = 0

    def check(condition: bool, label: str) -> None:
        nonlocal passed, failed
        if condition:
            passed += 1
            _log(f"  ok  - {label}")
        else:
            failed += 1
            _log(f" FAIL - {label}")

    def raises(fn, needle: str, label: str) -> None:
        try:
            fn()
        except GeneratorError as exc:
            check(needle in str(exc), f"{label} (message names {needle!r})")
        else:
            check(False, f"{label} (no GeneratorError raised)")

    # -- front matter ------------------------------------------------------------------
    mapping, body = read_frontmatter("---\nname: a\ndescription: d\n---\nbody\n", "x")
    check(mapping == {"name": "a", "description": "d"} and body == "body\n", "front matter parses")
    check(read_frontmatter("no fence\n", "x")[0] is None, "a file with no fence has no front matter")
    raises(lambda: read_frontmatter("---\nname: a\n", "x"), "never closed", "an unclosed fence fails")
    raises(
        lambda: read_frontmatter("---\nname: a\nname: b\n---\n", "x"),
        "duplicate", "a duplicate key fails (the loader takes the last, this reader the first)",
    )
    folded, _ = read_frontmatter("---\ndescription: >-\n  one\n  two\n---\n", "x")
    check(folded == {"description": "one two"}, "a folded scalar joins onto its key")

    # -- tools projection ---------------------------------------------------------------
    check(
        project_tools(["Read", "Grep", "Glob", "Edit", "Write", "Bash"], "x")
        == ["read", "edit", "search", "shell"],
        "the full engineering tool list projects onto all four capabilities, in fixed order",
    )
    check(
        project_tools(["Read", "Grep", "Glob", "Edit", "Write"], "x")
        == ["read", "edit", "search"],
        "a persona with no Bash does NOT get `shell` (developer-relations-community-lead)",
    )
    check(project_tools(["Write", "Edit"], "x") == ["edit"], "capabilities are deduped")
    raises(lambda: project_tools(["Telepathy"], "x"), "no Copilot capability", "an unknown tool fails closed")
    check(parse_tools("[Read, Bash]", "x") == ["Read", "Bash"], "a bracketed tools list parses")
    check(parse_tools("Read, Bash", "x") == ["Read", "Bash"], "a bare tools list parses")

    # -- agent front matter -------------------------------------------------------------
    projected = project_agent_frontmatter(
        {"name": "a", "description": "d", "tools": "[Read, Bash]", "model": "sonnet"}, "x"
    )
    check("model:" not in projected, "`model:` is dropped (Copilot has no antecedent)")
    check('tools: ["read", "shell"]' in projected, "tools are emitted as a Copilot capability list")
    check(projected.startswith("---\n") and projected.endswith("\n---"), "both fences are emitted")
    for restriction in RESTRICTION_AGENT_KEYS:
        raises(
            lambda r=restriction: project_agent_frontmatter(
                {"name": "a", "description": "d", r: "plan"}, "x"
            ),
            "RESTRICTION",
            f"`{restriction}:` is a hard error, never a silent drop that WIDENS the wrapper",
        )
    raises(
        lambda: project_agent_frontmatter({"name": "a", "description": "d", "hooks": "x"}, "x"),
        "unrecognized", "an unknown front-matter key fails closed",
    )
    raises(
        lambda: project_agent_frontmatter({"name": "a"}, "x"),
        "no `description:`", "a wrapper with no description fails",
    )

    # -- blocks -------------------------------------------------------------------------
    parts = split_blocks("a\n<!-- ai:block b1 -->\ninner\n<!-- ai:endblock b1 -->\nz", "x")
    check(
        [item[0] for item in parts] == [None, "b1", None] and parts[1][1] == "inner",
        "a block splits into passthrough / block / passthrough",
    )
    raises(lambda: split_blocks("<!-- ai:block b -->\n", "x"), "never closed", "an unclosed block fails")
    raises(
        lambda: split_blocks("<!-- ai:endblock b -->\n", "x"), "no open block",
        "a stray endblock fails",
    )
    raises(
        lambda: split_blocks("<!-- ai:block a -->\n<!-- ai:block b -->\n", "x"), "do not nest",
        "a nested block fails",
    )
    raises(
        lambda: split_blocks("<!-- ai:block a -->\n<!-- ai:endblock b -->\n", "x"), "closes block",
        "a mismatched endblock id fails",
    )

    # -- substitutions and leakage ------------------------------------------------------
    table = [
        {"kind": "literal", "pattern": ".claude/skills", "replace": ".github/skills"},
        {"kind": "literal", "pattern": ".claude/agents", "replace": ".github/agents"},
        {"kind": "regex", "pattern": r"\.github/agents/([A-Za-z0-9._-]+)\.md\b",
         "replace": ".github/agents/\\1.agent.md"},
    ]
    check(
        apply_substitutions(".claude/agents/tw.md", table) == ".github/agents/tw.agent.md",
        "the suffix regex runs AFTER the directory rewrite that produces its input",
    )
    check(
        apply_substitutions(".claude/skills/review-pr/SKILL.md", table)
        == ".github/skills/review-pr/SKILL.md",
        "the longer `.claude/skills` path rewrites before the shorter root would",
    )
    raises(
        lambda: check_leakage("see .claude/hooks", [r"\.claude/[A-Za-z]"], "src", "dst"), "survived into",
        "a surviving Claude-only spelling is an ERROR, not silent output",
    )
    check_leakage(".github/skills/x and a bare `.claude/` mention", [r"\.claude/[A-Za-z]"], "src", "dst")
    check(True, "clean passthrough text passes the leakage check")

    # -- link rebasing ------------------------------------------------------------------
    check(rebase_target("docs/adr/0015.md", "", ".github") == "../docs/adr/0015.md",
          "a root-relative doc link becomes ../docs/... from .github/")
    check(rebase_target(".github/workflows/reconcile.yml", "", ".github") == "workflows/reconcile.yml",
          "a .github link becomes workflows/..., which a naive s#docs/#../docs/# would miss")
    check(rebase_target("https://x/y", "", ".github") == "https://x/y", "an absolute URL is untouched")
    check(rebase_target("#anchor", "", ".github") == "#anchor", "a bare anchor is untouched")
    check(rebase_target("docs/x.md#s", "", ".github") == "../docs/x.md#s", "an anchored link keeps its fragment")
    check(
        rebase_links("`[a](docs/x.md)`", "", ".github") == "`[a](docs/x.md)`",
        "a link inside inline code is NOT rebased",
    )
    check(
        rebase_links("```\n[a](docs/x.md)\n```", "", ".github") == "```\n[a](docs/x.md)\n```",
        "a link inside a fenced block is NOT rebased",
    )
    check(
        rebase_links("[a](docs/x.md)", "", ".github") == "[a](../docs/x.md)",
        "an ordinary inline link IS rebased",
    )

    # -- assembly and staleness ---------------------------------------------------------
    assembled = _assemble("---\nname: a\n---", "src.md", "body")
    check(assembled.startswith("---\n"), "the front-matter fence stays at byte 0 for the gate")
    check("GENERATED from src.md" in assembled, "provenance names the canonical source")

    with tempfile.TemporaryDirectory() as tmp:
        os.makedirs(os.path.join(tmp, DEFAULT_OVERRIDES_DIR, BLOCKS_DIR_NAME, "x"), exist_ok=True)
        with open(os.path.join(tmp, DEFAULT_OVERRIDES_DIR, SUBSTITUTIONS_NAME), "w",
                  encoding="utf-8") as handle:
            json.dump({"substitutions": [], "leakage": []}, handle)
        with open(os.path.join(tmp, DEFAULT_OVERRIDES_DIR, BLOCKS_DIR_NAME, "x", "ghost.md"),
                  "w", encoding="utf-8") as handle:
            handle.write("orphan\n")
        gen = Generator(tmp, tmp, os.path.join(tmp, DEFAULT_OVERRIDES_DIR))
        check(
            gen.unused_overrides() == [os.path.join(BLOCKS_DIR_NAME, "x", "ghost.md")],
            "an override no ai:block marker claims is reported STALE, never ignored",
        )

    check(
        diff_trees({"a": "x"}, {})[0].endswith("MISSING (the canonical tree generates it)"),
        "an absent target is STALE, never a trivial pass",
    )
    check(
        "NOT generated" in diff_trees({}, {"a": "x"})[0],
        "a target the canonical tree does not generate is reported for deletion",
    )
    check(diff_trees({"a": "x"}, {"a": "x"}) == [], "an in-sync tree yields no drift lines")

    _log("")
    if failed:
        _log(f"::error::generator selftest: {failed} assertion(s) failed")
        return 1
    if passed != SELFTEST_ASSERTIONS:
        _log(
            f"::error::generator selftest: {passed} assertion(s) executed, expected "
            f"{SELFTEST_ASSERTIONS} (update SELFTEST_ASSERTIONS)"
        )
        return 1
    _log(f"generator selftest: {passed} assertion(s) passed")
    return 0


# --------------------------------------------------------------------------------------


def find_repo_root(start: str) -> str:
    current = os.path.abspath(start)
    while True:
        if os.path.isfile(os.path.join(current, REPO_ROOT_MARKER)):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return os.path.abspath(start)
        current = parent


def main(argv: "list[str] | None" = None) -> int:
    parser = argparse.ArgumentParser(
        description="Generate the Copilot AI-config tree from the canonical Claude Code tree.",
    )
    parser.add_argument("--check", action="store_true",
                        help="report staleness and exit 1 (the default)")
    parser.add_argument("--write", action="store_true", help="regenerate in place")
    parser.add_argument("--claude-root", default=None, help="repo root holding .claude/ and CLAUDE.md")
    parser.add_argument("--github-root", default=None, help="repo root holding .github/")
    parser.add_argument("--overrides", default=None, help="override tree (substitutions + blocks)")
    parser.add_argument("--selftest", action="store_true", help="run the generator's own tests")
    parser.add_argument("--require-full-coverage", action="store_true",
                        help="in CI: a reduced assertion count is a failure")
    args = parser.parse_args(argv)

    if args.selftest:
        strict = args.require_full_coverage or bool(
            os.environ.get("CI") or os.environ.get("GITHUB_ACTIONS")
        )
        return _selftest(strict)

    root = find_repo_root(os.curdir)
    claude_root = args.claude_root or root
    github_root = args.github_root or root
    overrides = args.overrides or os.path.join(root, DEFAULT_OVERRIDES_DIR)

    try:
        generator = Generator(claude_root, github_root, overrides)
        expected = generator.build()
        actual = existing_tree(github_root)
    except GeneratorError as exc:
        _log(f"::error::{exc}")
        _log("the Copilot tree COULD NOT BE VERIFIED (exit 2) — this is not 'in sync'")
        return 2

    if args.write:
        changed = write_tree(github_root, expected, actual)
        _log(
            f"copilot config: {len(expected)} file(s) generated, {changed} written"
            if changed else f"copilot config: {len(expected)} file(s) already in sync"
        )
        return 0

    drift = diff_trees(expected, actual)
    if drift:
        for line in drift:
            _log(line)
        _log("")
        _log(
            "::error::the committed Copilot tree is STALE. `.claude/**` is canonical — edit "
            "there, then re-run `python3 tools/aiconfig/generate-copilot.py --write`. Never "
            "hand-edit `.github/agents`, `.github/skills` or `.github/copilot-instructions.md`."
        )
        return 1
    _log(f"copilot config: {len(expected)} generated file(s) are in sync with .claude/**")
    return 0


if __name__ == "__main__":
    sys.exit(main())
