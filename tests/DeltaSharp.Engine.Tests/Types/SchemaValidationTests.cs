using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Types;

public class SchemaValidationTests
{
    [Fact]
    public void Struct_RejectsDuplicateFieldNames_WithPreciseMessage()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new StructType(new[]
            {
                new StructField("id", IntegerType.Instance),
                new StructField("name", StringType.Instance),
                new StructField("id", LongType.Instance),
            }));

        Assert.Contains("id", ex.Message);
        Assert.Contains("0", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    // #705/#707 (Round 4): the duplicate-name throw renders DiagnosticText.Sanitize(field.Name), NOT the raw
    // name — the same rule the sibling TypeCoercionException.ForPath pins hold for the coercion message (see
    // TypeCoercionTests). The producer is reachable from Snapshot.ParseSchema → SchemaJson.FromJson over an
    // attacker-influenceable metaData.schemaString in _delta_log, and it sits OUTSIDE the Storage
    // source-scan guard's root, so only a behavioural pin can catch a silent revert to '{field.Name}'.
    [Fact]
    public void Struct_DuplicateFieldName_MessageEchoesSanitizedName_NotTheRawOne()
    {
        // A duplicated name carrying CRLF (log injection) and an oversized run (unbounded log line).
        string hostile = "dup\r\nINJECTED" + new string('x', 200);

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new StructType(new[]
            {
                new StructField(hostile, IntegerType.Instance),
                new StructField(hostile, LongType.Instance),
            }));

        // The rule restated independently of the primitive: every control character becomes U+FFFD and the
        // token is capped at 128 retained characters with a trailing ellipsis.
        const string SanitizedHead = "dup\uFFFD\uFFFDINJECTED";
        string expected = SanitizedHead + new string('x', 128 - SanitizedHead.Length) + "…";

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 200), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);

        // The diagnostic value of the message — which two positions collided — survives sanitization.
        Assert.Contains("0 and 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Struct_AllowsCaseDifferingFieldNames()
    {
        // Spark parity: case-only ambiguity is a name-resolution concern, not a type error.
        var type = new StructType(new[]
        {
            new StructField("id", IntegerType.Instance),
            new StructField("ID", LongType.Instance),
        });

        Assert.Equal(2, type.Count);
    }

    [Fact]
    public void Map_RejectsNullTypeKey()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(NullType.Instance, IntegerType.Instance));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_RejectsMapTypeKey()
    {
        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(new MapType(StringType.Instance, IntegerType.Instance), IntegerType.Instance));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // #705/#707 (Round 4): the unsupported-key throw renders the bounded keyType.TypeName (a fixed literal
    // such as 'map'/'void'), NOT keyType.SimpleString — which recursively walks nested StructType field names
    // and would echo a foreign name, control characters and all, into the message. Same adjudication as the
    // sibling TypeCoercionException.ForPath pins: bounded KIND on the message, exact type off it.
    [Fact]
    public void Map_UnsupportedKeyType_MessageEchoesBoundedTypeName_NotRecursiveFieldNames()
    {
        var hostileKey = new MapType(
            StringType.Instance,
            new StructType(new[] { new StructField("ssn_col\r\nINJECTED", StringType.Instance) }));

        // Non-vacuity: SimpleString really does carry the nested foreign field name (and its CRLF), so the
        // assertions below discriminate between the two renderings rather than restating a tautology.
        Assert.Contains("ssn_col\r\nINJECTED", hostileKey.SimpleString, StringComparison.Ordinal);

        SchemaValidationException ex = Assert.Throws<SchemaValidationException>(() =>
            new MapType(hostileKey, IntegerType.Instance));

        Assert.Contains("'map'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ssn_col", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.True(ex.Message.Length < 256, $"message was {ex.Message.Length} chars; expected bounded.");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(39, 0)]
    [InlineData(-1, 0)]
    public void Decimal_RejectsPrecisionOutOfRange(int precision, int scale)
    {
        Assert.Throws<SchemaValidationException>(() => new DecimalType(precision, scale));
    }

    [Theory]
    [InlineData(10, -1)]
    [InlineData(10, 11)]
    public void Decimal_RejectsScaleOutOfRange(int precision, int scale)
    {
        Assert.Throws<SchemaValidationException>(() => new DecimalType(precision, scale));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(38, 38)]
    [InlineData(10, 2)]
    public void Decimal_AcceptsValidPrecisionAndScale(int precision, int scale)
    {
        var type = new DecimalType(precision, scale);
        Assert.Equal(precision, type.Precision);
        Assert.Equal(scale, type.Scale);
    }

    [Fact]
    public void StructField_RejectsNullOrEmptyName()
    {
        Assert.Throws<ArgumentNullException>(() => new StructField(null!, IntegerType.Instance));
        Assert.Throws<ArgumentException>(() => new StructField(string.Empty, IntegerType.Instance));
    }

    [Fact]
    public void StructField_RejectsNullType()
    {
        Assert.Throws<ArgumentNullException>(() => new StructField("x", null!));
    }

    [Fact]
    public void Array_RejectsNullElementType()
    {
        Assert.Throws<ArgumentNullException>(() => new ArrayType(null!));
    }

    [Fact]
    public void Map_RejectsNullKeyOrValueType()
    {
        Assert.Throws<ArgumentNullException>(() => new MapType(null!, IntegerType.Instance));
        Assert.Throws<ArgumentNullException>(() => new MapType(StringType.Instance, null!));
    }

    [Fact]
    public void Struct_RejectsNullFieldsArgument()
    {
        Assert.Throws<ArgumentNullException>(() => new StructType(null!));
    }

    [Fact]
    public void Metadata_RejectsNullKeyOrValue()
    {
        // A null key/value is an ArgumentNullException (consistent with MetadataValue.Array and
        // BCL dictionary null-key handling); ArgumentNullException derives from ArgumentException,
        // so callers catching the base type are unaffected.
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>(null!, "v") }));
        Assert.Throws<ArgumentNullException>(() =>
            FieldMetadata.FromEntries(new[] { new KeyValuePair<string, string>("k", null!) }));
    }
}
