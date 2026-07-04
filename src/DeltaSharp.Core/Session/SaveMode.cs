namespace DeltaSharp;

/// <summary>
/// How a write behaves when the target already exists, mirroring Apache Spark's
/// <c>org.apache.spark.sql.SaveMode</c>. It is set on a <see cref="DataFrameWriter"/> via
/// <see cref="DataFrameWriter.Mode(SaveMode)"/> (or the case-insensitive string overload) and is a
/// purely <b>logical</b> intent recorded on the write plan until an action (<see cref="DataFrameWriter.Save()"/>)
/// executes it.
/// </summary>
public enum SaveMode
{
    /// <summary>Append the data to existing data.</summary>
    Append,

    /// <summary>Overwrite existing data.</summary>
    Overwrite,

    /// <summary>Fail if the target already exists (Spark's default).</summary>
    ErrorIfExists,

    /// <summary>Silently ignore the operation if the target already exists.</summary>
    Ignore,
}
