using DeltaSharp.Analysis;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Core.Tests.Analysis;

/// <summary>
/// STORY-04.5.1 — the in-memory <see cref="LocalCatalog"/> seam: table-descriptor registration and
/// case-insensitive, side-effect-free, collision-free lookup by single-part and multipart
/// identifiers.
/// </summary>
public sealed class LocalCatalogTests
{
    private static StructType Schema(params string[] fieldNames) =>
        new(fieldNames.Select(n => new StructField(n, IntegerType.Instance)));

    [Fact]
    public void TryGetRelation_ReturnsRegisteredTableDescriptor()
    {
        var catalog = new LocalCatalog();
        StructType schema = Schema("a", "b");
        catalog.Register("t", schema);

        bool found = catalog.TryGetRelation(new[] { "t" }, out CatalogTable? table);

        Assert.True(found);
        Assert.NotNull(table);
        Assert.Same(schema, table!.Schema);
        Assert.Equal(new[] { "t" }, table.Identifier);
        Assert.Equal(CatalogTableType.Table, table.TableType);
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

        bool found = catalog.TryGetRelation(new[] { "ghost" }, out CatalogTable? table);

        Assert.False(found);
        Assert.Null(table);
    }

    [Fact]
    public void Register_Multipart_IsLookedUpByDottedIdentifier()
    {
        var catalog = new LocalCatalog();
        StructType schema = Schema("x");
        catalog.Register(new[] { "db", "t" }, schema);

        Assert.True(catalog.TryGetRelation(new[] { "db", "t" }, out CatalogTable? table));
        Assert.Same(schema, table!.Schema);
        Assert.False(catalog.TryGetRelation(new[] { "t" }, out _));
    }

    [Fact]
    public void Register_ReplacesExistingSchema()
    {
        var catalog = new LocalCatalog();
        catalog.Register("t", Schema("old"));
        StructType updated = Schema("new");
        catalog.Register("t", updated);

        Assert.True(catalog.TryGetRelation(new[] { "t" }, out CatalogTable? table));
        Assert.Same(updated, table!.Schema);
    }

    [Fact]
    public void Register_MultipartParts_DoNotCollide_AcrossDottedRenderings()
    {
        // F5: ["a.b", "c"] and ["a", "b.c"] share the naive dotted key "a.b.c" but are distinct
        // identifiers; the part-aware key must keep them separate.
        var catalog = new LocalCatalog();
        StructType left = Schema("left");
        StructType right = Schema("right");
        catalog.Register(new[] { "a.b", "c" }, left);
        catalog.Register(new[] { "a", "b.c" }, right);

        Assert.True(catalog.TryGetRelation(new[] { "a.b", "c" }, out CatalogTable? first));
        Assert.True(catalog.TryGetRelation(new[] { "a", "b.c" }, out CatalogTable? second));
        Assert.Same(left, first!.Schema);
        Assert.Same(right, second!.Schema);
    }

    [Fact]
    public void Register_Multipart_RejectsEmptyPart()
    {
        var catalog = new LocalCatalog();

        Assert.Throws<ArgumentException>(() => catalog.Register(new[] { "db", "" }, Schema("x")));
    }
}
