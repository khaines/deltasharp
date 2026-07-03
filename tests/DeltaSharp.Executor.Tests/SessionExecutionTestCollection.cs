using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Serializes the Executor tests that create a <see cref="SparkSession"/> through the public API. A
/// session touches process-wide active/default session state (and disposing one stops it), so two
/// session-using test classes must not run in parallel or one could stop the session another holds.
/// </summary>
/// <remarks>
/// The <see cref="ExecutorBootstrapFixture"/> forces the execution backend to register before any test
/// in this collection runs, so the read-door end-to-end tests pass <b>in isolation</b> (e.g. under
/// <c>--filter ~ReadDoorEndToEnd</c>) even when no sibling test has loaded an Executor type to trigger
/// the module initializer (M2).
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionExecutionTestCollection : ICollectionFixture<ExecutorBootstrapFixture>
{
    /// <summary>The xUnit collection name shared by every session-using Executor test class.</summary>
    public const string Name = "Executor session serial";
}

/// <summary>Enables the DeltaSharp execution backend once for the whole collection, so a Core-only
/// action path (<c>CreateDataFrame(...).Collect()</c>) can run without first touching an Executor type
/// to trigger the module initializer (proves the M2 <see cref="DeltaSharpExecutor.Enable"/> bootstrap).</summary>
public sealed class ExecutorBootstrapFixture
{
    /// <summary>Registers the executor backend deterministically (idempotent).</summary>
    public ExecutorBootstrapFixture() => DeltaSharpExecutor.Enable();
}
