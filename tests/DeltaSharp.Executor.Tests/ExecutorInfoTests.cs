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

    [Fact]
    public void ExecutionBackendLine_ReportsSelectedBackendAndEvaluatesKernel()
    {
        // Exercises ExecutionBackends.Select() through the executor entry point (ADR-0001).
        // affine(20) for kernel (2, 1) is 41 on either backend (parity), proving the seam runs.
        string line = Program.ExecutionBackendLine();
        Assert.Contains("DeltaSharp execution backend:", line);
        Assert.Contains("affine(20)=41", line);
    }
}
