using System.Globalization;
using System.Text;
using DeltaSharp.TestSupport;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Reproduction bundle for a randomized Change Data Feed <b>hardening</b> case (increment 4 of #193; design
/// §3.3 correctness oracles / checklist item 21 "proof, not assertion"). It captures the exact tuple §3.3
/// mandates for a randomized CDF case — <c>{ seed, schema, command-sequence, backend, expected
/// change-manifest }</c> — so a CI failure is deterministically replayable from the log alone. Modeled on the
/// commit-simulation <c>ReproductionBundle</c>: it emits the house <c>[deltasharp-seed]</c> line honoring
/// <see cref="TestSeed.EnvironmentVariable"/> (<c>DELTASHARP_TEST_SEED</c>), and the full bundle is rendered
/// only on failure.
/// </summary>
internal sealed record ChangeFeedReproduction
{
    /// <summary>The base seed (the value to set <c>DELTASHARP_TEST_SEED</c> to when reproducing).</summary>
    public required int BaseSeed { get; init; }

    /// <summary>The per-case effective seed (<see cref="TestSeed.Combine"/> of the base seed and the case
    /// scope) actually fed to <see cref="Random"/> — the value that pins THIS case's history.</summary>
    public required int EffectiveSeed { get; init; }

    /// <summary>The failing test's simple class name — the <c>FullyQualifiedName~</c> filter to rerun it.</summary>
    public required string Scope { get; init; }

    /// <summary>The (partitioned) table schema the case ran against.</summary>
    public required string Schema { get; init; }

    /// <summary>The backend the case ran against (kind + temp root).</summary>
    public required string Backend { get; init; }

    /// <summary>The generated legal command sequence (append / overwrite / DV delete / optimize / enableCdf),
    /// version-annotated, that produced the history under test.</summary>
    public required string CommandSequence { get; init; }

    /// <summary>The expected change-manifest (the model's per-version change multiset, or the snapshot the
    /// feed must fold to) — the oracle's authoritative side of the comparison.</summary>
    public required string ExpectedManifest { get; init; }

    /// <summary>The observed / actual side of the comparison (rendered for the diff).</summary>
    public string? Actual { get; init; }

    /// <summary>The single house reproduction line (always safe to log, even on success).</summary>
    public string ReproductionLine =>
        string.Format(
            CultureInfo.InvariantCulture,
            "[deltasharp-seed] scope={0} baseSeed={1} effectiveSeed={2} | reproduce: {3}={1} dotnet test tests/DeltaSharp.Storage.Tests --filter \"FullyQualifiedName~{0}\"",
            Scope,
            BaseSeed,
            EffectiveSeed,
            TestSeed.EnvironmentVariable);

    /// <summary>Renders the full bundle (reproduction line + captured tuple) for a failure message.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(ReproductionLine).Append('\n');
        sb.Append("--- CDF reproduction bundle ---").Append('\n');
        sb.Append("seed:        base=").Append(BaseSeed.ToString(CultureInfo.InvariantCulture))
            .Append(" effective=").Append(EffectiveSeed.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("schema:      ").Append(Schema).Append('\n');
        sb.Append("backend:     ").Append(Backend).Append('\n');
        sb.Append("commands:    ").Append(CommandSequence).Append('\n');
        sb.Append("expected:").Append('\n').Append(Indent(ExpectedManifest)).Append('\n');
        if (Actual is not null)
        {
            sb.Append("actual:").Append('\n').Append(Indent(Actual)).Append('\n');
        }

        return sb.ToString();
    }

    public override string ToString() => Render();

    private static string Indent(string block) =>
        block.Length == 0
            ? "  <empty>"
            : "  " + block.Replace("\n", "\n  ", StringComparison.Ordinal);
}
