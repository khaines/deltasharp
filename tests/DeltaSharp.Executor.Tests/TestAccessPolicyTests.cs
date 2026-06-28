using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace DeltaSharp.Executor.Tests;

/// <summary>
/// Regression guard proving the central test-access policy in <c>Directory.Build.props</c>
/// also grants internals-visibility from the <c>DeltaSharp.Executor</c> production assembly
/// to its <c>.Tests</c> assembly — so all three <c>src/</c> assemblies (Core, Engine,
/// Executor) are covered and none can silently regress. See
/// <c>docs/engineering/design/testing-conventions.md</c>.
/// </summary>
public class TestAccessPolicyTests
{
    [Fact]
    public void Executor_GrantsInternalsVisibility_ToItsTestAssembly()
    {
        Assembly executor = typeof(Program).Assembly;

        IEnumerable<InternalsVisibleToAttribute> friends =
            executor.GetCustomAttributes<InternalsVisibleToAttribute>();

        Assert.Contains(friends, a => a.AssemblyName == "DeltaSharp.Executor.Tests");
    }
}
