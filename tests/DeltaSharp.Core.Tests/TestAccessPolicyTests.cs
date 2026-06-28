using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace DeltaSharp.Core.Tests;

/// <summary>
/// Regression guard proving the central test-access policy in <c>Directory.Build.props</c>
/// applies to every production assembly under <c>src/</c> — not just the engine — so the
/// packable <c>DeltaSharp.Core</c> assembly also grants internals-visibility to its
/// <c>.Tests</c> assembly. See <c>docs/engineering/design/testing-conventions.md</c>.
/// </summary>
public class TestAccessPolicyTests
{
    [Fact]
    public void Core_GrantsInternalsVisibility_ToItsTestAssembly()
    {
        Assembly core = typeof(DeltaSharpInfo).Assembly;

        IEnumerable<InternalsVisibleToAttribute> friends =
            core.GetCustomAttributes<InternalsVisibleToAttribute>();

        Assert.Contains(friends, a => a.AssemblyName == "DeltaSharp.Core.Tests");
    }
}
