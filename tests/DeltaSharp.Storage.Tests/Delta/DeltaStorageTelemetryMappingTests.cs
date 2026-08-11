using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Diagnostics;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Unit coverage for the telemetry vocabulary mappers on <see cref="DeltaStorageTelemetry"/> and
/// <see cref="StorageBackendKind"/>: every enum value must map to a stable, low-cardinality label string
/// (design §7.3 closed value sets). Pinning the strings here guards the dashboards/alerts and the
/// OpenTelemetry export pipeline that key on them from silent drift, and exercises the mapper arms that a
/// single end-to-end commit scenario does not reach. The internal enums are asserted from [Fact] bodies
/// (not exposed on public [Theory] signatures, which would be an inaccessible-type error).
/// </summary>
public sealed class DeltaStorageTelemetryMappingTests
{
    [Fact]
    public void BackendKind_MapsToBoundedLabel()
    {
        Assert.Equal("s3", StorageBackendKind.S3.ToLabel());
        Assert.Equal("adls", StorageBackendKind.Adls.ToLabel());
        Assert.Equal("gcs", StorageBackendKind.Gcs.ToLabel());
        Assert.Equal("pvc", StorageBackendKind.Pvc.ToLabel());
        Assert.Equal("unknown", ((StorageBackendKind)999).ToLabel());
    }

    [Fact]
    public void Outcome_MapsToBoundedLabel()
    {
        Assert.Equal("success", DeltaStorageTelemetry.ToLabel(CommitOutcome.Success));
        Assert.Equal("skipped", DeltaStorageTelemetry.ToLabel(CommitOutcome.Skipped));
        Assert.Equal("conflict", DeltaStorageTelemetry.ToLabel(CommitOutcome.Conflict));
        Assert.Equal("contention", DeltaStorageTelemetry.ToLabel(CommitOutcome.Contention));
        Assert.Equal("unknown_state", DeltaStorageTelemetry.ToLabel(CommitOutcome.UnknownState));
        Assert.Equal("partial_transaction", DeltaStorageTelemetry.ToLabel(CommitOutcome.PartialTransaction));
        Assert.Equal("cancelled", DeltaStorageTelemetry.ToLabel(CommitOutcome.Cancelled));
        Assert.Equal("failure", DeltaStorageTelemetry.ToLabel(CommitOutcome.Failure));
        Assert.Equal("failure", DeltaStorageTelemetry.ToLabel((CommitOutcome)999));
    }

    [Fact]
    public void RetryReason_MapsToBoundedLabel()
    {
        Assert.Equal("conflict_rebase", DeltaStorageTelemetry.ToLabel(CommitRetryReason.ConflictRebase));
        Assert.Equal("ambiguous_slot_free", DeltaStorageTelemetry.ToLabel(CommitRetryReason.AmbiguousSlotFree));
        Assert.Equal("transient", DeltaStorageTelemetry.ToLabel(CommitRetryReason.Transient));
        Assert.Equal("transient", DeltaStorageTelemetry.ToLabel((CommitRetryReason)999));
    }

    [Fact]
    public void CheckpointFallbackReason_MapsToBoundedLabel()
    {
        // Exhaustive net (#763): every declared reason must have an explicit, pinned label here. This is not a
        // convenience — the ToLabel switch ends in a `_ => "malformed"` fail-safe, so a future reason (e.g. a new
        // security class) added WITHOUT its own arm would ship silently mapped to `malformed`, re-introducing the
        // exact bit-rot/forgery conflation this issue exists to remove. The Assert.True below fails the moment a
        // reason lacks a pinned expectation, forcing the author to add both a ToLabel arm and an entry here.
        var expected = new Dictionary<CheckpointFallbackReason, string>
        {
            [CheckpointFallbackReason.UnsupportedFeature] = "unsupported_feature",
            [CheckpointFallbackReason.Malformed] = "malformed",
            [CheckpointFallbackReason.ForgedMultiMetadata] = "forged_multi_metadata",
        };

        foreach (CheckpointFallbackReason reason in Enum.GetValues<CheckpointFallbackReason>())
        {
            Assert.True(
                expected.ContainsKey(reason),
                $"CheckpointFallbackReason.{reason} has no pinned label — add a ToLabel arm and an expectation "
                + "here rather than relying on the `_ => \"malformed\"` fail-safe (#763).");
            Assert.Equal(expected[reason], DeltaStorageTelemetry.ToLabel(reason));
        }

        // The runtime fail-safe for an out-of-range cast stays the bounded `malformed` (never free-text).
        Assert.Equal("malformed", DeltaStorageTelemetry.ToLabel((CheckpointFallbackReason)999));
    }

    [Fact]
    public void ConflictKind_MapsToBoundedClass()
    {
        Assert.Equal("concurrent_append", DeltaStorageTelemetry.ToConflictClass(DeltaConflictKind.ConcurrentAppend));
        Assert.Equal("concurrent_delete_read", DeltaStorageTelemetry.ToConflictClass(DeltaConflictKind.ConcurrentDeleteRead));
        Assert.Equal("metadata_changed", DeltaStorageTelemetry.ToConflictClass(DeltaConflictKind.MetadataChanged));
        Assert.Equal("protocol_changed", DeltaStorageTelemetry.ToConflictClass(DeltaConflictKind.ProtocolChanged));
        Assert.Equal("concurrent_transaction", DeltaStorageTelemetry.ToConflictClass(DeltaConflictKind.ConcurrentTransaction));
        Assert.Equal("concurrent_write", DeltaStorageTelemetry.ToConflictClass((DeltaConflictKind)999));
    }
}
