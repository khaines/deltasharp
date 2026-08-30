using System.Collections.Immutable;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using Xunit;
using Xunit.Abstractions;
using PqMapField = Parquet.Schema.MapField;
using PqStructField = Parquet.Schema.StructField;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Regression pins for the #676 reader/projection guards the RFL council found MUTATION-PROVEN VACUOUS (a
/// guard could be deleted with the suite still green): the §3.8 map canonical <c>key</c>/<c>value</c>
/// name guard (Parquet.Net binds <c>key_value</c> positionally), the §3.13/§3.16 reader-side duplicate
/// guards (top-level <c>byName</c> intolerance, name/none-mode duplicate leaf-path), the §3.4/§3.5 read-exit
/// typed inverse-relabel congruence guard (<see cref="ColumnMappingProjection.BuildFullBatch"/> must fail
/// closed as a typed <see cref="DeltaStorageException"/>, never a bare <see cref="ArgumentException"/> or a
/// leaked physical name), and the §3.6 name-mode reversed-footer positional-independence witness. Plus a
/// §3.33 seeded property harness that drives the id-mode READER with forged-footer tampers (the surface the
/// schema-only harness in <see cref="NestedColumnMappingTamperFuzzTests"/> never reaches).
/// </summary>
public sealed class NestedColumnMappingGuardCoverageTests
{
    private readonly ITestOutputHelper _output;

    public NestedColumnMappingGuardCoverageTests(ITestOutputHelper output) => _output = output;

    // -------------------------------------------------------------------------------------------------
    // §3.8 — map canonical key/value name guard (mode-independent; Parquet.Net binds key_value positionally)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void MapKeyValue_NonCanonicalChildNames_FailsClosed_NameMode()
    {
        // A map whose key_value children are named 'k'/'v' (not the canonical 'key'/'value') fails closed at
        // ValidateShape before any decode — because Parquet.Net would otherwise bind them positionally.
        var fileSchema = new ParquetSchema(
            new PqMapField("m", new DataField<string>("k", false), new DataField<long>("v")));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => ResolveNameMode(
            fileSchema, new StructField("m", new MapType(DataTypes.StringType, DataTypes.LongType), nullable: true)));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("not named the canonical", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapKeyValue_SwappedChildren_MapLongLong_RequiredValue_FailsClosed_NoneMode()
    {
        // The witness the design calls out: map<long,long> with a REQUIRED value and both children long — the
        // type/level/EnsureRequiredMapKey guards cannot separate them, so ONLY the canonical-name guard catches
        // the transposition. Children declared [value-named, key-named] → the guard sees Key.Name='value'.
        var fileSchema = new ParquetSchema(
            new PqMapField("m", new DataField<long>("value"), new DataField<long>("key")));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => ResolveNameMode(
            fileSchema, new StructField("m", new MapType(DataTypes.LongType, DataTypes.LongType, valueContainsNull: false), nullable: true)));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("not named the canonical", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.13 / §3.16 — reader-side duplicate guards (mode-independent byName; name/none leaf-path)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void DuplicateTopLevelPhysicalColumnName_FailsClosed_AtResolveFileFields()
    {
        var fileSchema = new ParquetSchema(
            new PqStructField("home", new DataField<long>("v")),
            new PqStructField("home", new DataField<long>("w")));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => ResolveNameMode(
            fileSchema, new StructField("home", new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: true) }), nullable: true)));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("more than one top-level column named", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateDecodedLeafPhysicalPath_FailsClosed_NameMode()
    {
        // A struct with two children at the same physical leaf path (home/v twice) fails the name/none-mode
        // leaf-path uniqueness guard (which BuildFieldIdMap enforces only in id mode).
        var fileSchema = new ParquetSchema(
            new PqStructField("home", new DataField<long>("v"), new DataField<long>("v")));

        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => ResolveNameMode(
            fileSchema, new StructField("home", new StructType(new[] { new StructField("v", DataTypes.LongType, nullable: true) }), nullable: true)));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        Assert.Contains("same physical path", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.4 / §3.5 — read-exit typed inverse-relabel congruence (BuildFullBatch), never bare ArgumentException
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void BuildFullBatch_PhysicalStructChildCountDiffers_FailsClosed_TypedSchemaMismatch()
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => RelabelStruct(
            logicalChildren: new[] { ("a", (DataType)DataTypes.LongType, true), ("b", DataTypes.LongType, true) },
            physicalChildren: new[] { ("x", (DataType)DataTypes.LongType, true) }));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    [Fact]
    public void BuildFullBatch_PhysicalStructChildTypeDiffers_FailsClosed_TypedSchemaMismatch_NotArgumentException()
    {
        // Child b is long logically but string physically → typed SchemaMismatch. If AssertStructCongruent were
        // removed, RelabelTo would throw a bare ArgumentException — so asserting the typed kind pins the guard.
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => RelabelStruct(
            logicalChildren: new[] { ("a", (DataType)DataTypes.LongType, true), ("b", DataTypes.LongType, true) },
            physicalChildren: new[] { ("a", (DataType)DataTypes.LongType, true), ("b", DataTypes.StringType, true) }));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
        // No raw physical field name leaks; the message names only the sanitized LOGICAL path.
        Assert.Contains("addr.b", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFullBatch_PhysicalStructChildNullabilityDiffers_FailsClosed_TypedSchemaMismatch()
    {
        DeltaStorageException error = Assert.Throws<DeltaStorageException>(() => RelabelStruct(
            logicalChildren: new[] { ("a", (DataType)DataTypes.LongType, true) },
            physicalChildren: new[] { ("a", (DataType)DataTypes.LongType, false) }));

        Assert.Equal(StorageErrorKind.SchemaMismatch, error.Kind);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.6 — name-mode same-typed siblings, footer reversed, binds by PHYSICAL NAME (witness-disjoint)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NameMode_StructSameTypedSiblings_FooterReversed_BindsByPhysicalName_WitnessDisjoint()
    {
        // Footer declares struct s children in order [b, a] with disjoint domains a→'A-*', b→'B-*'. Name-mode
        // read binds each child by physical NAME (order-independent), so requesting s<a,b> reads a='A-*',
        // b='B-*' despite the reversed footer order — a positional bind would transpose them.
        var schema = new ParquetSchema(
            new PqStructField("s", new DataField<string>("b"), new DataField<string>("a")));
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms))
            {
                using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
                DataField[] leaves = schema.GetDataFields();
                await rowGroup.WriteAsync(leaves[0], new[] { "B-2000" }, null); // b
                await rowGroup.WriteAsync(leaves[1], new[] { "A-1000" }, null); // a
            }

            bytes = ms.ToArray();
        }

        var requested = new StructType(new[]
        {
            new StructField(
                "s",
                new StructType(new[]
                {
                    new StructField("a", DataTypes.StringType, nullable: true),
                    new StructField("b", DataTypes.StringType, nullable: true),
                }),
                nullable: true),
        });

        using var input = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            input, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: false, CancellationToken.None))
        {
            only = b;
        }

        var s = Assert.IsType<StructColumnVector>(only!.Column(0));
        Assert.Equal("A-1000", Encoding.UTF8.GetString(s.Child(0).GetBytes(0))); // a, by name
        Assert.Equal("B-2000", Encoding.UTF8.GetString(s.Child(1).GetBytes(0))); // b, by name
    }

    // -------------------------------------------------------------------------------------------------
    // §3.33 — seeded property harness driving the id-mode READER with forged-footer tampers
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SeededProperty_IdModeContainmentReader_ValidRoundTrips_TamperedFailsClosed()
    {
        const string scope = nameof(SeededProperty_IdModeContainmentReader_ValidRoundTrips_TamperedFailsClosed);
        int baseSeed = TestSeed.Resolve();
        var random = new Random(TestSeed.Combine(baseSeed, scope));
        _output.WriteLine($"[deltasharp-seed] {scope} baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");

        const int iterations = 200; // house precedent (ChangeFeedCdcFuzzTests.cs:103)
        for (int i = 0; i < iterations; i++)
        {
            // Two struct containers home/work, each one string leaf, with RANDOM distinct positive field_ids
            // and DISJOINT witness domains — a positional/cross-container mis-bind cannot pass on equal values.
            int homeId = 1 + random.Next(1_000);
            int workId = homeId + 1 + random.Next(1_000);
            byte[] bytes = await WriteTwoStructsAsync(homeId, workId, $"home-{i}", $"work-{i}");

            // Invariant A: the VALID id-keyed request round-trips (child binds by its own contained field_id).
            try
            {
                ColumnBatch batch = await ReadIdModeAsync(bytes, "home", childId: homeId);
                var home = Assert.IsType<StructColumnVector>(batch.Column(0));
                Assert.Equal($"home-{i}", Encoding.UTF8.GetString(home.Child(0).GetBytes(0)));
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"valid id-mode read rejected at iteration {i} (seed {baseSeed}): {ex}");
            }

            // Invariant B: an enumerated footer/request tamper makes the reader fail closed with a typed
            // DeltaStorageException. Operators: relocate the child id to the sibling container's leaf; request a
            // REQUIRED child whose id is absent from the footer (a required lane cannot null-fill); stamp the
            // container's declared id onto a footer leaf. (A NULLABLE absent id null-fills — 866b evolution
            // tolerance — so the absent-id tamper uses a required child to stay fail-closed.)
            int op = random.Next(3);
            (string container, int childId, int? containerId, bool nullable) = op switch
            {
                0 => ("home", workId, (int?)null, true),                 // relocate to sibling container's leaf
                1 => ("home", workId + 10_000 + i, (int?)null, false),   // REQUIRED id absent from footer
                _ => ("home", homeId, (int?)homeId, true),               // container id stamped on a footer leaf
            };

            try
            {
                await ReadIdModeAsync(bytes, container, childId, containerId, nullable);
                throw new Xunit.Sdk.XunitException(
                    $"tamper op {op} at iteration {i} (seed {baseSeed}) did NOT fail closed");
            }
            catch (DeltaStorageException)
            {
                // fail-closed (the expected typed rejection)
            }
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    private static void ResolveNameMode(ParquetSchema fileSchema, StructField requested) =>
        ParquetFileReader.ResolveFileFields(
            fileSchema, new StructType(new[] { requested }), nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, byFieldId: null);

    private static async Task<byte[]> WriteTwoStructsAsync(int homeId, int workId, string homeVal, string workVal)
    {
        var schema = new ParquetSchema(
            new PqStructField("home", new DataField<string>("v") { FieldId = homeId }),
            new PqStructField("work", new DataField<string>("v") { FieldId = workId }));
        using var stream = new MemoryStream();
        await using (ParquetWriter writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            DataField[] leaves = schema.GetDataFields();
            await rowGroup.WriteAsync(leaves[0], new[] { homeVal }, null);
            await rowGroup.WriteAsync(leaves[1], new[] { workVal }, null);
        }

        return stream.ToArray();
    }

    private static async Task<ColumnBatch> ReadIdModeAsync(byte[] bytes, string container, int childId, int? containerId = null, bool childNullable = true)
    {
        FieldMetadata containerMeta = containerId is int cid
            ? FieldMetadata.FromValues(new[] { new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(cid)) })
            : FieldMetadata.Empty;
        var requested = new StructType(new[]
        {
            new StructField(
                container,
                new StructType(new[]
                {
                    new StructField("v", DataTypes.StringType, nullable: childNullable, FieldMetadata.FromValues(new[]
                    {
                        new KeyValuePair<string, MetadataValue>(ColumnMapping.IdKey, MetadataValue.Long(childId)),
                    })),
                }),
                nullable: true,
                containerMeta),
        });

        using var input = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch b in new ParquetFileReader().ReadAsync(
            input, requested, keepRowGroup: null, nullFillMissingColumns: false,
            allowTypeWideningPromotion: false, resolveByFieldId: true, CancellationToken.None))
        {
            only = b;
        }

        return Assert.IsAssignableFrom<ColumnBatch>(only);
    }

    // Drives ColumnMappingProjection.BuildFullBatch with a single nested-struct column whose PHYSICAL vector
    // type is non-congruent with the LOGICAL schema (the read-exit inverse relabel must reject it typed).
    private static void RelabelStruct(
        (string Name, DataType Type, bool Nullable)[] logicalChildren,
        (string Name, DataType Type, bool Nullable)[] physicalChildren)
    {
        var logical = new StructType(new[]
        {
            new StructField(
                "addr",
                new StructType(logicalChildren.Select(c => new StructField(c.Name, c.Type, c.Nullable)).ToArray()),
                nullable: true),
        });
        var physType = new StructType(physicalChildren.Select(c => new StructField(c.Name, c.Type, c.Nullable)).ToArray());
        var physChildren = physicalChildren.Select(c => OneRow(c.Type)).ToArray();
        var physVec = new StructColumnVector(physType, physChildren, new[] { false });
        var dataBatch = new ManagedColumnBatch(
            new StructType(new[] { new StructField("col-addr", physType, nullable: true) }),
            new ColumnVector[] { physVec },
            1);

        ColumnMappingProjection.BuildFullBatch(
            ImmutableSortedDictionary<string, string?>.Empty, logical, new[] { "col-addr" }, new[] { 0 }, dataBatch);
    }

    private static ColumnVector OneRow(DataType type)
    {
        MutableColumnVector v = ColumnVectors.Create(type, 1);
        if (type is StringType)
        {
            v.AppendBytes(Encoding.UTF8.GetBytes("x"));
        }
        else
        {
            v.AppendValue(1L);
        }

        return v;
    }
}
