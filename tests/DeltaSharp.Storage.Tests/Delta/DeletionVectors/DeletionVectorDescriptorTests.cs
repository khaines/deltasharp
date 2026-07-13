using System.Text.Json;
using DeltaSharp.Storage.Delta.DeletionVectors;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// Tests for <see cref="DeletionVectorDescriptor"/> parse/serialize, derived fields, and — most importantly
/// — the <b>interop oracle</b>: the Delta protocol's "JSON Example 3 — Inline" DV
/// (<c>pathOrInlineDv = "wi5b=000010000siXQKl0rr91000f55c8Xg0@@D72lkbi5=-{L"</c>, <c>sizeInBytes = 40</c>,
/// <c>cardinality = 6</c>) MUST decode to the row indexes {3, 4, 7, 11, 18, 29} the protocol states. This
/// is a hand-verified, spec-provided vector (not a self-round-trip), so it proves the Z85 +
/// RoaringBitmapArray pipeline reads bytes a Spark/Databricks writer produced.
/// </summary>
public sealed class DeletionVectorDescriptorTests
{
    // The Delta protocol "Deletion Vectors" §"JSON Example 3 — Inline" oracle.
    private const string ProtocolInlineExample3 = "wi5b=000010000siXQKl0rr91000f55c8Xg0@@D72lkbi5=-{L";

    [Fact]
    public void ProtocolInlineExample3_DecodesToDocumentedRowIndexes()
    {
        var descriptor = new DeletionVectorDescriptor(
            DeletionVectorDescriptor.StorageTypeInline,
            ProtocolInlineExample3,
            Offset: null,
            SizeInBytes: 40,
            Cardinality: 6);

        byte[] raw = descriptor.DecodeInlineBytes();
        Assert.Equal(40, raw.Length);

        long[] positions = RoaringBitmapArray.Deserialize(raw, numRecords: 100, expectedCardinality: 6);
        Assert.Equal(new long[] { 3, 4, 7, 11, 18, 29 }, positions);
    }

    [Fact]
    public void Parse_InlineExample3Json_RoundTripsThroughDescriptor()
    {
        const string json = """
            {"deletionVector":{"storageType":"i","pathOrInlineDv":"wi5b=000010000siXQKl0rr91000f55c8Xg0@@D72lkbi5=-{L","sizeInBytes":40,"cardinality":6}}
            """;
        using JsonDocument doc = JsonDocument.Parse(json);

        DeletionVectorDescriptor? descriptor =
            DeletionVectorDescriptor.Parse(doc.RootElement, action: "add", version: 0, line: 1);

        Assert.NotNull(descriptor);
        Assert.True(descriptor!.IsInline);
        Assert.Equal(6, descriptor.Cardinality);
        long[] positions = RoaringBitmapArray.Deserialize(
            descriptor.DecodeInlineBytes(), numRecords: 100, expectedCardinality: 6);
        Assert.Equal(new long[] { 3, 4, 7, 11, 18, 29 }, positions);
    }

    [Fact]
    public void Inline_RoundTrip_FromPositions()
    {
        long[] positions = { 1, 9, 100, 4096, 70000 };
        byte[] raw = RoaringBitmapArray.Serialize(positions);
        DeletionVectorDescriptor descriptor = DeletionVectorDescriptor.ForInline(raw, positions.LongLength);

        Assert.True(descriptor.IsInline);
        Assert.Equal(raw.Length, descriptor.SizeInBytes);
        Assert.Equal(
            positions,
            RoaringBitmapArray.Deserialize(descriptor.DecodeInlineBytes(), 70001, positions.LongLength));
    }

    [Fact]
    public void UniqueId_DistinguishesSameFileDifferentDv()
    {
        var a = DeletionVectorDescriptor.ForInline(RoaringBitmapArray.Serialize(new long[] { 1 }), 1);
        var b = DeletionVectorDescriptor.ForInline(RoaringBitmapArray.Serialize(new long[] { 2 }), 1);

        Assert.NotEqual(a.UniqueId, b.UniqueId);
    }

    [Fact]
    public void ResolveRelativePath_ForUuidStorage_DerivesCanonicalBinName()
    {
        var uuid = new Guid("0123456789abcdef0123456789abcdef");
        string pathOrInlineDv = DeletionVectorDescriptor.BuildRelativePathOrInlineDv(string.Empty, uuid);
        var descriptor = DeletionVectorDescriptor.ForRelativePath(pathOrInlineDv, offset: 1, sizeInBytes: 40, cardinality: 6);

        Assert.Equal("deletion_vector_" + uuid.ToString("D") + ".bin", descriptor.ResolveRelativePath());
    }

    [Fact]
    public void ResolveRelativePath_WithRandomPrefix_KeepsPrefixAsDirectory()
    {
        var uuid = new Guid("0123456789abcdef0123456789abcdef");
        string pathOrInlineDv = DeletionVectorDescriptor.BuildRelativePathOrInlineDv("ab", uuid);
        var descriptor = DeletionVectorDescriptor.ForRelativePath(pathOrInlineDv, offset: 1, sizeInBytes: 40, cardinality: 6);

        Assert.Equal("ab/deletion_vector_" + uuid.ToString("D") + ".bin", descriptor.ResolveRelativePath());
    }

    [Fact]
    public void Parse_AbsolutePathStorage_IsPreservedButFailsClosedOnRelativeResolve()
    {
        var descriptor = new DeletionVectorDescriptor(
            DeletionVectorDescriptor.StorageTypeAbsolutePath, "/tmp/x.bin", Offset: 1, SizeInBytes: 40, Cardinality: 6);

        Assert.Throws<DeltaStorageException>(() => descriptor.ResolveRelativePath());
    }
}
