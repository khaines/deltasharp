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
    public void Version_IsNotNullOrWhiteSpace()
    {
        Assert.False(string.IsNullOrWhiteSpace(DeltaSharpInfo.Version));
    }
}
