using DeltaSharp.Engine;
using DeltaSharp.Engine.Execution;

namespace DeltaSharp.Executor;

public static class Program
{
    public static int Main()
    {
        Console.WriteLine(BuildInfoLine());
        Console.WriteLine(ExecutionBackendLine());
        return 0;
    }

    public static string BuildInfoLine() => $"DeltaSharp engine framework: {EngineBuildInfo.FrameworkName}";

    /// <summary>
    /// Exercises <see cref="ExecutionBackends.Select()"/> and the backend seam end-to-end so the
    /// NativeAOT publish gate (aot.yml) proves the optional compiled tier is elided with no
    /// trim/AOT warnings (ADR-0001). Under NativeAOT this resolves to the interpreted backend.
    /// </summary>
    public static string ExecutionBackendLine()
    {
        IExecutionBackend backend = ExecutionBackends.Select();
        Func<long, long> affine = backend.BuildAffineEvaluator(new AffineInt64Kernel(2, 1));
        return $"DeltaSharp execution backend: {backend.Name} " +
            $"(dynamic-code={backend.UsesDynamicCode}); affine(20)={affine(20)}";
    }
}
