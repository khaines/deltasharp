using System.Text.Json;
using DeltaSharp.Storage.Delta.DeletionVectors;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta.DeletionVectors;

/// <summary>
/// Tests for <see cref="DeletionVectorDescriptor"/> parse/serialize, derived fields, and the inline Z85 +
/// <see cref="RoaringBitmapArray"/> decode pipeline. The definitive external interop oracle lives in
/// <c>DeletionVectorStoreTests</c>/<c>RoaringBitmapArrayTests</c> (real Spark on-disk <c>.bin</c> goldens);
/// here we pin the inline (<c>'i'</c>) storage path on the format this build actually reads and writes — the
/// <b>portable little-endian</b> RoaringBitmapArray. (The Delta protocol's inline "JSON Example 3" prints the
/// DEPRECATED native/big-endian serialization, which real Spark does not persist and which this build neither
/// reads nor writes; the portable serialization of the same row set {3,4,7,11,18,29} is 44 bytes, not 40.)
/// </summary>
public sealed class DeletionVectorDescriptorTests
{
    // Portable-LE inline oracle for the row set {3,4,7,11,18,29}: the Z85 encoding of this build's 44-byte
    // portable RoaringBitmapArray (independently computed; cross-checked here against DeltaSharp's Z85 encode
    // and decode). sizeInBytes = 44, cardinality = 6.
    private const string PortableInlineRowSet = "^Bg9^0rr910000000000iXQKl0rr91000f55c8Xg0@@D72lkbi5=-{L";

    [Fact]
    public void Inline_PortableRowSet_DecodesToDocumentedRowIndexes()
    {
        // Build an inline descriptor from this build's portable serialization and prove the whole inline
        // pipeline (Z85 encode → descriptor → Z85 decode → RoaringBitmapArray decode) reconstructs the exact
        // documented row indexes. The Z85 string is also pinned to the independently-computed oracle, so a
        // regression in DeltaSharp's Z85 encoder would fail here too.
        byte[] raw = RoaringBitmapArray.Serialize(new long[] { 3, 4, 7, 11, 18, 29 });
        var descriptor = DeletionVectorDescriptor.ForInline(raw, cardinality: 6);

        Assert.Equal(44, descriptor.SizeInBytes);
        Assert.Equal(PortableInlineRowSet, descriptor.PathOrInlineDv);

        long[] positions = RoaringBitmapArray.Deserialize(
            descriptor.DecodeInlineBytes(), numRecords: 100, expectedCardinality: 6);
        Assert.Equal(new long[] { 3, 4, 7, 11, 18, 29 }, positions);
    }

    [Fact]
    public void Parse_InlinePortableJson_RoundTripsThroughDescriptor()
    {
        // A committed 'i' (inline) DV action carrying the portable-LE bytes: parse it exactly as the log
        // reader would, then decode through the production path. Uses the independently-computed Z85 oracle
        // string, so this cross-verifies DeltaSharp's Z85 DECODER against an external encoder.
        const string json = """
            {"deletionVector":{"storageType":"i","pathOrInlineDv":"^Bg9^0rr910000000000iXQKl0rr91000f55c8Xg0@@D72lkbi5=-{L","sizeInBytes":44,"cardinality":6}}
            """;
        using JsonDocument doc = JsonDocument.Parse(json);

        DeletionVectorDescriptor? descriptor =
            DeletionVectorDescriptor.Parse(doc.RootElement, action: "add", version: 0, line: 1);

        Assert.NotNull(descriptor);
        Assert.True(descriptor!.IsInline);
        Assert.Equal(6, descriptor.Cardinality);
        Assert.Equal(44, descriptor.SizeInBytes);
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
