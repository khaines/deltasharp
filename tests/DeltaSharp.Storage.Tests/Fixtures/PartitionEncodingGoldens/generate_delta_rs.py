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
import atexit
import hashlib
import json
import os
import shutil
import sys
import tempfile
from urllib.parse import unquote

VALUES = [
    "US", "a=b", "na me", "région", "名前", "e🎯moji", "o'brien", "a/b", "c:d", "q?x", "h#h",
    "p%p", "amp&r", "plus+", "comma,", "semi;", "excl!", "dollar$", "paren()", "star*", "tilde~",
    "lt<gt>", "pipe|", "brace{}", "brack[]", "caret^", "quote\"", "back\\", "at@", "hash`bt",
    # --- §3.2 axes measured in the R2 fix round (previously absent -> the STATUS block overclaimed) ---
    # null -> the __HIVE_DEFAULT_PARTITION__ sentinel. NOTE: the EMPTY string is deliberately NOT a
    # separate row: Spark folds "" onto the same sentinel partition as null (measured -- it emits no
    # distinct directory or add-action for it, and reads the value back as null), so "" has no
    # reference encoding of its own to pin. That measurement is asserted DS-side instead.
    None,
    # control-adjacent: escaped on disk (%09/%01) and double-encoded in add.path.
    "tab\tx", "soh\x01y",
]

# ASCII-safe readable table for the ref->DS read test (no non-ASCII → no macOS NFC/NFD hazard, R6).
READ_ROWS = {"id": [1, 2, 3, 4, 5, 6], "name": ["a1", "b2", "c3", "d4", "e5", "f6"],
             "region": ["US", "a=b", "na me", "o'brien", "US", "p%p"]}


def _guard_out_dir(out_dir: str) -> None:
    """Refuse to rmtree an arbitrary path: `out_dir` comes from argv, and this script deletes
    `<out_dir>/read-table`. Require it to be an existing engine fixture dir (or empty/new)."""
    if not os.path.exists(out_dir):
        return
    if not os.path.isdir(out_dir):
        raise SystemExit(f"refusing to write: {out_dir!r} is not a directory")
    entries = set(os.listdir(out_dir))
    if entries and not ({"matrix.json", "SHA256SUMS", "read-table"} & entries):
        raise SystemExit(
            f"refusing to overwrite {out_dir!r}: not an engine golden dir "
            "(expected matrix.json / SHA256SUMS / read-table)")


def main(out_dir: str) -> None:
    _guard_out_dir(out_dir)
    os.makedirs(out_dir, exist_ok=True)

    import deltalake
    import pyarrow as pa
    from deltalake import write_deltalake

    # (1) Full matrix table → throwaway temp dir (not committed) to harvest matrix.json.
    matrix_staging = tempfile.mkdtemp(prefix="ds806-matrix-")
    # atexit so an engine failure mid-run cannot leave the staged table behind.
    atexit.register(shutil.rmtree, matrix_staging, ignore_errors=True)
    matrix_src = os.path.join(matrix_staging, "matrix-table")
    tab = pa.table({"id": list(range(len(VALUES))), "region": VALUES})
    write_deltalake(matrix_src, tab, partition_by=["region"])

    disk_dirs = [n for n in os.listdir(matrix_src) if n.startswith("region=")]
    log = os.path.join(matrix_src, "_delta_log", "00000000000000000000.json")
    add_by_value = {}
    for line in open(log, encoding="utf-8"):
        o = json.loads(line)
        if "add" in o:
            add_by_value[o["add"]["partitionValues"]["region"]] = o["add"]["path"]

    # Commit the engine's OWN transaction log for the full matrix table. matrix.json is DERIVED (harvested by
    # this script), so on its own a hand-edited row could bless a buggy encoder and pass every test
    # (design risk R7). This file is written by the reference engine itself and is the ground truth the
    # differential test cross-checks all rows against. Only the LOG is committed, never the matrix table's
    # data files or directories -- so the non-ASCII / control-bearing values appear solely as text inside
    # this JSON, never as filesystem paths (design R6, the macOS NFC/NFD hazard).
    shutil.copyfile(log, os.path.join(out_dir, "matrix-log.json"))

    matrix = []
    for v in VALUES:
        add_path = add_by_value[v]
        decoded = unquote(add_path.split("/")[0])
        assert decoded in disk_dirs, f"dir {decoded!r} for {v!r} not on disk"
        matrix.append({"value": v, "on_disk_dir": decoded, "add_path_segment": add_path.split("/")[0]})
    # (1b) The EMPTY-STRING axis, harvested from a real run. Unlike Spark, delta-rs gives "" its OWN
    #      directory (region=) and its own add-action, so its behaviour differs from Spark here; both are
    #      recorded so the differential test can pin the divergence instead of asserting it in prose.
    empty_staging = tempfile.mkdtemp(prefix="ds806-empty-")
    atexit.register(shutil.rmtree, empty_staging, ignore_errors=True)
    empty_src = os.path.join(empty_staging, "empty-table")
    write_deltalake(empty_src, pa.table({"id": [0, 1, 2], "region": [None, "", "keep"]}), partition_by=["region"])
    empty_dirs = sorted(n for n in os.listdir(empty_src) if n.startswith("region="))
    empty_adds = []
    for line in open(os.path.join(empty_src, "_delta_log", "00000000000000000000.json"), encoding="utf-8"):
        o = json.loads(line)
        if "add" in o:
            empty_adds.append((o["add"]["partitionValues"]["region"], o["add"]["path"].split("/")[0]))
    from deltalake import DeltaTable
    back = DeltaTable(empty_src).to_pyarrow_table().to_pydict()
    pairs = dict(zip(back["id"], back["region"]))
    empty_string = {
        "on_disk_dirs": empty_dirs,
        "add_partition_values": sorted({a[0] for a in empty_adds}, key=str),
        "distinct_add_action": any(a[0] == "" for a in empty_adds),
        "folds_onto_sentinel_with_null": not any(a[0] == "" for a in empty_adds),
        "add_path_segment": next((a[1] for a in empty_adds if a[0] == ""), None),
        "read_back_value": pairs.get(1),
        "read_back_is_null": pairs.get(1) is None,
    }
    shutil.rmtree(empty_staging, ignore_errors=True)

    shutil.rmtree(matrix_staging, ignore_errors=True)

    # (2) Committed small readable table.
    read_table = os.path.join(out_dir, "read-table")
    shutil.rmtree(read_table, ignore_errors=True)
    write_deltalake(read_table, pa.table(READ_ROWS), partition_by=["region"])
    _strip_crc(read_table)

    out = {
        "engine": "delta-rs",
        "version": deltalake.__version__,
        "pyarrow": pa.__version__,
        "note": "delta-rs reference (dir, add.path). It escapes space/non-ASCII (and some sub-delims like '&') "
                "on disk (diverges from Spark). DeltaSharp follows Spark; this fixture backs the read-compat + "
                "documented-residual tests.",
        "column": "region",
        "matrix": matrix,
        "empty_string": empty_string,
    }
    with open(os.path.join(out_dir, "matrix.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    _write_checksums(out_dir)
    print(f"wrote {len(matrix)} delta-rs golden rows + {len(READ_ROWS['id'])}-row read-table to {out_dir}")


def _strip_crc(root: str) -> None:
    for cur, _dirs, files in os.walk(root):
        for name in files:
            if name.endswith(".crc"):
                os.remove(os.path.join(cur, name))


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
