using System.Globalization;
using System.Text;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class DeltaLogTests : IDisposable
{
    private const string EmptySchema = """{\"type\":\"struct\",\"fields\":[]}""";

    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltalog-tests-" + Guid.NewGuid().ToString("N"));
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
            // best-effort test cleanup
        }
    }

    private async Task WriteCommitAsync(long version, params string[] lines)
    {
        string name = "_delta_log/" + version.ToString(CultureInfo.InvariantCulture).PadLeft(20, '0') + ".json";
        byte[] content = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        await _backend.PutIfAbsentAsync(name, content, CancellationToken.None);
    }

    private static string Protocol => """{"protocol":{"minReaderVersion":1,"minWriterVersion":2}}""";

    private static string Metadata =>
        """{"metaData":{"id":"t","format":{"provider":"parquet","options":{}},"schemaString":"__S__","partitionColumns":[],"configuration":{}}}"""
            .Replace("__S__", EmptySchema, StringComparison.Ordinal);

    private static string Add(string path) =>
        """{"add":{"path":"__P__","partitionValues":{},"size":1,"modificationTime":1,"dataChange":true}}"""
            .Replace("__P__", path, StringComparison.Ordinal);

    private static string Remove(string path) =>
        """{"remove":{"path":"__P__","deletionTimestamp":1,"dataChange":true}}"""
            .Replace("__P__", path, StringComparison.Ordinal);

    [Fact]
    public async Task LoadLatest_ReplaysCommitChain_ToActiveFiles()
    {
        await WriteCommitAsync(0, Protocol, Metadata, Add("a.parquet"), Add("b.parquet"));
        await WriteCommitAsync(1, Remove("a.parquet"), Add("c.parquet"));

        Snapshot snapshot = await new DeltaLog(_backend).LoadSnapshotAsync();

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(["b.parquet", "c.parquet"], snapshot.ActiveFiles.Select(a => a.Path));
        Assert.Equal(2, snapshot.Metrics.ReplayedCommitCount);
    }

    [Fact]
    public async Task LoadVersion_TimeTravels_ToAnEarlierSnapshot()
    {
        await WriteCommitAsync(0, Protocol, Metadata, Add("a.parquet"));
        await WriteCommitAsync(1, Add("b.parquet"));

        Snapshot v0 = await new DeltaLog(_backend).LoadSnapshotAsync(version: 0);

        Assert.Equal(0, v0.Version);
        Assert.Equal(["a.parquet"], v0.ActiveFiles.Select(a => a.Path)); // does NOT observe v1's add (isolation)
    }

    [Fact]
    public async Task Load_FailsClosed_OnEmptyLog()
    {
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(_backend).LoadSnapshotAsync());
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
    }

    [Fact]
    public async Task Load_FailsClosed_OnMissingRequestedVersion()
    {
        await WriteCommitAsync(0, Protocol, Metadata, Add("a.parquet"));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(_backend).LoadSnapshotAsync(version: 5));
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
    }

    [Fact]
    public async Task Load_FailsClosed_OnGapInCommitChain()
    {
        await WriteCommitAsync(0, Protocol, Metadata, Add("a.parquet"));
        await WriteCommitAsync(2, Add("c.parquet")); // version 1 is missing

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(_backend).LoadSnapshotAsync());
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
        Assert.Contains("gap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_FailsClosed_OnOversizedCommit()
    {
        await WriteCommitAsync(0, Protocol, Metadata, Add("a.parquet"));

        // A commit file is untrusted input; a read ceiling makes an oversized/corrupt object fail closed
        // rather than driving an unbounded allocation (design §5.4 C-DECODE). A small injected ceiling
        // exercises the bound without materializing a multi-hundred-MiB file.
        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaLog(_backend, maxLogObjectBytes: 8).LoadSnapshotAsync());
        Assert.Equal(DeltaProtocolErrorKind.InconsistentLog, ex.Kind);
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }
}
