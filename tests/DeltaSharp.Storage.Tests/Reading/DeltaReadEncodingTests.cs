using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Reading;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Reading;

/// <summary>
/// Unit tests for <see cref="DeltaReadEncoding.BuildConstantColumn"/>, the read-side inverse that
/// const/null-fills a partition column from its canonical <c>add.partitionValues</c> string (#499).
/// Focus: the Hive default-partition sentinel (<see cref="DeltaWriteEncoding.HiveDefaultPartition"/>) is
/// treated as NULL — a cross-engine robustness requirement so a foreign/non-canonical writer that records
/// the sentinel string literally on a <b>typed</b> (int/long/date) partition column does NOT crash the read
/// with an out-of-range parse error.
/// </summary>
public sealed class DeltaReadEncodingTests
{
    [Theory]
    [InlineData("integer")]
    [InlineData("long")]
    [InlineData("date")]
    public void BuildConstantColumn_HiveSentinel_OnTypedPartition_FillsNull_NoThrow(string typeName)
    {
        DataType type = TypeFor(typeName);

        // The exact sentinel a foreign writer may store literally in add.partitionValues.
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(
            type, DeltaWriteEncoding.HiveDefaultPartition, rowCount: 3);

        Assert.True(column.HasNulls);
        for (int r = 0; r < 3; r++)
        {
            Assert.True(column.IsNull(r), $"row {r} of a sentinel-valued {typeName} partition must be null");
        }
    }

    [Fact]
    public void BuildConstantColumn_JsonNull_FillsNull()
    {
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(IntegerType.Instance, value: null, rowCount: 2);

        Assert.True(column.IsNull(0));
        Assert.True(column.IsNull(1));
    }

    [Fact]
    public void BuildConstantColumn_NormalIntegerValue_StillParses()
    {
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(IntegerType.Instance, "42", rowCount: 2);

        Assert.False(column.HasNulls);
        Assert.Equal(42, column.GetValue<int>(0));
        Assert.Equal(42, column.GetValue<int>(1));
    }

    [Fact]
    public void BuildConstantColumn_IntPartitionValue_UnderWidenedLongType_ParsesAsLong_Issue537()
    {
        // #537: an intra-family partition-column widening (int→long) is metadata-only — the partition value
        // stays the SAME canonical STRING ("5") in add.partitionValues, and the read door const-fills it
        // under the field's now-WIDENED type (LongType). Older rows written when the column was int therefore
        // read back promoted to long, WITHOUT any data-file rewrite. This pins that BuildConstantColumn
        // parses the (unchanged) int-era partition string "5" correctly into a long lane.
        ColumnVector column = DeltaReadEncoding.BuildConstantColumn(LongType.Instance, "5", rowCount: 3);

        Assert.Equal(LongType.Instance, column.Type);
        Assert.False(column.HasNulls);
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(5L, column.GetValue<long>(r));
        }
    }

    [Fact]
    public void BuildConstantColumn_NormalStringValue_MatchingSentinelText_IsNull_ButOtherStringParses()
    {
        // A genuine (non-sentinel) string value round-trips as data...
        ColumnVector normal = DeltaReadEncoding.BuildConstantColumn(StringType.Instance, "US", rowCount: 1);
        Assert.False(normal.IsNull(0));

        // ...and the sentinel on a string partition is still null (the read never materializes the literal).
        ColumnVector sentinel = DeltaReadEncoding.BuildConstantColumn(
            StringType.Instance, DeltaWriteEncoding.HiveDefaultPartition, rowCount: 1);
        Assert.True(sentinel.IsNull(0));
    }

    [Theory]
    [InlineData("integer")]   // routes through ParseInteger's out-of-range throw
    [InlineData("long")]      // routes through the shared ParseFailure helper
    public void BuildConstantColumn_UnparsablePartitionValue_FailsClosedWithoutEchoingValue(string typeName)
    {
        // Message hygiene (#653 / obs-conventions §row-values): a partition value is user/attacker data recorded
        // in add.partitionValues, so a parse failure must NOT echo it into the surfaced .Message. A crafted
        // non-numeric sentinel on a typed partition column proves no-echo on BOTH parse paths.
        const string sentinel = "att4cker-p4rtition-s3ntinel";
        DataType type = TypeFor(typeName);

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => DeltaReadEncoding.BuildConstantColumn(type, sentinel, rowCount: 3));

        Assert.Equal(StorageErrorKind.CorruptData, ex.Kind);
        Assert.DoesNotContain(sentinel, ex.Message, StringComparison.Ordinal);   // the value is never surfaced
        Assert.Contains("Partition value", ex.Message, StringComparison.Ordinal);   // fixed diagnostic prefix
    }

    [Theory]
    [InlineData(null)]                             // JSON null — the value-conditional ESCAPE (Security seat, #685)
    [InlineData("__HIVE_DEFAULT_PARTITION__")]     // Hive sentinel — same null arm, same escape
    [InlineData("some-value")]                     // non-null — the arm that was already guarded inside FillTyped
    public void BuildConstantColumn_NonScalarPartitionType_FailsClosedBounded_NoNestedNameLeak(string? value)
    {
        // #685 Critical (Security seat, executed end-to-end). A FOREIGN metaData.schemaString can declare a
        // partition column of a NON-SCALAR type (struct/array/map). The unsupported-type guard used to live
        // inside FillTyped, which is reached ONLY for a non-null value — so a hostile table recording null or
        // __HIVE_DEFAULT_PARTITION__ in add.partitionValues bypassed it, hit ColumnVectors.Create(struct), and
        // its AppendNull threw a RAW, unbounded, injection-bearing child-shape message carrying the attacker-
        // authored nested field NAMES (DeltaSharp.Engine.Columnar cannot see DiagnosticText). The guard now runs
        // at BuildConstantColumn's ENTRY, before ColumnVectors.Create and the null short-circuit, so EVERY value
        // arm fails closed with the same bounded type-name message and no nested field name is ever rendered.
        const string poison = "secret\r\n[CRITICAL] forged\u2028\u202Ename";
        var structType = new StructType(new[] { new StructField(poison, IntegerType.Instance, nullable: true) });

        DeltaStorageException ex = Assert.Throws<DeltaStorageException>(
            () => DeltaReadEncoding.BuildConstantColumn(structType, value, rowCount: 3));

        Assert.Equal(StorageErrorKind.UnsupportedFeature, ex.Kind);
        Assert.Contains("not supported as a Delta partition column", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);        // no nested field name (any form)
        Assert.DoesNotContain("forged", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', ex.Message);                                       // no CR/LF injection
        Assert.DoesNotContain('\u2028', ex.Message);
        Assert.DoesNotContain('\u202E', ex.Message);                                   // no bidi override
        Assert.True(ex.Message.Length < 200, "bounded type-name message, not the unbounded raw child-shape throw");
    }

    private static DataType TypeFor(string name) => name switch
    {
        "integer" => IntegerType.Instance,
        "long" => LongType.Instance,
        "date" => DateType.Instance,
        _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "unknown type"),
    };
}
