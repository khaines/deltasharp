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
}
