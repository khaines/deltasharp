using DeltaSharp.Engine;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Storage;
using DeltaSharp.Types;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Executor;

public static class Program
{
    public static int Main()
    {
        Console.WriteLine(BuildInfoLine());
        Console.WriteLine(ExecutionBackendLine());
        Console.WriteLine(StorageSchemaJsonWritePathLine());
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

    /// <summary>
    /// Executes a minimal end-to-end Delta write through the public storage facade so NativeAOT publish
    /// gates include Storage's schema-JSON write path in the rooted image.
    /// </summary>
    public static string StorageSchemaJsonWritePathLine()
    {
        string tablePath = Path.Combine(Path.GetTempPath(), "deltasharp-aot-smoke-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tablePath);
        try
        {
            var schema = new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: false) });
            MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 1);
            id.AppendValue(1L);
            ColumnBatch batch = new ManagedColumnBatch(schema, new ColumnVector[] { id }, 1);

            using var target = DeltaWriteTarget.ForLocalPath(tablePath);
            DeltaWriteResult result = target.AppendAsync(
                schema,
                Array.Empty<string>(),
                new[] { batch },
                mergeSchema: false,
                enforcer: null,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            return $"DeltaSharp storage schema-json write path: ok version={result.Version} files={result.FilesWritten} rows={result.RowsWritten}";
        }
        finally
        {
            Directory.Delete(tablePath, recursive: true);
        }
    }
}
