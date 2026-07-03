namespace DeltaSharp.Executor;

/// <summary>
/// The internal signal the <see cref="RowMaterializer"/> raises when a configured result bound
/// (<c>spark.deltasharp.execution.maxResultRows</c>/<c>maxResultBytes</c>) would be exceeded — the
/// deterministic "bounded, not OOM" stop required by STORY-04.6.4 (#176) criterion 3. The
/// <see cref="LocalQueryExecutor"/> catches it, attaches the run's <see cref="ExecutionMetrics"/>, and
/// re-surfaces it as a public <see cref="QueryExecutionException"/> attributed to
/// <see cref="QueryExecutionStage.Materialize"/> with this as the preserved root cause. It is thrown
/// <b>before</b> the offending batch is materialized, so the row list never grows past the bound.
/// </summary>
internal sealed class ResultLimitExceededException : Exception
{
    private ResultLimitExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Builds the signal for a row-count bound.</summary>
    /// <param name="maxRows">The configured maximum result rows.</param>
    /// <param name="wouldBe">The row count materialization would have reached.</param>
    /// <returns>A ready-to-throw signal.</returns>
    public static ResultLimitExceededException Rows(long maxRows, long wouldBe) =>
        new($"The result exceeds the configured maximum of {maxRows} row(s) "
            + $"(materialization reached {wouldBe} row(s)); increase '{SparkSessionConfigKeys.MaxResultRows}' "
            + "or add a narrower filter/limit.");

    /// <summary>Builds the signal for a byte bound.</summary>
    /// <param name="maxBytes">The configured maximum result bytes.</param>
    /// <param name="wouldBe">The estimated byte total materialization would have reached.</param>
    /// <returns>A ready-to-throw signal.</returns>
    public static ResultLimitExceededException Bytes(long maxBytes, long wouldBe) =>
        new($"The result exceeds the configured maximum of {maxBytes} byte(s) "
            + $"(estimated materialization reached {wouldBe} byte(s)); increase "
            + $"'{SparkSessionConfigKeys.MaxResultBytes}' or add a narrower filter/limit.");
}

/// <summary>
/// The <c>spark.deltasharp.execution.*</c> configuration key names, mirrored here for diagnostics
/// messages the Executor emits (the authoritative constants live on Core's <c>SparkSession</c>).
/// </summary>
internal static class SparkSessionConfigKeys
{
    /// <summary>The driver result row cap key.</summary>
    public const string MaxResultRows = "spark.deltasharp.execution.maxResultRows";

    /// <summary>The driver result byte cap key.</summary>
    public const string MaxResultBytes = "spark.deltasharp.execution.maxResultBytes";
}
