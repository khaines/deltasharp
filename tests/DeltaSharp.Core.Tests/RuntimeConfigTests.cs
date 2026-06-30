using System.Collections.Generic;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Covers <see cref="RuntimeConfig"/> read/write semantics and its lifecycle interaction: reads
/// remain valid after the session is stopped, while writes throw the lifecycle error. See
/// <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class RuntimeConfigTests
{
    public RuntimeConfigTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    [Fact]
    public void Get_MissingKey_Throws()
    {
        using SparkSession spark = SparkSession.Builder().AppName("conf").GetOrCreate();

        Assert.Throws<KeyNotFoundException>(() => spark.Conf.Get("no.such.key"));
    }

    [Fact]
    public void Get_MissingKey_WithDefault_ReturnsDefault()
    {
        using SparkSession spark = SparkSession.Builder().AppName("conf").GetOrCreate();

        Assert.Equal("fallback", spark.Conf.Get("no.such.key", "fallback"));
        Assert.False(spark.Conf.Contains("no.such.key"));
    }

    [Fact]
    public void GetAll_ReturnsImmutableSnapshot()
    {
        using SparkSession spark = SparkSession.Builder().AppName("snap").GetOrCreate();

        IReadOnlyDictionary<string, string> snapshot = spark.Conf.GetAll();
        spark.Conf.Set("added.after.snapshot", "1");

        Assert.False(snapshot.ContainsKey("added.after.snapshot"));
        Assert.Equal("snap", snapshot["spark.app.name"]);
    }

    [Fact]
    public void Set_TypedOverloads_StoreInvariantStrings()
    {
        using SparkSession spark = SparkSession.Builder().AppName("set").GetOrCreate();

        spark.Conf.Set("b", true);
        spark.Conf.Set("n", 7L);

        Assert.Equal("true", spark.Conf.Get("b"));
        Assert.Equal("7", spark.Conf.Get("n"));
    }

    [Fact]
    public void Conf_ReadsRemainValid_AfterStop()
    {
        SparkSession spark = SparkSession.Builder().AppName("post-mortem").GetOrCreate();
        spark.Stop();

        Assert.Equal("post-mortem", spark.Conf.Get("spark.app.name"));
        Assert.True(spark.Conf.Contains("spark.app.name"));
    }

    [Fact]
    public void Conf_Set_AfterStop_ThrowsSessionStopped()
    {
        SparkSession spark = SparkSession.Builder().AppName("post-mortem").GetOrCreate();
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => spark.Conf.Set("k", "v"));
    }
}
