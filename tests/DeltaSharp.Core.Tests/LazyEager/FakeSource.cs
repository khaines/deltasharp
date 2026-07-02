using System.Collections.Generic;
using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// A test double for a DeltaSharp data source that makes the lazy/eager boundary observable. Building
/// its logical descriptor (<see cref="Describe"/>) is a pure <b>transformation</b> — it constructs an
/// immutable <see cref="UnresolvedRelation"/> and touches <b>no</b> I/O and <b>no</b>
/// <see cref="IExecutionAudit"/>. Only <see cref="Read"/> — which stands in for the eager scan a real
/// reader performs when an action runs (the seam #158 will implement) — opens the file and reads rows,
/// notifying the ambient audit sink.
/// </summary>
internal sealed class FakeSource
{
    private readonly string _name;
    private readonly long _rowCount;

    public FakeSource(string name, long rowCount)
    {
        _name = name;
        _rowCount = rowCount;
    }

    /// <summary>
    /// Builds the immutable logical descriptor for this source. This is the plan-construction path a
    /// transformation follows; it performs no work and never notifies the audit sink.
    /// </summary>
    /// <returns>An unresolved relation naming this source.</returns>
    public UnresolvedRelation Describe() =>
        new(new[] { _name });

    /// <summary>
    /// Simulates the eager scan that only an <b>action</b> may trigger: it opens the source and reads
    /// its rows, notifying the ambient <see cref="ExecutionAudit"/> sink. This is the wiring point a
    /// real reader (#158) calls from its pull loop.
    /// </summary>
    /// <returns>The number of rows read.</returns>
    public long Read()
    {
        ExecutionAudit.FileOpened(_name);
        ExecutionAudit.RowsRead(_rowCount);
        return _rowCount;
    }
}
