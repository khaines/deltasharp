using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeltaSharp.Storage.Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The non-owning window the §2.4b reconciliation reads its just-written footer through.
/// </summary>
/// <remarks>
/// N2. The window rebases positions and reports its own <see cref="Stream.Length"/>, and Parquet.Net locates a
/// footer by seeking from the END — so a window whose bounds run past the backing stream would have the footer
/// reader parse whatever the backing stream happens to hold (or read short and mis-report a truncated file as
/// corrupt) instead of failing at construction. The bounds are therefore validated up front.
/// </remarks>
public sealed class NonOwningWindowStreamTests
{
    [Fact]
    public void Constructor_RejectsANullBackingStream() =>
        Assert.Throws<ArgumentNullException>(() => new NonOwningWindowStream(null!, 0, 0));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-5, -5)]
    public void Constructor_RejectsNegativeBounds(long offset, long length)
    {
        using var inner = new MemoryStream(new byte[16]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NonOwningWindowStream(inner, offset, length));
    }

    [Theory]
    [InlineData(17, 0)]
    [InlineData(0, 17)]
    [InlineData(10, 7)]
    [InlineData(16, 1)]
    public void Constructor_RejectsAWindowThatRunsPastTheBackingStream(long offset, long length)
    {
        using var inner = new MemoryStream(new byte[16]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NonOwningWindowStream(inner, offset, length));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(16, 0)]
    [InlineData(4, 8)]
    public void Constructor_AcceptsAnInBoundsWindow(long offset, long length)
    {
        using var inner = new MemoryStream(new byte[16]);
        var window = new NonOwningWindowStream(inner, offset, length);
        Assert.Equal(length, window.Length);
    }

    [Fact]
    public async Task Dispose_LeavesTheBackingStreamOpen()
    {
        // The reason the type exists: ParquetFileReader.OpenAsync disposes the stream it is handed, and the
        // reconciliation reads back the CALLER's output stream mid-write-path.
        using var inner = new MemoryStream(new byte[16]);
        var window = new NonOwningWindowStream(inner, 0, 16);
        await window.DisposeAsync();
        window.Dispose();

        Assert.Equal(16, inner.Length);
        inner.Position = 0;
        Assert.Equal(16, await inner.ReadAsync(new byte[16].AsMemory(), CancellationToken.None));
    }

    [Fact]
    public void Read_IsRebasedAndClampedToTheWindow()
    {
        var payload = new byte[16];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        using var inner = new MemoryStream(payload);
        var window = new NonOwningWindowStream(inner, 4, 8);

        var buffer = new byte[16];
        int read = window.Read(buffer, 0, buffer.Length);

        Assert.Equal(8, read);
        Assert.Equal(new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 }, buffer.AsSpan(0, read).ToArray());
    }
}
