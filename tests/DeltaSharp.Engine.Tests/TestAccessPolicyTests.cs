using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace DeltaSharp.Engine.Tests;

/// <summary>
/// Regression guard for the repository test-access policy
/// (<c>docs/engineering/design/testing-conventions.md</c>): every production assembly
/// under <c>src/</c> grants internals-visibility to its matching <c>.Tests</c> assembly
/// through the central <c>InternalsVisibleTo</c> wiring in <c>Directory.Build.props</c>.
/// This is what lets <c>DeltaSharp.Engine.Tests</c> unit-test <c>internal</c> engine
/// implementation details (relied on by the EPIC-02 contracts) without widening them.
/// </summary>
public class TestAccessPolicyTests
{
    [Fact]
    public void Engine_GrantsInternalsVisibility_ToItsTestAssembly()
    {
        Assembly engine = typeof(EngineBuildInfo).Assembly;

        IEnumerable<InternalsVisibleToAttribute> friends =
            engine.GetCustomAttributes<InternalsVisibleToAttribute>();

        Assert.Contains(friends, a => a.AssemblyName == "DeltaSharp.Engine.Tests");
    }
}
