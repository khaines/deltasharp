#!/usr/bin/env python3
"""Regenerate the delta-rs partition-encoding golden fixture for #806 Inc-C.

PROVENANCE (design §3.2 / risk R7): emitted by REAL delta-rs (the Rust `deltalake` engine), never
regenerated from DeltaSharp output. Records where delta-rs DIVERGES from Apache Spark (it percent-
escapes space and non-ASCII in the on-disk directory, and hence in add.path). DeltaSharp follows
Spark on disk (design D1), so a delta-rs table is read-compatible but NOT byte-identical — this
fixture pins that divergence and backs the ref->DS read-compat test.

Pinned engine (recorded in matrix.json.engine / .version):
    deltalake==1.6.3   pyarrow==25.0.1

Usage:
    python3 -m venv /tmp/dr-gen && . /tmp/dr-gen/bin/activate
    pip install deltalake==1.6.3 pyarrow
    python generate_delta_rs.py <out_dir>
"""
import hashlib
import json
import os
import shutil
import sys
from urllib.parse import unquote

VALUES = [
    "US", "a=b", "na me", "région", "名前", "e🎯moji", "o'brien", "a/b", "c:d", "q?x", "h#h",
    "p%p", "amp&r", "plus+", "comma,", "semi;", "excl!", "dollar$", "paren()", "star*", "tilde~",
    "lt<gt>", "pipe|", "brace{}", "brack[]", "caret^", "quote\"", "back\\", "at@", "hash`bt",
]


def main(out_dir: str) -> None:
    import deltalake
    import pyarrow as pa
    from deltalake import write_deltalake

    table_dir = os.path.join(out_dir, "table")
    shutil.rmtree(table_dir, ignore_errors=True)
    tab = pa.table({"id": list(range(len(VALUES))), "region": VALUES})
    write_deltalake(table_dir, tab, partition_by=["region"])

    disk_dirs = [n for n in os.listdir(table_dir) if n.startswith("region=")]
    log = os.path.join(table_dir, "_delta_log", "00000000000000000000.json")
    add_by_value = {}
    for line in open(log, encoding="utf-8"):
        o = json.loads(line)
        if "add" in o:
            add_by_value[o["add"]["partitionValues"]["region"]] = o["add"]["path"]

    matrix = []
    for v in VALUES:
        add_path = add_by_value[v]
        decoded = unquote(add_path.split("/")[0])
        assert decoded in disk_dirs, f"dir {decoded!r} for {v!r} not on disk"
        matrix.append({"value": v, "on_disk_dir": decoded, "add_path_segment": add_path.split("/")[0]})

    out = {
        "engine": "delta-rs",
        "version": deltalake.__version__,
        "pyarrow": pa.__version__,
        "note": "delta-rs reference (dir, add.path). It escapes space/non-ASCII on disk (diverges from Spark). "
                "DeltaSharp follows Spark; this fixture backs the read-compat + documented-residual tests.",
        "column": "region",
        "matrix": matrix,
    }
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "matrix.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    _write_checksums(out_dir)
    print(f"wrote {len(matrix)} delta-rs golden rows + readable table to {out_dir}")


def _write_checksums(out_dir: str) -> None:
    lines = []
    for root, _dirs, files in os.walk(out_dir):
        for name in sorted(files):
            if name == "SHA256SUMS":
                continue
            p = os.path.join(root, name)
            h = hashlib.sha256(open(p, "rb").read()).hexdigest()
            lines.append(f"{h}  {os.path.relpath(p, out_dir)}")
    with open(os.path.join(out_dir, "SHA256SUMS"), "w", encoding="utf-8") as f:
        f.write("\n".join(sorted(lines)) + "\n")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__)) + "/delta-rs")
