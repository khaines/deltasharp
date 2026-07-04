using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace DeltaSharp.Plans.Logical;

/// <summary>
/// The M1 write-format taxonomy shared by the write door (<see cref="DeltaSharp.DataFrameWriter"/>) and
/// the analyzer. A <see cref="SinkDescriptor.Format"/> falls into exactly one <see cref="WriteFormatKind"/>:
/// an engine-backed <b>local</b> sink that executes end-to-end in M1, a format whose writer is
/// <b>deferred to EPIC-05</b> (Delta transaction-log storage), or an <b>unsupported</b> format with no
/// M1 mapping. Classification is deterministic and case-insensitive (Spark stores source names
/// case-insensitively), so the diagnostics the analyzer raises are stable run-to-run.
/// </summary>
internal static class WriteFormats
{
    /// <summary>Spark's in-memory collectable sink name — the one engine-backed local sink M1 supports
    /// end-to-end (AC1). The Executor's in-memory sink registry backs this format.</summary>
    public const string Memory = "memory";

    /// <summary>Spark's default write source (<c>spark.sql.sources.default</c>). A writer that never calls
    /// <see cref="DeltaSharp.DataFrameWriter.Format(string)"/> writes <c>parquet</c> — which in M1 routes
    /// to the EPIC-05 deferral (AC4), matching Spark's default while keeping storage out of this story.</summary>
    public const string Default = "parquet";

    private static readonly FrozenSet<string> LocalFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Memory }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Formats DeltaSharp intends to write but whose writer is delivered by EPIC-05 (Delta/Parquet
    // storage), not this story. They resolve to a deterministic EPIC-05 diagnostic (AC4) rather than an
    // "unsupported format" one, so a user targeting Delta/Parquet learns ownership, not that it is invalid.
    private static readonly FrozenSet<string> DeferredFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "delta", "parquet" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Classifies <paramref name="format"/> into its M1 write disposition.</summary>
    /// <param name="format">The sink format name (case-insensitive).</param>
    /// <returns>The format's <see cref="WriteFormatKind"/>.</returns>
    public static WriteFormatKind Classify(string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        if (LocalFormats.Contains(format))
        {
            return WriteFormatKind.Local;
        }

        return DeferredFormats.Contains(format)
            ? WriteFormatKind.DeferredToEpic05
            : WriteFormatKind.Unsupported;
    }

    /// <summary>The recognized local sink formats, ordered, for a diagnostic's "supported formats" list.</summary>
    public static IReadOnlyList<string> LocalFormatNames =>
        LocalFormats.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The EPIC-05-deferred formats, ordered, for a diagnostic's "deferred formats" list.</summary>
    public static IReadOnlyList<string> DeferredFormatNames =>
        DeferredFormats.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>The M1 disposition of a write format (see <see cref="WriteFormats.Classify(string)"/>).</summary>
internal enum WriteFormatKind
{
    /// <summary>An engine-backed local sink that executes end-to-end in M1 (AC1).</summary>
    Local,

    /// <summary>A format whose writer is owned by EPIC-05 (Delta/Parquet storage); deferred here (AC4).</summary>
    DeferredToEpic05,

    /// <summary>A format with no M1 write mapping; a deterministic unsupported-format diagnostic (AC3).</summary>
    Unsupported,
}
