namespace DeltaSharp;

/// <summary>
/// A distributed collection of data organized into named columns, equivalent to Apache Spark's
/// <c>DataFrame</c> (an untyped <c>Dataset&lt;Row&gt;</c>).
/// </summary>
/// <remarks>
/// <b>M1 placeholder.</b> STORY-04.1.1 (#157) introduces this type only as the return shape of the
/// <see cref="SparkSession"/> doors (<see cref="SparkSession.Sql(string)"/> and the reader). Its
/// transformation and action surface is delivered by later FEAT-04.1/FEAT-04.2 stories
/// (#158/#159 and following); it is intentionally inert here. Instances are created by the engine,
/// not by user code, so the constructor is non-public.
/// </remarks>
public sealed class DataFrame
{
    internal DataFrame()
    {
    }
}
