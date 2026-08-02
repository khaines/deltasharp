#!/usr/bin/env python3
"""Trim every corpus axis in a test file to one element and report which trims stay GREEN.

WHY THIS EXISTS
    A corpus axis -- a literal list a test iterates to build cells -- is only load-bearing if
    removing entries from it can turn the test RED. An axis that can be trimmed to a single element
    while the suite stays green is measuring nothing, and the claim it was added to support has
    quietly stopped being executed. That failure is invisible: the suite is green either way.

    In DeltaSharp's path-disclosure hygiene suite this was found the expensive way. A commit repaired
    one axis (slicing it from a shared constant) and, ten lines later in the same diff, wrote a second
    literal list beside it with no pin at all. Trimming the second axis to `[string.Empty]` left the
    test passing, so the directional half of the claim it supported could have stopped being measured
    without anything going red.

WHAT A PINNED AXIS LOOKS LIKE
    One of three, and the test should carry whichever applies:
      1. the axis is a slice of a shared constant, so widening the constant widens the axis;
      2. a chokepoint guarantees the axis is reached;
      3. the test asserts a bounded reachability counter -- "N cells were explained ONLY by this
         axis", bounded on BOTH sides so neither an empty axis nor a dominating one passes.

KNOWN BLIND SPOT -- READ THIS BEFORE TRUSTING A GREEN
    "Trim to the first element" keeps a C# collection-expression SPREAD (`.. SharedConstant`) intact,
    because the spread IS the first element. An axis whose first entry is a spread will therefore be
    reported GREEN even when it is properly pinned. Check any GREEN by hand before believing it; the
    instrument for finding unpinned axes has an unpinned assumption of its own.

USAGE
    python3 tools/testing/axis-trim-sweep.py <test-file.cs> <test-project-dir>

    Rebuilds and re-runs the owning test once per axis, so it is slow by construction. It restores the
    file when it finishes, and on failure the original is left in <test-file.cs>.orig.
"""

import json
import os
import re
import shutil
import subprocess
import sys

DECL = re.compile(
    r"^\s*(?:private static readonly\s+)?(?:string\[\]|\(string [^)]*\)\[\])\s+(\w+)\s*=\s*(?:$|\[|\{|new)"
)
MEMBER = re.compile(r"^\s*(?:public|private|internal|protected)\s")
LAST_ID = re.compile(r"(\w+)\s*\(")


def _skip_literal(text, i):
    """Return the index just past the string or char literal starting at i, else None."""
    n = len(text)
    if text[i] == "@" and i + 1 < n and text[i + 1] == '"':
        i += 2
        while i < n:
            if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                i += 2
                continue
            if text[i] == '"':
                return i + 1
            i += 1
        return n
    if text[i] in "\"'":
        quote = text[i]
        i += 1
        while i < n:
            if text[i] == "\\":
                i += 2
                continue
            if text[i] == quote:
                return i + 1
            i += 1
        return n
    return None


def bracket_depth(line):
    depth = 0
    i = 0
    while i < len(line):
        skipped = _skip_literal(line, i)
        if skipped is not None:
            i = skipped
            continue
        if line[i] == "/" and i + 1 < len(line) and line[i + 1] == "/":
            break
        if line[i] in "[{":
            depth += 1
        elif line[i] in "]}":
            depth -= 1
        i += 1
    return depth


def split_top_level(text):
    parts = []
    current = ""
    depth = 0
    i = 0
    while i < len(text):
        skipped = _skip_literal(text, i)
        if skipped is not None:
            current += text[i:skipped]
            i = skipped
            continue
        char = text[i]
        if char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
        if char == "," and depth == 0:
            parts.append(current.strip())
            current = ""
            i += 1
            continue
        current += char
        i += 1
    if current.strip():
        parts.append(current.strip())
    return parts


def find_axes(lines):
    member = "<class>"
    owner = {}
    for index, line in enumerate(lines):
        if MEMBER.match(line) and "(" in line and not line.rstrip().endswith(";"):
            names = LAST_ID.findall(line)
            if names:
                member = names[-1]
        owner[index] = member

    axes = []
    index = 0
    while index < len(lines):
        match = DECL.match(lines[index])
        if not match:
            index += 1
            continue
        end = index
        depth = 0
        while end < len(lines):
            depth += bracket_depth(lines[end])
            if depth == 0 and lines[end].rstrip().endswith(";"):
                break
            end += 1
        axes.append({"name": match.group(1), "member": owner[index], "start": index, "end": end})
        index = end + 1
    return axes


def trimmed_declaration(lines, axis):
    block = "\n".join(lines[axis["start"] : axis["end"] + 1])
    match = re.match(
        r"^(\s*(?:private static readonly\s+)?(?:string\[\]|\(string [^)]*\)\[\])\s+\w+\s*=\s*)(.*)$",
        block,
        re.S,
    )
    head, body = match.group(1), match.group(2).strip()
    body = re.sub(r"^new\[\]\s*", "", body).strip()
    closing = "]" if body[0] == "[" else "}"
    first = split_top_level(body[1 : body.rindex(closing)])[0]
    return head + "[" + first + "];"


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    path, project = sys.argv[1], sys.argv[2]
    original = open(path, encoding="utf-8").read()
    shutil.copy(path, path + ".orig")
    lines = original.split("\n")
    tests = set(re.findall(r"public (?:async Task|void) (\w+)\(", original))
    axes = find_axes(lines)
    suite = os.path.splitext(os.path.basename(path))[0]

    results = []
    for axis in axes:
        mutated = list(lines)
        try:
            mutated[axis["start"] : axis["end"] + 1] = trimmed_declaration(lines, axis).split("\n")
        except (AttributeError, IndexError, ValueError):
            results.append((axis["member"], axis["name"], "UNPARSED"))
            continue
        open(path, "w", encoding="utf-8").write("\n".join(mutated))
        target = axis["member"] if axis["member"] in tests else suite
        completed = subprocess.run(
            [
                "dotnet", "test", project, "-c", "Debug",
                "-p:ContinuousIntegrationBuild=true",
                "--filter", "FullyQualifiedName~" + target,
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        if "Passed!" in completed.stdout:
            verdict = "GREEN"
        elif "Failed!" in completed.stdout:
            verdict = "RED"
        else:
            verdict = "BUILDFAIL"
        results.append((axis["member"], axis["name"], verdict))
        print("%-10s %-22s %s" % (verdict, axis["name"], axis["member"]), flush=True)

    open(path, "w", encoding="utf-8").write(original)
    os.remove(path + ".orig")
    unpinned = [r for r in results if r[2] != "RED"]
    print("\n%d axes trimmed, %d RED, %d needing a look" % (len(results), len(results) - len(unpinned), len(unpinned)))
    print(json.dumps(unpinned, indent=2))
    return 1 if unpinned else 0


if __name__ == "__main__":
    sys.exit(main())
