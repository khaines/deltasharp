using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Covers STORY-04.1.1 AC3 (a stopped or disposed session yields a deterministic lifecycle error
/// from the data doors) plus the stop/dispose state-machine rules. See
/// <c>docs/engineering/design/sparksession-lifecycle.md</c>.
/// </summary>
[Collection(SparkSessionTestCollection.Name)]
public sealed class SparkSessionLifecycleTests
{
    public SparkSessionLifecycleTests()
    {
        SparkSession.ClearActiveSession();
        SparkSession.ClearDefaultSession();
    }

    // ----- AC3: stopped/disposed session -> deterministic lifecycle error -----

    [Fact]
    public void Read_OnStoppedSession_ThrowsSessionStopped()
    {
        SparkSession spark = SparkSession.Builder().AppName("stopped").GetOrCreate();
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => _ = spark.Read);
    }

    [Fact]
    public void Sql_OnStoppedSession_ThrowsSessionStopped()
    {
        SparkSession spark = SparkSession.Builder().AppName("stopped").GetOrCreate();
        spark.Stop();

        Assert.Throws<SessionStoppedException>(() => spark.Sql("SELECT 1"));
    }

    [Fact]
    public void Read_OnDisposedSession_ThrowsSessionStopped()
    {
        SparkSession spark = SparkSession.Builder().AppName("disposed").GetOrCreate();
        spark.Dispose();

        Assert.Throws<SessionStoppedException>(() => _ = spark.Read);
    }

    [Fact]
    public void Sql_OnDisposedSession_ThrowsSessionStopped()
    {
        SparkSession spark = SparkSession.Builder().AppName("disposed").GetOrCreate();
        spark.Dispose();

        Assert.Throws<SessionStoppedException>(() => spark.Sql("SELECT 1"));
    }

    [Fact]
    public void SessionStopped_Message_IsDeterministicAndNamesApp()
    {
        SparkSession spark = SparkSession.Builder().AppName("payments").GetOrCreate();
        spark.Stop();

        SessionStoppedException ex = Assert.Throws<SessionStoppedException>(() => spark.Sql("SELECT 1"));

        Assert.Equal(
            "Cannot call 'Sql' on SparkSession 'app=payments': the session was stopped or disposed. " +
            "Create a new session with SparkSession.Builder().GetOrCreate().",
            ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void Read_OnActiveSession_ThrowsNotSupported_NotLifecycle()
    {
        // On an ACTIVE session the M1 doors report "not yet available" (NotSupportedException),
        // which must be distinct from the lifecycle error so callers can tell the states apart.
        using SparkSession spark = SparkSession.Builder().AppName("active").GetOrCreate();

        Assert.Throws<NotSupportedException>(() => _ = spark.Read);
        Assert.Throws<NotSupportedException>(() => spark.Sql("SELECT 1"));
    }

    // ----- stop/dispose state machine -----

    [Fact]
    public void Stop_IsIdempotent()
    {
        SparkSession spark = SparkSession.Builder().AppName("idem").GetOrCreate();

        spark.Stop();
        spark.Stop();
        spark.Dispose();

        Assert.False(spark.IsActive);
    }

    [Fact]
    public void Dispose_StopsSession()
    {
        SparkSession spark = SparkSession.Builder().AppName("dispose").GetOrCreate();

        spark.Dispose();

        Assert.False(spark.IsActive);
    }

    [Fact]
    public void Stop_ClearsActiveAndDefault()
    {
        SparkSession spark = SparkSession.Builder().AppName("clearing").GetOrCreate();
        Assert.Same(spark, SparkSession.GetActiveSession());
        Assert.Same(spark, SparkSession.GetDefaultSession());

        spark.Stop();

        Assert.Null(SparkSession.GetActiveSession());
        Assert.Null(SparkSession.GetDefaultSession());
    }

    [Fact]
    public void ActiveSession_SetClear_RoundTrips()
    {
        using SparkSession spark = SparkSession.Builder().AppName("roundtrip").GetOrCreate();

        SparkSession.ClearActiveSession();
        Assert.Null(SparkSession.GetActiveSession());

        SparkSession.SetActiveSession(spark);
        Assert.Same(spark, SparkSession.GetActiveSession());

        Assert.Throws<ArgumentNullException>(() => SparkSession.SetActiveSession(null!));
    }
}
