using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

public sealed class DeltaLogFilesTests
{
    private static string V(long version) => version.ToString().PadLeft(20, '0');

    [Fact]
    public void Classifies_JsonCommit()
    {
        DeltaLogFile file = DeltaLogFiles.Classify(V(7) + ".json");
        Assert.Equal(DeltaLogFileKind.Commit, file.Kind);
        Assert.Equal(7, file.Version);
    }

    [Fact]
    public void Classifies_SinglePartClassicCheckpoint()
    {
        DeltaLogFile file = DeltaLogFiles.Classify(V(12) + ".checkpoint.parquet");
        Assert.Equal(DeltaLogFileKind.ClassicCheckpoint, file.Kind);
        Assert.Equal(12, file.Version);
        Assert.Equal(1, file.Part);
        Assert.Equal(1, file.Parts);
    }

    [Fact]
    public void Classifies_MultiPartClassicCheckpoint()
    {
        DeltaLogFile file = DeltaLogFiles.Classify(V(30) + ".checkpoint.0000000002.0000000004.parquet");
        Assert.Equal(DeltaLogFileKind.ClassicCheckpoint, file.Kind);
        Assert.Equal(30, file.Version);
        Assert.Equal(2, file.Part);
        Assert.Equal(4, file.Parts);
    }

    [Theory]
    [InlineData(".checkpoint.3a0d1f6e-0000-0000-0000-000000000000.parquet")]
    [InlineData(".checkpoint.3a0d1f6e-0000-0000-0000-000000000000.json")]
    public void Classifies_V2UuidCheckpoint(string suffix)
    {
        DeltaLogFile file = DeltaLogFiles.Classify(V(9) + suffix);
        Assert.Equal(DeltaLogFileKind.V2Checkpoint, file.Kind);
        Assert.Equal(9, file.Version);
    }

    [Theory]
    [InlineData("_last_checkpoint")]
    [InlineData("00000000000000000001.crc")]
    [InlineData("0000000000000000000A.json")] // non-digit version
    [InlineData("1.json")] // wrong width
    [InlineData("00000000000000000001.checkpoint.0000000005.0000000002.parquet")] // part > parts
    [InlineData("00000000000000000001.checkpoint.1.2.3.parquet")] // too many tokens
    [InlineData("00000000000000000001.checkpoint.parquet.tmp")] // trailing junk
    public void Classifies_UnknownAsOther(string fileName)
    {
        Assert.Equal(DeltaLogFileKind.Other, DeltaLogFiles.Classify(fileName).Kind);
    }
}
