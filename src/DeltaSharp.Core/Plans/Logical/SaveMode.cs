namespace DeltaSharp.Plans.Logical;

/// <summary>
/// How a write behaves when the target already exists. Spark parity (<c>SaveMode</c>).
/// </summary>
internal enum SaveMode
{
    /// <summary>Append the data to existing data.</summary>
    Append,

    /// <summary>Overwrite existing data.</summary>
    Overwrite,

    /// <summary>Fail if the target already exists.</summary>
    ErrorIfExists,

    /// <summary>Silently ignore the operation if the target already exists.</summary>
    Ignore,
}
