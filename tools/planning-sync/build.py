#!/usr/bin/env python3
"""Build GitHub milestones/labels/issues/sub-issues from model.json. Idempotent."""
import argparse, json, os, re, sqlite3, subprocess, sys, time, tempfile

HERE = os.path.dirname(__file__)
REPO = "khaines/deltasharp"
BLOB = "https://github.com/khaines/deltasharp/blob/main"
MODEL = os.path.join(HERE, "model.json")
STATE = os.path.join(HERE, "state.db")
THROTTLE = float(os.environ.get("DS_THROTTLE", "1.2"))

# ---------- gh helpers ----------

def gh(args, check=True, capture=True):
    p = subprocess.run(["gh"] + args, capture_output=capture, text=True)
    if check and p.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args)} -> {p.returncode}\n{p.stderr}")
    return p


def gh_api_json(path, paginate=False, method=None, fields=None):
    cmd = ["api", path]
    if paginate:
        cmd += ["--paginate"]
    cmd += ["-X", method or "GET"]
    for k, v in (fields or {}).items():
        cmd += ["-f", f"{k}={v}"]
    p = gh(cmd)
    return p.stdout


def _transient(err):
    return any(s in err for s in (
        "bad credentials", "401", "was submitted too quickly", "rate limit",
        "abuse", "secondary", "502", "503", "504", "timeout", "timed out",
        "connection reset", "could not resolve", "eof", "tls handshake",
        "i/o timeout", "server error"))


def gh_api_post(path, payload):
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
        json.dump(payload, f)
        fn = f.name
    try:
        for attempt in range(8):
            p = gh(["api", path, "-X", "POST", "--input", fn], check=False)
            if p.returncode == 0:
                return json.loads(p.stdout)
            err = p.stderr.lower()
            if _transient(err):
                wait = min(60, 8 * (attempt + 1))
                print(f"    transient error (attempt {attempt+1}), backoff {wait}s: {p.stderr.strip()[:120]}", flush=True)
                time.sleep(wait)
                continue
            raise RuntimeError(f"POST {path} failed: {p.stderr.strip()[:300]}")
        raise RuntimeError(f"POST {path} exhausted retries")
    finally:
        os.unlink(fn)


def graphql(query, variables):
    cmd = ["api", "graphql", "-f", f"query={query}"]
    for k, v in variables.items():
        cmd += ["-f", f"{k}={v}"]
    p = gh(cmd, check=False)
    return p


# ---------- state ----------

def db():
    c = sqlite3.connect(STATE)
    c.execute("CREATE TABLE IF NOT EXISTS issues(plan_id TEXT PRIMARY KEY, kind TEXT, number INTEGER, node_id TEXT)")
    c.execute("CREATE TABLE IF NOT EXISTS links(parent TEXT, child TEXT, done INTEGER DEFAULT 0, PRIMARY KEY(parent,child))")
    return c


def reconcile(c):
    """Populate state from existing issues by reading hidden markers."""
    raw = gh_api_json(f"/repos/{REPO}/issues", paginate=True,
                      fields={"state": "all", "per_page": "100"})
    # --paginate concatenates JSON arrays; split them
    found = 0
    for chunk in re.findall(r"\[.*?\]\s*(?=\[|\Z)", raw, re.S) or [raw]:
        try:
            arr = json.loads(chunk)
        except Exception:
            continue
        for it in arr:
            if "pull_request" in it:
                continue
            body = it.get("body") or ""
            m = re.search(r"<!-- ds-plan-id:\s*(\S+)\s*-->", body)
            if not m:
                continue
            pid = m.group(1)
            kind = "epic" if pid.startswith("EPIC") else "feature" if pid.startswith("FEAT") else "story"
            c.execute("INSERT OR REPLACE INTO issues(plan_id,kind,number,node_id) VALUES(?,?,?,?)",
                      (pid, kind, it["number"], it["node_id"]))
            found += 1
    c.commit()
    return found


def get_issue(c, plan_id):
    r = c.execute("SELECT number,node_id FROM issues WHERE plan_id=?", (plan_id,)).fetchone()
    return r  # (number, node_id) or None


# ---------- milestones ----------

def milestone_map():
    arr = json.loads(gh_api_json(f"/repos/{REPO}/milestones", fields={"state": "all", "per_page": "100"}))
    m = {}
    for ms in arr:
        mk = re.match(r"(M\d+|v1\.0)", ms["title"])
        if mk:
            m[mk.group(1)] = ms["number"]
    return m


# ---------- labels ----------

PERSONA_COLOR = "0e6fbf"
TYPE_COLORS = {"epic": "5319e7", "feature": "1d76db", "story": "0e8a16"}
SIZE_COLORS = {"XS": "c2e0c6", "S": "bfdadc", "M": "fef2c0", "L": "f9d0c4", "XL": "e99695"}
EPIC_COLOR = "d4c5f9"


def persona_label(slug):
    """GitHub label for a persona. Labels cap at 50 chars; if `persona:<slug>`
    overflows, drop the redundant trailing `-engineer` (deterministic, only
    affects the longest slug). The exact slug is always preserved in issue bodies."""
    name = f"persona:{slug}"
    if len(name) <= 50:
        return name
    short = slug[:-len("-engineer")] if slug.endswith("-engineer") else slug
    return f"persona:{short}"[:50]


def cmd_labels(args):
    model = json.load(open(MODEL))
    epics = model["epics"]
    labels = []  # (name, color, desc)
    for t, col in TYPE_COLORS.items():
        labels.append((t, col, f"DeltaSharp {t} work item"))
    personas = set()
    for e in epics:
        personas.update(e["primary_personas"])
        for f in e["features"]:
            personas.update(f["personas"]["primary"])
            personas.update(f["personas"]["collaborators"])
            for s in f["stories"]:
                personas.update(s["personas"]["primary"])
                personas.update(s["personas"]["collaborators"])
    for p in sorted(personas):
        labels.append((persona_label(p), PERSONA_COLOR, f"Implementer persona: {p}"))
    for sz, col in SIZE_COLORS.items():
        labels.append((f"size:{sz}", col, f"Relative size {sz}"))
    for e in epics:
        labels.append((f"epic:{e['num']}", EPIC_COLOR, f"{e['id']}: {e['title']}"[:99]))

    print(f"creating/updating {len(labels)} labels ...")
    for name, color, desc in labels:
        p = gh(["label", "create", name, "--color", color, "--description", desc[:100],
                "--force", "-R", REPO], check=False)
        status = "ok" if p.returncode == 0 else f"FAIL {p.stderr.strip()[:80]}"
        print(f"  {name:55s} {status}")
    print("labels done")


# ---------- body rendering ----------

def gh_anchor(header):
    a = header.lower()
    a = re.sub(r"[^\w\s-]", "", a)
    a = a.replace(" ", "-")
    return a


def persona_str(personas):
    pri = ", ".join(f"`{s}`" for s in personas["primary"]) or "—"
    out = f"Primary {pri}"
    if personas["collaborators"]:
        out += "; Collaborators " + ", ".join(f"`{s}`" for s in personas["collaborators"])
    return out


def src_link(epic, label, header=None):
    url = f"{BLOB}/{epic['source']}"
    if header:
        url += "#" + gh_anchor(header)
    return f"[{label}]({url})"


def epic_body(e, ms_key):
    adrs = ", ".join(e["adrs"]) or "—"
    deps = ", ".join(e["depends_on"]) or "none"
    pri = ", ".join(f"`{s}`" for s in e["primary_personas"]) or "—"
    hdr = f"{e['id']}: {e['title']}"
    link = src_link(e, e["id"], hdr)
    head = (f"> **Epic {e['id']}** · Milestone **{ms_key}** · Size **{e['size']}** · "
            f"Related ADRs: {adrs}\n>\n"
            f"> Source: {link} · Depends on: {deps}\n\n")
    body = e["body_md"].strip()
    foot = (f"\n\n---\n**Primary persona(s):** {pri}\n\n"
            f"_Features are tracked as sub-issues of this epic._\n\n"
            f"<!-- ds-plan-id: {e['id']} -->")
    return head + body + foot


def feature_body(e, f, ms_key, epic_number):
    deps = ", ".join(f["depends_on"]) or "none"
    hdr = f"{f['id']}: {f['title']}"
    link = src_link(e, f["id"], hdr)
    head = (f"> **Feature {f['id']}** · Epic #{epic_number} ({e['id']}) · Milestone **{ms_key}**\n>\n"
            f"> Source: {link} · Depends on: {deps}\n\n")
    obj = f"**Objective.** {f['objective']}\n\n"
    per = f"**Implementer persona(s):** {persona_str(f['personas'])}\n\n"
    foot = (f"Parent epic: #{epic_number}\n\n"
            f"_Stories are tracked as sub-issues of this feature._\n\n"
            f"<!-- ds-plan-id: {f['id']} -->")
    return head + obj + per + foot


def story_body(e, f, s, ms_key, feature_number):
    deps = ", ".join(s["depends_on"]) or "none"
    hdr = f"{s['id']}: {s['title']}"
    link = src_link(e, s["id"], hdr)
    size = s["size"] or "—"
    head = (f"> **Story {s['id']}** · Feature #{feature_number} ({f['id']}) · "
            f"Epic {e['id']} · Milestone **{ms_key}** · Size **{size}**\n>\n"
            f"> Source: {link} · Depends on: {deps}\n\n")
    us = (s["user_story"].replace("**As a**", "_As a_").replace("**I want**", "_I want_")
          .replace("**so that**", "_so that_")) if s["user_story"] else ""
    us = (us + "\n\n") if us else ""
    per = f"**Implementer persona(s):** {persona_str(s['personas'])}\n\n"
    acc = "**Acceptance criteria**\n" + "\n".join(f"- [ ] {a}" for a in s["acceptance"]) + "\n\n"
    dod = f"**Definition of done.** {s['dod']}\n\n" if s["dod"] else ""
    foot = f"Parent feature: #{feature_number}\n\n<!-- ds-plan-id: {s['id']} -->"
    return head + us + per + acc + dod + foot


# ---------- create ----------

def create_issue(c, plan_id, kind, title, body, labels, milestone_num):
    existing = get_issue(c, plan_id)
    if existing:
        return existing[0], existing[1], False
    payload = {"title": title, "body": body, "labels": labels}
    if milestone_num:
        payload["milestone"] = milestone_num
    resp = gh_api_post(f"/repos/{REPO}/issues", payload)
    num, node = resp["number"], resp["node_id"]
    c.execute("INSERT OR REPLACE INTO issues(plan_id,kind,number,node_id) VALUES(?,?,?,?)",
              (plan_id, kind, num, node))
    c.commit()
    time.sleep(THROTTLE)
    return num, node, True


def cmd_create(args):
    model = json.load(open(MODEL))
    epics = model["epics"]
    msmap = milestone_map()
    c = db()
    print("reconciling existing issues ...")
    n = reconcile(c)
    print(f"  found {n} pre-existing tracked issues")

    only = args.only  # epics|features|stories|all
    created = {"epic": 0, "feature": 0, "story": 0}
    skipped = 0

    # Epics
    for e in epics:
        ms = msmap.get(e["milestone_key"])
        if only in ("all", "epics"):
            title = f"[Epic] {e['id']}: {e['title']}"
            labels = ["epic", f"epic:{e['num']}"]
            num, node, made = create_issue(c, e["id"], "epic", title, epic_body(e, e["milestone_key"]), labels, ms)
            created["epic"] += 1 if made else 0
            skipped += 0 if made else 1
            if made:
                print(f"  epic   #{num}  {e['id']}")

    if only in ("all", "features", "stories"):
        for e in epics:
            ms = msmap.get(e["milestone_key"])
            epic_rec = get_issue(c, e["id"])
            epic_number = epic_rec[0] if epic_rec else None
            for f in e["features"]:
                if only in ("all", "features"):
                    title = f"[Feature] {f['id']}: {f['title']}"
                    labels = ["feature", f"epic:{e['num']}"] + [persona_label(p) for p in f["personas"]["primary"]]
                    num, node, made = create_issue(c, f["id"], "feature", title,
                                                   feature_body(e, f, e["milestone_key"], epic_number), labels, ms)
                    created["feature"] += 1 if made else 0
                    skipped += 0 if made else 1
                    if made:
                        print(f"  feat   #{num}  {f['id']}")

    if only in ("all", "stories"):
        for e in epics:
            ms = msmap.get(e["milestone_key"])
            for f in e["features"]:
                feat_rec = get_issue(c, f["id"])
                feature_number = feat_rec[0] if feat_rec else None
                for s in f["stories"]:
                    title = f"[Story] {s['id']}: {s['title']}"
                    labels = ["story", f"epic:{e['num']}"]
                    labels += [persona_label(p) for p in s["personas"]["primary"]]
                    if s["size"]:
                        labels.append(f"size:{s['size']}")
                    num, node, made = create_issue(c, s["id"], "story", title,
                                                   story_body(e, f, s, e["milestone_key"], feature_number), labels, ms)
                    created["story"] += 1 if made else 0
                    skipped += 0 if made else 1
                    if made and created["story"] % 25 == 0:
                        print(f"  ... {created['story']} stories created")
    print(f"create done: {created}; skipped(existing)={skipped}")


# ---------- link sub-issues ----------

ADD_SUBISSUE = "mutation($p:ID!,$c:ID!){addSubIssue(input:{issueId:$p,subIssueId:$c}){issue{number}}}"


def link_one(c, parent_pid, child_pid):
    row = c.execute("SELECT done FROM links WHERE parent=? AND child=?", (parent_pid, child_pid)).fetchone()
    if row and row[0]:
        return "skip"
    p = get_issue(c, parent_pid)
    ch = get_issue(c, child_pid)
    if not p or not ch:
        return f"MISSING({parent_pid}->{child_pid})"
    for attempt in range(6):
        r = graphql(ADD_SUBISSUE, {"p": p[1], "c": ch[1]})
        if r.returncode == 0:
            c.execute("INSERT OR REPLACE INTO links(parent,child,done) VALUES(?,?,1)", (parent_pid, child_pid))
            c.commit()
            time.sleep(THROTTLE)
            return "linked"
        err = r.stderr.lower() + r.stdout.lower()
        if "already" in err or "duplicate" in err or "sub-issue already" in err or "cannot be added" in err:
            c.execute("INSERT OR REPLACE INTO links(parent,child,done) VALUES(?,?,1)", (parent_pid, child_pid))
            c.commit()
            return "already"
        if _transient(err):
            time.sleep(min(60, 8 * (attempt + 1))); continue
        return f"ERR {r.stderr.strip()[:120] or r.stdout.strip()[:120]}"
    return "ERR retries"


def cmd_link(args):
    model = json.load(open(MODEL))
    c = db()
    reconcile(c)
    stats = {"linked": 0, "already": 0, "skip": 0, "err": 0}
    for e in model["epics"]:
        for f in e["features"]:
            r = link_one(c, e["id"], f["id"])
            k = "err" if r.startswith(("ERR", "MISSING")) else r
            stats[k] = stats.get(k, 0) + 1
            if r.startswith(("ERR", "MISSING")):
                print(f"  {e['id']}->{f['id']}: {r}")
            for s in f["stories"]:
                r = link_one(c, f["id"], s["id"])
                k = "err" if r.startswith(("ERR", "MISSING")) else r
                stats[k] = stats.get(k, 0) + 1
                if r.startswith(("ERR", "MISSING")):
                    print(f"  {f['id']}->{s['id']}: {r}")
        print(f"  linked epic {e['id']} subtree; running stats={stats}")
    print(f"link done: {stats}")


# ---------- preview ----------

def cmd_preview(args):
    model = json.load(open(MODEL))
    e = model["epics"][0]
    f = e["features"][0]
    s = f["stories"][0]
    print("================ EPIC TITLE ================")
    print(f"[Epic] {e['id']}: {e['title']}")
    print("================ EPIC BODY =================")
    print(epic_body(e, e["milestone_key"]))
    print("\n================ FEATURE TITLE =============")
    print(f"[Feature] {f['id']}: {f['title']}")
    print("================ FEATURE BODY ==============")
    print(feature_body(e, f, e["milestone_key"], 101))
    print("\n================ STORY TITLE ===============")
    print(f"[Story] {s['id']}: {s['title']}")
    print("================ STORY BODY ================")
    print(story_body(e, f, s, e["milestone_key"], 202))


# ---------- verify ----------

def cmd_verify(args):
    c = db()
    reconcile(c)
    counts = {}
    for kind in ("epic", "feature", "story"):
        counts[kind] = c.execute("SELECT COUNT(*) FROM issues WHERE kind=?", (kind,)).fetchone()[0]
    links = c.execute("SELECT COUNT(*) FROM links WHERE done=1").fetchone()[0]
    print(f"tracked issues: {counts}  total={sum(counts.values())}")
    print(f"recorded sub-issue links: {links}")
    # API-side counts
    for lbl in ("epic", "feature", "story"):
        n = gh(["issue", "list", "-R", REPO, "--label", lbl, "--state", "all", "--limit", "1000",
                "--json", "number", "-q", "length"]).stdout.strip()
        print(f"  API issues labeled '{lbl}': {n}")


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("labels").set_defaults(func=cmd_labels)
    pc = sub.add_parser("create"); pc.add_argument("--only", default="all",
                                                    choices=["all", "epics", "features", "stories"]); pc.set_defaults(func=cmd_create)
    sub.add_parser("link").set_defaults(func=cmd_link)
    sub.add_parser("preview").set_defaults(func=cmd_preview)
    sub.add_parser("verify").set_defaults(func=cmd_verify)
    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
