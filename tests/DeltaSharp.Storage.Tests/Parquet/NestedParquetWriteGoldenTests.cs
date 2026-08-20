using System.Security.Cryptography;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #843 — a byte-level <b>SHA-256 golden</b> per in-scope nested Parquet write shape, plus a Parquet.Net
/// <b>version-bump regression gate</b>. The nested-write round-trip oracle (#841) and the wire-level def/rep
/// differential validate the reader-visible <i>structure</i>, but nothing in the suite notices a
/// compression / encoding / <c>created_by</c> / footer-metadata drift when the pinned Parquet.Net dependency
/// is upgraded — precisely the hazard that broke the read path on the 6.0.3 → 6.1.0 bump (#832/#837). This
/// test pins the exact emitted bytes: the freshly-written file (at a fixed row-group limit + fixed model) must
/// hash to the committed golden, so any byte drift fails a test rather than shipping silently.
/// </summary>
/// <remarks>
/// <para><b>Determinism.</b> DeltaSharp's writer is byte-identical for a fixed model at a fixed
/// <see cref="ParquetFileWriter.RowGroupRowLimit"/> (pinned by
/// <c>NestedParquetWriteTests.NestedWrite_IsByteIdentical_AcrossRepeatedWrites</c>), so a stable golden is
/// well-defined.</para>
/// <para><b>Regeneration (reviewed).</b> A legitimate encoding change (or a sanctioned Parquet.Net bump) is
/// expected to change these hashes. To regenerate, run with <c>DELTASHARP_REGEN_WRITE_GOLDENS=1</c>: the test
/// emits the new <c>(shape → sha256)</c> constants to the test output, which are then pasted into
/// <see cref="Goldens"/> in a reviewed commit. The regeneration is deliberate and diff-visible — it is NOT an
/// auto-heal. See <c>docs/engineering/parquet-net-upgrade-checklist.md</c>.</para>
/// </remarks>
public sealed class NestedParquetWriteGoldenTests
{
    /// <summary>Set to <c>1</c> to emit fresh golden hashes to the test output instead of asserting.</summary>
    private const string RegenEnvVar = "DELTASHARP_REGEN_WRITE_GOLDENS";

    // A FIXED row-group limit so the golden is stable and small (multiple row groups exercise the
    // boundary-emitting path). Changing this value changes every golden and requires regeneration.
    private const int GoldenRowGroupRowLimit = 2;

    private static readonly StructType StructShape = DataTypes.CreateStructType(new[]
    {
        new StructField("s", DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        }), nullable: true),
    });

    private static readonly StructType ArrayShape = DataTypes.CreateStructType(new[]
    {
        new StructField("arr", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
    });

    private static readonly StructType MapShape = DataTypes.CreateStructType(new[]
    {
        new StructField("m", DataTypes.CreateMapType(DataTypes.StringType, DataTypes.IntegerType), nullable: true),
    });

    private static readonly StructType MixedShape = DataTypes.CreateStructType(new[]
    {
        new StructField("id", DataTypes.LongType, nullable: false),
        new StructField("s", DataTypes.CreateStructType(new[]
        {
            new StructField("a", DataTypes.IntegerType, nullable: true),
            new StructField("b", DataTypes.StringType, nullable: true),
        }), nullable: true),
        new StructField("tags", DataTypes.CreateArrayType(DataTypes.IntegerType), nullable: true),
    });

    // The committed goldens — the SHA-256 (lowercase hex) of the bytes each shape's fixed model writes at
    // GoldenRowGroupRowLimit. Regenerate with DELTASHARP_REGEN_WRITE_GOLDENS=1 (reviewed) on a sanctioned
    // encoding/dependency change.
    private static readonly IReadOnlyDictionary<string, string> Goldens = new Dictionary<string, string>
    {
        ["struct<scalars>"] = "db5df2be167025e592b8dda6d93b87eae5625e8b1e6ccb72174e82130fe853fb",
        ["array<scalar>"] = "242a0e0cd1266d274a7665fbbb0f9defa4c49130057a3ddb3581ea11b84c9f45",
        ["map<scalar,scalar>"] = "81934ab0615a472fb678305669c11f422e353550c3f5e1670206311792e4b36d",
        ["mixed<scalar,struct,array>"] = "2fca4cd6979dd9000a7533577716a22f7427da38cf54db064c77c0aaac13e1a0",
    };

    private readonly ITestOutputHelper _output;

    public NestedParquetWriteGoldenTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("struct<scalars>")]
    [InlineData("array<scalar>")]
    [InlineData("map<scalar,scalar>")]
    [InlineData("mixed<scalar,struct,array>")]
    public async Task NestedWrite_MatchesByteGolden(string shape)
    {
        (StructType schema, ColumnBatch batch) = ModelFor(shape);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch }, GoldenRowGroupRowLimit);
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (IsRegen)
        {
            _output.WriteLine($"[golden] [\"{shape}\"] = \"{actual}\",  // len={bytes.Length}");
            return;
        }

        Assert.True(
            Goldens.TryGetValue(shape, out string? golden) && golden != "__REGEN__",
            $"No committed golden for shape '{shape}'. Run with {RegenEnvVar}=1 to emit one, then commit it.");
        Assert.True(
            string.Equals(actual, golden, StringComparison.Ordinal),
            $"Nested-write bytes for shape '{shape}' drifted from the committed golden.\n"
            + $"  expected: {golden}\n  actual:   {actual}\n"
            + $"A Parquet.Net version bump or an encoding change likely altered the emitted bytes. If the change "
            + $"is intentional, regenerate with {RegenEnvVar}=1 and commit the new hash (reviewed).");
    }

    private static bool IsRegen =>
        string.Equals(Environment.GetEnvironmentVariable(RegenEnvVar), "1", StringComparison.Ordinal);

    private static (StructType Schema, ColumnBatch Batch) ModelFor(string shape)
    {
        switch (shape)
        {
            case "struct<scalars>":
                {
                    var inner = (StructType)StructShape["s"].DataType;
                    var rows = new (int?, string?)?[] { (1, "one"), null, (null, "two"), (3, null) };
                    StructColumnVector v = NestedVectors.IntStringStruct(inner, rows);
                    return (StructShape, new ManagedColumnBatch(StructShape, new ColumnVector[] { v }, rows.Length));
                }

            case "array<scalar>":
                {
                    var rows = new int?[]?[] { new int?[] { 1, null, 3 }, null, Array.Empty<int?>(), new int?[] { 4 } };
                    ListColumnVector v = NestedVectors.IntList((ArrayType)ArrayShape["arr"].DataType, rows);
                    return (ArrayShape, new ManagedColumnBatch(ArrayShape, new ColumnVector[] { v }, rows.Length));
                }

            case "map<scalar,scalar>":
                {
                    var rows = new IReadOnlyList<(string, int?)>?[]
                    {
                    new[] { ("k1", (int?)1), ("k2", (int?)null) },
                    null,
                    Array.Empty<(string, int?)>(),
                    new[] { ("k3", (int?)3) },
                    };
                    MapColumnVector v = NestedVectors.StringIntMap((MapType)MapShape["m"].DataType, rows);
                    return (MapShape, new ManagedColumnBatch(MapShape, new ColumnVector[] { v }, rows.Length));
                }

            case "mixed<scalar,struct,array>":
                {
                    var inner = (StructType)MixedShape["s"].DataType;
                    var structRows = new (int?, string?)?[] { (10, "a"), null, (null, "c"), (30, null) };
                    var arrRows = new int?[]?[] { new int?[] { 11, 12 }, null, Array.Empty<int?>(), new int?[] { 13 } };
                    MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 4);
                    foreach (long value in new long[] { 1000, 1001, 1002, 1003 })
                    {
                        id.AppendValue(value);
                    }

                    StructColumnVector s = NestedVectors.IntStringStruct(inner, structRows);
                    ListColumnVector tags = NestedVectors.IntList((ArrayType)MixedShape["tags"].DataType, arrRows);
                    return (MixedShape, new ManagedColumnBatch(MixedShape, new ColumnVector[] { id, s, tags }, 4));
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown golden shape");
        }
    }
}
