using DeltaSharp.Diagnostics;
using DeltaSharp.Plans.Logical;

namespace DeltaSharp.Core.Tests.LazyEager;

/// <summary>
/// A <see cref="FakeSource"/>-style test double whose eager scan is <b>booby-trapped</b>: building
/// its logical descriptor (<see cref="Describe"/>) is a pure transformation that touches no I/O, but
/// any call to <see cref="Read"/> throws. It makes the lazy invariant self-enforcing — if a
/// <see cref="DataFrame"/> transformation ever triggered a scan while building the plan, the throw
/// would surface as a failed test (the marquee STORY-04.2.1 / #160 lazy proof).
/// </summary>
internal sealed class ThrowOnReadSource
{
    private readonly string _name;

    public ThrowOnReadSource(string name) => _name = name;

    /// <summary>The number of times <see cref="Read"/> was (illegally) invoked; must stay zero.</summary>
    public int ReadCount { get; private set; }

    /// <summary>Builds the immutable logical descriptor — the plan-construction path a transformation
    /// follows. Performs no work and never reads.</summary>
    public UnresolvedRelation Describe() => new(new[] { _name });

    /// <summary>The eager scan only an action may trigger. Here it always throws, so any accidental
    /// read during transformation building fails loudly.</summary>
    public long Read()
    {
        ReadCount++;
        ExecutionAudit.FileOpened(_name);
        throw new InvalidOperationException(
            $"Source '{_name}' was read while only transformations should have run — the lazy "
            + "invariant (ADR-0001) was violated.");
    }
}
