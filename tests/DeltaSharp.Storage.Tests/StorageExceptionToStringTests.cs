using System;
using System.Text.Json;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #664 (RF-8b parity): the storage decode/validation exceptions scrub attacker-influenceable content from
/// their sanitized <see cref="Exception.Message"/> but retain the raw underlying cause as the inner for
/// server-side diagnostics. Their <see cref="object.ToString"/> overrides must therefore render the sanitized
/// message (+ Kind) and the exception's own stack trace but NEVER the <see cref="Exception.InnerException"/>
/// chain — so a sink that logs <c>ex.ToString()</c> (or the default <c>ILogger.LogError(ex, …)</c> providers,
/// which render <c>ToString()</c>) cannot re-surface the raw inner (a Parquet.Net message / crafted bytes /
/// JSON parse text) that <see cref="Exception.Message"/> deliberately dropped. The inner stays reachable via
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class StorageExceptionToStringTests
{
    private const string RawInnerLeak = "RAW-INNER-LEAK\r\ncrafted-bytes-0xDEADBEEF\u2028more";

    [Fact]
    public void DeltaStorageException_ToString_OmitsRawInner_KeepsSanitizedMessageAndKind()
    {
        var raw = new InvalidOperationException(RawInnerLeak);
        DeltaStorageException ex = DeltaStorageException.CorruptData("Parquet footer is malformed.", raw);

        string rendered = ex.ToString();

        Assert.Contains("Parquet footer is malformed.", rendered, StringComparison.Ordinal); // sanitized message kept
        Assert.Contains(nameof(StorageErrorKind.CorruptData), rendered, StringComparison.Ordinal); // Kind kept
        Assert.Contains(nameof(DeltaStorageException), rendered, StringComparison.Ordinal); // type name kept
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal); // raw inner NOT surfaced
        Assert.DoesNotContain("crafted-bytes-0xDEADBEEF", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', rendered); // no injected control chars from the inner
        // The raw cause is RETAINED for server-side diagnostics — omitted from ToString(), not discarded.
        Assert.Same(raw, ex.InnerException);
        Assert.Equal(RawInnerLeak, ex.InnerException!.Message);
    }

    [Fact]
    public void DeltaProtocolException_ToString_OmitsRawInner_KeepsSanitizedMessageAndKind()
    {
        var raw = new JsonException(RawInnerLeak);
        DeltaProtocolException ex = DeltaProtocolException.Malformed(
            "A Delta add.stats value is not valid JSON.", raw);

        string rendered = ex.ToString();

        Assert.Contains("A Delta add.stats value is not valid JSON.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaProtocolErrorKind.MalformedAction), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("crafted-bytes-0xDEADBEEF", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', rendered);
        Assert.Same(raw, ex.InnerException);
    }

    [Fact]
    public void DeltaReadException_ToString_OmitsEntireChain_DownToRawParquetInner()
    {
        // The read facade re-wraps a storage exception whose OWN inner is the raw cause. ToString() must omit
        // the whole chain — neither the intermediate storage message nor the deepest raw inner may surface.
        var raw = new InvalidOperationException(RawInnerLeak);
        DeltaStorageException storage = DeltaStorageException.CorruptData("Parquet footer is malformed.", raw);
        var read = new DeltaReadException("The table could not be read.", storage);

        string rendered = read.ToString();

        Assert.Contains("The table could not be read.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaReadException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal); // deepest raw inner absent
        Assert.DoesNotContain("Parquet footer is malformed.", rendered, StringComparison.Ordinal); // intermediate inner absent
        Assert.DoesNotContain('\r', rendered);
        // Chain retained for diagnostics: read -> storage -> raw.
        Assert.Same(storage, read.InnerException);
        Assert.Same(raw, read.InnerException!.InnerException);
    }

    [Fact]
    public void DeltaReadException_ToString_WithNoInner_RendersMessage()
    {
        var read = new DeltaReadException("The path is not a Delta table.");

        string rendered = read.ToString();

        Assert.Contains("The path is not a Delta table.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaReadException), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DeltaReadSchemaEvolutionException_ToString_OmitsInner_KeepsPathOnProperty()
    {
        // The attacker-controllable data-file path is kept only on .FilePath (never in the message); the
        // originating storage exception is the inner and must not be auto-rendered.
        const string poisonedPath = "s3://evil/\r\ninjected/file.parquet";
        var inner = DeltaStorageException.ColumnNotPresentInFile("secret_col");
        var ex = new DeltaReadSchemaEvolutionException(poisonedPath, inner);

        string rendered = ex.ToString();

        Assert.Contains(nameof(DeltaReadSchemaEvolutionException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(poisonedPath, rendered, StringComparison.Ordinal); // path only on .FilePath
        Assert.DoesNotContain("secret_col", rendered, StringComparison.Ordinal); // inner not auto-rendered
        Assert.DoesNotContain('\r', rendered);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(poisonedPath, ex.FilePath); // raw path retained on the typed property
    }

    [Fact]
    public void DeltaCommitUnknownStateException_ToString_OmitsRawInner_KeepsMessage()
    {
        var raw = new InvalidOperationException(RawInnerLeak);
        var ex = new DeltaCommitUnknownStateException(42, "The commit outcome could not be resolved.", raw);

        string rendered = ex.ToString();

        Assert.Contains("The commit outcome could not be resolved.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaCommitUnknownStateException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', rendered);
        Assert.Same(raw, ex.InnerException);
        Assert.Equal(42, ex.Version);
    }

    [Fact]
    public void OptimizeSchemaEvolutionException_ToString_OmitsInner_KeepsPathOnProperty()
    {
        // Red-team R1: OPTIMIZE's narrow-file failure chains the raw storage exception and had no override.
        const string poisonedPath = "s3://evil/\r\ninjected/opt.parquet";
        DeltaStorageException inner = DeltaStorageException.ColumnNotPresentInFile("secret_col_leak");
        var ex = new OptimizeSchemaEvolutionException(poisonedPath, inner);

        string rendered = ex.ToString();

        Assert.Contains(nameof(OptimizeSchemaEvolutionException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(poisonedPath, rendered, StringComparison.Ordinal); // path only on .FilePath
        Assert.DoesNotContain("secret_col_leak", rendered, StringComparison.Ordinal); // inner not auto-rendered
        Assert.DoesNotContain('\r', rendered);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(poisonedPath, ex.FilePath); // raw path retained on the typed property
    }

    [Fact]
    public void CoveredException_WrappedInOuterOrAggregate_ToString_TransitivelyOmitsRawInner()
    {
        // Production reality: these exceptions are usually caught-and-wrapped or framework-logged, not
        // ToString()'d directly. Exception.ToString() recurses into the inner via the inner's OWN (overridden)
        // ToString(), so a covered storage exception nested in a plain outer exception or an
        // AggregateException must STILL suppress its raw inner — the load-bearing transitive-virtual-dispatch
        // behavior the default ILogger/OTel rendering relies on (SRE R1). Nothing else locks this.
        var raw = new InvalidOperationException(RawInnerLeak);
        DeltaStorageException storage = DeltaStorageException.CorruptData("Parquet footer is malformed.", raw);

        // (a) wrapped in a plain outer exception (caught-and-rethrown) — the covered layer's sanitized message
        // surfaces, but its raw inner does not.
        var wrapped = new InvalidOperationException("outer read step failed", storage);
        string wrappedRendered = wrapped.ToString();
        Assert.Contains("Parquet footer is malformed.", wrappedRendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", wrappedRendered, StringComparison.Ordinal);
        Assert.DoesNotContain("crafted-bytes-0xDEADBEEF", wrappedRendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', wrappedRendered);

        // (b) AggregateException + (c) its Flatten() (the Task / parallel pattern, common before logging).
        var aggregate = new AggregateException(storage);
        Assert.DoesNotContain("RAW-INNER-LEAK", aggregate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\r', aggregate.ToString());
        Assert.DoesNotContain("RAW-INNER-LEAK", aggregate.Flatten().ToString(), StringComparison.Ordinal);
    }
}
