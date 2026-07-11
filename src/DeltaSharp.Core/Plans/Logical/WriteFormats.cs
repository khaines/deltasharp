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

    /// <summary>Spark's Delta Lake write source — a real, storage-backed sink wired end-to-end by
    /// STORY-05.3.3 follow-up (#487): the analyzer accepts it, and the Executor resolves it through the
    /// Storage↔Executor sink adapter (a DeltaSharp.Storage write facade over the Delta transaction
    /// log + Parquet). Unlike <see cref="Memory"/>, its rows land in a real Delta table
    /// (Parquet data files + <c>_delta_log</c>), not an in-process registry.</summary>
    public const string Delta = "delta";

    private static readonly FrozenSet<string> LocalFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Memory }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Storage-backed formats the analyzer accepts (they pass through to physical planning, where the
    // Executor's sink adapter resolves a real writer). `delta` is wired end-to-end by #487; the in-memory
    // `memory` sink is a distinct LOCAL format. These are the write formats whose sink is NOT the in-process
    // memory registry but a durable table on the configured storage backend.
    private static readonly FrozenSet<string> StorageFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Delta }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Formats DeltaSharp intends to write but whose writer is not yet wired. `parquet` (Spark's default
    // write source) still routes to a deterministic EPIC-05 deferral diagnostic (AC4) rather than an
    // "unsupported format" one, so a user targeting it learns ownership, not that it is invalid. (`delta`
    // graduated out of this set in #487.)
    private static readonly FrozenSet<string> DeferredFormats =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "parquet" }
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

        if (StorageFormats.Contains(format))
        {
            return WriteFormatKind.StorageBacked;
        }

        return DeferredFormats.Contains(format)
            ? WriteFormatKind.DeferredToEpic05
            : WriteFormatKind.Unsupported;
    }

    /// <summary>The write formats the analyzer accepts, ordered, for a diagnostic's "supported formats"
    /// list (the in-memory <see cref="Memory"/> local sink plus every storage-backed format such as
    /// <see cref="Delta"/>).</summary>
    public static IReadOnlyList<string> LocalFormatNames =>
        LocalFormats.Concat(StorageFormats)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>The EPIC-05-deferred formats, ordered, for a diagnostic's "deferred formats" list.</summary>
    public static IReadOnlyList<string> DeferredFormatNames =>
        DeferredFormats.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>The M1 disposition of a write format (see <see cref="WriteFormats.Classify(string)"/>).</summary>
internal enum WriteFormatKind
{
    /// <summary>An engine-backed local sink that executes end-to-end in M1 (AC1).</summary>
    Local,

    /// <summary>A storage-backed write format (for example <see cref="WriteFormats.Delta"/>) the analyzer
    /// accepts and the Executor resolves to a real durable sink over the configured storage backend (#487).</summary>
    StorageBacked,

    /// <summary>A format whose writer is owned by EPIC-05 (Delta/Parquet storage); deferred here (AC4).</summary>
    DeferredToEpic05,

    /// <summary>A format with no M1 write mapping; a deterministic unsupported-format diagnostic (AC3).</summary>
    Unsupported,
}
