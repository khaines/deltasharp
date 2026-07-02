using System.Collections.Generic;
using System.Threading;
using DeltaSharp.Diagnostics;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// A thread-safe recording <see cref="IExecutionAudit"/> sink for the lazy/eager regression tests. It
/// counts file opens and rows read with <see cref="Interlocked"/> and captures the ordered
/// <see cref="ExecutionStage"/> path so a test can assert both "no execution happened" (all counters
/// zero, empty path) and "the expected analyzer → planner → backend path was observed".
/// </summary>
internal sealed class RecordingAudit : IExecutionAudit
{
    private readonly object _stageLock = new();
    private readonly List<ExecutionStage> _stages = new();
    private long _filesOpened;
    private long _rowsRead;

    /// <summary>The number of <see cref="IExecutionAudit.OnFileOpened(string)"/> notifications received.</summary>
    public long FilesOpened => Interlocked.Read(ref _filesOpened);

    /// <summary>The total number of rows reported through <see cref="IExecutionAudit.OnRowsRead(long)"/>.</summary>
    public long RowsRead => Interlocked.Read(ref _rowsRead);

    /// <summary>A snapshot of the ordered pipeline stages entered so far.</summary>
    public IReadOnlyList<ExecutionStage> StagePath
    {
        get
        {
            lock (_stageLock)
            {
                return _stages.ToArray();
            }
        }
    }

    /// <summary>
    /// <see langword="true"/> when nothing eager has been observed: no file opened, no row read, and
    /// no stage entered. This is the assertion the AC1/AC2 lazy tests make while only transformations
    /// run.
    /// </summary>
    public bool ObservedNoExecution
    {
        get
        {
            lock (_stageLock)
            {
                return Interlocked.Read(ref _filesOpened) == 0
                    && Interlocked.Read(ref _rowsRead) == 0
                    && _stages.Count == 0;
            }
        }
    }

    /// <inheritdoc/>
    public void OnFileOpened(string source) => Interlocked.Increment(ref _filesOpened);

    /// <inheritdoc/>
    public void OnRowsRead(long count) => Interlocked.Add(ref _rowsRead, count);

    /// <inheritdoc/>
    public void OnStageEntered(ExecutionStage stage)
    {
        lock (_stageLock)
        {
            _stages.Add(stage);
        }
    }
}
