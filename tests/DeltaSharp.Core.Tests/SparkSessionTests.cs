using System.Collections.Generic;
using System.Reflection;
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

    // ----- B1: ExecutionBackend is derived from conf and never diverges from it -----

    [Fact]
    public void GetOrCreate_ReuseChangingBackend_KeepsExecutionBackendAndConfInAgreement()
    {
        const string backendKey = "spark.deltasharp.execution.backend";

        using SparkSession first = SparkSession.Builder()
            .Config(backendKey, "interpreted")
            .GetOrCreate();
        Assert.Equal(ExecutionBackend.Interpreted, first.ExecutionBackend);
        Assert.Equal("interpreted", first.Conf.Get(backendKey));

        // Reuse the session, changing the backend to compiled. The enum and conf must AGREE on the
        // new value — no stale construction-time cache. (Regression for the #174 bridge, which reads
        // session.ExecutionBackend and must see a value consistent with conf.)
        SparkSession second = SparkSession.Builder()
            .Config(backendKey, "compiled")
            .GetOrCreate();
        Assert.Same(first, second);
        Assert.Equal(ExecutionBackend.Compiled, second.ExecutionBackend);
        Assert.Equal("compiled", second.Conf.Get(backendKey));
        Assert.Equal(second.ExecutionBackend, ParseConfBackend(second));

        // The original handle observes the same updated value — there is a single source of truth.
        Assert.Equal(ExecutionBackend.Compiled, first.ExecutionBackend);

        // The other direction: reuse back to interpreted; agreement holds again.
        SparkSession third = SparkSession.Builder()
            .Config(backendKey, "interpreted")
            .GetOrCreate();
        Assert.Same(first, third);
        Assert.Equal(ExecutionBackend.Interpreted, third.ExecutionBackend);
        Assert.Equal("interpreted", third.Conf.Get(backendKey));
        Assert.Equal(third.ExecutionBackend, ParseConfBackend(third));
    }

    [Fact]
    public void ExecutionBackend_ReflectsConfSet_AfterCreation()
    {
        const string backendKey = "spark.deltasharp.execution.backend";
        using SparkSession spark = SparkSession.Builder().AppName("conf-set").GetOrCreate();
        Assert.Equal(ExecutionBackend.Auto, spark.ExecutionBackend);

        spark.Conf.Set(backendKey, "interpreted");

        // ExecutionBackend is derived from the live conf, so a runtime Conf.Set is reflected too.
        Assert.Equal(ExecutionBackend.Interpreted, spark.ExecutionBackend);
        Assert.Equal("interpreted", spark.Conf.Get(backendKey));
    }

    private static ExecutionBackend ParseConfBackend(SparkSession session)
        => session.Conf.Get("spark.deltasharp.execution.backend", "auto") switch
        {
            "interpreted" => ExecutionBackend.Interpreted,
            "compiled" => ExecutionBackend.Compiled,
            _ => ExecutionBackend.Auto,
        };

    // ----- B4: invalid backend on the REUSE path still fails fast at GetOrCreate -----

    [Fact]
    public void Config_ExecutionBackend_InvalidValue_OnReusePath_ThrowsAtGetOrCreate()
    {
        const string backendKey = "spark.deltasharp.execution.backend";

        // A reusable session already exists; backend parse/validation is hoisted before the reuse
        // decision and runs UNCONDITIONALLY, so an invalid value fails fast even on the reuse path.
        using SparkSession first = SparkSession.Builder().AppName("reuse").GetOrCreate();

        SparkSessionBuilder builder = SparkSession.Builder().Config(backendKey, "turbo");
        ArgumentException ex = Assert.Throws<ArgumentException>(() => builder.GetOrCreate());
        Assert.Contains("turbo", ex.Message, StringComparison.Ordinal);

        // The invalid value was never applied to the reused session's conf.
        Assert.False(first.Conf.Contains(backendKey));
        Assert.Equal(ExecutionBackend.Auto, first.ExecutionBackend);
        Assert.True(first.IsActive);
    }

    // ----- AC1/AC4: instrumented proof that creation/config/backend recording does NO work -----

    [Fact]
    public void Core_ReferencesNoEngineAssembly_SoNoQueryWorkIsPossible()
    {
        // The strongest AC1/AC4 "without executing / without initializing work" proof is structural:
        // the public surface cannot touch the engine because DeltaSharp.Core references no
        // DeltaSharp.Engine assembly. Session construction, config, GetOrCreate, and backend
        // recording therefore cannot perform any query/engine work.
        AssemblyName[] referenced = typeof(SparkSession).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(
            referenced,
            a => a.Name is not null && a.Name.Contains("Engine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetOrCreate_RecordingBackend_HasNoSideEffectBeyondConfigStorage()
    {
        // Backend recording stores ONLY the user-supplied keys — no engine warm-up keys, capability
        // probes, or other observable side effects beyond config storage.
        using SparkSession spark = SparkSession.Builder()
            .AppName("ac4")
            .Config("spark.deltasharp.execution.backend", "interpreted")
            .GetOrCreate();

        IReadOnlyDictionary<string, string> all = spark.Conf.GetAll();
        Assert.Equal(
            new HashSet<string> { "spark.app.name", "spark.deltasharp.execution.backend" },
            new HashSet<string>(all.Keys));
        Assert.Equal(ExecutionBackend.Interpreted, spark.ExecutionBackend);
    }

    [Fact]
    public void CreateDataFrame_OnActiveSession_DoesNotEnumerateItsInput()
    {
        // Instrumented no-work proof for the AC3 DataFrame door: the M1 door reports "not yet
        // available" WITHOUT enumerating (touching) the supplied local data — a spy counter stays 0.
        using SparkSession spark = SparkSession.Builder().AppName("nowork").GetOrCreate();
        CountingEnumerable spy = new();

        Assert.Throws<NotSupportedException>(() => spark.CreateDataFrame(spy));

        Assert.Equal(0, spy.EnumerationCount);
    }

    private sealed class CountingEnumerable : System.Collections.IEnumerable
    {
        public int EnumerationCount { get; private set; }

        public System.Collections.IEnumerator GetEnumerator()
        {
            EnumerationCount++;
            yield break;
        }
    }
}
