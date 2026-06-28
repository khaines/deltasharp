using DeltaSharp.Engine;
using Xunit;

namespace DeltaSharp.Executor.Tests;

public class ExecutorInfoTests
{
    [Fact]
    public void BuildInfoLine_ContainsEngineFrameworkName()
    {
        Assert.Contains(EngineBuildInfo.FrameworkName, Program.BuildInfoLine());
    }
}
