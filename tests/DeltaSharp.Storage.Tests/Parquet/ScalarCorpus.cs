using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// The scalar type corpus and its row builder, shared by the FOOTER-side artifact tests
/// (<c>ParquetWriterTests</c>) and the LOG-side parity tests
/// (<c>DeltaFooterLogSchemaParityTests</c>).
/// </summary>
/// <remarks>
/// <para>
/// This exists because the log-side parity guard was keyed on a single hand-written schema of
/// three fields and two types, so any divergence conditioned on a type it did not contain -- a
/// fast path taken only for fixed-width schemas, say, or one that mishandles the decimal family --
/// was unreachable there while being well covered on the footer side. Two seats reported that
/// independently.
/// </para>
/// <para>
/// It is SHARED rather than copied deliberately. Restating the corpus in the parity test would
/// have reproduced, in this PR's own test suite, exactly the defect the PR removes from the
/// product: two lists that must agree, drifting apart with nothing to say so. Sharing it also
/// means the parity guard inherits
/// <c>ScalarArtifactCorpus_CoversEveryTypeTheWriterAccepts</c>, which derives the writer's accepted
/// type set by attempting a real write of each candidate -- so a newly accepted type widens BOTH
/// sides at once and cannot silently drop out of either.
/// </para>
/// <para>
/// Note the direction: this is test-owned input, supplied to the code under test. It is not used
/// to compute any expectation about what the writer should emit, so it sits OUTSIDE both the
/// prober and the probed rather than between them.
/// </para>
/// </remarks>
internal static class ScalarCorpus
{
    /// <summary>
    /// Every atomic type <c>ParquetTypeMapping.CreateField</c> accepts, plus the decimal parameter
    /// family at its boundaries.
    /// </summary>
    internal static readonly StructType Schema = new(new[]
    {
        new StructField("c_bool", DataTypes.BooleanType, nullable: false),
        new StructField("c_byte", DataTypes.ByteType, nullable: true),
        new StructField("c_short", DataTypes.ShortType, nullable: false),
        new StructField("c_int", DataTypes.IntegerType, nullable: true),
        new StructField("c_long", DataTypes.LongType, nullable: false),
        new StructField("c_float", DataTypes.FloatType, nullable: true),
        new StructField("c_double", DataTypes.DoubleType, nullable: false),
        new StructField("c_string", DataTypes.StringType, nullable: true),
        new StructField("c_binary", DataTypes.BinaryType, nullable: false),
        new StructField("c_date", DataTypes.DateType, nullable: true),
        new StructField("c_ts", DataTypes.TimestampType, nullable: false),
        new StructField("c_ts_ntz", DataTypes.TimestampNtzType, nullable: true),
        // decimal(p,s) is a parameterised FAMILY, and p/s are protocol-visible in schemaString, so
        // pinning one member pins almost nothing. These are its boundaries: minimum precision,
        // a mid-range value, zero scale, maximum supported precision (28 ==
        // ParquetTypeMapping.MaxSupportedDecimalPrecision), and scale == precision.
        new StructField("c_dec_min", DataTypes.CreateDecimalType(1, 0), nullable: true),
        new StructField("c_dec_mid", DataTypes.CreateDecimalType(9, 2), nullable: false),
        new StructField("c_dec_int", DataTypes.CreateDecimalType(10, 0), nullable: true),
        new StructField("c_dec_max", DataTypes.CreateDecimalType(28, 7), nullable: false),
        new StructField("c_dec_scale", DataTypes.CreateDecimalType(28, 28), nullable: true),
    });

    /// <summary>Appends one legal, non-null value of <paramref name="type"/>.</summary>
    /// <remarks>
    /// The default arm FAILS CLOSED, so a newly accepted writer type cannot quietly drop out of
    /// any caller's coverage: it becomes a failing test rather than a silently narrower corpus.
    /// </remarks>
    internal static void AppendOne(MutableColumnVector vector, DataType type)
    {
        switch (type)
        {
            case BooleanType:
                vector.AppendValue(true);
                break;
            case ByteType:
                // NOT sbyte. This arm was dead until this helper was shared: its only previous
                // caller appended a null for every nullable field, and c_byte is nullable, so the
                // wrong overload never ran. A fail-closed default arm proves a type is LISTED, not
                // that the value it builds is legal -- only executing it does that.
                vector.AppendValue((byte)1);
                break;
            case ShortType:
                vector.AppendValue((short)1);
                break;
            case IntegerType:
            case DateType:
                vector.AppendValue(1);
                break;
            case LongType:
            case TimestampType:
            case TimestampNtzType:
                vector.AppendValue(1L);
                break;
            case DecimalType { IsCompact: true }:
                vector.AppendValue(1L);
                break;
            case DecimalType:
                // Non-compact decimals are backed by Int128, not Int64 (ColumnVectors.Create).
                // The second dead arm this sharing exposed: the previous caller only ever built a
                // decimal value for the compact members of the family.
                vector.AppendValue((Int128)1);
                break;
            case FloatType:
                vector.AppendValue(1f);
                break;
            case DoubleType:
                vector.AppendValue(1d);
                break;
            case StringType:
            case BinaryType:
                vector.AppendBytes(new byte[] { 1 });
                break;
            default:
                Assert.Fail(
                    $"No value is constructible here for {type.SimpleString}, so a schema using it "
                    + "would silently drop out of the corpus. Add an arm.");
                break;
        }
    }
}
