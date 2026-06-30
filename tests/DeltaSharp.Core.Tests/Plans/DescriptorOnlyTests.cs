using System.IO;
using System.Reflection;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// AC3: scan and write nodes hold logical source/sink descriptors only — no open readers,
/// writers, streams, tasks, or backend handles, and constructing them performs no I/O.
/// </summary>
public sealed class DescriptorOnlyTests
{
    private static readonly Type[] AllIrTypes =
    {
        typeof(UnresolvedRelation), typeof(Project), typeof(Filter), typeof(Aggregate),
        typeof(Join), typeof(Sort), typeof(Limit), typeof(Distinct), typeof(Union),
        typeof(WriteToSource), typeof(SinkDescriptor),
        typeof(UnresolvedAttribute), typeof(UnresolvedFunction),
    };

    [Fact]
    public void UnresolvedRelationExposesOnlyIdentifierAndOptions()
    {
        var relation = new UnresolvedRelation(new[] { "db", "t" });
        Assert.Equal(new[] { "db", "t" }, relation.Identifier);
        Assert.Empty(relation.Options);
    }

    [Fact]
    public void WriteNodeExposesOnlyLogicalSinkDescriptor()
    {
        var sink = new SinkDescriptor(
            "parquet",
            SaveMode.Append,
            path: "/out",
            partitionColumns: new[] { "year" });
        var write = new WriteToSource(PlanFixtures.Relation("t"), sink);

        Assert.Equal("parquet", write.Sink.Format);
        Assert.Equal(SaveMode.Append, write.Sink.Mode);
        Assert.Equal("/out", write.Sink.Path);
        Assert.Equal(new[] { "year" }, write.Sink.PartitionColumns);
    }

    [Fact]
    public void NoIrPropertyIsTypedAsAStreamWriterOrEngineHandle()
    {
        foreach (Type type in AllIrTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type propertyType = property.PropertyType;
                Assert.False(
                    typeof(Stream).IsAssignableFrom(propertyType)
                    || typeof(TextReader).IsAssignableFrom(propertyType)
                    || typeof(TextWriter).IsAssignableFrom(propertyType)
                    || typeof(IDisposable).IsAssignableFrom(propertyType),
                    $"{type.Name}.{property.Name} is a stream/handle/disposable type "
                    + $"({propertyType.Name}); plan nodes must hold logical descriptors only.");

                Assert.False(
                    propertyType.Namespace?.StartsWith("DeltaSharp.Engine", StringComparison.Ordinal) == true,
                    $"{type.Name}.{property.Name} references the engine type {propertyType.FullName}; "
                    + "the logical plan IR must be Engine-free.");
            }
        }
    }

    [Fact]
    public void CoreAssemblyDoesNotReferenceTheEngineAssembly()
    {
        IEnumerable<string> referenced = typeof(UnresolvedRelation).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain("DeltaSharp.Engine", referenced);
    }

    [Fact]
    public void ConstructingScanAndWriteOverANonExistentPathDoesNoIo()
    {
        string bogus = Path.Combine(
            Path.GetTempPath(), "deltasharp-plan-ir-nonexistent-path-ac3-fixture");

        // No exception: construction reads nothing and opens nothing.
        var scan = new UnresolvedRelation(new[] { bogus });
        var write = new WriteToSource(
            scan, new SinkDescriptor("parquet", SaveMode.Overwrite, path: bogus));

        Assert.False(File.Exists(bogus));
        Assert.NotNull(write.Sink.Path);
    }
}
