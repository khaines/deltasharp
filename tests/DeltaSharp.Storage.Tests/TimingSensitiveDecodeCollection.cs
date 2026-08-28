using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// xUnit collection that runs its member classes with <b>parallelization disabled against every other
/// collection</b> (#869). Its members assert genuinely time-based bounded-decode invariants — a healthy
/// decode completes while real decoder threads are starved (I1), a fast per-row-group decode wins its own
/// wall-clock budget while a slow consumer sleeps between batches, and a crafted checkpoint fails closed
/// within a real wall-clock budget. Those premises hold with room to spare in isolation but RACE the
/// deadline when the rest of the storage suite is hammering the shared <see cref="System.Threading.ThreadPool"/>
/// in parallel (the full run pins dozens of blocked pool threads, so thread-injection latency widens every
/// window). Running these — and only these — in a non-parallel collection removes the sibling-thread
/// starvation while keeping each test exercising the REAL machinery (real clocks, real threads, real decode),
/// so the flake is gone without weakening any invariant. Only the handful of timing-sensitive methods live
/// here, so the ~58 deterministic BoundedDecode / DeltaFuzz tests keep running fully in parallel — the suite
/// is not serialized.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveDecodeCollection
{
    public const string Name = "Timing-sensitive bounded-decode (real wall-clock deadlines; #869)";
}
