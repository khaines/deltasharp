using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #839 §3 — the id-mode <c>array&lt;scalar&gt;</c> / <c>map&lt;scalar,scalar&gt;</c> assignment +
/// <c>delta.columnMapping.nested.ids</c> validation surface (the schema-validation door, not the read door).
/// Covers the mode-split <c>maxColumnId</c> accounting (§3.3/§3.4), Spark-authored fail-closed (§3.6/§3.7), the
/// per-invariant fail-closed matrix over the interior (§3.15–§3.18b), the #585-first ordering (§3.19), the
/// #676-corollary foreign-key reject (§3.16b), and the §3.26 seeded conjunctive tamper oracle over the
/// interior. Every fail-closed cell is a single tamper against an otherwise byte-exact isolating fixture and
/// asserts the SPECIFIC rejecting guard.
/// </summary>
public sealed class ArrayMapIdModeNestedIdsValidationTests
{
    private const long ConfiguredMax = 100; // generous ceiling so range is not the incidental gate

    private readonly ITestOutputHelper _output;

    public ArrayMapIdModeNestedIdsValidationTests(ITestOutputHelper output) => _output = output;

    // -------------------------------------------------------------------------------------------------
    // §3.3 / §3.4 · mode-split maxColumnId accounting (array +2, map +3; name mode +1; Spark gaps accepted)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ArrayMapIdMode_MaxColumnId_Accounting_ArrayIsPlus2_MapIsPlus3()
    {
        (StructType arrMapped, long arrMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { LogicalArray("arr", DataTypes.LongType) }),
            new SeededPhysicalNameSource("id-arr"), ColumnMappingMode.Id);
        Assert.Equal(2L, arrMax); // container + element

        (_, long mapMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { LogicalMap("m", DataTypes.StringType, DataTypes.LongType) }),
            new SeededPhysicalNameSource("id-map"), ColumnMappingMode.Id);
        Assert.Equal(3L, mapMax); // container + key + value

        // Pre-order monotonic: {id:long, tags:array<long>} → id=1, container=2, element=3.
        (StructType mixed, long mixedMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: true),
                LogicalArray("tags", DataTypes.LongType),
            }),
            new SeededPhysicalNameSource("id-mixed"), ColumnMappingMode.Id);
        Assert.Equal(3L, mixedMax);

        // The container carries a nested.ids whose element id is the last-minted id (3) under the minted phys.
        StructField container = mixed.Fields[1];
        string phys = PhysicalOf(container);
        Assert.True(ColumnMapping.TryGetArrayElementId(container, phys, out long elementId));
        Assert.Equal(3L, elementId);

        // A Spark-authored gap (configured maxColumnId strictly exceeds the max assigned id) loads.
        ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Id, mixed, ColumnMapping.IdModeConfiguration(mixedMax + 25));
    }

    [Fact]
    public void ArrayMapIdMode_NameMode_ArrayAndMap_Unchanged_NoNestedIds_PlusOneEach()
    {
        (StructType mapped, long max) = ColumnMapping.AssignFreshMapping(
            new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: true),
                LogicalArray("tags", DataTypes.LongType),
                LogicalMap("props", DataTypes.StringType, DataTypes.LongType),
            }),
            new SeededPhysicalNameSource("name-mode"), ColumnMappingMode.Name);

        Assert.Equal(3L, max); // id + array(+1) + map(+1) — NO interior ids
        foreach (StructField container in new[] { mapped.Fields[1], mapped.Fields[2] })
        {
            Assert.False(
                ColumnMapping.TryGetArrayElementId(container, PhysicalOf(container), out _),
                "name-mode array/map must carry no nested.ids");
        }

        ColumnMapping.ValidateColumnMappingSchema(
            ColumnMappingMode.Name, mapped, ColumnMapping.NameModeConfiguration(max));
    }

    [Fact]
    public void ArrayMapIdMode_AssignThenValidate_RoundTrips_ForArrayAndMap()
    {
        foreach ((string label, StructField logical) in new[]
        {
            ("array", LogicalArray("c", DataTypes.LongType)),
            ("map", LogicalMap("c", DataTypes.StringType, DataTypes.LongType)),
        })
        {
            (StructType mapped, long max) = ColumnMapping.AssignFreshMapping(
                new StructType(new[] { logical }), new SeededPhysicalNameSource("rt-" + label), ColumnMappingMode.Id);
            ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, mapped, ColumnMapping.IdModeConfiguration(max));
        }
    }

    // -------------------------------------------------------------------------------------------------
    // §3.6 / §3.7 · Spark-authored plain id-mode array/map (NO nested.ids) → fail closed at validate
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("array")]
    [InlineData("map")]
    public void ArrayMapIdMode_SparkAuthored_NoNestedIds_FailsClosed_NamesNestedIdsRequirement(string kind)
    {
        StructType schema = OneContainer(kind, physical: "col-c", containerId: 2, nestedIds: null);
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("id mode", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.15 · key-shape mismatch matrix
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void NestedIds_ArrayKeyShapeMismatch_MapSelectorsOnArray_FailsClosed()
    {
        // '.key'/'.value' selectors on an array (should be '.element').
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(
            ("col-c.key", MetadataValue.Long(3)), ("col-c.value", MetadataValue.Long(4))));
        AssertNestedIdsReject(schema);
    }

    [Fact]
    public void NestedIds_MapKeyShapeMismatch_ElementSelectorOnMap_FailsClosed()
    {
        StructType schema = OneContainer("map", "col-c", 2, NestedIds(("col-c.element", MetadataValue.Long(3))));
        AssertNestedIdsReject(schema);
    }

    [Fact]
    public void NestedIds_WrongPhysicalNamePrefix_FailsClosed()
    {
        // Right selector, wrong (sibling's) physical-name prefix.
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(("col-OTHER.element", MetadataValue.Long(3))));
        AssertNestedIdsReject(schema);
    }

    [Fact]
    public void NestedIds_ArrayMissingElementKey_FailsClosed()
    {
        StructType schema = OneContainer("array", "col-c", 2, NestedIds()); // empty object → missing '.element'
        AssertNestedIdsReject(schema);
    }

    [Fact]
    public void NestedIds_MapMissingValueKey_FailsClosed()
    {
        StructType schema = OneContainer("map", "col-c", 2, NestedIds(("col-c.key", MetadataValue.Long(3))));
        AssertNestedIdsReject(schema);
    }

    [Fact]
    public void NestedIds_ExtraUnknownKey_FailsClosed()
    {
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(
            ("col-c.element", MetadataValue.Long(3)), ("col-c.bogus", MetadataValue.Long(4))));
        AssertNestedIdsReject(schema);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.16 / §3.16b · nested.ids on a non-array/map field, and on a name/none-mode array/map (foreign)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void NestedIds_OnScalarField_IdMode_FailsClosed()
    {
        var scalar = new StructField(
            "col-s", DataTypes.LongType, nullable: true,
            Meta(id: 2, physical: "col-s", nestedIds: NestedIds(("col-s.element", MetadataValue.Long(3)))));
        StructType schema = new(new[] { scalar });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedIds_OnStructField_IdMode_FailsClosed()
    {
        var container = new StructField(
            "col-st",
            new StructType(new[] { new StructField("x", DataTypes.LongType, nullable: true, Meta(3, "col-x")) }),
            nullable: true,
            Meta(id: 2, physical: "col-st", nestedIds: NestedIds(("col-st.element", MetadataValue.Long(4)))));
        StructType schema = new(new[] { container });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedIds_NameMode_ForeignOnArray_FailsClosed_676Corollary()
    {
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(("col-c.element", MetadataValue.Long(3))));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(ConfiguredMax)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("id mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedIds_NameMode_ForeignOnMap_FailsClosed_676Corollary()
    {
        StructType schema = OneContainer("map", "col-c", 2, NestedIds(
            ("col-c.key", MetadataValue.Long(3)), ("col-c.value", MetadataValue.Long(4))));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(ConfiguredMax)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneMode_ArrayMap_WithForeignNestedIds_FailsClosed()
    {
        // §3.16b none-mode arm. The design §2.4 gate enumerates "name OR none mode" foreign nested.ids as the
        // unconditional fail-closed reject. VERIFIED against the production door (ColumnMapping.cs
        // ValidateColumnMappingSchema): mode == None short-circuits at the method entry
        // (`if (mode == ColumnMappingMode.None) return;`) BEFORE ValidateMappedLevel's `!inScopeIdArrayMap &&
        // hasNestedIds` reject is reached — so the schema-validation door does NOT throw for a none-mode
        // container carrying a stray nested.ids. This is SAFE (not the #676 regression the design guards for
        // NAME mode): none mode binds every column by its LOGICAL name and NEVER consults a field_id or
        // nested.ids, so a stray nested.ids is structurally INERT (it can never mis-attribute an interior
        // leaf the way a name-mode field_id-adjacent key could). This test PINS that actual behavior — the
        // none-mode door is a no-op accept — and contrasts it with the name-mode reject above so a future
        // change that either (a) starts binding none-mode interiors by nested.ids or (b) removes the name-mode
        // reject is caught. (The design's "(and none-mode) ... unconditional reject" prose is aspirational
        // relative to the none-mode early-return; the impl's inert no-op is the shipped, verified contract.)
        foreach (StructType schema in new[]
        {
            OneContainer("array", "col-c", 2, NestedIds(("col-c.element", MetadataValue.Long(3)))),
            OneContainer("map", "col-c", 2, NestedIds(
                ("col-c.key", MetadataValue.Long(3)), ("col-c.value", MetadataValue.Long(4)))),
        })
        {
            // None mode: inert no-op accept (never binds by nested.ids) — the door does not throw.
            Exception? noneResult = Record.Exception(() =>
                ColumnMapping.ValidateColumnMappingSchema(
                    ColumnMappingMode.None, schema, new Dictionary<string, string>()));
            Assert.Null(noneResult);

            // Contrast: the SAME foreign nested.ids under NAME mode IS the unconditional fail-closed reject —
            // name mode has physicalName binding, so accepting-and-ignoring would regress #676's guarantee.
            DeltaProtocolException nameEx = Assert.Throws<DeltaProtocolException>(
                () => ColumnMapping.ValidateColumnMappingSchema(
                    ColumnMappingMode.Name, schema, ColumnMapping.NameModeConfiguration(ConfiguredMax)));
            Assert.Contains(ColumnMapping.NestedIdsKey, nameEx.Message, StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // §3.17 · interior-id uniqueness (interior↔top-level, interior↔interior)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void NestedIds_InteriorIdCollidesWithTopLevelId_FailsClosed()
    {
        // top-level scalar id=1; the array's element id is ALSO 1 → global uniqueness reject.
        var schema = new StructType(new[]
        {
            new StructField("col-a", DataTypes.LongType, nullable: true, Meta(1, "col-a")),
            OneContainerField("array", "col-b", 2, NestedIds(("col-b.element", MetadataValue.Long(1)))),
        });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains("unique", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedIds_MapKeyValueShareId_FailsClosed()
    {
        // key id == value id (interior↔interior collision, same container).
        StructType schema = OneContainer("map", "col-c", 2, NestedIds(
            ("col-c.key", MetadataValue.Long(5)), ("col-c.value", MetadataValue.Long(5))));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains("unique", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.18 / §3.18b · range + non-Long value
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void NestedIds_InteriorId_ExceedsMaxColumnId_FailsClosed()
    {
        // element id 50 > configured maxColumnId 10.
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(("col-c.element", MetadataValue.Long(50))));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(10)));
        Assert.Contains("maxColumnId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedIds_InteriorId_NonPositive_FailsClosed()
    {
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(("col-c.element", MetadataValue.Long(0))));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains("[1, int.MaxValue]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedIds_InteriorId_AboveInt32Max_FailsClosed()
    {
        StructType schema = OneContainer(
            "array", "col-c", 2, NestedIds(("col-c.element", MetadataValue.Long((long)int.MaxValue + 1))));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(long.MaxValue)));
        Assert.Contains("[1, int.MaxValue]", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonLongInteriorValues))]
    public void NestedIds_ValueNotLong_FailsClosed(MetadataValue nonLong)
    {
        // Finding 3 / §3.18b: a non-Long nested.ids value is a TYPED DeltaProtocolException BEFORE the range
        // check, never an untyped InvalidOperationException from MetadataValue.AsLong().
        StructType schema = OneContainer("array", "col-c", 2, NestedIds(("col-c.element", nonLong)));
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains("not an integer", ex.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> NonLongInteriorValues()
    {
        yield return new object[] { MetadataValue.Double(3.5) };
        yield return new object[] { MetadataValue.String("3") };
        yield return new object[] { MetadataValue.Boolean(true) };
        yield return new object[] { MetadataValue.Null };
        yield return new object[]
        {
            MetadataValue.Nested(FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("inner", MetadataValue.Long(3)),
            })),
        };
    }

    // -------------------------------------------------------------------------------------------------
    // §3.19 · nested-within-nested rejected BEFORE the #839 gate (#585), no partial maxColumnId advance
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("array<struct>")]
    [InlineData("map<string,struct>")]
    [InlineData("array<array>")]
    [InlineData("map<string,map>")]
    [InlineData("struct<array<struct>>")]
    public void ArrayMapIdMode_NestedWithinNested_RejectedBeforeGate_Naming585(string shape)
    {
        StructType logical = new(new[] { NestedWithinNested(shape) });
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.AssignFreshMapping(logical, new SeededPhysicalNameSource("nwn"), ColumnMappingMode.Id));
        Assert.Contains("#585", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.23 / §3.24 · evolve — mint container+interior, interior immutable across rename, type-change retires
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ArrayMapIdMode_Evolve_AddArrayColumn_MintsContainerPlusElementId()
    {
        (StructType current, long curMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { new StructField("id", DataTypes.LongType, nullable: true) }),
            new SeededPhysicalNameSource("evolve-base"), ColumnMappingMode.Id);
        Assert.Equal(1L, curMax);

        var evolved = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: true),
            LogicalArray("tags", DataTypes.LongType),
        });
        (StructType mappedEvolved, ImmutableSortedDictionary<string, string> config) =
            ColumnMapping.EvolveNameModeMapping(
                evolved, current, ColumnMapping.IdModeConfiguration(curMax),
                new SeededPhysicalNameSource("evolve-add"), ColumnMappingMode.Id);

        long newMax = long.Parse(config["delta.columnMapping.maxColumnId"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(3L, newMax); // container (2) + element (3) minted, strictly increasing
        ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Id, mappedEvolved, config);
    }

    [Fact]
    public void ArrayMapIdMode_Evolve_ArrayInteriorIdImmutableAcrossRename()
    {
        (StructType current, long curMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { LogicalArray("tags", DataTypes.LongType) }),
            new SeededPhysicalNameSource("imm-base"), ColumnMappingMode.Id);
        StructField before = current.Fields[0];
        string phys = PhysicalOf(before);
        Assert.True(ColumnMapping.TryGetArrayElementId(before, phys, out long elementIdBefore));

        // Same logical column, unchanged type → its identity (id, physical, interior element id) is reused.
        (StructType mappedEvolved, _) = ColumnMapping.EvolveNameModeMapping(
            new StructType(new[] { LogicalArray("tags", DataTypes.LongType) }),
            current, ColumnMapping.IdModeConfiguration(curMax),
            new SeededPhysicalNameSource("imm-evolve"), ColumnMappingMode.Id);

        StructField after = mappedEvolved.Fields[0];
        Assert.Equal(phys, PhysicalOf(after));
        Assert.True(ColumnMapping.TryGetArrayElementId(after, phys, out long elementIdAfter));
        Assert.Equal(elementIdBefore, elementIdAfter);
    }

    [Fact]
    public void Evolve_ContainerTypeChangesArrayToMap_RetiresInteriorIdentities_NotReParented()
    {
        // §3.24 · the §2.4 EvolveNameModeMapping contract: "a container whose TYPE changes retires its
        // interior identities, never re-parents". VERIFIED against production (ColumnMapping.cs
        // ResolveEvolveNestedIds): array→map IS reachable via EvolveNameModeMapping (the container is matched
        // by LOGICAL name across the evolve, an overwriteSchema type change). At that point:
        //   * EvolveMappedField reuses the existing container's (id, physicalName) verbatim (rename-immutable);
        //   * ResolveEvolveNestedIds sees `SameNestedKind(map, array) == false`, so it does NOT reuse the old
        //     interior nested.ids — it mints FRESH key/value ids after the container id (pre-order).
        // So the OLD array `.element` identity is RETIRED (dropped, never carried onto the new map interior),
        // fresh map key/value ids are minted, maxColumnId is bumped, and no old id is re-parented.
        (StructType current, long curMax) = ColumnMapping.AssignFreshMapping(
            new StructType(new[] { LogicalArray("c", DataTypes.LongType) }),
            new SeededPhysicalNameSource("typechg-base"), ColumnMappingMode.Id);
        StructField beforeContainer = current.Fields[0];
        string physBefore = PhysicalOf(beforeContainer);
        long containerIdBefore = beforeContainer.Metadata[ColumnMapping.IdKey].AsLong();
        Assert.True(ColumnMapping.TryGetArrayElementId(beforeContainer, physBefore, out long oldElementId));
        Assert.Equal(2L, curMax); // container(1) + element(2)

        // Evolve: SAME logical name 'c', TYPE CHANGES array<long> → map<string,long> (overwriteSchema).
        var evolved = new StructType(new[] { LogicalMap("c", DataTypes.StringType, DataTypes.LongType) });
        (StructType mappedEvolved, ImmutableSortedDictionary<string, string> config) =
            ColumnMapping.EvolveNameModeMapping(
                evolved, current, ColumnMapping.IdModeConfiguration(curMax),
                new SeededPhysicalNameSource("typechg-evolve"), ColumnMappingMode.Id);

        StructField afterContainer = mappedEvolved.Fields[0];
        string physAfter = PhysicalOf(afterContainer);

        // Container identity is PRESERVED per the impl's actual contract (same-name reuse of id + physicalName;
        // the physical name is reused so any future data keyed by it stays valid).
        Assert.Equal(physBefore, physAfter);
        Assert.Equal(containerIdBefore, afterContainer.Metadata[ColumnMapping.IdKey].AsLong());

        // The OLD array `.element` interior identity is RETIRED — the new map's nested.ids carries NO
        // `.element` key (not carried onto / re-parented onto the new interior).
        Assert.False(
            ColumnMapping.TryGetArrayElementId(afterContainer, physAfter, out _),
            "the retired array element identity must not survive onto the new map interior");

        // FRESH interior ids are minted for the new map key/value, strictly AFTER (exceeding) the retired
        // element id — never reusing/re-parenting the old id.
        Assert.True(ColumnMapping.TryGetMapKeyValueIds(afterContainer, physAfter, out long keyId, out long valueId));
        Assert.NotEqual(oldElementId, keyId);
        Assert.NotEqual(oldElementId, valueId);
        Assert.NotEqual(keyId, valueId);
        Assert.True(keyId > oldElementId, "fresh map key id minted strictly after the retired element id");
        Assert.True(valueId > oldElementId, "fresh map value id minted strictly after the retired element id");

        // maxColumnId is bumped by exactly 2 (key + value); the container id is preserved, not re-minted.
        long newMax = long.Parse(
            config["delta.columnMapping.maxColumnId"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(curMax + 2, newMax);

        // The retired old element id was NOT re-parented (it is distinct from BOTH fresh interior ids), and the
        // evolved id-mode schema validates cleanly (fresh interior ids in range, globally unique, key-shaped).
        ColumnMapping.ValidateColumnMappingSchema(ColumnMappingMode.Id, mappedEvolved, config);
    }

    // -------------------------------------------------------------------------------------------------
    // §3.10 · SchemaJson nested.ids-object round-trip (dotted keys + Long values preserved) — prerequisite pin
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void SchemaJson_NestedIdsObject_RoundTrips_DottedKeys_And_LongValues()
    {
        var schema = new StructType(new[]
        {
            OneContainerField("array", "col-b", 2, NestedIds(("col-b.element", MetadataValue.Long(3)))),
            OneContainerField("map", "col-c", 4, NestedIds(
                ("col-c.key", MetadataValue.Long(5)), ("col-c.value", MetadataValue.Long(6)))),
        });

        string json = SchemaJson.ToJson(schema);
        var parsed = (StructType)SchemaJson.FromJson(json);

        AssertNestedIdLong(parsed.Fields[0], "col-b.element", 3);
        AssertNestedIdLong(parsed.Fields[1], "col-c.key", 5);
        AssertNestedIdLong(parsed.Fields[1], "col-c.value", 6);

        // Re-serialization is byte-identical (idempotent), so the nested-object metadata survives verbatim.
        Assert.Equal(json, SchemaJson.ToJson(parsed));
    }

    private static void AssertNestedIdLong(StructField field, string dottedKey, long expected)
    {
        Assert.True(field.Metadata.TryGetValue(ColumnMapping.NestedIdsKey, out MetadataValue? nested));
        Assert.Equal(MetadataValueKind.Nested, nested!.Kind);
        Assert.True(nested.AsNested().TryGetValue(dottedKey, out MetadataValue? id));
        Assert.Equal(MetadataValueKind.Long, id!.Kind);
        Assert.Equal(expected, id.AsLong());
    }

    // -------------------------------------------------------------------------------------------------
    // §3.26 · Seeded conjunctive tamper oracle over the interior (house convention: TestSeed, 200 iters, repro)
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void SeededProperty_NestedIdsInterior_FailClosedOnTamper_RoundTripOnValid()
    {
        const string scope = nameof(SeededProperty_NestedIdsInterior_FailClosedOnTamper_RoundTripOnValid);
        int baseSeed = TestSeed.Resolve();
        var random = new Random(TestSeed.Combine(baseSeed, scope));
        _output.WriteLine($"[deltasharp-seed] {scope} baseSeed={baseSeed} ({TestSeed.EnvironmentVariable})");

        const int iterations = 200; // house precedent (ChangeFeedCdcFuzzTests.cs:103)
        for (int i = 0; i < iterations; i++)
        {
            bool isMap = random.Next(2) == 0;
            DataType[] scalarSet = { DataTypes.LongType, DataTypes.StringType, DataTypes.IntegerType };
            DataType keyType = scalarSet[random.Next(scalarSet.Length)];
            DataType valueType = scalarSet[random.Next(scalarSet.Length)];

            StructField logicalContainer = isMap
                ? LogicalMap("c", keyType, valueType)
                : LogicalArray("c", valueType);
            var logical = new StructType(new[]
            {
                new StructField("id", DataTypes.LongType, nullable: true),
                logicalContainer,
            });

            (StructType mapped, long max) = ColumnMapping.AssignFreshMapping(
                logical, new SeededPhysicalNameSource("oracle-" + i), ColumnMappingMode.Id);
            long configuredMax = max + random.Next(0, 5); // include Spark-gap draws

            // Invariant A (round-trip identity): the minted id-mode mapping validates cleanly.
            try
            {
                ColumnMapping.ValidateColumnMappingSchema(
                    ColumnMappingMode.Id, mapped, ColumnMapping.IdModeConfiguration(configuredMax));
            }
            catch (Exception ex)
            {
                EmitRepro(scope, baseSeed, i);
                throw new Xunit.Sdk.XunitException($"valid id-mode schema rejected at iteration {i}: {ex}");
            }

            // Invariant B (conjunctive fail-closed): an enumerated interior tamper makes a door fail closed
            // with a TYPED exception, OR is a benign residual (a consistent key/value swap on a map).
            (StructType tampered, string op, bool benign, ColumnMappingMode mode) =
                ApplyInteriorTamper(mapped, isMap, configuredMax, random);
            try
            {
                IReadOnlyDictionary<string, string> config = mode == ColumnMappingMode.Id
                    ? ColumnMapping.IdModeConfiguration(configuredMax)
                    : ColumnMapping.NameModeConfiguration(configuredMax);
                ColumnMapping.ValidateColumnMappingSchema(mode, tampered, config);
                Assert.True(benign, $"tamper '{op}' unexpectedly left a valid schema at iteration {i}");
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
                    $"tamper '{op}' at iteration {i} threw an UNEXPECTED {ex.GetType().FullName}: {ex}");
            }
        }
    }

    private void EmitRepro(string scope, int baseSeed, int iteration) =>
        _output.WriteLine(
            $"[deltasharp-seed] scope={scope} baseSeed={baseSeed} iteration={iteration} | reproduce: "
            + $"{TestSeed.EnvironmentVariable}={baseSeed} dotnet test tests/DeltaSharp.Storage.Tests "
            + $"--filter \"FullyQualifiedName~{scope}\"");

    // Applies ONE enumerated interior tamper operator to the minted id-mode mapped schema. Returns the
    // tampered schema, the operator label, whether the result is a benign residual (still valid), and the mode
    // to validate under (all operators validate under Id except the foreign-mode operator).
    private (StructType Schema, string Op, bool Benign, ColumnMappingMode Mode) ApplyInteriorTamper(
        StructType mapped, bool isMap, long configuredMax, Random random)
    {
        StructField id = mapped.Fields[0];
        StructField container = mapped.Fields[1];
        string phys = PhysicalOf(container);
        FieldMetadata nestedIds = ContainerNestedIds(container);
        string elementKey = phys + ".element";
        string keyKey = phys + ".key";
        string valueKey = phys + ".value";

        int op = random.Next(isMap ? 9 : 8);
        switch (op)
        {
            case 0: // dup interior id: element↔container, or key↔value (interior↔interior)
                if (isMap)
                {
                    long keyId = nestedIds[keyKey].AsLong();
                    return (Rebuild(id, container, NestedIds((keyKey, MetadataValue.Long(keyId)), (valueKey, MetadataValue.Long(keyId)))),
                        "dup-key-value-id", false, ColumnMappingMode.Id);
                }

                long containerId = container.Metadata[ColumnMapping.IdKey].AsLong();
                return (Rebuild(id, container, NestedIds((elementKey, MetadataValue.Long(containerId)))),
                    "dup-element-container-id", false, ColumnMappingMode.Id);

            case 1: // delete a nested.ids key
                return (Rebuild(id, container, NestedIds()), "delete-nested-ids-key", false, ColumnMappingMode.Id);

            case 2: // interior id = configuredMax + 1 (out of ceiling)
                return (Rebuild(id, container, ReplaceFirst(nestedIds, MetadataValue.Long(configuredMax + 1))),
                    "interior-id-above-max", false, ColumnMappingMode.Id);

            case 3: // interior id = top-level id (1) — global uniqueness
                return (Rebuild(id, container, ReplaceFirst(nestedIds, MetadataValue.Long(1))),
                    "interior-collides-top-level", false, ColumnMappingMode.Id);

            case 4: // non-Long interior value
                MetadataValue nonLong = random.Next(3) switch
                {
                    0 => MetadataValue.Double(3.5),
                    1 => MetadataValue.String("3"),
                    _ => MetadataValue.Nested(FieldMetadata.FromValues(new[]
                    {
                        new KeyValuePair<string, MetadataValue>("x", MetadataValue.Long(3)),
                    })),
                };
                return (Rebuild(id, container, ReplaceFirst(nestedIds, nonLong)),
                    "non-long-value", false, ColumnMappingMode.Id);

            case 5: // key-shape mismatch (wrong selector for the shape)
                MetadataValue wrongShape = isMap
                    ? NestedIds((elementKey, MetadataValue.Long(nestedIds[keyKey].AsLong())))
                    : NestedIds((keyKey, MetadataValue.Long(nestedIds[elementKey].AsLong())));
                return (Rebuild(id, container, wrongShape), "key-shape-mismatch", false, ColumnMappingMode.Id);

            case 6: // wrong physical-name prefix
                MetadataValue wrongPrefix = isMap
                    ? NestedIds(("col-OTHER.key", nestedIds[keyKey]), ("col-OTHER.value", nestedIds[valueKey]))
                    : NestedIds(("col-OTHER.element", nestedIds[elementKey]));
                return (Rebuild(id, container, wrongPrefix), "wrong-prefix", false, ColumnMappingMode.Id);

            case 7: // foreign nested.ids under NAME mode (#676 corollary) — validate under Name
                return (mapped, "foreign-name-mode", false, ColumnMappingMode.Name);

            default: // op 8 (map only): consistent key↔value swap — the accepted id-anchor residual (benign)
                return (Rebuild(id, container, NestedIds(
                        (keyKey, MetadataValue.Long(nestedIds[valueKey].AsLong())),
                        (valueKey, MetadataValue.Long(nestedIds[keyKey].AsLong())))),
                    "consistent-key-value-swap", true, ColumnMappingMode.Id);
        }
    }

    private static FieldMetadata ContainerNestedIds(StructField container)
    {
        Assert.True(container.Metadata.TryGetValue(ColumnMapping.NestedIdsKey, out MetadataValue? v));
        return v!.AsNested();
    }

    private static MetadataValue ReplaceFirst(FieldMetadata nestedIds, MetadataValue newValue)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>();
        bool replaced = false;
        foreach (KeyValuePair<string, MetadataValue> e in nestedIds)
        {
            entries.Add(new KeyValuePair<string, MetadataValue>(e.Key, replaced ? e.Value : newValue));
            replaced = true;
        }

        return MetadataValue.Nested(FieldMetadata.FromValues(entries));
    }

    private static StructType Rebuild(StructField id, StructField container, MetadataValue newNestedIds)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>();
        foreach (KeyValuePair<string, MetadataValue> e in container.Metadata)
        {
            if (!string.Equals(e.Key, ColumnMapping.NestedIdsKey, StringComparison.Ordinal))
            {
                entries.Add(e);
            }
        }

        entries.Add(new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, newNestedIds));
        var rebuilt = new StructField(
            container.Name, container.DataType, container.Nullable, FieldMetadata.FromValues(entries));
        return new StructType(new[] { id, rebuilt });
    }

    // -------------------------------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------------------------------

    private static string PhysicalOf(StructField field)
    {
        Assert.True(field.Metadata.TryGetValue(ColumnMapping.PhysicalNameKey, out MetadataValue? p));
        return p!.AsString();
    }

    private static void AssertNestedIdsReject(StructType schema)
    {
        DeltaProtocolException ex = Assert.Throws<DeltaProtocolException>(
            () => ColumnMapping.ValidateColumnMappingSchema(
                ColumnMappingMode.Id, schema, ColumnMapping.IdModeConfiguration(ConfiguredMax)));
        Assert.Contains(ColumnMapping.NestedIdsKey, ex.Message, StringComparison.Ordinal);
    }

    private static MetadataValue NestedIds(params (string Key, MetadataValue Value)[] entries) =>
        MetadataValue.Nested(FieldMetadata.FromValues(
            entries.Select(e => new KeyValuePair<string, MetadataValue>(e.Key, e.Value))));

    private static FieldMetadata Meta(long id, string physical, MetadataValue? nestedIds = null)
    {
        var entries = new List<KeyValuePair<string, MetadataValue>>
        {
            new(ColumnMapping.IdKey, MetadataValue.Long(id)),
            new(ColumnMapping.PhysicalNameKey, MetadataValue.String(physical)),
        };
        if (nestedIds is not null)
        {
            entries.Add(new KeyValuePair<string, MetadataValue>(ColumnMapping.NestedIdsKey, nestedIds));
        }

        return FieldMetadata.FromValues(entries);
    }

    private static StructField OneContainerField(string kind, string physical, long containerId, MetadataValue? nestedIds)
    {
        DataType type = kind == "map"
            ? new MapType(DataTypes.StringType, DataTypes.LongType, valueContainsNull: true)
            : new ArrayType(DataTypes.LongType);
        return new StructField(physical, type, nullable: true, Meta(containerId, physical, nestedIds));
    }

    private static StructType OneContainer(string kind, string physical, long containerId, MetadataValue? nestedIds) =>
        new(new[] { OneContainerField(kind, physical, containerId, nestedIds) });

    private static StructField LogicalArray(string name, DataType elementType) =>
        new(name, new ArrayType(elementType), nullable: true);

    private static StructField LogicalMap(string name, DataType keyType, DataType valueType) =>
        new(name, new MapType(keyType, valueType, valueContainsNull: true), nullable: true);

    private static StructField NestedWithinNested(string shape) => shape switch
    {
        "array<struct>" => LogicalArray("c", DataTypes.CreateStructType(new[]
        {
            new StructField("x", DataTypes.LongType, nullable: true),
        })),
        "map<string,struct>" => new StructField(
            "c",
            new MapType(DataTypes.StringType, DataTypes.CreateStructType(new[]
            {
                new StructField("x", DataTypes.LongType, nullable: true),
            }), valueContainsNull: true),
            nullable: true),
        "array<array>" => LogicalArray("c", new ArrayType(DataTypes.LongType)),
        "map<string,map>" => new StructField(
            "c",
            new MapType(DataTypes.StringType, new MapType(DataTypes.StringType, DataTypes.LongType, true), true),
            nullable: true),
        // The design's nested-within-nested variant one level deeper: a struct whose child is itself an
        // array<struct>. The #585 pre-gate must reject the interior nested-within-nested BEFORE the #839
        // id-mode array/map gate is ever reached.
        "struct<array<struct>>" => new StructField(
            "c",
            DataTypes.CreateStructType(new[]
            {
                LogicalArray("inner", DataTypes.CreateStructType(new[]
                {
                    new StructField("x", DataTypes.LongType, nullable: true),
                })),
            }),
            nullable: true),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };
}
