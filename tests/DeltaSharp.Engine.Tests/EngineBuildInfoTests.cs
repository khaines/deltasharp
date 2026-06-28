using Xunit;

namespace DeltaSharp.Engine.Tests;

public class EngineBuildInfoTests
{
    [Fact]
    public void FrameworkName_TargetsNet10()
    {
        Assert.Contains("v10.0", EngineBuildInfo.FrameworkName);
    }
}
