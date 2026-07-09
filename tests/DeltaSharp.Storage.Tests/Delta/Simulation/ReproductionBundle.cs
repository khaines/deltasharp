using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using DeltaSharp.TestSupport;

namespace DeltaSharp.Storage.Tests.Delta.Simulation;

/// <summary>
/// The reproduction bundle for a commit-simulation run (design §3.0 / checklist 21: <i>"Randomized plan
/// tests capture seed, schema, data, partitioning, backend, storage trace, and expected output"</i>). It
/// captures exactly the tuple mandated by §3.4 — <c>{ seed, schema, data, partition-spec, backend
/// fault-schedule, writer interleaving, expected state }</c> — so any failing case is deterministically
/// reproducible. <see cref="ReproductionLine"/> emits the house <c>[deltasharp-seed]</c> line that honors
/// <see cref="TestSeed.EnvironmentVariable"/> (<c>DELTASHARP_TEST_SEED</c>), so a CI failure is replayed
/// locally byte-for-byte. The bundle is only rendered on failure (it is otherwise inert).
/// </summary>
internal sealed record ReproductionBundle
{
    public required int BaseSeed { get; init; }

    public required int EffectiveSeed { get; init; }

    public required string Scope { get; init; }

    public required string Schema { get; init; }

    public required string PartitionSpec { get; init; }

    public required int WriterCount { get; init; }

    public required string FaultSchedule { get; init; }

    /// <summary>The seed-determined interleaving actually taken: the ordered writer-resume sequence.</summary>
    public required string Interleaving { get; init; }

    /// <summary>The declared data / write manifest per writer (the "data" + "expected output" tuple field).</summary>
    public required ImmutableArray<string> WriterManifests { get; init; }

    /// <summary>The final expected/observed table state (active files + version).</summary>
    public required string ExpectedState { get; init; }

    /// <summary>The <c>[deltasharp-seed]</c> reproduction line honoring <c>DELTASHARP_TEST_SEED</c>.</summary>
    public string ReproductionLine =>
        string.Format(
            CultureInfo.InvariantCulture,
            "[deltasharp-seed] scope={0} baseSeed={1} effectiveSeed={2} | reproduce: DELTASHARP_TEST_SEED={1} dotnet test --filter \"FullyQualifiedName~{0}\"",
            Scope,
            BaseSeed,
            EffectiveSeed);

    /// <summary>Renders the full bundle (multi-line) for an assertion message — the minimized regression
    /// artifact when a case fails.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(ReproductionLine).Append('\n');
        sb.Append("--- reproduction bundle ---").Append('\n');
        sb.Append("seed:           base=").Append(BaseSeed.ToString(CultureInfo.InvariantCulture))
            .Append(" effective=").Append(EffectiveSeed.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("schema:         ").Append(Schema).Append('\n');
        sb.Append("partition-spec: ").Append(PartitionSpec).Append('\n');
        sb.Append("writers:        ").Append(WriterCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("fault-schedule: ").Append(FaultSchedule).Append('\n');
        sb.Append("interleaving:   ").Append(Interleaving).Append('\n');
        sb.Append("data/manifest:").Append('\n');
        foreach (string manifest in WriterManifests)
        {
            sb.Append("  - ").Append(manifest).Append('\n');
        }

        sb.Append("expected-state: ").Append(ExpectedState).Append('\n');
        return sb.ToString();
    }

    public override string ToString() => Render();
}
