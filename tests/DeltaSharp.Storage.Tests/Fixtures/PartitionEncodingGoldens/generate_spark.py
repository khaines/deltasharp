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
import hashlib
import json
import os
import shutil
import sys

# The partition-value matrix: ASCII-unreserved, every ASCII-reserved char, sub-delims, the
# URI-illegal set (< > | { } ` " \ [ ] ^ space), non-ASCII (Latin, CJK, emoji), and a value that
# already contains a percent-escape. The column NAME axis (space + quote) is covered separately by
# H1; here the name is the fixed unreserved `region` so each row isolates the VALUE encoding.
VALUES = [
    "US", "a=b", "na me", "région", "名前", "e🎯moji", "o'brien", "a/b", "c:d", "q?x", "h#h",
    "p%p", "amp&r", "plus+", "comma,", "semi;", "excl!", "dollar$", "paren()", "star*", "tilde~",
    "lt<gt>", "pipe|", "brace{}", "brack[]", "caret^", "quote\"", "back\\", "at@", "hash`bt",
]

# The small ASCII-SAFE readable table consumed by the ref->DS read test. Deliberately NO non-ASCII
# (avoids the macOS NFC/NFD filesystem-normalization hazard, design R6); covers unreserved, the '='
# reserved char, a quote, a space, and a 2-row partition (US).
READ_ROWS = [(1, "a1", "US"), (2, "b2", "a=b"), (3, "c3", "na me"), (4, "d4", "o'brien"), (5, "e5", "US")]


def main(out_dir: str) -> None:
    import pyspark
    from delta import configure_spark_with_delta_pip
    from pyspark.sql import SparkSession

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
    matrix_src = os.path.join(out_dir, ".matrix-src")
    shutil.rmtree(matrix_src, ignore_errors=True)
    rows = [(i, v) for i, v in enumerate(VALUES)]
    spark.createDataFrame(rows, ["id", "region"]).write.format("delta").partitionBy("region").mode("overwrite").save(matrix_src)
    disk_dirs = [n for n in os.listdir(matrix_src) if n.startswith("region=")]
    log = os.path.join(matrix_src, "_delta_log", "00000000000000000000.json")
    add_by_value = {}
    for line in open(log, encoding="utf-8"):
        o = json.loads(line)
        if "add" in o:
            add_by_value[o["add"]["partitionValues"]["region"]] = o["add"]["path"]

    from urllib.parse import unquote
    matrix = []
    for v in VALUES:
        add_path = add_by_value[v]
        decoded = unquote(add_path.split("/")[0])
        assert decoded in disk_dirs, f"dir {decoded!r} for value {v!r} not on disk: {disk_dirs}"
        matrix.append({"value": v, "on_disk_dir": decoded, "add_path_segment": add_path.split("/")[0]})
    shutil.rmtree(matrix_src, ignore_errors=True)

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
    }
    os.makedirs(out_dir, exist_ok=True)
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
