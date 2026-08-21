using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;
using LeafExpectation = DeltaSharp.Storage.Tests.Parquet.NestedWriteModelEncoder.LeafExpectation;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// The #844 (design §3.1) <b>model-encoder differential</b>: a third, independent oracle for the nested
/// Parquet WRITE path. <see cref="NestedWriteModelEncoder"/> emits the expected per-leaf
/// <c>(values, def, rep)</c> from first principles; this suite (a) anchors that encoder to the literal §2.3
/// tables the committed <see cref="NestedParquetLevelStreamTests"/> hard-codes, (b) asserts the REAL
/// <c>NestedColumnShredder</c> output equals the encoder across the branch matrix, (c) runs a seeded generative
/// lane over nested DATA (including sliced / non-zero-base vectors) with a real shrink step, and (d) proves the
/// round-trip comparators are total via a mutation kill-set.
/// </summary>
public sealed class NestedWriteModelEncoderTests
{
    private const string Scope = nameof(NestedWriteModelEncoderTests);

    private readonly ITestOutputHelper _output;

    public NestedWriteModelEncoderTests(ITestOutputHelper output) => _output = output;

    // =====================================================================================================
    // ANCHOR: the independent encoder must reproduce the LITERAL §2.3 tables the level-stream suite pins.
    // If any of these disagree, the encoder is wrong (that is the whole value of an independent oracle).
    // =====================================================================================================

    [Fact]
    public void Encoder_ReproducesCommittedArrayTable()
    {
        var rows = new int?[]?[]
        {
            new int?[] { 10, 20 },
            null,
            Array.Empty<int?>(),
            new int?[] { null },
            new int?[] { 30 },
        };

        LeafExpectation leaf = NestedWriteModelEncoder.EncodeArray("a", rows, elementNullable: true);

        Assert.Equal(new[] { 3, 3, 0, 1, 2, 3 }, leaf.Def);
        Assert.Equal(new[] { 0, 1, 0, 0, 0, 0 }, leaf.Rep);
        Assert.Equal(new object[] { 10, 20, 30 }, leaf.Values);
    }

    [Fact]
    public void Encoder_ReproducesCommittedRequiredArrayTable()
    {
        var rows = new int?[]?[] { new int?[] { 1, 2 }, null, Array.Empty<int?>() };

        LeafExpectation leaf = NestedWriteModelEncoder.EncodeArray("a", rows, elementNullable: false);

        Assert.Equal(new[] { 2, 2, 0, 1 }, leaf.Def);
        Assert.Equal(new[] { 0, 1, 0, 0 }, leaf.Rep);
    }

    [Fact]
    public void Encoder_ReproducesCommittedStructTable()
    {
        var rows = new (int? A, string? B)?[] { (1, "x"), null, (null, "y"), (3, null) };

        (LeafExpectation a, LeafExpectation b) =
            NestedWriteModelEncoder.EncodeStruct("s", rows, aNullable: true, bNullable: true);

        Assert.Equal(new[] { 2, 0, 1, 2 }, a.Def);
        Assert.Equal(new[] { 2, 0, 2, 1 }, b.Def);
        Assert.Empty(a.Rep);
        Assert.Empty(b.Rep);
        Assert.Equal(new object[] { 1, 3 }, a.Values);
        Assert.Equal(new object[] { "x", "y" }, b.Values);
    }

    [Fact]
    public void Encoder_ReproducesCommittedRequiredStructTable()
    {
        var rows = new (int? A, string? B)?[] { (1, "x"), null, (3, null) };

        (LeafExpectation req, LeafExpectation opt) =
            NestedWriteModelEncoder.EncodeStruct("s", rows, aNullable: false, bNullable: true);

        Assert.Equal(new[] { 1, 0, 1 }, req.Def);
        Assert.Equal(new[] { 2, 0, 1 }, opt.Def);
    }

    [Fact]
    public void Encoder_ReproducesCommittedMapTable()
    {
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("a", (int?)1), ("b", null) },
            null,
            Array.Empty<(string, int?)>(),
            new[] { ("c", (int?)3) },
        };

        (LeafExpectation key, LeafExpectation value) =
            NestedWriteModelEncoder.EncodeMap("m", rows, valueNullable: true);

        Assert.Equal(new[] { 2, 2, 0, 1, 2 }, key.Def);
        Assert.Equal(new[] { 3, 2, 0, 1, 3 }, value.Def);
        Assert.Equal(new[] { 0, 1, 0, 0, 0 }, key.Rep);
        Assert.Equal(key.Rep, value.Rep);
        Assert.Equal(new object[] { "a", "b", "c" }, key.Values);
        Assert.Equal(new object[] { 1, 3 }, value.Values);
    }

    // =====================================================================================================
    // AC1: the REAL shredder output == the encoder, across the branch matrix, plus full round-trip identity.
    // =====================================================================================================

    [Fact]
    public async Task Shredder_MatchesEncoder_Struct_AllBranches()
    {
        var rows = new (int? A, string? B)?[] { (1, "one"), null, (null, "two"), (3, null), (4, "four") };
        await VerifyStructAsync(rows, aNullable: true, bNullable: true);
    }

    [Fact]
    public async Task Shredder_MatchesEncoder_Struct_RequiredChild()
    {
        // No null in the REQUIRED int field a; the string field b still exercises the null-child branch.
        var rows = new (int? A, string? B)?[] { (1, "one"), null, (2, null), (3, "three") };
        await VerifyStructAsync(rows, aNullable: false, bNullable: true);
    }

    [Fact]
    public async Task Shredder_MatchesEncoder_Array_AllBranches()
    {
        var rows = new int?[]?[]
        {
            new int?[] { 10, 20 },
            null,
            Array.Empty<int?>(),
            new int?[] { null },
            new int?[] { 30, 40, 50 },
        };
        await VerifyArrayAsync(rows, elementNullable: true, prebuilt: null);
    }

    [Fact]
    public async Task Shredder_MatchesEncoder_Array_RequiredElement()
    {
        var rows = new int?[]?[] { new int?[] { 1, 2 }, null, Array.Empty<int?>(), new int?[] { 3 } };
        await VerifyArrayAsync(rows, elementNullable: false, prebuilt: null);
    }

    [Fact]
    public async Task Shredder_MatchesEncoder_Map_AllBranches()
    {
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("a", (int?)1), ("b", null) },
            null,
            Array.Empty<(string, int?)>(),
            new[] { ("c", (int?)3), ("d", 4), ("e", null) },
        };
        await VerifyMapAsync(rows, valueNullable: true, prebuilt: null);
    }

    [Fact]
    public async Task Shredder_MatchesEncoder_Map_RequiredValue()
    {
        var rows = new IReadOnlyList<(string Key, int? Value)>?[]
        {
            new[] { ("a", (int?)1) },
            null,
            Array.Empty<(string, int?)>(),
            new[] { ("b", (int?)2), ("c", 3) },
        };
        await VerifyMapAsync(rows, valueNullable: false, prebuilt: null);
    }

    // =====================================================================================================
    // AC2: seeded generative lane over nested DATA (200 iters), sliced/non-zero-base vectors, + shrink.
    // =====================================================================================================

    [Fact]
    public void ModelEncoderDifferential_HoldsOverSeededNestedData()
    {
        int baseSeed = TestSeed.Resolve();
        var random = new Random(TestSeed.Combine(baseSeed, Scope));
        _output.WriteLine($"[deltasharp-seed] {Scope} baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");

        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            switch (random.Next(3))
            {
                case 0:
                    RunArrayDraw(random, i, baseSeed);
                    break;
                case 1:
                    RunStructDraw(random, i, baseSeed);
                    break;
                default:
                    RunMapDraw(random, i, baseSeed);
                    break;
            }
        }
    }

    // A PLACEHOLDER for the permanent minimized regression. When the generative lane above discovers a failing
    // draw, its shrunk model is emitted on the `[deltasharp-seed] … FAILING …` line; pin that model here as a
    // dedicated `[Fact]` so it becomes a permanent, self-contained regression (a differential that no longer
    // depends on the seed). No failure has been observed, so no case is pinned yet.
    //
    // [Fact]
    // public async Task Regression_Issue844_<short-description>()
    // {
    //     var rows = /* the minimized model from the FAILING line */;
    //     await VerifyArrayAsync(rows, elementNullable: <flag>, prebuilt: null);
    // }

    [Fact]
    public void Shrinker_MinimizesToSmallestStillFailingModel()
    {
        // A SYNTHETIC injected failure proves the shrink machinery is real: a model "fails" iff it contains a
        // present list of length >= 2. The unique minimal witness is a single row holding a present two-element
        // list, values collapsed toward zero — the shrinker must reach exactly that.
        static bool Fails(IReadOnlyList<int?[]?> model) =>
            model.Any(row => row is not null && row.Length >= 2);

        var seed = new int?[]?[]
        {
            null,
            new int?[] { 5, 6, 7 },
            Array.Empty<int?>(),
            new int?[] { 1 },
            new int?[] { 9, 9 },
        };
        Assert.True(Fails(seed));

        IReadOnlyList<int?[]?> shrunk = ModelShrinker.Shrink(seed, Fails, ModelShrinker.ShrinkArrayRow);

        Assert.True(Fails(shrunk));
        Assert.Single(shrunk);
        Assert.NotNull(shrunk[0]);
        Assert.Equal(2, shrunk[0]!.Length);
        Assert.All(shrunk[0]!, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Shrinker_ReturnsInput_WhenNothingReproduces()
    {
        // A predicate that never fires leaves the model untouched — the shrinker never fabricates a "failure".
        var seed = new int?[]?[] { new int?[] { 1, 2 }, null };
        IReadOnlyList<int?[]?> shrunk = ModelShrinker.Shrink(seed, _ => false, ModelShrinker.ShrinkArrayRow);
        Assert.Equal(2, shrunk.Count);
    }

    // =====================================================================================================
    // AC3: comparator kill-rate — each mutation of the expected model MUST be caught by the comparator.
    // =====================================================================================================

    [Fact]
    public void Comparators_Pass_OnIdenticalModels()
    {
        // Non-vacuity guard: the comparators DO pass on equal inputs, so every "throws" below is meaningful.
        var lists = new int?[]?[] { new int?[] { 1, null }, null, Array.Empty<int?>() };
        NestedVectors.AssertListsEqual(lists, lists);

        var structs = new (int? A, string? B)?[] { (1, "x"), null, (null, "y") };
        NestedVectors.AssertStructsEqual(structs, structs);

        var maps = new List<(string Key, int? Value)>?[]
        {
            new List<(string, int?)> { ("a", 1), ("b", null) },
            null,
        };
        NestedVectors.AssertMapsEqual(maps, maps);
    }

    [Fact]
    public void Mutation_ValidityFlip_IsCaught()
    {
        // present <-> null element cell.
        var expected = new int?[]?[] { new int?[] { 1 } };
        var actual = new int?[]?[] { new int?[] { null } };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertListsEqual(expected, actual));

        var expectedStruct = new (int? A, string? B)?[] { (1, "x") };
        var actualStruct = new (int? A, string? B)?[] { (null, "x") };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertStructsEqual(expectedStruct, actualStruct));
    }

    [Fact]
    public void Mutation_NullEmptyContainerSwap_IsCaught()
    {
        var expectedList = new int?[]?[] { null };
        var actualList = new int?[]?[] { Array.Empty<int?>() };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertListsEqual(expectedList, actualList));

        var expectedMap = new List<(string Key, int? Value)>?[] { null };
        var actualMap = new List<(string Key, int? Value)>?[] { new() };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertMapsEqual(expectedMap, actualMap));
    }

    [Fact]
    public void Mutation_ListOffsetsShifted_PreservingTotal_IsCaught()
    {
        // Same total element count (3), regrouped across rows.
        var expected = new int?[]?[] { new int?[] { 1 }, new int?[] { 2, 3 } };
        var actual = new int?[]?[] { new int?[] { 1, 2 }, new int?[] { 3 } };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertListsEqual(expected, actual));
    }

    [Fact]
    public void Mutation_MapValueLaneRotated_KeysFixed_IsCaught()
    {
        var expected = new List<(string Key, int? Value)>?[]
        {
            new() { ("a", 1), ("b", 2), ("c", 3) },
        };
        var actual = new List<(string Key, int? Value)>?[]
        {
            new() { ("a", 3), ("b", 1), ("c", 2) },
        };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertMapsEqual(expected, actual));
    }

    [Fact]
    public void Mutation_StructValueShiftedAcrossNullStructRow_IsCaught()
    {
        // Positional shift straddling a null-struct row: index 1 flips from null-struct to present.
        var expected = new (int? A, string? B)?[] { (1, "a"), null, (2, "b") };
        var actual = new (int? A, string? B)?[] { (1, "a"), (2, "b"), null };
        Assert.ThrowsAny<Exception>(() => NestedVectors.AssertStructsEqual(expected, actual));
    }

    [Fact]
    public void Mutation_LeafLevelStreamMismatch_IsCaught()
    {
        // The model-encoder leaf comparator (Assert.Equal over int[]) catches a single flipped def slot.
        var expected = new[] { 2, 0, 1, 2 };
        var actual = new[] { 2, 0, 1, 3 };
        Assert.ThrowsAny<Exception>(() => Assert.Equal(expected, actual));
    }

    // =====================================================================================================
    // Verification helpers (shared by AC1 facts and the AC2 generative lane).
    // =====================================================================================================

    private static async Task VerifyStructAsync(
        IReadOnlyList<(int? A, string? B)?> rows, bool aNullable, bool bNullable,
        StructColumnVector? prebuilt = null)
    {
        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: aNullable),
            new StructField("b", DataTypes.StringType, nullable: bNullable),
        });
        var schema = DataTypes.CreateStructType(new[] { new StructField("s", inner, nullable: true) });
        StructColumnVector vector = prebuilt ?? NestedVectors.IntStringStruct(inner, rows);

        byte[] bytes = await NestedParquetLevelStreamTests.WriteAsync(schema, vector);

        (LeafExpectation a, LeafExpectation b) =
            NestedWriteModelEncoder.EncodeStruct("s", rows, aNullable, bNullable);
        if (rows.Count == 0)
        {
            return; // 0-row write is exercised; there is no row group whose leaf streams could be read.
        }

        await AssertLeafAsync(bytes, a);
        await AssertLeafAsync(bytes, b);

        ColumnBatch decoded = await ReadBackAsync(bytes, schema);
        List<(int? A, string? B)?> back =
            NestedVectors.ReadIntStringStruct((StructColumnVector)decoded.Column(0));
        NestedVectors.AssertStructsEqual(rows, back);

        Assert.Equal(a.Values, FlattenStructA(back));
        Assert.Equal(b.Values, FlattenStructB(back));
    }

    private static async Task VerifyArrayAsync(
        IReadOnlyList<int?[]?> rows, bool elementNullable, ListColumnVector? prebuilt)
    {
        var type = DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: elementNullable);
        var schema = DataTypes.CreateStructType(new[] { new StructField("a", type, nullable: true) });
        ListColumnVector vector = prebuilt ?? NestedVectors.IntList(type, rows);

        byte[] bytes = await NestedParquetLevelStreamTests.WriteAsync(schema, vector);

        LeafExpectation leaf = NestedWriteModelEncoder.EncodeArray("a", rows, elementNullable);
        if (rows.Count == 0)
        {
            return;
        }

        await AssertLeafAsync(bytes, leaf);

        ColumnBatch decoded = await ReadBackAsync(bytes, schema);
        List<int?[]?> back = NestedVectors.ReadIntList((ListColumnVector)decoded.Column(0));
        NestedVectors.AssertListsEqual(rows, back);

        Assert.Equal(leaf.Values, FlattenArray(back));
    }

    private static async Task VerifyMapAsync(
        IReadOnlyList<IReadOnlyList<(string Key, int? Value)>?> rows, bool valueNullable,
        MapColumnVector? prebuilt)
    {
        var type = DataTypes.CreateMapType(
            DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: valueNullable);
        var schema = DataTypes.CreateStructType(new[] { new StructField("m", type, nullable: true) });
        MapColumnVector vector = prebuilt ?? NestedVectors.StringIntMap(type, rows);

        byte[] bytes = await NestedParquetLevelStreamTests.WriteAsync(schema, vector);

        (LeafExpectation key, LeafExpectation value) =
            NestedWriteModelEncoder.EncodeMap("m", rows, valueNullable);
        if (rows.Count == 0)
        {
            return;
        }

        await AssertLeafAsync(bytes, key);
        await AssertLeafAsync(bytes, value);

        ColumnBatch decoded = await ReadBackAsync(bytes, schema);
        List<List<(string Key, int? Value)>?> back =
            NestedVectors.ReadStringIntMap((MapColumnVector)decoded.Column(0));
        NestedVectors.AssertMapsEqual(rows, back);

        Assert.Equal(key.Values, FlattenMapKeys(back));
        Assert.Equal(value.Values, FlattenMapValues(back));
    }

    private static async Task AssertLeafAsync(byte[] bytes, LeafExpectation expectation)
    {
        (_, int[] def, int[] rep) = await NestedParquetLevelStreamTests.ReadLeafAsync(bytes, expectation.Path);
        Assert.Equal(expectation.Def, def);
        if (expectation.Rep.Length == 0)
        {
            Assert.Empty(rep);
        }
        else
        {
            Assert.Equal(expectation.Rep, rep);
        }
    }

    private static async Task<ColumnBatch> ReadBackAsync(byte[] bytes, StructType schema)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        ColumnBatch? only = null;
        await foreach (ColumnBatch decoded in new ParquetFileReader().ReadAsync(
            stream, schema, null, nullFillMissingColumns: false, allowTypeWideningPromotion: false,
            CancellationToken.None))
        {
            only = decoded;
        }

        Assert.NotNull(only);
        return only!;
    }

    private static List<object> FlattenArray(IReadOnlyList<int?[]?> rows)
    {
        var flat = new List<object>();
        foreach (int?[]? row in rows)
        {
            if (row is null)
            {
                continue;
            }

            foreach (int? cell in row)
            {
                if (cell is not null)
                {
                    flat.Add(cell.Value);
                }
            }
        }

        return flat;
    }

    private static List<object> FlattenStructA(IReadOnlyList<(int? A, string? B)?> rows)
    {
        var flat = new List<object>();
        foreach ((int? A, string? B)? row in rows)
        {
            if (row is not null && row.Value.A is not null)
            {
                flat.Add(row.Value.A.Value);
            }
        }

        return flat;
    }

    private static List<object> FlattenStructB(IReadOnlyList<(int? A, string? B)?> rows)
    {
        var flat = new List<object>();
        foreach ((int? A, string? B)? row in rows)
        {
            if (row is not null && row.Value.B is not null)
            {
                flat.Add(row.Value.B);
            }
        }

        return flat;
    }

    private static List<object> FlattenMapKeys(IReadOnlyList<List<(string Key, int? Value)>?> rows)
    {
        var flat = new List<object>();
        foreach (List<(string Key, int? Value)>? row in rows)
        {
            if (row is null)
            {
                continue;
            }

            foreach ((string key, _) in row)
            {
                flat.Add(key);
            }
        }

        return flat;
    }

    private static List<object> FlattenMapValues(IReadOnlyList<List<(string Key, int? Value)>?> rows)
    {
        var flat = new List<object>();
        foreach (List<(string Key, int? Value)>? row in rows)
        {
            if (row is null)
            {
                continue;
            }

            foreach ((_, int? value) in row)
            {
                if (value is not null)
                {
                    flat.Add(value.Value);
                }
            }
        }

        return flat;
    }

    // =====================================================================================================
    // Generative draws: build a possibly-sliced vector, verify against the encoder + round trip, and on any
    // failure minimize the LOGICAL model with the real shrinker and surface it as the repro.
    // =====================================================================================================

    private void RunArrayDraw(Random random, int iteration, int baseSeed)
    {
        bool elementNullable = random.Next(2) == 1;
        var type = DataTypes.CreateArrayType(DataTypes.IntegerType, containsNull: elementNullable);

        int pre = random.Next(3);
        int core = random.Next(0, 6);
        int post = random.Next(3);
        var full = new List<int?[]?>();
        for (int k = 0; k < pre + core + post; k++)
        {
            full.Add(GenArrayRow(random, elementNullable));
        }

        List<int?[]?> logical = full.GetRange(pre, core);
        ListColumnVector vector = NestedVectors.IntList(type, full);
        if (pre > 0 || core != full.Count)
        {
            vector = (ListColumnVector)vector.Slice(pre, core);
        }

        string? failure = Attempt(() => VerifyArrayAsync(logical, elementNullable, vector));
        if (failure is null)
        {
            return;
        }

        IReadOnlyList<int?[]?> minimized = ModelShrinker.Shrink(
            logical,
            model => Attempt(() => VerifyArrayAsync(model, elementNullable, null)) is not null,
            ModelShrinker.ShrinkArrayRow);

        _output.WriteLine(
            $"[deltasharp-seed] {Scope} baseSeed={baseSeed} FAILING array iter={iteration} " +
            $"elementNullable={elementNullable} minimized={DescribeArray(minimized)}");
        Assert.Fail($"array model-encoder differential failed at iter {iteration}: {failure}");
    }

    private void RunStructDraw(Random random, int iteration, int baseSeed)
    {
        bool aNullable = random.Next(2) == 1;
        bool bNullable = random.Next(2) == 1;
        var inner = DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: aNullable),
            new StructField("b", DataTypes.StringType, nullable: bNullable),
        });

        int pre = random.Next(3);
        int core = random.Next(0, 6);
        int post = random.Next(3);
        var full = new List<(int? A, string? B)?>();
        for (int k = 0; k < pre + core + post; k++)
        {
            full.Add(GenStructRow(random, aNullable, bNullable));
        }

        List<(int? A, string? B)?> logical = full.GetRange(pre, core);
        StructColumnVector vector = NestedVectors.IntStringStruct(inner, full);
        if (pre > 0 || core != full.Count)
        {
            vector = (StructColumnVector)vector.Slice(pre, core);
        }

        string? failure = Attempt(() => VerifyStructAsync(logical, aNullable, bNullable, vector));
        if (failure is null)
        {
            return;
        }

        IReadOnlyList<(int? A, string? B)?> minimized = ModelShrinker.Shrink(
            logical,
            model => Attempt(() => VerifyStructAsync(model, aNullable, bNullable, null)) is not null,
            row => ShrinkStructRow(row, aNullable, bNullable));

        _output.WriteLine(
            $"[deltasharp-seed] {Scope} baseSeed={baseSeed} FAILING struct iter={iteration} " +
            $"aNullable={aNullable} bNullable={bNullable} minimized={DescribeStruct(minimized)}");
        Assert.Fail($"struct model-encoder differential failed at iter {iteration}: {failure}");
    }

    private void RunMapDraw(Random random, int iteration, int baseSeed)
    {
        bool valueNullable = random.Next(2) == 1;
        var type = DataTypes.CreateMapType(
            DataTypes.StringType, DataTypes.IntegerType, valueContainsNull: valueNullable);

        int pre = random.Next(3);
        int core = random.Next(0, 6);
        int post = random.Next(3);
        var full = new List<IReadOnlyList<(string Key, int? Value)>?>();
        for (int k = 0; k < pre + core + post; k++)
        {
            full.Add(GenMapRow(random, valueNullable));
        }

        List<IReadOnlyList<(string Key, int? Value)>?> logical = full.GetRange(pre, core);
        MapColumnVector vector = NestedVectors.StringIntMap(type, full);
        if (pre > 0 || core != full.Count)
        {
            vector = (MapColumnVector)vector.Slice(pre, core);
        }

        string? failure = Attempt(() => VerifyMapAsync(logical, valueNullable, vector));
        if (failure is null)
        {
            return;
        }

        IReadOnlyList<IReadOnlyList<(string Key, int? Value)>?> minimized = ModelShrinker.Shrink(
            logical,
            model => Attempt(() => VerifyMapAsync(model, valueNullable, null)) is not null,
            row => ShrinkMapRow(row, valueNullable));

        _output.WriteLine(
            $"[deltasharp-seed] {Scope} baseSeed={baseSeed} FAILING map iter={iteration} " +
            $"valueNullable={valueNullable} minimized={DescribeMap(minimized)}");
        Assert.Fail($"map model-encoder differential failed at iter {iteration}: {failure}");
    }

    private static string? Attempt(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ----- random row generators -----

    private static int?[]? GenArrayRow(Random random, bool elementNullable)
    {
        int roll = random.Next(4);
        if (roll == 0)
        {
            return null;
        }

        if (roll == 1)
        {
            return Array.Empty<int?>();
        }

        int length = random.Next(1, 4);
        var row = new int?[length];
        for (int k = 0; k < length; k++)
        {
            row[k] = elementNullable && random.Next(4) == 0 ? null : random.Next(-50, 50);
        }

        return row;
    }

    private static (int? A, string? B)? GenStructRow(Random random, bool aNullable, bool bNullable)
    {
        if (random.Next(4) == 0)
        {
            return null;
        }

        int? a = aNullable && random.Next(3) == 0 ? null : random.Next(-50, 50);
        string? b = bNullable && random.Next(3) == 0 ? null : RandomString(random, minLength: 0);
        return (a, b);
    }

    private static IReadOnlyList<(string Key, int? Value)>? GenMapRow(Random random, bool valueNullable)
    {
        int roll = random.Next(4);
        if (roll == 0)
        {
            return null;
        }

        if (roll == 1)
        {
            return Array.Empty<(string, int?)>();
        }

        int count = random.Next(1, 4);
        var row = new List<(string Key, int? Value)>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        while (row.Count < count)
        {
            string key = RandomString(random, minLength: 1);
            if (!used.Add(key))
            {
                continue;
            }

            int? value = valueNullable && random.Next(3) == 0 ? null : random.Next(-50, 50);
            row.Add((key, value));
        }

        return row;
    }

    private static string RandomString(Random random, int minLength)
    {
        int length = random.Next(minLength, 4);
        if (length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length);
        for (int k = 0; k < length; k++)
        {
            sb.Append((char)('a' + random.Next(26)));
        }

        return sb.ToString();
    }

    // ----- per-shape row shrinkers (nullability-aware, so a shrink never fabricates an illegal model) -----

    private static IEnumerable<(int? A, string? B)?> ShrinkStructRow(
        (int? A, string? B)? row, bool aNullable, bool bNullable)
    {
        if (row is null)
        {
            yield break;
        }

        yield return null;

        (int? a, string? b) = row.Value;
        if (aNullable && a is not null)
        {
            yield return (null, b);
        }

        if (bNullable && b is not null)
        {
            yield return (a, null);
        }

        if (a is int av && av != 0)
        {
            yield return (0, b);
        }

        if (b is { Length: > 0 })
        {
            yield return (a, string.Empty);
        }
    }

    private static IEnumerable<IReadOnlyList<(string Key, int? Value)>?> ShrinkMapRow(
        IReadOnlyList<(string Key, int? Value)>? row, bool valueNullable)
    {
        if (row is null)
        {
            yield break;
        }

        yield return null;
        if (row.Count > 0)
        {
            yield return Array.Empty<(string, int?)>();
        }

        for (int drop = 0; drop < row.Count; drop++)
        {
            var reduced = new List<(string Key, int? Value)>(row.Count - 1);
            for (int k = 0; k < row.Count; k++)
            {
                if (k != drop)
                {
                    reduced.Add(row[k]);
                }
            }

            yield return reduced;
        }

        if (valueNullable)
        {
            for (int k = 0; k < row.Count; k++)
            {
                if (row[k].Value is not null)
                {
                    var nulled = new List<(string Key, int? Value)>(row);
                    nulled[k] = (row[k].Key, null);
                    yield return nulled;
                }
            }
        }
    }

    // ----- minimized-model descriptions for the repro line -----

    private static string DescribeArray(IReadOnlyList<int?[]?> rows) =>
        "[" + string.Join(", ", rows.Select(r =>
            r is null ? "null" : "[" + string.Join(",", r.Select(v => v?.ToString() ?? "null")) + "]")) + "]";

    private static string DescribeStruct(IReadOnlyList<(int? A, string? B)?> rows) =>
        "[" + string.Join(", ", rows.Select(r =>
            r is null ? "null" : $"({r.Value.A?.ToString() ?? "null"},{r.Value.B ?? "null"})")) + "]";

    private static string DescribeMap(IReadOnlyList<IReadOnlyList<(string Key, int? Value)>?> rows) =>
        "[" + string.Join(", ", rows.Select(r =>
            r is null
                ? "null"
                : "{" + string.Join(",", r.Select(e => $"{e.Key}:{e.Value?.ToString() ?? "null"}")) + "}")) + "]";
}
