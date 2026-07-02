using DeltaSharp.Analysis;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// STORY-04.5.1 — the in-memory <see cref="LocalCatalog"/> seam: schema registration and
/// case-insensitive, side-effect-free lookup by single-part and multipart identifiers.
/// </summary>
public sealed class LocalCatalogTests
{
    private static StructType Schema(params string[] fieldNames) =>
        new(fieldNames.Select(n => new StructField(n, IntegerType.Instance)));

    [Fact]
    public void TryGetRelation_ReturnsRegisteredSchema()
    {
        var catalog = new LocalCatalog();
        StructType schema = Schema("a", "b");
        catalog.Register("t", schema);

        bool found = catalog.TryGetRelation(new[] { "t" }, out StructType? resolved);

        Assert.True(found);
        Assert.Same(schema, resolved);
    }

    [Fact]
    public void TryGetRelation_IsCaseInsensitive()
    {
        var catalog = new LocalCatalog();
        catalog.Register("Orders", Schema("id"));

        Assert.True(catalog.TryGetRelation(new[] { "ORDERS" }, out _));
        Assert.True(catalog.TryGetRelation(new[] { "orders" }, out _));
    }

    [Fact]
    public void TryGetRelation_MissReturnsFalseAndNull()
    {
        var catalog = new LocalCatalog();

        bool found = catalog.TryGetRelation(new[] { "ghost" }, out StructType? resolved);

        Assert.False(found);
        Assert.Null(resolved);
    }

    [Fact]
    public void Register_Multipart_IsLookedUpByDottedIdentifier()
    {
        var catalog = new LocalCatalog();
        StructType schema = Schema("x");
        catalog.Register(new[] { "db", "t" }, schema);

        Assert.True(catalog.TryGetRelation(new[] { "db", "t" }, out StructType? resolved));
        Assert.Same(schema, resolved);
        Assert.False(catalog.TryGetRelation(new[] { "t" }, out _));
    }

    [Fact]
    public void Register_ReplacesExistingSchema()
    {
        var catalog = new LocalCatalog();
        catalog.Register("t", Schema("old"));
        StructType updated = Schema("new");
        catalog.Register("t", updated);

        Assert.True(catalog.TryGetRelation(new[] { "t" }, out StructType? resolved));
        Assert.Same(updated, resolved);
    }
}
