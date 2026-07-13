using System.Collections.Immutable;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class ProtocolNegotiationTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public ProtocolNegotiationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "proto-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void EnsureReadable_AllowsBasicReaderVersion1()
    {
        ProtocolSupport.EnsureReadable(new ProtocolAction(1, 2, [], []));
    }

    [Fact]
    public void EnsureReadable_AllowsReaderVersion3_WithNoReaderFeatures()
    {
        ProtocolSupport.EnsureReadable(new ProtocolAction(3, 7, [], ["appendOnly"]));
    }

    [Fact]
    public void EnsureReadable_RejectsReaderVersion2_ColumnMapping()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ProtocolSupport.EnsureReadable(new ProtocolAction(2, 5, [], [])));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Contains("columnMapping", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureReadable_RejectsUnsupportedReaderFeature()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ProtocolSupport.EnsureReadable(new ProtocolAction(3, 7, ["deletionVectors"], [])));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Contains("deletionVectors", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureReadable_RejectsUnknownReaderVersion()
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ProtocolSupport.EnsureReadable(new ProtocolAction(4, 7, [], [])));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSnapshot_FailsClosed_OnUnsupportedReaderFeature()
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0,
            DeltaTestHarness.ProtocolWithReaderFeature("deletionVectors"),
            DeltaTestHarness.Metadata(),
            DeltaTestHarness.Add("a.parquet"));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(_backend).LoadSnapshotAsync());
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Contains("deletionVectors", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSnapshot_Serves_BaselineTable()
    {
        await DeltaTestHarness.WriteCommitAsync(_backend, 0,
            DeltaTestHarness.Protocol(),
            DeltaTestHarness.Metadata(),
            DeltaTestHarness.Add("a.parquet"));

        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();
        Assert.Equal(["a.parquet"], snapshot.ActiveFiles.Select(a => a.Path));
    }

    [Fact]
    public async Task LoadSnapshot_FailsClosed_WhenFeatureAppearsInLaterCommit()
    {
        // A table upgraded mid-history to require an unsupported reader feature must fail closed at the
        // latest snapshot. (columnMapping is now supported — STORY-05.4.3 — so this uses deletionVectors,
        // which remains unimplemented.)
        await DeltaTestHarness.WriteCommitAsync(_backend, 0,
            DeltaTestHarness.Protocol(),
            DeltaTestHarness.Metadata(),
            DeltaTestHarness.Add("a.parquet"));
        await DeltaTestHarness.WriteCommitAsync(_backend, 1,
            DeltaTestHarness.ProtocolWithReaderFeature("deletionVectors"));

        await Assert.ThrowsAsync<DeltaProtocolException>(() => new DeltaLog(_backend).LoadSnapshotAsync());

        // But time travel to the pre-upgrade version 0 still serves.
        Snapshot v0 = await new DeltaLog(_backend).LoadSnapshotAsync(version: 0);
        Assert.Equal(1, v0.Protocol.MinReaderVersion);
    }
}
