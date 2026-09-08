#!/usr/bin/env python3
"""Regenerate the Apache Spark partition-encoding golden fixture for #806 Inc-C.

PROVENANCE (design §3.2 / risk R7): this fixture is emitted by REAL Apache Spark + Delta Lake
(a JVM engine DeltaSharp cannot produce), never regenerated from DeltaSharp output. It is the
reference oracle DeltaSharp's `(on-disk dir, add.path)` encoding is measured against. Do NOT
hand-edit `matrix.json` or `table/`; re-run this script against the pinned engine to update.

Pinned engine (recorded in matrix.json.engine / .version):
    pyspark==3.5.3   delta-spark==3.2.0   (Java 8/11)

Usage:
    python3.11 -m venv /tmp/spark-gen && . /tmp/spark-gen/bin/activate
    pip install pyspark==3.5.3 delta-spark==3.2.0
    JAVA_HOME=<jdk8-or-11> python generate_spark.py <out_dir>
"""
import atexit
import hashlib
import json
import os
import shutil
import sys
import tempfile

# The partition-value matrix: ASCII-unreserved, every ASCII-reserved char, sub-delims, the
# URI-illegal set (< > | { } ` " \ [ ] ^ space), non-ASCII (Latin, CJK, emoji), and a value that
# already contains a percent-escape. The column NAME axis (space + quote) is covered separately by
# H1; here the name is the fixed unreserved `region` so each row isolates the VALUE encoding.
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

# The small ASCII-SAFE readable table consumed by the ref->DS read test. Deliberately NO non-ASCII
# (avoids the macOS NFC/NFD filesystem-normalization hazard, design R6); covers unreserved, the '='
# reserved char, a quote, a space, and a 2-row partition (US).
READ_ROWS = [(1, "a1", "US"), (2, "b2", "a=b"), (3, "c3", "na me"), (4, "d4", "o'brien"), (5, "e5", "US"),
             (6, "f6", "p%p")]


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

    import pyspark
    from delta import configure_spark_with_delta_pip
    from pyspark.sql import SparkSession
    from pyspark.sql.types import IntegerType, StringType, StructField, StructType

    builder = (
        SparkSession.builder.appName("ds-806-golden").master("local[1]")
        .config("spark.sql.extensions", "io.delta.sql.DeltaSparkSessionExtension")
        .config("spark.sql.catalog.spark_catalog", "org.apache.spark.sql.delta.catalog.DeltaCatalog")
        .config("spark.ui.enabled", "false")
    )
    spark = configure_spark_with_delta_pip(builder).getOrCreate()
    spark.sparkContext.setLogLevel("ERROR")

    # (1) The full matrix table is written to a throwaway temp dir (NOT committed) purely to harvest
    #     the (on-disk dir, add.path) mapping into matrix.json.
    matrix_staging = tempfile.mkdtemp(prefix="ds806-matrix-")
    # atexit so an engine failure mid-run cannot leave the staged table behind.
    atexit.register(shutil.rmtree, matrix_staging, ignore_errors=True)
    matrix_src = os.path.join(matrix_staging, "matrix-table")
    rows = [(i, v) for i, v in enumerate(VALUES)]
    # Explicit nullable schema: the null row must not depend on schema inference.
    matrix_schema = StructType([StructField("id", IntegerType(), False),
                               StructField("region", StringType(), True)])
    spark.createDataFrame(rows, matrix_schema).write.format("delta").partitionBy("region").mode("overwrite").save(matrix_src)
    disk_dirs = [n for n in os.listdir(matrix_src) if n.startswith("region=")]
    log = os.path.join(matrix_src, "_delta_log", "00000000000000000000.json")
    add_by_value = {}
    for line in open(log, encoding="utf-8"):
        o = json.loads(line)
        if "add" in o:
            add_by_value[o["add"]["partitionValues"]["region"]] = o["add"]["path"]

    from urllib.parse import unquote
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
        assert decoded in disk_dirs, f"dir {decoded!r} for value {v!r} not on disk: {disk_dirs}"
        matrix.append({"value": v, "on_disk_dir": decoded, "add_path_segment": add_path.split("/")[0]})
    # (1b) The EMPTY-STRING axis, harvested from a real run. It cannot be a matrix row: Spark folds "" onto
    #      the SAME __HIVE_DEFAULT_PARTITION__ partition as null and emits no distinct directory or add-action
    #      for it, so there is no separate (dir, add.path) pair to record. What IS recordable — and what the
    #      differential test pins — is exactly that folding behaviour plus what the engine reads back.
    empty_staging = tempfile.mkdtemp(prefix="ds806-empty-")
    atexit.register(shutil.rmtree, empty_staging, ignore_errors=True)
    empty_src = os.path.join(empty_staging, "empty-table")
    spark.createDataFrame([(0, None), (1, ""), (2, "keep")], matrix_schema).write.format("delta") \
        .partitionBy("region").mode("overwrite").save(empty_src)
    empty_dirs = sorted(n for n in os.listdir(empty_src) if n.startswith("region="))
    empty_adds = []
    for line in open(os.path.join(empty_src, "_delta_log", "00000000000000000000.json"), encoding="utf-8"):
        o = json.loads(line)
        if "add" in o:
            empty_adds.append((o["add"]["partitionValues"]["region"], o["add"]["path"].split("/")[0]))
    read_back = sorted((r["id"], r["region"]) for r in spark.read.format("delta").load(empty_src).collect())
    empty_row = next((v for v in read_back if v[0] == 1), None)
    empty_string = {
        "on_disk_dirs": empty_dirs,
        "add_partition_values": sorted({a[0] if a[0] is not None else None for a in empty_adds}, key=str),
        "distinct_add_action": any(a[0] == "" for a in empty_adds),
        "folds_onto_sentinel_with_null": not any(a[0] == "" for a in empty_adds),
        # Row presence harvested explicitly (see the delta-rs generator's note): a missing row must not be
        # indistinguishable from a row that read back as null.
        "read_back_ids": sorted(v[0] for v in read_back),
        "read_back_value": empty_row[1] if empty_row else None,
        "read_back_is_null": (empty_row is not None and empty_row[1] is None),
    }
    # Commit this table's log too. Without it the empty_string block would have SINGLE-FILE provenance —
    # the one-file-edit failure mode matrix-log.json exists to remove — even though these cells are the sole
    # evidence for the #899 sentinel decision. Only the LOG is committed, never the table's data files.
    shutil.copyfile(
        os.path.join(empty_src, "_delta_log", "00000000000000000000.json"),
        os.path.join(out_dir, "empty-string-log.json"))
    shutil.rmtree(empty_staging, ignore_errors=True)

    shutil.rmtree(matrix_staging, ignore_errors=True)

    # (2) The committed small readable table.
    read_table = os.path.join(out_dir, "read-table")
    shutil.rmtree(read_table, ignore_errors=True)
    spark.createDataFrame(READ_ROWS, ["id", "name", "region"]).write.format("delta").partitionBy("region").mode("overwrite").save(read_table)
    _strip_crc(read_table)

    out = {
        "engine": "apache-spark",
        "version": pyspark.__version__,
        "delta": "3.2.0",
        "note": "Reference (dir, add.path) partition-encoding golden. Emitted by real Spark; never from DeltaSharp.",
        "column": "region",
        "matrix": matrix,
        "empty_string": empty_string,
    }
    with open(os.path.join(out_dir, "matrix.json"), "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
    _write_checksums(out_dir)
    spark.stop()
    print(f"wrote {len(matrix)} golden rows + {len(READ_ROWS)}-row read-table to {out_dir}")


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
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__)) + "/spark")
