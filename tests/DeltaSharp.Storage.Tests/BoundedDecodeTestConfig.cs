using System.Runtime.CompilerServices;
using DeltaSharp.Storage;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// Widens the process-wide <see cref="BoundedDecode"/> tier for the whole test assembly. Several fail-closed
/// regression tests (#647/#699/#716) deliberately feed genuinely NON-TERMINATING crafted inputs through the
/// decode doors; each such decode strands FOREVER (the runtime cannot abort it), permanently occupying one
/// slot on the dedicated decode scheduler. With the small production default (a quarter of the cores), the
/// accumulated strands would saturate the tier and starve/reject every LATER decode in the suite — the
/// admission cap firing on unrelated valid-decode tests. Raising the shared caps here keeps the handful of
/// deliberate strands from ever filling the tier, so unrelated tests decode normally. The admission-cap and
/// scheduling SEMANTICS are still asserted precisely — on isolated <see cref="BoundedDecoder"/> instances with
/// tiny caps — so this widening never hides the very behavior under test.
/// </summary>
internal static class BoundedDecodeTestConfig
{
    [ModuleInitializer]
    internal static void Widen() => BoundedDecode.ConfigureSharedForTests(
        maxConcurrentDecodes: 256, maxDetachedDecodes: 4096);
}
