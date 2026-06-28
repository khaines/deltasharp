using System.Reflection;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

/// <summary>
/// Verifies the cross-cutting acceptance criteria of STORY-02.1.1: the contracts are independent
/// of <c>Apache.Arrow</c> (AC4) and the hot-path read does not box per row (AC1, checklist 08).
/// </summary>
public class ColumnContractTests
{
    [Fact]
    public void ContractSurface_NamesNoApacheArrowType_AndAssemblyDoesNotReferenceArrow()
    {
        Assembly engine = typeof(ColumnVector).Assembly;

        Assert.DoesNotContain(
            engine.GetReferencedAssemblies(),
            a => a.Name is not null && a.Name.StartsWith("Apache.Arrow", StringComparison.Ordinal));

        Type[] contracts =
        {
            typeof(ColumnVector), typeof(MutableColumnVector), typeof(ColumnBatch), typeof(SelectionVector),
            typeof(SelectedColumnVector), typeof(ColumnVectors),
        };
        foreach (Type contract in contracts)
        {
            foreach (MemberInfo member in contract.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (Type referenced in MemberTypes(member))
                {
                    Assert.DoesNotContain("Apache.Arrow", referenced.FullName ?? referenced.Name, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void ManagedReferenceImplementation_SatisfiesTheContracts()
    {
        // AC4: a non-Arrow implementation fulfils the abstract contracts.
        MutableColumnVector vector = ColumnVectors.Create(IntegerType.Instance, 1);
        Assert.IsAssignableFrom<ColumnVector>(vector);

        var schema = new StructType(new[] { new StructField("id", IntegerType.Instance) });
        vector.AppendValue(1);
        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { vector }, 1);
        Assert.IsAssignableFrom<ColumnBatch>(batch);
    }

    [Fact]
    public void HotPathSpanRead_DoesNotAllocatePerRow()
    {
        MutableColumnVector vector = ColumnVectors.Create(IntegerType.Instance, 1024);
        for (int i = 0; i < 1024; i++)
        {
            vector.AppendValue(i);
        }

        long Sum()
        {
            long total = 0;
            ReadOnlySpan<int> span = vector.GetValues<int>();
            for (int i = 0; i < span.Length; i++)
            {
                total += span[i];
            }

            return total;
        }

        Sum(); // warm up the JIT for this exact path

        long before = GC.GetAllocatedBytesForCurrentThread();
        long result = Sum();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(523776L, result); // sum of 0..1023
        Assert.True(after - before <= 64, $"hot-path read allocated {after - before} bytes (expected ~0)");
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method:
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }

                break;
            case PropertyInfo property:
                yield return property.PropertyType;
                foreach (ParameterInfo indexParameter in property.GetIndexParameters())
                {
                    yield return indexParameter.ParameterType;
                }

                break;
        }
    }
}
