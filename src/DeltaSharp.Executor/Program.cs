using DeltaSharp.Engine;

namespace DeltaSharp.Executor;

public static class Program
{
    public static int Main()
    {
        Console.WriteLine(BuildInfoLine());
        return 0;
    }

    public static string BuildInfoLine() => $"DeltaSharp engine framework: {EngineBuildInfo.FrameworkName}";
}
