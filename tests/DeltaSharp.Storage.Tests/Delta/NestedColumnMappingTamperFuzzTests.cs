using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The §3 oracle for nested column mapping (#676) — the FAIL-CLOSED / tamper half. Every cell asserts the
/// EXACT exception type the production door throws (design §3 preamble). These cells drive the schema
/// assignment/validation/transform doors directly (<see cref="ColumnMapping.AssignFreshMapping"/>,
/// <see cref="ColumnMapping.ValidateColumnMappingSchema"/>, <see cref="ColumnMapping.EvolveNameModeMapping"/>,
/// <see cref="ColumnMapping.ToPhysicalSchema"/>) with hand-authored mapped schemas — the exact shape a
/// poisoned/foreign committed <c>metaData</c> carries — so a mis-bind cannot pass on equal values.
/// </summary>
public sealed class NestedColumnMappingTamperFuzzTests
{
    private const string IdKey = ColumnMapping.IdKey;
    private const string PhysKey = ColumnMapping.PhysicalNameKey;

    private readonly ITestOutputHelper _output;

    public NestedColumnMappingTamperFuzzTests(ITestOutputHelper output) => _output = output;

    // =========================================================================================
    // §3.14 · #839 array/map id-mode boundary — shape × door matrix (each names #839 + "id mode")
    // =========================================================================================

    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public void IdMode_ArrayOrMap_ValidateColumnMappingSchema_FailsClosed_839(string kind)
    {
        StructType schema = OneNestedContainer(kind, id: 2, physical: "col-c");
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(2)));
        Assert.Contains("#839", ex.Message, StringComparison.Ordinal);
        Assert.Contains("id mode", ex.Message, StringComparison.Ordinal);
    }

    // §3.14 clarification (Architect A): ToPhysicalSchema / MapWriteSchemaToPhysical are a PURE name-only
    // relabel (design §2.3 "Balanced-4") and do NOT themselves gate id-mode array/map — they pass the container
    // through (keeping its id). The #839 gate is ValidateColumnMappingSchema (commit AND load, asserted above),
    // so an id-mode array/map table can never be committed OR loaded; the relabel helper is a downstream pure
    // substitution. This test pins that actual contract so the §2.3-vs-§3.14 wording ambiguity cannot silently
    // flip the helper into (or out of) a redundant reject.
    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public void IdMode_ToPhysicalSchema_ArrayOrMap_IsPureRelabel_DoesNotGate_839(string kind)
    {
        StructType schema = OneNestedContainer(kind, id: 2, physical: "col-c");

        // Pure relabel: no throw, container renamed to its physical name, id preserved on the container.
        StructType physical = ColumnMapping.ToPhysicalSchema(schema, ColumnMappingMode.Id);
        Assert.Equal(2, physical.Count); // both the scalar 'id' and the array/map container pass through
        Assert.True(physical.TryGetField("col-c", out StructField container));
        Assert.True(ColumnMapping.TryGetId(container, out long id) && id == 2);
    }

    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public void IdMode_CreateDoor_ArrayOrMap_MintsThenFailsAtCommitValidate_839(string kind)
    {
        // AssignFreshMapping is mode-INDEPENDENT (it mints regardless), so an id-mode CREATE mints the mapping
        // then fails closed at the commit's ValidateColumnMappingSchema step — never bricking as a
        // permanently-unreadable table. Assert the mint succeeds and the validate is the gate.
        StructType logical = LogicalOneNestedContainer(kind);
        (StructType mapped, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource("id-arraymap"));
        Assert.Equal(2L, maxColumnId); // container contributes exactly 1 (id + container), never element/kv

        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, mapped, ColumnMapping.IdModeConfiguration(maxColumnId)));
        Assert.Contains("#839", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdMode_CreateTableWithArrayColumn_FailsClosedAtCommit_NotOnlyAtRead()
    {
        // The gate is ValidateColumnMappingSchema (which sits on BOTH commit and load); an id-mode array create
        // must fail at COMMIT, not silently commit and then only fail at read.
        StructType logical = LogicalOneNestedContainer("array");
        (StructType mapped, long maxColumnId) =
            ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource("id-array-commit"));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, mapped, ColumnMapping.IdModeConfiguration(maxColumnId)));
        Assert.Contains("#839", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NameMode_ArrayOrMap_ValidateColumnMappingSchema_Succeeds()
    {
        // The dual: the SAME array/map shape is accepted under name mode (only id mode is deferred to #839).
        foreach (string kind in new[] { "array", "map" })
        {
            StructType schema = OneNestedContainer(kind, id: 2, physical: "col-c");
            ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2));
        }
    }

    // =========================================================================================
    // §3.26 · ID-mode nested-within-nested at the assignment/validation door — shape × door matrix (#866).
    // NAME/none mode now recurses (866a); the retained fail-closed gate is ID mode until 866b.
    // =========================================================================================

    public static IEnumerable<object[]> NestedWithinNestedShapes()
    {
        yield return new object[] { "array<struct>" };
        yield return new object[] { "struct<struct>" };
        yield return new object[] { "map<string,struct>" };
        yield return new object[] { "array<array>" };
        yield return new object[] { "map<string,map>" };
    }

    [Theory]
    [MemberData(nameof(NestedWithinNestedShapes))]
    public void NestedWithinNested_AssignFreshMapping_FailsClosed_866(string shape)
    {
        StructType logical = NestedWithinNestedLogical(shape);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource("nwn-assign"), ColumnMappingMode.Id));
        AssertNestedWithinNested(ex);
    }

    [Theory]
    [MemberData(nameof(NestedWithinNestedShapes))]
    public void NestedWithinNested_ValidateColumnMappingSchema_FailsClosed_866(string shape)
    {
        // A raw/foreign committed metaData carrying such an ID-mode shape (mapped) fails closed at the load
        // choke point (the id-mode NWN gate fires first in ValidateMappedLevel's switch).
        StructType mapped = NestedWithinNestedMapped(shape);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, mapped, ColumnMapping.IdModeConfiguration(3)));
        AssertNestedWithinNested(ex);
    }

    [Theory]
    [MemberData(nameof(NestedWithinNestedShapes))]
    public void NestedWithinNested_ToPhysicalSchema_FailsClosed_866(string shape)
    {
        StructType mapped = NestedWithinNestedMapped(shape);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ToPhysicalSchema(mapped, ColumnMappingMode.Id));
        AssertNestedWithinNested(ex);
    }

    [Theory]
    [MemberData(nameof(NestedWithinNestedShapes))]
    public void NestedWithinNested_EvolveIdModeMapping_FailsClosed_866(string shape)
    {
        // Evolve a valid flat ID-mode table by ADDING a nested-within-nested column: fails closed naming #866
        // before any id is minted (no partial maxColumnId advance).
        var current = new StructType(new[] { MappedLeaf("id", DataTypes.LongType, 1, "col-id", false) });
        var evolved = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("payload", NestedWithinNestedType(shape)),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.EvolveNameModeMapping(
                evolved, current, ColumnMapping.IdModeConfiguration(1), new SeededPhysicalNameSource("nwn-evolve"),
                ColumnMappingMode.Id));
        AssertNestedWithinNested(ex);
    }

    [Fact]
    public void NestedWithinNested_AssignFreshMapping_NoPartialMaxColumnIdAdvance()
    {
        // The ID-mode reject fires BEFORE any child id is minted: the valid PREFIX ({id, addr:struct<city>})
        // minted alone yields maxColumnId=3; the same prefix followed by a nested-within-nested column throws
        // with no partially-advanced id observable (it never returns a maxColumnId at all).
        var prefix = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("addr", new StructType(new[] { new StructField("city", DataTypes.StringType) })),
        });
        (_, long baseline) = ColumnMapping.AssignFreshMapping(
            prefix, new SeededPhysicalNameSource("prefix"), ColumnMappingMode.Id);
        Assert.Equal(3L, baseline);

        var poisoned = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("addr", new StructType(new[] { new StructField("city", DataTypes.StringType) })),
            new StructField("payload", NestedWithinNestedType("struct<struct>")),
        });
        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.AssignFreshMapping(poisoned, new SeededPhysicalNameSource("prefix"), ColumnMappingMode.Id));
    }

    [Fact]
    public void ZeroFieldMappedStruct_ValidateColumnMappingSchema_FailsClosed()
    {
        var schema = new StructType(new[]
        {
            MappedLeaf("id", DataTypes.LongType, 1, "col-id", false),
            new StructField("empty", new StructType(Array.Empty<StructField>()), true, Meta(2, "col-empty")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains("zero-field struct", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // §3.15 · Duplicate physicalName among sibling struct children (validate cell)
    // =========================================================================================

    [Fact]
    public void DuplicatePhysicalName_AmongSiblingStructChildren_ValidateFailsClosed_Inconsistent()
    {
        // Two children of the same struct share a physicalName → per-level dup guard (Ordinal).
        var schema = new StructType(new[]
        {
            MappedLeaf("id", DataTypes.LongType, 1, "col-id", false),
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("a", DataTypes.LongType, 3, "col-dup"),
                MappedLeaf("b", DataTypes.LongType, 4, "col-dup"), // SAME physicalName as sibling 'a'
            }), true, Meta(2, "col-addr")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(4)));
        Assert.Contains("is assigned to more than one column", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // §3.16 · Duplicate top-level container name
    // =========================================================================================

    [Fact]
    public void DuplicateTopLevelPhysicalName_ValidateFailsClosed_Inconsistent()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[] { MappedLeaf("city", DataTypes.StringType, 2, "col-city") }), true, Meta(1, "col-dup")),
            new StructField("tags", new ArrayType(DataTypes.StringType), true, Meta(3, "col-dup")), // SAME as 'addr'
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(3)));
        Assert.Contains("is assigned to more than one column", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // §3.18 · Duplicate field_id anywhere in the nested tree
    // =========================================================================================

    [Fact]
    public void DuplicateFieldId_AnywhereInNestedTree_ValidateFailsClosed_Inconsistent()
    {
        var schema = new StructType(new[]
        {
            MappedLeaf("id", DataTypes.LongType, 1, "col-id", false),
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("city", DataTypes.StringType, 1, "col-city"), // id 1 collides with top-level 'id'
            }), true, Meta(2, "col-addr")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains("assigned to more than one column", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // §3.19 · Missing id OR physicalName on a nested StructField (two distinct cells)
    // =========================================================================================

    [Fact]
    public void NestedChild_MissingId_ValidateFailsClosed_Inconsistent()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, true, PhysOnly("col-city")), // no id
            }), true, Meta(1, "col-addr")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains("has no 'delta.columnMapping.id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedChild_MissingPhysicalName_ValidateFailsClosed()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType, true, IdOnly(2)), // no physicalName
            }), true, Meta(1, "col-addr")),
        });
        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
    }

    // =========================================================================================
    // §3.20 · Parent mapped, child unmapped (partial-recursion drift)
    // =========================================================================================

    [Fact]
    public void NestedStruct_ParentMapped_ChildUnmapped_ValidateFailsClosed()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                new StructField("city", DataTypes.StringType), // no mapping metadata at all
            }), true, Meta(1, "col-addr")),
        });
        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
    }

    // =========================================================================================
    // §3.21 · Non-positive nested field_id
    // =========================================================================================

    [Fact]
    public void NestedChildId_NonPositive_ValidateFailsClosed_Inconsistent()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("city", DataTypes.StringType, 0, "col-city"), // id 0 is out of range
            }), true, Meta(1, "col-addr")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains("outside the valid column-mapping", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // §3.22 · Nested child id exceeds maxColumnId (nested ceiling)
    // =========================================================================================

    [Fact]
    public void NestedChildId_ExceedsMaxColumnId_ValidateFailsClosed_Inconsistent()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("city", DataTypes.StringType, 9, "col-city"), // 9 > maxColumnId=2
            }), true, Meta(1, "col-addr")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains("exceeds the tracked", ex.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // §3.23 · Nested child physicalName safety (dot / control char) + accepted equal-to-top-level dual
    // =========================================================================================

    [Theory]
    [InlineData("col.dot")]
    [InlineData("col\u0001ctrl")]
    public void NestedChildPhysicalName_UnsafeComponent_ValidateFailsClosed(string physical)
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("city", DataTypes.StringType, 2, physical),
            }), true, Meta(1, "col-addr")),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains("not a safe path component", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedChildPhysicalNameEqualToTopLevelPhysicalName_IsAccepted_AndDoesNotMisCorrelate()
    {
        // physicalName uniqueness is PER LEVEL: a nested child may reuse a top-level sibling's physical name
        // (their footer paths differ: <child> vs <parent>.<child>). Accepted.
        var schema = new StructType(new[]
        {
            MappedLeaf("shared", DataTypes.LongType, 1, "col-shared", false),
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("city", DataTypes.StringType, 3, "col-shared"), // same physical as top-level 'shared'
            }), true, Meta(2, "col-addr")),
        });
        ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(3)); // no throw
    }

    // =========================================================================================
    // §3.16b · name-mode foreign nested.ids reject (a #676 C1 corollary — nested.ids is meaningful
    //           ONLY under id mode on an array/map; a stray one on a name-mode field fails closed and
    //           is NEVER accepted-and-ignored)
    // =========================================================================================

    [Fact]
    public void MappedSchemaCarryingNestedIds_NameMode_ValidateFailsClosed_ForeignKey()
    {
        var meta = ImmutableDictionary<string, MetadataValue>.Empty
            .Add(IdKey, MetadataValue.Long(2))
            .Add(PhysKey, MetadataValue.String("col-tags"))
            .Add(ColumnMapping.NestedIdsKey, MetadataValue.String("{\"element\":7}"));
        var schema = new StructType(new[]
        {
            MappedLeaf("id", DataTypes.LongType, 1, "col-id", false),
            new StructField("tags", new ArrayType(DataTypes.StringType), true, FieldMetadata.FromValues(meta)),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(2)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("id mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignMappedSchema_NestedCaseInsensitiveSiblingCollision_ValidateFailsClosed()
    {
        var schema = new StructType(new[]
        {
            new StructField("addr", new StructType(new[]
            {
                MappedLeaf("city", DataTypes.StringType, 2, "col-city"),
                MappedLeaf("CITY", DataTypes.StringType, 3, "col-city2"), // case-insensitive collision
            }), true, Meta(1, "col-addr")),
        });
        Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(3)));
    }

    // =========================================================================================
    // §3.28 · Evolve: drop a nested child, re-add same logical name → fresh id + physicalName
    // =========================================================================================

    [Fact]
    public void Evolve_DropNestedChildThenReAddSameLogicalName_MintsFreshIdAndPhysicalName_MaxColumnIdStrictlyIncreases()
    {
        var initial = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("addr", new StructType(new[] { new StructField("city", DataTypes.StringType) })),
        });
        (StructType mapped, long max0) = ColumnMapping.AssignFreshMapping(initial, new SeededPhysicalNameSource("evolve-0"));
        var addr0 = (StructType)mapped["addr"].DataType;
        long cityId0 = GetId(addr0["city"]);
        string cityPhys0 = GetPhys(addr0["city"]);

        // Drop 'city' (evolve to a struct with no children is illegal — keep a placeholder), then re-add.
        // Model the drop→re-add as: evolve to a schema WITHOUT city (add a keeper), then evolve re-adding city.
        var dropped = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("addr", new StructType(new[] { new StructField("zip", DataTypes.LongType) })),
        });
        (StructType mappedDropped, ImmutableSortedDictionary<string, string> cfg1) =
            ColumnMapping.EvolveNameModeMapping(dropped, mapped, ColumnMapping.NameModeConfiguration(max0), new SeededPhysicalNameSource("evolve-1"));

        var readded = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("addr", new StructType(new[]
            {
                new StructField("zip", DataTypes.LongType),
                new StructField("city", DataTypes.StringType),
            })),
        });
        (StructType mappedReadded, ImmutableSortedDictionary<string, string> cfg2) =
            ColumnMapping.EvolveNameModeMapping(readded, mappedDropped, cfg1, new SeededPhysicalNameSource("evolve-2"));

        var addr2 = (StructType)mappedReadded["addr"].DataType;
        long cityId2 = GetId(addr2["city"]);
        string cityPhys2 = GetPhys(addr2["city"]);

        Assert.NotEqual(cityId0, cityId2);       // fresh id
        Assert.NotEqual(cityPhys0, cityPhys2);   // fresh physicalName
        Assert.True(long.Parse(cfg2[ColumnMapping.MaxColumnIdKey]) > max0, "maxColumnId strictly increases");
    }

    // =========================================================================================
    // §3.32 · Metadata-only nested rename/drop — IMPLEMENTED by #840
    // =========================================================================================
    // The §3.32 conjunctive no-rewrite assertion (exactly one metaData action + zero add/remove ∧ per-file
    // byte-identity ∧ maxColumnId unchanged ∧ post-read under the new name) is reproduced verbatim as §3.1 in
    // NestedRenameDropTests (#840) — the segment-array rename/drop door. The deferral placeholder that stood
    // here has been discharged.

    // =========================================================================================
    // §3.33 · Seeded property harness (house convention — fixed 200 iterations, TestSeed, repro line)
    // =========================================================================================

    [Fact]
    public void SeededProperty_NestedMappingDoors_FailClosedOnTamper_RoundTripOnValid()
    {
        const string scope = nameof(SeededProperty_NestedMappingDoors_FailClosedOnTamper_RoundTripOnValid);
        int baseSeed = TestSeed.Resolve();
        var random = new Random(TestSeed.Combine(baseSeed, scope));
        _output.WriteLine($"[deltasharp-seed] {scope} baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");

        const int iterations = 200; // house precedent (ChangeFeedCdcFuzzTests.cs:103)
        for (int i = 0; i < iterations; i++)
        {
            (StructType logical, StructType mapped, long maxColumnId) = GenerateValidMappedSchema(random);

            // Invariant A: a VALID mapped schema validates cleanly under name mode (no throw) AND its logical
            // form re-mints the SAME maxColumnId (assignment is a deterministic StructField count at every depth).
            try
            {
                ColumnMapping.ValidateColumnMappingSchema(
                    ColumnMappingMode.Name, mapped, ColumnMapping.NameModeConfiguration(maxColumnId));
                (_, long reMaxColumnId) = ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource("prop-" + i));
                Assert.Equal(maxColumnId, reMaxColumnId);
            }
            catch (Exception ex)
            {
                EmitRepro(scope, baseSeed, i);
                throw new Xunit.Sdk.XunitException($"valid schema rejected at iteration {i}: {ex}");
            }

            // Invariant B: applying an enumerated tamper operator makes a door fail closed with a TYPED
            // exception ∈ {DeltaProtocolException, DeltaStorageException}.
            (StructType tampered, int op) = ApplyTamper(mapped, random);
            try
            {
                ColumnMapping.ValidateColumnMappingSchema(
                    ColumnMappingMode.Name, tampered, ColumnMapping.NameModeConfiguration(maxColumnId));

                // A few operators (e.g. reversing sibling order) may leave a still-valid schema — that is
                // acceptable (a benign tamper), matching the CDF harness's benign-mutation tolerance.
                Assert.True(op == TamperReverseSiblings, $"tamper op {op} unexpectedly left a valid schema at iteration {i}");
            }
            catch (DeltaProtocolException)
            {
                // fail-closed (the expected typed rejection)
            }
            catch (DeltaStorageException)
            {
                // fail-closed (the other permitted typed rejection)
            }
            catch (Exception ex)
            {
                EmitRepro(scope, baseSeed, i);
                throw new Xunit.Sdk.XunitException(
                    $"tamper op {op} at iteration {i} threw an UNEXPECTED {ex.GetType().FullName}: {ex}");
            }
        }
    }

    private void EmitRepro(string scope, int baseSeed, int iteration) =>
        _output.WriteLine(
            $"[deltasharp-seed] scope={scope} baseSeed={baseSeed} iteration={iteration} | reproduce: "
            + $"{TestSeed.EnvironmentVariable}={baseSeed} dotnet test tests/DeltaSharp.Storage.Tests "
            + $"--filter \"FullyQualifiedName~{scope}\"");

    // ---- generator + tamper operators (§3.33) ----

    private const int TamperSwapSiblingPhysical = 0;
    private const int TamperDeleteChildId = 1;
    private const int TamperIdAboveMax = 2;
    private const int TamperEmbeddedDot = 3;
    private const int TamperInjectNestedIds = 4;
    private const int TamperReverseSiblings = 5;
    private const int TamperDuplicatePhysical = 6;

    // Draws a valid nested mapped schema: {id:long} plus one struct<1..3 scalar children> with per-leaf
    // disjoint value domains implicit in the type set; the mapping is minted by AssignFreshMapping so it is
    // guaranteed internally consistent.
    private static (StructType Logical, StructType Mapped, long MaxColumnId) GenerateValidMappedSchema(Random random)
    {
        DataType[] scalarSet = { DataTypes.LongType, DataTypes.StringType, DataTypes.IntegerType, DataTypes.DoubleType };
        int childCount = 1 + random.Next(3);
        var children = new List<StructField>(childCount);
        for (int c = 0; c < childCount; c++)
        {
            children.Add(new StructField("c" + c, scalarSet[random.Next(scalarSet.Length)]));
        }

        var logical = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("s", new StructType(children)),
        });
        (StructType mapped, long maxColumnId) = ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource("gen"));
        return (logical, mapped, maxColumnId);
    }

    // Applies one enumerated tamper operator to the struct level of the mapped schema (design §3.33).
    private static (StructType Tampered, int Op) ApplyTamper(StructType mapped, Random random)
    {
        var s = (StructType)mapped["s"].DataType;
        int op = random.Next(7);
        List<StructField> kids = s.ToList();

        switch (op)
        {
            case TamperSwapSiblingPhysical when kids.Count >= 2:
                string p0 = GetPhys(kids[0]);
                string p1 = GetPhys(kids[1]);
                kids[0] = Relabel(kids[0], GetId(kids[0]), p1);
                kids[1] = Relabel(kids[1], GetId(kids[1]), p0);
                // NOTE: a pure swap keeps both names unique — benign at the validate door; classify as reverse.
                return (WithStruct(mapped, new StructType(kids)), TamperReverseSiblings);
            case TamperDeleteChildId:
                kids[0] = new StructField(kids[0].Name, kids[0].DataType, kids[0].Nullable, PhysOnly(GetPhys(kids[0])));
                break;
            case TamperIdAboveMax:
                kids[0] = Relabel(kids[0], 999_999, GetPhys(kids[0]));
                break;
            case TamperEmbeddedDot:
                kids[0] = Relabel(kids[0], GetId(kids[0]), "col.dot");
                break;
            case TamperInjectNestedIds:
                var meta = ImmutableDictionary<string, MetadataValue>.Empty
                    .Add(IdKey, MetadataValue.Long(GetId(kids[0])))
                    .Add(PhysKey, MetadataValue.String(GetPhys(kids[0])))
                    .Add(ColumnMapping.NestedIdsKey, MetadataValue.String("{}"));
                kids[0] = new StructField(kids[0].Name, kids[0].DataType, kids[0].Nullable, FieldMetadata.FromValues(meta));
                break;
            case TamperReverseSiblings:
                kids.Reverse();
                break;
            case TamperDuplicatePhysical when kids.Count >= 2:
                kids[1] = Relabel(kids[1], GetId(kids[1]), GetPhys(kids[0]));
                break;
            default:
                // op with too-few siblings to apply — fall back to a guaranteed-poison delete-id.
                kids[0] = new StructField(kids[0].Name, kids[0].DataType, kids[0].Nullable, PhysOnly(GetPhys(kids[0])));
                break;
        }

        return (WithStruct(mapped, new StructType(kids)), op);
    }

    private static StructType WithStruct(StructType mapped, StructType newInner) =>
        new StructType(mapped.Select(f => f.Name == "s"
            ? new StructField(f.Name, newInner, f.Nullable, f.Metadata) : f).ToList());

    // =========================================================================================
    // Helpers — mapped-field builders
    // =========================================================================================

    private static FieldMetadata Meta(long id, string physical) =>
        FieldMetadata.FromValues(ImmutableDictionary<string, MetadataValue>.Empty
            .Add(IdKey, MetadataValue.Long(id))
            .Add(PhysKey, MetadataValue.String(physical)));

    private static FieldMetadata IdOnly(long id) =>
        FieldMetadata.FromValues(ImmutableDictionary<string, MetadataValue>.Empty.Add(IdKey, MetadataValue.Long(id)));

    private static FieldMetadata PhysOnly(string physical) =>
        FieldMetadata.FromValues(ImmutableDictionary<string, MetadataValue>.Empty.Add(PhysKey, MetadataValue.String(physical)));

    private static StructField MappedLeaf(string name, DataType type, long id, string physical, bool nullable = true) =>
        new StructField(name, type, nullable, Meta(id, physical));

    private static StructField Relabel(StructField field, long id, string physical) =>
        new StructField(field.Name, field.DataType, field.Nullable, Meta(id, physical));

    private static long GetId(StructField field)
    {
        Assert.True(field.Metadata.TryGetLong(IdKey, out long id));
        return id;
    }

    private static string GetPhys(StructField field)
    {
        Assert.True(field.Metadata.TryGetString(PhysKey, out string? p) && p is not null);
        return p!;
    }

    // A top-level {id:long, c:<array|map>} MAPPED schema (ids 1,2; physicalName supplied for the container).
    private static StructType OneNestedContainer(string kind, long id, string physical)
    {
        DataType container = kind == "array"
            ? new ArrayType(DataTypes.StringType)
            : new MapType(DataTypes.StringType, DataTypes.LongType);
        return new StructType(new[]
        {
            MappedLeaf("id", DataTypes.LongType, 1, "col-id", false),
            new StructField("c", container, true, Meta(id, physical)),
        });
    }

    private static StructType LogicalOneNestedContainer(string kind)
    {
        DataType container = kind == "array"
            ? new ArrayType(DataTypes.StringType)
            : new MapType(DataTypes.StringType, DataTypes.LongType);
        return new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("c", container),
        });
    }

    // ---- nested-within-nested shapes ----

    private static DataType NestedWithinNestedType(string shape) => shape switch
    {
        "array<struct>" => new ArrayType(new StructType(new[] { new StructField("x", DataTypes.LongType) })),
        "struct<struct>" => new StructType(new[] { new StructField("inner", new StructType(new[] { new StructField("x", DataTypes.LongType) })) }),
        "map<string,struct>" => new MapType(DataTypes.StringType, new StructType(new[] { new StructField("x", DataTypes.LongType) })),
        "array<array>" => new ArrayType(new ArrayType(DataTypes.LongType)),
        "map<string,map>" => new MapType(DataTypes.StringType, new MapType(DataTypes.StringType, DataTypes.LongType)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static StructType NestedWithinNestedLogical(string shape) =>
        new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, false),
            new StructField("payload", NestedWithinNestedType(shape)),
        });

    private static StructType NestedWithinNestedMapped(string shape) =>
        new StructType(new[]
        {
            MappedLeaf("id", DataTypes.LongType, 1, "col-id", false),
            new StructField("payload", NestedWithinNestedType(shape), true, Meta(2, "col-payload")),
        });

    private static void AssertNestedWithinNested(DeltaProtocolException ex)
    {
        Assert.Contains("nested type within a nested type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#866", ex.Message, StringComparison.Ordinal);
    }
}
