using Xunit;

namespace DeltaSharp.Core.Tests;

public class DeltaSharpInfoTests
{
    [Fact]
    public void Product_ReturnsDeltaSharp()
    {
        Assert.Equal("DeltaSharp", DeltaSharpInfo.Product);
    }

    [Fact]
    public void Version_StartsWithExpectedPrefix()
    {
        Assert.StartsWith("0.1", DeltaSharpInfo.Version);
    }
}
