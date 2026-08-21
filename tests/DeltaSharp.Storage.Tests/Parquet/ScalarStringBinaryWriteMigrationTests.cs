using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #845 item 5 (design §2.3b N5): behavioral pins for migrating the SCALAR string/binary write path off the
/// per-value <c>Encoding.UTF8.GetString</c> + non-generic string <c>WriteAsync</c> (one string allocation per
/// value, CancellationToken dropped) onto the zero-alloc generic <c>WriteAsync&lt;ReadOnlyMemory&lt;T&gt;&gt;</c>
/// lane. A string and its <see cref="System.ReadOnlyMemory{T}"/> encode identically in Parquet.Net 6.1.0, so
/// the emitted bytes and null handling are unchanged — asserted here by round-tripping a corpus of
/// null/empty/unicode/large values. The source-level pin (no <c>GetString</c>, token plumbed) lives in the
/// guard-wiring test.
/// </summary>
public sealed class ScalarStringBinaryWriteMigrationTests
{
    private static readonly StructType StringSchema = new(new[]
    {
        new StructField("s", DataTypes.StringType, nullable: true),
    });

    private static readonly StructType BinarySchema = new(new[]
    {
        new StructField("b", DataTypes.BinaryType, nullable: true),
    });

    // A corpus that stresses the transcode/copy: null, empty, ASCII, multi-byte UTF-8, and a value long enough
    // to force the pooled scratch to hold multiple present payloads back to back.
    private static readonly string?[] StringCorpus =
    {
        null, string.Empty, "a", "héllo", "日本語テスト", new string('x', 4096), null, "tail",
    };

    [Fact]
    public async Task ScalarString_RoundTripsByteIdentically_ThroughTheGenericLane()
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.StringType, StringCorpus.Length);
        foreach (string? value in StringCorpus)
        {
            if (value is null)
            {
                vector.AppendNull();
            }
            else
            {
                vector.AppendBytes(Encoding.UTF8.GetBytes(value));
            }
        }

        var batch = new ManagedColumnBatch(StringSchema, new ColumnVector[] { vector }, StringCorpus.Length);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(StringSchema, new[] { batch });
        ColumnBatch decoded = Assert.Single(await ParquetTestHelpers.ReadAllAsync(bytes, StringSchema));

        ColumnVector actual = decoded.SelectedColumn(0);
        Assert.Equal(StringCorpus.Length, decoded.LogicalRowCount);
        for (int i = 0; i < StringCorpus.Length; i++)
        {
            if (StringCorpus[i] is null)
            {
                Assert.True(actual.IsNull(i));
            }
            else
            {
                Assert.False(actual.IsNull(i));
                Assert.Equal(StringCorpus[i], Encoding.UTF8.GetString(actual.GetBytes(i)));
            }
        }
    }

    [Fact]
    public async Task ScalarBinary_RoundTripsByteIdentically_ThroughTheGenericLane()
    {
        byte[]?[] corpus =
        {
            null, Array.Empty<byte>(), new byte[] { 0x00 }, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray(), null, new byte[] { 0xFF, 0x00, 0xFF },
        };

        MutableColumnVector vector = ColumnVectors.Create(DataTypes.BinaryType, corpus.Length);
        foreach (byte[]? value in corpus)
        {
            if (value is null)
            {
                vector.AppendNull();
            }
            else
            {
                vector.AppendBytes(value);
            }
        }

        var batch = new ManagedColumnBatch(BinarySchema, new ColumnVector[] { vector }, corpus.Length);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(BinarySchema, new[] { batch });
        ColumnBatch decoded = Assert.Single(await ParquetTestHelpers.ReadAllAsync(bytes, BinarySchema));

        ColumnVector actual = decoded.SelectedColumn(0);
        Assert.Equal(corpus.Length, decoded.LogicalRowCount);
        for (int i = 0; i < corpus.Length; i++)
        {
            if (corpus[i] is null)
            {
                Assert.True(actual.IsNull(i));
            }
            else
            {
                Assert.False(actual.IsNull(i));
                Assert.Equal(corpus[i], actual.GetBytes(i).ToArray());
            }
        }
    }

    [Fact]
    public async Task ScalarString_HonorsAnAlreadyCanceledToken()
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.StringType, 1);
        vector.AppendBytes(Encoding.UTF8.GetBytes("v"));
        var batch = new ManagedColumnBatch(StringSchema, new ColumnVector[] { vector }, 1);

        var writer = new ParquetFileWriter();
        using var stream = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.WriteAsync(stream, StringSchema, new[] { batch }, cts.Token));
    }
}
