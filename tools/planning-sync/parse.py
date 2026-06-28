#!/usr/bin/env python3
"""Parse docs/planning/epics/*.md into a structured epic/feature/story model."""
import json, os, re, sys, glob

HERE = os.path.dirname(os.path.abspath(__file__))


def find_repo_root(start):
    d = start
    for _ in range(8):
        if os.path.isdir(os.path.join(d, "docs", "planning", "epics")):
            return d
        nd = os.path.dirname(d)
        if nd == d:
            break
        d = nd
    return "/Users/kenhaines/code/git/deltasharp"


REPO_ROOT = find_repo_root(HERE)
EPIC_DIR = os.path.join(REPO_ROOT, "docs/planning/epics")
BLOB = "https://github.com/khaines/deltasharp/blob/main"

SLUG_RE = re.compile(r"`([a-z0-9][a-z0-9-]+)`")


def slugs(text):
    return SLUG_RE.findall(text or "")


def parse_personas(line):
    """Parse '... Primary `a`; Collaborators `b`, `c`.' -> dict."""
    primary, collab = [], []
    if not line:
        return {"primary": [], "collaborators": []}
    # split on Collaborators
    m = re.search(r"Collaborators?", line, re.I)
    if m:
        pri_part = line[: m.start()]
        col_part = line[m.end():]
    else:
        pri_part, col_part = line, ""
    primary = slugs(pri_part)
    collab = slugs(col_part)
    return {"primary": primary, "collaborators": collab}


def parse_depends(text):
    if not text:
        return []
    ids = re.findall(r"(?:EPIC|FEAT|STORY)-[0-9.]+", text)
    return ids


def extract_field(body, label):
    """Extract a single-line bullet field value like '- **Label:** value'."""
    m = re.search(r"^- \*\*" + re.escape(label) + r":\*\*\s*(.+)$", body, re.M)
    return m.group(1).strip() if m else ""


def split_sections(lines, level):
    """Yield (header_line_index) for ATX headers of exactly `level`."""
    out = []
    for i, ln in enumerate(lines):
        m = re.match(r"^(#+)\s", ln)
        if m and len(m.group(1)) == level:
            out.append(i)
    return out


def parse_epic_file(path):
    text = open(path).read()
    lines = text.splitlines()
    # Epic title
    m = re.search(r"^# EPIC-(\d+):\s*(.+)$", text, re.M)
    num, title = m.group(1), m.group(2).strip()
    epic_id = f"EPIC-{num}"

    # Epic metadata block (between title and first ## )
    h2 = split_sections(lines, 2)
    intro = "\n".join(lines[: h2[0]]) if h2 else text

    milestone = extract_field(intro, "Roadmap milestone")
    _mk = re.search(r"(M\d+|v1\.0)", milestone)
    milestone_key = _mk.group(1) if _mk else ""
    primary_personas = slugs(extract_field(intro, "Primary persona(s)"))
    adrs = re.findall(r"ADR-\d+", extract_field(intro, "Related ADRs"))
    depends = parse_depends(extract_field(intro, "Depends on"))
    size = extract_field(intro, "Size")

    # Epic body = Objective + Scope + Exit criteria (everything up to ## Features)
    feat_hdr_idx = None
    for i in h2:
        if re.match(r"^##\s+Features\b", lines[i]):
            feat_hdr_idx = i
            break
    body_start = h2[0] if h2 else len(lines)
    epic_body = "\n".join(lines[body_start:feat_hdr_idx]).strip() if feat_hdr_idx else ""

    # Features: ### FEAT-..
    features = []
    feat_idxs = [i for i, ln in enumerate(lines) if re.match(r"^### FEAT-", ln)]
    for fi, start in enumerate(feat_idxs):
        end = feat_idxs[fi + 1] if fi + 1 < len(feat_idxs) else len(lines)
        block = lines[start:end]
        blocktext = "\n".join(block)
        fm = re.match(r"^### (FEAT-[0-9.]+):\s*(.+)$", block[0])
        feat_id, feat_title = fm.group(1), fm.group(2).strip()
        # feature meta = up to #### Stories
        stories_idx = None
        for j, ln in enumerate(block):
            if re.match(r"^#### Stories", ln):
                stories_idx = j
                break
        feat_meta = "\n".join(block[1:stories_idx]).strip() if stories_idx else "\n".join(block[1:]).strip()
        f_obj = extract_field(feat_meta, "Objective")
        f_personas = parse_personas(extract_field(feat_meta, "Implementer persona(s)"))
        f_dep = parse_depends(extract_field(feat_meta, "Depends on"))

        # Stories: ##### STORY-..
        stories = []
        story_idxs = [j for j, ln in enumerate(block) if re.match(r"^##### STORY-", ln)]
        for si, sstart in enumerate(story_idxs):
            send = story_idxs[si + 1] if si + 1 < len(story_idxs) else len(block)
            sblock = block[sstart:send]
            sm = re.match(r"^##### (STORY-[0-9.]+):\s*(.+)$", sblock[0])
            story_id, story_title = sm.group(1), sm.group(2).strip()
            sbody = "\n".join(sblock[1:]).strip()
            user_story = ""
            um = re.search(r"^- (\*\*As a\*\*.+?so that\*\*.+?)$", sbody, re.M | re.S)
            if um:
                user_story = um.group(1).strip()
            s_personas = parse_personas(extract_field(sbody, "Implementer persona(s)"))
            size_line = extract_field(sbody, "Size")
            s_size = ""
            szm = re.match(r"([A-Z]{1,2})", size_line)
            if szm:
                s_size = szm.group(1)
            # depends may be on the Size line ('Size: M. Depends on: X.')
            s_dep = parse_depends(size_line) + parse_depends(extract_field(sbody, "Depends on"))
            s_dep = sorted(set(s_dep))
            # acceptance criteria bullets
            acc = re.findall(r"^\s*- \[ \] (.+)$", sbody, re.M)
            dod = extract_field(sbody, "Definition of done")
            stories.append({
                "id": story_id, "title": story_title, "user_story": user_story,
                "personas": s_personas, "size": s_size, "depends_on": s_dep,
                "acceptance": acc, "dod": dod, "body_md": sbody,
            })
        features.append({
            "id": feat_id, "title": feat_title, "objective": f_obj,
            "personas": f_personas, "depends_on": f_dep, "body_md": feat_meta,
            "stories": stories,
        })

    return {
        "id": epic_id, "num": num, "title": title,
        "milestone": milestone, "milestone_key": milestone_key,
        "primary_personas": primary_personas, "adrs": adrs,
        "depends_on": depends, "size": size, "body_md": epic_body,
        "source": os.path.relpath(path, REPO_ROOT), "features": features,
    }


def main():
    files = sorted(glob.glob(os.path.join(EPIC_DIR, "EPIC-*.md")))
    epics = [parse_epic_file(f) for f in files]
    model = {"epics": epics, "blob": BLOB}
    out = os.path.join(os.path.dirname(__file__), "model.json")
    with open(out, "w") as fh:
        json.dump(model, fh, indent=2)

    # Stats
    nf = sum(len(e["features"]) for e in epics)
    ns = sum(len(f["stories"]) for e in epics for f in e["features"])
    nac = sum(len(s["acceptance"]) for e in epics for f in e["features"] for s in f["stories"])
    personas = set()
    sizes = set()
    for e in epics:
        personas.update(e["primary_personas"])
        for f in e["features"]:
            personas.update(f["personas"]["primary"]); personas.update(f["personas"]["collaborators"])
            for s in f["stories"]:
                personas.update(s["personas"]["primary"]); personas.update(s["personas"]["collaborators"])
                if s["size"]:
                    sizes.add(s["size"])
    print(f"epics={len(epics)} features={nf} stories={ns} acceptance_criteria={nac}")
    print(f"distinct_personas_used={len(personas)}")
    print(f"sizes_used={sorted(sizes)}")
    print("PERSONAS=" + json.dumps(sorted(personas)))
    # quick per-epic counts
    for e in epics:
        sc = sum(len(f["stories"]) for f in e["features"])
        print(f"  {e['id']} [{e['milestone_key']}] feats={len(e['features']):2d} stories={sc:2d}  {e['title'][:50]}")
    # flag stories missing fields
    missing = []
    for e in epics:
        for f in e["features"]:
            for s in f["stories"]:
                if not s["acceptance"] or not s["size"] or not s["personas"]["primary"]:
                    missing.append(s["id"])
    if missing:
        print(f"WARN stories_missing_fields={len(missing)}: {missing[:20]}")
    else:
        print("all stories have acceptance criteria, size, and a primary persona")
    print("wrote " + out)


if __name__ == "__main__":
    main()
