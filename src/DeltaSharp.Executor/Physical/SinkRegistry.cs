using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Resolves a logical <see cref="SinkDescriptor"/> to the concrete local sink the physical
/// <see cref="WriteToSinkPlan"/> drains its child rows into — the M1 <b>data-out</b> seam, the mirror
/// image of the <see cref="IScanSource"/> data-in seam. The analyzer has already gated the sink format
/// to a supported <b>local</b> sink (a deferred/unsupported format never reaches planning), so a factory
/// that returns <see langword="false"/> here is a defense-in-depth miss surfaced as a deterministic
/// <see cref="UnsupportedPlanException"/> rather than a silent no-op.
/// </summary>
internal interface ILocalSinkFactory
{
    /// <summary>Tries to create a sink for <paramref name="descriptor"/> writing rows of <paramref name="schema"/>.</summary>
    /// <param name="descriptor">The logical sink descriptor (format, mode, path, options).</param>
    /// <param name="schema">The schema of the rows the write will commit.</param>
    /// <param name="sink">The resolved sink when the format is backed.</param>
    /// <returns><see langword="true"/> if a sink was created; otherwise <see langword="false"/>.</returns>
    bool TryCreate(SinkDescriptor descriptor, StructType schema, [NotNullWhen(true)] out ILocalSink? sink);
}

/// <summary>
/// A resolved write target the <see cref="WriteToSinkPlan"/> commits fully-materialized rows to. The
/// commit is <b>atomic</b> and honors the descriptor's <see cref="SaveMode"/>, so a write either lands
/// entirely or (on a mode conflict) not at all — no partial output (STORY-04.6.3 AC1/AC3).
/// </summary>
internal interface ILocalSink
{
    /// <summary>Atomically commits <paramref name="rows"/> to the target, honoring the save mode.</summary>
    /// <param name="schema">The schema the committed rows conform to.</param>
    /// <param name="rows">The fully-materialized rows to commit.</param>
    /// <returns>The number of rows written (0 when an <see cref="SaveMode.Ignore"/> skipped an existing target).</returns>
    /// <exception cref="InvalidOperationException">The mode conflicts with the target's current state
    /// (<see cref="SaveMode.ErrorIfExists"/> onto an existing target, or an <see cref="SaveMode.Append"/>
    /// schema mismatch).</exception>
    long Commit(StructType schema, IReadOnlyList<Row> rows);

    /// <summary>
    /// Cheaply decides — <b>before</b> the write input is executed/materialized — whether an
    /// <see cref="SaveMode.Ignore"/>/<see cref="SaveMode.ErrorIfExists"/> write onto an ALREADY-EXISTING
    /// target can be short-circuited, so a doomed or skipped write never reads or materializes the whole
    /// DataFrame just to throw or return 0 (an OOM risk on large inputs, and a Spark-parity gap — Spark
    /// checks existence before running the job for these modes). Returns <see langword="true"/> when the
    /// write must be SKIPPED (<see cref="SaveMode.Ignore"/> onto an existing target — the caller reports 0
    /// committed rows without executing the child); returns <see langword="false"/> when the write must
    /// PROCEED (a fresh target, or an <see cref="SaveMode.Append"/>/<see cref="SaveMode.Overwrite"/> mode).
    /// This is purely an <b>optimization</b>: <see cref="Commit"/> REMAINS the atomic boundary and
    /// re-checks existence, so a race that creates the target between this probe and the commit is still
    /// caught (an <see cref="SaveMode.ErrorIfExists"/> still throws at commit).
    /// </summary>
    /// <returns><see langword="true"/> to skip the write (Ignore onto an existing target); otherwise <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException"><see cref="SaveMode.ErrorIfExists"/> onto an existing
    /// target — the same conflict <see cref="Commit"/> would throw, raised before the child executes.</exception>
    bool ShouldSkipOrThrow();
}

/// <summary>
/// An <see cref="ILocalSinkFactory"/> backed by an in-process table registry keyed by write target — the
/// one engine-backed M1 local sink (Spark's <c>memory</c> format, AC1) and the write-door mirror of
/// <see cref="InMemoryScanSource"/>. A committed target is retained in-process so a test (or a later
/// read-back path) can observe exactly the rows a <c>Save</c> wrote, proving the write executed as an
/// eager action end-to-end. Commits are serialized under a monitor so the <see cref="SaveMode"/>
/// check-and-set is atomic even on the process-wide <see cref="Default"/> instance.
/// </summary>
internal sealed class InMemorySinkRegistry : ILocalSinkFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CommittedTable> _tables = new(StringComparer.Ordinal);

    /// <summary>A process-wide default sink the auto-registered <see cref="LocalQueryExecutor"/> writes to.</summary>
    public static InMemorySinkRegistry Default { get; } = new();

    /// <inheritdoc/>
    public bool TryCreate(SinkDescriptor descriptor, StructType schema, [NotNullWhen(true)] out ILocalSink? sink)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        // Defense-in-depth: only the "memory" local format is engine-backed in M1. The analyzer already
        // rejects every other format, so a mismatch here means a bypassed analyzer — fail (no sink).
        if (!string.Equals(descriptor.Format, WriteFormats.Memory, StringComparison.OrdinalIgnoreCase))
        {
            sink = null;
            return false;
        }

        sink = new InMemorySink(this, TargetKey(descriptor), descriptor.Mode);
        return true;
    }

    /// <summary>Reads back the rows committed to <paramref name="target"/> (test / read-back seam).</summary>
    /// <param name="target">The target key (the write path).</param>
    /// <param name="schema">The committed schema when present.</param>
    /// <param name="rows">The committed rows when present.</param>
    /// <returns><see langword="true"/> if a table has been committed to the target; otherwise <see langword="false"/>.</returns>
    public bool TryRead(string target, [NotNullWhen(true)] out StructType? schema, out IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (_tables.TryGetValue(target, out CommittedTable? table))
            {
                schema = table.Schema;
                rows = table.Rows;
                return true;
            }

            schema = null;
            rows = Array.Empty<Row>();
            return false;
        }
    }

    /// <summary>Removes any table committed to <paramref name="target"/> (test hygiene on the shared default).</summary>
    /// <param name="target">The target key to clear.</param>
    public void Clear(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            _tables.Remove(target);
        }
    }

    // The atomic check-and-set the InMemorySink delegates to. Serialized under the monitor so the
    // SaveMode existence check and the mutation are one indivisible step (no torn state on the shared
    // Default across parallel test collections).
    private long Commit(string target, SaveMode mode, StructType schema, IReadOnlyList<Row> rows)
    {
        lock (_gate)
        {
            bool exists = _tables.TryGetValue(target, out CommittedTable? existing);
            switch (mode)
            {
                case SaveMode.ErrorIfExists:
                    if (exists)
                    {
                        throw ErrorIfExistsConflict(target);
                    }

                    _tables[target] = new CommittedTable(schema, rows);
                    return rows.Count;

                case SaveMode.Ignore:
                    // Silently skip when the target exists (Spark parity): no write, zero rows.
                    if (exists)
                    {
                        return 0;
                    }

                    _tables[target] = new CommittedTable(schema, rows);
                    return rows.Count;

                case SaveMode.Overwrite:
                    _tables[target] = new CommittedTable(schema, rows);
                    return rows.Count;

                case SaveMode.Append:
                    if (!exists)
                    {
                        _tables[target] = new CommittedTable(schema, rows);
                        return rows.Count;
                    }

                    if (!existing!.Schema.Equals(schema))
                    {
                        throw new InvalidOperationException(
                            $"Cannot append to '{SecretRedaction.RedactPath(target)}': the data schema "
                            + $"'{schema.SimpleString}' does not match the existing schema '{existing.Schema.SimpleString}'.");
                    }

                    var combined = new List<Row>(existing.Rows.Count + rows.Count);
                    combined.AddRange(existing.Rows);
                    combined.AddRange(rows);
                    _tables[target] = new CommittedTable(schema, combined);
                    return rows.Count;

                default:
                    throw new InvalidOperationException($"Unknown save mode '{mode}'.");
            }
        }
    }

    // The cheap pre-commit existence probe the InMemorySink delegates ShouldSkipOrThrow to, used by
    // WriteToSinkPlan to short-circuit an Ignore/ErrorIfExists write onto an existing target BEFORE the
    // child is executed/materialized. Returns true when an Ignore must skip an existing target, throws the
    // ErrorIfExists conflict (the SAME exception Commit throws), and returns false when the write must
    // proceed (fresh target, or an Append/Overwrite mode). It re-uses the same monitor as Commit, but is
    // only an OPTIMIZATION: Commit re-checks existence under the lock, so a target created between this
    // probe and the commit is still handled correctly there (ErrorIfExists still throws at commit).
    private bool CheckPreCommit(string target, SaveMode mode)
    {
        if (mode is not (SaveMode.Ignore or SaveMode.ErrorIfExists))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_tables.ContainsKey(target))
            {
                return false;
            }

            if (mode == SaveMode.Ignore)
            {
                return true;
            }

            throw ErrorIfExistsConflict(target);
        }
    }

    // The ErrorIfExists conflict, shared by Commit and the CheckPreCommit early probe so the two paths
    // never drift. The target is redacted so a collision diagnostic (or a log capturing it) never leaks a
    // secret embedded in the write path (parity with the analyzer diagnostics, #432).
    private static InvalidOperationException ErrorIfExistsConflict(string target) =>
        new($"Cannot write to '{SecretRedaction.RedactPath(target)}': it already exists and the save "
            + "mode is ErrorIfExists. Use Overwrite, Append, or Ignore to write to an existing target.");

    // A write is keyed by its path (the common case), then a table identifier, then a stable default for a
    // path-less memory Save. Path discovery (descriptor.Path or a case-insensitive `path` option) is shared
    // with DeltaLocalSink via SinkDescriptorPaths so the two sinks never drift; the memory registry adds a
    // TableIdentifier / "memory://default" fallback the delta sink does not have.
    private static string TargetKey(SinkDescriptor descriptor) =>
        SinkDescriptorPaths.ResolvePath(descriptor)
        ?? (descriptor.TableIdentifier is { Count: > 0 } identifier
            ? string.Join('.', identifier)
            : "memory://default");

    private sealed record CommittedTable(StructType Schema, IReadOnlyList<Row> Rows);

    private sealed class InMemorySink : ILocalSink
    {
        private readonly InMemorySinkRegistry _registry;
        private readonly string _target;
        private readonly SaveMode _mode;

        public InMemorySink(InMemorySinkRegistry registry, string target, SaveMode mode)
        {
            _registry = registry;
            _target = target;
            _mode = mode;
        }

        public long Commit(StructType schema, IReadOnlyList<Row> rows)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(rows);
            return _registry.Commit(_target, _mode, schema, rows);
        }

        public bool ShouldSkipOrThrow() => _registry.CheckPreCommit(_target, _mode);
    }
}
