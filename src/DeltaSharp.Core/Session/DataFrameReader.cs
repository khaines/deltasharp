namespace DeltaSharp;

/// <summary>
/// An entry point for reading data into a <see cref="DataFrame"/>, equivalent to Apache Spark's
/// <c>DataFrameReader</c> obtained from <c>spark.read</c>.
/// </summary>
/// <remarks>
/// <b>M1 placeholder.</b> STORY-04.1.1 (#157) introduces this type only as the return shape of
/// <see cref="SparkSession.Read"/>. Its format/options/load surface (for example <c>Parquet</c>)
/// is delivered by STORY-04.1.2 (#158); it is intentionally inert here. Instances are created by
/// <see cref="SparkSession"/>, so the constructor is non-public.
/// </remarks>
public sealed class DataFrameReader
{
    internal DataFrameReader()
    {
    }
}
