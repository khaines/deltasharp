using System.Text;
using System.Text.Json;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Storage.Writing;
using DeltaSharp.Types;
using Parquet;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// The <b>log-side</b> artifact layer for issue #679, and the only place anything compares a real
/// <c>_delta_log</c> <c>metaData.schemaString</c> to a real Parquet footer <c>delta.schema</c>.
/// </summary>
/// <remarks>
/// <para>
/// #679 consolidated the footer serializer onto the shared one, which closes <i>"two
/// implementations disagree"</i>. The artifact guards in <c>ParquetWriterTests</c> then close
/// <i>"one implementation, given less than it was told"</i> and <i>"what we wrote differs from
/// what we declared"</i>. Every one of those keys on the <b>footer</b> call site
/// (<c>ParquetFileWriter</c>), and each compares the footer to a <b>helper invocation</b> of
/// <c>SchemaJson.ToJson</c> made by the test itself.
/// </para>
/// <para>
/// That leaves the original defect's own shape unguarded. <c>DeltaTableWriter</c> calls
/// <c>SchemaJson.ToJson</c> at seven <b>sibling call sites</b> to produce
/// <c>metaData.schemaString</c>, and a transform applied at any of them — the shared serializer
/// left entirely alone, its <i>input</i> narrowed on the way in — reproduces #679's footer/log
/// divergence exactly, with every footer-side guard still green, because no footer-side guard ever
/// looks at the log. Comparing the footer to a helper cannot see it either: the helper is not the
/// log. The fourth guarantee is <i>"the two sides of the same commit disagree"</i>, and only an
/// end-to-end comparison of the two real artifacts can hold it.
/// </para>
/// <para>
/// So these tests go through the public write door, then read both bytes back off disk: the
/// committed log JSON, and the footer of the data file that commit points at. Nothing is
/// re-serialized in between. That is deliberate — a comparison that re-serializes either side is
/// once again comparing an artifact to a helper.
/// </para>
/// </remarks>
public sealed class DeltaFooterLogSchemaParityTests : IDisposable
{
    private readonly string _root;

    public DeltaFooterLogSchemaParityTests() =>
        _root = Path.Combine(Path.GetTempPath(), "delta-footerlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Unpartitioned_FooterSchemaString_IsByteIdenticalToTheCommittedLog()
    {
        StructType schema = HostileSchema();

        await AppendAsync(schema, Array.Empty<string>());

        string footer = await ReadFooterSchema();
        string log = ReadCommittedSchemaString();

        if (!string.Equals(footer, log, StringComparison.Ordinal))
        {
            Assert.Fail(
                "The committed _delta_log schemaString and the data file's footer delta.schema "
                + "disagree -- issue #679's original divergence, at the two REAL artifacts."
                + $"{Environment.NewLine}  log    ({log.Length} chars): {Truncate(log)}"
                + $"{Environment.NewLine}  footer ({footer.Length} chars): {Truncate(footer)}"
                + $"{Environment.NewLine}  first difference at char {FirstDifference(log, footer)}");
        }
    }

    /// <summary>
    /// Pins the one place the two artifacts are legitimately allowed to differ, so that the
    /// equality above cannot be "fixed" by widening it into a rule that hides a real defect.
    /// </summary>
    /// <remarks>
    /// A partitioned table stores partition values in the <c>add</c> action's
    /// <c>partitionValues</c> rather than in the data file, so the footer legitimately declares
    /// FEWER columns than the log. That is measured rather than assumed (issue #724), and stating
    /// it as an exact relationship — footer equals the log's schema minus the partition columns,
    /// same order, byte for byte — means a transform that drops something else from either side
    /// still fails here.
    /// </remarks>
    [Fact]
    public async Task Partitioned_FooterOmitsExactlyThePartitionColumns_AndNothingElse()
    {
        StructType schema = HostileSchema();
        string[] partitionColumns = { schema[0].Name };

        await AppendAsync(schema, partitionColumns);

        string footer = await ReadFooterSchema();
        string log = ReadCommittedSchemaString();

        Assert.NotEqual(log, footer);

        var remainder = new StructType(
            schema.Where(f => !partitionColumns.Contains(f.Name, StringComparer.Ordinal)).ToArray());
        Assert.Equal(SchemaJson.ToJson(remainder), footer);
        Assert.Equal(SchemaJson.ToJson(schema), log);
    }

    /// <summary>
    /// A schema built to make an input-narrowing transform at EITHER call site observable, since
    /// such a transform is the shape that survives every other guard in this PR.
    /// </summary>
    /// <remarks>
    /// The plausible forms are threshold-guarded fast paths — "if this field carries more than N
    /// metadata entries, take the bulk path" — and content filters that drop what the fast path
    /// cannot handle. So a field must carry enough metadata entries to trip such a threshold
    /// (well past the 64 that showed up in the rogues), and the entries must include the content a
    /// bulk path would choke on: non-ASCII keys and values, an astral pair, an empty key, an empty
    /// value, and characters requiring escapes.
    /// <para>
    /// The value kinds are enumerated from <see cref="MetadataValueKind"/> itself rather than
    /// listed, so a kind added later is carried here without anyone remembering to add it.
    /// </para>
    /// </remarks>
    private static StructType HostileSchema()
    {
        var kinds = Enum.GetValues<MetadataValueKind>();
        var bulky = new List<KeyValuePair<string, MetadataValue>>
        {
            new(string.Empty, MetadataValue.String("value under an empty key")),
            new("emptyValue", MetadataValue.String(string.Empty)),
            new("café", MetadataValue.String("caffè")),
            new("ключ", MetadataValue.String("значение")),
            new("astral\U0001F600", MetadataValue.String("\U0001F600")),
            new("quote\"tab\tnewline\n", MetadataValue.String("back\\slash")),
        };

        for (int i = 0; bulky.Count < 96; i++)
        {
            MetadataValueKind kind = kinds[i % kinds.Length];
            bulky.Add(new KeyValuePair<string, MetadataValue>($"pad.é{i:D3}", ValueOfKind(kind, i)));
        }

        return new StructType(new[]
        {
            new StructField("région", DataTypes.StringType, nullable: true, FieldMetadata.FromValues(bulky)),
            new StructField("id", DataTypes.LongType, nullable: false, FieldMetadata.FromValues(new[]
            {
                new KeyValuePair<string, MetadataValue>("delta.columnMapping.id", MetadataValue.Long(1)),
            })),
            new StructField("naïve", DataTypes.StringType, nullable: true),
        });
    }

    private static MetadataValue ValueOfKind(MetadataValueKind kind, int i) => kind switch
    {
        MetadataValueKind.Null => MetadataValue.Null,
        MetadataValueKind.String => MetadataValue.String($"é{i}"),
        MetadataValueKind.Long => MetadataValue.Long(i),
        MetadataValueKind.Double => MetadataValue.Double(i + 0.5),
        MetadataValueKind.Boolean => MetadataValue.Boolean(i % 2 == 0),
        MetadataValueKind.Array => MetadataValue.Array(new[] { MetadataValue.String("é"), MetadataValue.Long(i) }),
        MetadataValueKind.Nested => MetadataValue.Nested(FieldMetadata.FromValues(new[]
        {
            new KeyValuePair<string, MetadataValue>("né", MetadataValue.String("sté")),
        })),
        _ => throw new NotSupportedException(
            $"MetadataValueKind.{kind} was added but this corpus does not build one, so the "
            + "footer/log parity guard would silently stop covering it."),
    };

    private async Task AppendAsync(StructType schema, IReadOnlyList<string> partitionColumns)
    {
        using DeltaWriteTarget target = DeltaWriteTarget.ForLocalPath(_root);

        MutableColumnVector region = ColumnVectors.Create(DataTypes.StringType, 2);
        MutableColumnVector id = ColumnVectors.Create(DataTypes.LongType, 2);
        MutableColumnVector name = ColumnVectors.Create(DataTypes.StringType, 2);
        region.AppendBytes(Encoding.UTF8.GetBytes("west"));
        region.AppendBytes(Encoding.UTF8.GetBytes("west"));
        id.AppendValue(1L);
        id.AppendValue(2L);
        name.AppendBytes(Encoding.UTF8.GetBytes("a"));
        name.AppendBytes(Encoding.UTF8.GetBytes("b"));

        var batch = new ManagedColumnBatch(schema, new ColumnVector[] { region, id, name }, 2);
        await target.AppendAsync(schema, partitionColumns, new[] { batch });
    }

    /// <summary>Reads the <c>delta.schema</c> footer metadata off the committed data file.</summary>
    private async Task<string> ReadFooterSchema()
    {
        string[] files = Directory.GetFiles(_root, "*.parquet", SearchOption.AllDirectories);
        string path = Assert.Single(files);

        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader =
            await ParquetReader.CreateAsync(stream, null, false, CancellationToken.None);
        return reader.CustomMetadata[FooterWireKeys.Schema];
    }

    /// <summary>
    /// Reads <c>metaData.schemaString</c> out of the committed log's RAW BYTES, rather than through
    /// a snapshot object, so nothing between the writer and this assertion can normalize a
    /// difference away.
    /// </summary>
    private string ReadCommittedSchemaString()
    {
        string commit = Path.Combine(_root, "_delta_log", "00000000000000000000.json");
        foreach (string line in File.ReadAllLines(commit))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("metaData", out JsonElement metadata)
                && metadata.TryGetProperty("schemaString", out JsonElement schemaString))
            {
                return schemaString.GetString()!;
            }
        }

        Assert.Fail($"No metaData action was committed to {commit}.");
        return string.Empty;
    }

    private static int FirstDifference(string left, string right)
    {
        int shared = Math.Min(left.Length, right.Length);
        for (int i = 0; i < shared; i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }

        return shared;
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + $"... (+{value.Length - 400} chars)";
}
