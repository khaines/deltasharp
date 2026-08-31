using System.Text;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Writing;
using Xunit;

namespace DeltaSharp.Storage.Tests.Writing;

/// <summary>
/// Unit oracle for the #806 Inc-B two-layer partition-path encoders in <see cref="DeltaWriteEncoding"/>:
/// <see cref="DeltaWriteEncoding.EscapePathName"/> (layer 1 — Apache Spark <c>escapePathName</c> parity),
/// <see cref="DeltaWriteEncoding.ToAddPath"/> (layer 2 — the URI-encoded <c>add.path</c>, the exact inverse of
/// the Inc-A resolver decode), and the encoded-length budget enforced by
/// <see cref="DeltaWriteEncoding.HivePartitionSegment"/> (§2.6).
/// </summary>
public sealed class DeltaWriteEncodingTests
{
    // ---- EscapePathName (layer 1 — Spark charToEscape) -------------------------------------------

    [Theory]
    // Structural / injection chars are escaped as uppercase %XX (a hostile value cannot fabricate a segment).
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a=b", "a%3Db")]
    [InlineData("a:b", "a%3Ab")]
    [InlineData("a\\b", "a%5Cb")]
    [InlineData("100%", "100%25")]
    [InlineData("a\"b", "a%22b")]
    [InlineData("a#b", "a%23b")]
    [InlineData("a'b", "a%27b")]
    [InlineData("a*b", "a%2Ab")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a{b}c", "a%7Bb}c")]
    [InlineData("a[b]c", "a%5Bb%5Dc")]
    [InlineData("a^b", "a%5Eb")]
    [InlineData("a\tb", "a%09b")]   // control char (tab)
    [InlineData("a\nb", "a%0Ab")]   // control char (newline)
    // Passthrough: space, '.', '@', '(', ')', '!', '~', '-', '_', and all non-ASCII — Spark leaves these raw.
    [InlineData("my col", "my col")]
    [InlineData("a.b", "a.b")]
    [InlineData("a@b.com", "a@b.com")]
    [InlineData("région", "région")]
    [InlineData("名前", "名前")]
    [InlineData("__HIVE_DEFAULT_PARTITION__", "__HIVE_DEFAULT_PARTITION__")]
    [InlineData("plain", "plain")]
    public void EscapePathName_MatchesSparkAlphabet(string input, string expected)
    {
        Assert.Equal(expected, DeltaWriteEncoding.EscapePathName(input));
    }

    [Fact]
    public void EscapePathName_UsesUppercaseHex()
    {
        Assert.Equal("a%2Fb", DeltaWriteEncoding.EscapePathName("a/b")); // %2F not %2f
    }

    // ---- ToAddPath (layer 2 — URI-encoded add.path) ---------------------------------------------

    [Theory]
    [InlineData("region=US/part-x.parquet", "region%3DUS/part-x.parquet")]
    // A layer-1 %2F is re-encoded to %252F; the '=' separator to %3D; the '/' separators are preserved.
    [InlineData("region=a%2Fb/part-x.parquet", "region%3Da%252Fb/part-x.parquet")]
    // Space (layer-1 passthrough) becomes %20; non-ASCII becomes its UTF-8 %-triplets.
    [InlineData("my col=east/part-x.parquet", "my%20col%3Deast/part-x.parquet")]
    [InlineData("region=café/part-x.parquet", "region%3Dcaf%C3%A9/part-x.parquet")]
    // A non-partitioned file (no '=', unreserved chars) is unchanged.
    [InlineData("part-x.parquet", "part-x.parquet")]
    public void ToAddPath_UriEncodesSegments_PreservingSeparators(string physical, string expected)
    {
        Assert.Equal(expected, DeltaWriteEncoding.ToAddPath(physical));
    }

    [Theory]
    [InlineData("region=US/part-x.parquet")]
    [InlineData("region=a%2Fb/part-x.parquet")]
    [InlineData("my col=east/part-x.parquet")]
    [InlineData("region=café/part-x.parquet")]
    [InlineData("a=b/c=d/part-x.parquet")]
    [InlineData("part-x.parquet")]
    public void ToAddPath_IsExactInverseOfResolverDecode(string physical)
    {
        // UnescapeDataString(ToAddPath(p)) == p for every physical path — this is precisely what
        // PartitionPathResolver (Inc-A) relies on to recover the on-disk key from add.path.
        string addPath = DeltaWriteEncoding.ToAddPath(physical);
        Assert.Equal(physical, Uri.UnescapeDataString(addPath));
    }

    // ---- HivePartitionSegment (two-layer composition + length budget) ---------------------------

    [Fact]
    public void HivePartitionSegment_ComposesEscapePathNameNameAndValue()
    {
        Assert.Equal("region=a%2Fb", DeltaWriteEncoding.HivePartitionSegment("region", "a/b"));
        Assert.Equal("my col%27x=east", DeltaWriteEncoding.HivePartitionSegment("my col'x", "east"));
        Assert.Equal("région=café", DeltaWriteEncoding.HivePartitionSegment("région", "café"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HivePartitionSegment_NullOrEmptyValue_UsesHiveDefaultSentinel(string? value)
    {
        // Spark parity: both a null and an empty-string value map to the sentinel directory (they collide on
        // disk, disambiguated by the authoritative add.partitionValues).
        Assert.Equal(
            "region=__HIVE_DEFAULT_PARTITION__",
            DeltaWriteEncoding.HivePartitionSegment("region", value));
    }

    [Fact]
    public void HivePartitionSegment_WithinBudget_Succeeds()
    {
        // A value near but under the component budget composes without throwing.
        string value = new string('a', DeltaWriteEncoding.MaxEncodedPathComponentBytes - 20);
        string segment = DeltaWriteEncoding.HivePartitionSegment("region", value);
        Assert.True(Encoding.UTF8.GetByteCount(segment) <= DeltaWriteEncoding.MaxEncodedPathComponentBytes);
    }

    [Fact]
    public void HivePartitionSegment_EscapeExpandingAscii_OverBudget_FailsClosed()
    {
        // #806 §2.6 (Quality F2): the residual expansion under escapePathName is ESCAPE-HEAVY ASCII, not
        // non-ASCII. A value of many '%' chars (each -> 3 bytes) breaches the encoded component budget and must
        // fail closed BEFORE commit, because of the ENCODED expansion (its raw length is well under the limit).
        string value = new string('%', DeltaWriteEncoding.MaxEncodedPathComponentBytes); // raw N bytes -> ~3N encoded
        Assert.True(Encoding.UTF8.GetByteCount(value) <= DeltaWriteEncoding.MaxEncodedPathComponentBytes,
            "the raw value is within budget — only the ENCODED form breaches it");

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => DeltaWriteEncoding.HivePartitionSegment("region", value));
        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        // Message hygiene: the (potentially PII) value is never echoed — only the bounded byte counts.
        Assert.DoesNotContain("%25", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            DeltaWriteEncoding.MaxEncodedPathComponentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HivePartitionSegment_NonAsciiWithinRawBudget_DoesNotBlowUp()
    {
        // Under the OLD Uri.EscapeDataString scheme a 128-byte all-non-ASCII value tripled (é -> %C3%A9) and
        // could breach NAME_MAX; escapePathName passes non-ASCII through 1:1, so it stays within budget.
        string value = new string('é', 100); // 200 UTF-8 bytes raw, 200 on disk (no expansion)
        string segment = DeltaWriteEncoding.HivePartitionSegment("region", value);
        Assert.Equal("region=" + value, segment);
    }
}
