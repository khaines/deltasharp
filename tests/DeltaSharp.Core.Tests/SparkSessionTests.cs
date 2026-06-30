using System.Collections.Generic;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Covers STORY-04.1.1 acceptance criteria AC1 (builder/config exposed without execution), AC2
/// (GetOrCreate returns the same active session), and AC4 (execution-backend selection is recorded
/// without initializing work). See <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class SparkSessionTests
{
    public SparkSessionTests()
    {
        // Reset the process-wide active/default tracking so each test starts from a clean slate.
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    // ----- AC1: builder + config exposed via a usable session, without executing -----

    [Fact]
    public void Builder_AppNameAndConfig_AreExposedThroughConf()
    {
        using SparkSession spark = SparkSession.Builder()
            .AppName("etl-job")
            .Config("spark.deltasharp.demo", "on")
            .GetOrCreate();

        Assert.Equal("etl-job", spark.Conf.Get("spark.app.name"));
        Assert.Equal("on", spark.Conf.Get("spark.deltasharp.demo"));
    }

    [Fact]
    public void GetOrCreate_ReturnsUsableActiveSession_WithoutExecuting()
    {
        // A usable session is returned and is active; no engine type is referenced from Core, so no
        // query work can occur at creation. The observable signal is an active, configured session.
        using SparkSession spark = SparkSession.Builder().AppName("usable").GetOrCreate();

        Assert.True(spark.IsActive);
        Assert.Same(spark, SparkSession.GetActiveSession());
        Assert.Equal("usable", spark.Conf.Get("spark.app.name"));
    }

    [Fact]
    public void Builder_ConfigOverloads_StoreInvariantStrings()
    {
        using SparkSession spark = SparkSession.Builder()
            .Config("k.bool", true)
            .Config("k.long", 42L)
            .Config("k.double", 1.5d)
            .Config("k.string", "text")
            .GetOrCreate();

        Assert.Equal("true", spark.Conf.Get("k.bool"));
        Assert.Equal("42", spark.Conf.Get("k.long"));
        Assert.Equal("1.5", spark.Conf.Get("k.double"));
        Assert.Equal("text", spark.Conf.Get("k.string"));
    }

    // ----- AC2: existing active session is reused; config applied to it -----

    [Fact]
    public void GetOrCreate_WithActiveSession_ReturnsSameInstance()
    {
        using SparkSession first = SparkSession.Builder().AppName("shared").GetOrCreate();
        SparkSession second = SparkSession.Builder().AppName("shared").GetOrCreate();

        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreate_AppliesNewConfig_ToExistingSession()
    {
        using SparkSession first = SparkSession.Builder().AppName("shared").GetOrCreate();

        SparkSession second = SparkSession.Builder()
            .Config("spark.deltasharp.added", "later")
            .GetOrCreate();

        Assert.Same(first, second);
        Assert.Equal("later", first.Conf.Get("spark.deltasharp.added"));
    }

    [Fact]
    public void GetActiveSession_AfterGetOrCreate_ReturnsCreatedSession()
    {
        Assert.Null(SparkSession.GetActiveSession());

        using SparkSession spark = SparkSession.Builder().AppName("active").GetOrCreate();

        Assert.Same(spark, SparkSession.GetActiveSession());
        Assert.Same(spark, SparkSession.GetDefaultSession());
    }

    // ----- AC4: execution backend recorded without initializing work -----

    [Fact]
    public void ExecutionBackend_DefaultsToAuto()
    {
        using SparkSession spark = SparkSession.Builder().AppName("backend").GetOrCreate();

        Assert.Equal(ExecutionBackend.Auto, spark.ExecutionBackend);
    }

    [Theory]
    [InlineData("interpreted", ExecutionBackend.Interpreted)]
    [InlineData("compiled", ExecutionBackend.Compiled)]
    [InlineData("auto", ExecutionBackend.Auto)]
    public void Config_ExecutionBackend_Interpreted_IsRecorded(string value, ExecutionBackend expected)
    {
        using SparkSession spark = SparkSession.Builder()
            .Config("spark.deltasharp.execution.backend", value)
            .GetOrCreate();

        Assert.Equal(expected, spark.ExecutionBackend);
    }

    [Theory]
    [InlineData("Interpreted")]
    [InlineData("  INTERPRETED  ")]
    public void Config_ExecutionBackend_IsCaseInsensitive(string value)
    {
        using SparkSession spark = SparkSession.Builder()
            .Config("spark.deltasharp.execution.backend", value)
            .GetOrCreate();

        Assert.Equal(ExecutionBackend.Interpreted, spark.ExecutionBackend);
    }

    [Fact]
    public void Config_ExecutionBackend_InvalidValue_ThrowsAtGetOrCreate()
    {
        SparkSessionBuilder builder = SparkSession.Builder()
            .Config("spark.deltasharp.execution.backend", "turbo");

        ArgumentException ex = Assert.Throws<ArgumentException>(() => builder.GetOrCreate());
        Assert.Contains("turbo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOrCreate_RecordsBackend_WithoutTouchingEngine()
    {
        // Recording the backend parses the config string into the enum and stores it. There is no
        // engine reference from Core (net8.0 cannot reference the net10.0 engine), so creation cannot
        // initialize backend work. The provable signal: the recorded value is exposed and the raw
        // config string is preserved verbatim for the #174 bridge.
        using SparkSession spark = SparkSession.Builder()
            .Config("spark.deltasharp.execution.backend", "compiled")
            .GetOrCreate();

        Assert.Equal(ExecutionBackend.Compiled, spark.ExecutionBackend);
        Assert.Equal("compiled", spark.Conf.Get("spark.deltasharp.execution.backend"));
        Assert.True(spark.IsActive);
    }
}
