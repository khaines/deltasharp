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

    [Fact]
    public void StorageSchemaJsonWritePathLine_RootsStorageWritePath()
    {
        string line = Program.StorageSchemaJsonWritePathLine();
        Assert.Contains("DeltaSharp storage schema-json write path: ok", line);
        Assert.Contains("version=0", line);
        Assert.Contains("files=1", line);
        Assert.Contains("rows=1", line);
        Assert.Contains("schemaString-pinned=True", line);
    }
}
