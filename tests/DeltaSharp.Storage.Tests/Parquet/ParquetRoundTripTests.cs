using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.TestSupport;
using DeltaSharp.Types;
using Xunit;
using Xunit.Abstractions;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// The key parity oracle: for every supported atomic type — required <b>and</b> nullable — build a
/// batch of random values (including nulls, empty/unicode strings, decimals, timestamps), write it
/// through <see cref="ParquetFileWriter"/>, read it back through <see cref="ParquetFileReader"/>, and
/// assert values, validity, and order are identical.
/// </summary>
public sealed class ParquetRoundTripTests
{
    private readonly SeededRandom _random;

    public ParquetRoundTripTests(ITestOutputHelper output)
    {
        _random = SeededRandom.Create(output);
    }

    public static IEnumerable<object[]> SupportedTypes()
    {
        foreach (DataType type in SupportedAtomicTypes())
        {
            yield return new object[] { type, false };
            yield return new object[] { type, true };
        }
    }

    private static IEnumerable<DataType> SupportedAtomicTypes()
    {
        yield return DataTypes.BooleanType;
        yield return DataTypes.ByteType;
        yield return DataTypes.ShortType;
        yield return DataTypes.IntegerType;
        yield return DataTypes.LongType;
        yield return DataTypes.FloatType;
        yield return DataTypes.DoubleType;
        yield return DataTypes.StringType;
        yield return DataTypes.BinaryType;
        yield return DataTypes.DateType;
        yield return DataTypes.TimestampType;
        yield return DataTypes.CreateDecimalType(9, 2);   // compact (long lane)
        yield return DataTypes.CreateDecimalType(18, 6);  // compact boundary
        yield return DataTypes.CreateDecimalType(28, 4);  // wide (Int128 lane)
    }

    [Theory]
    [MemberData(nameof(SupportedTypes))]
    public async Task RoundTrips_EachSupportedType(DataType type, bool nullable)
    {
        var schema = new StructType(new[] { new StructField("value", type, nullable) });
        ColumnBatch expected = TestData.RandomBatch(schema, rowCount: 256, _random);

        ColumnBatch actual = await WriteThenReadSingleBatchAsync(schema, expected, schema);

        TestData.AssertBatchesEqual(expected, actual);
    }

    [Fact]
    public async Task RoundTrips_AllTypesInOneBatch()
    {
        var fields = new List<StructField>();
        int i = 0;
        foreach (DataType type in SupportedAtomicTypes())
        {
            fields.Add(new StructField($"c{i++}_req", type, nullable: false));
            fields.Add(new StructField($"c{i++}_opt", type, nullable: true));
        }

        var schema = new StructType(fields);
        ColumnBatch expected = TestData.RandomBatch(schema, rowCount: 300, _random);

        ColumnBatch actual = await WriteThenReadSingleBatchAsync(schema, expected, schema);

        TestData.AssertBatchesEqual(expected, actual);
    }

    [Fact]
    public async Task RoundTrips_AcrossMultipleRowGroups()
    {
        var schema = new StructType(new[]
        {
            new StructField("id", DataTypes.LongType, nullable: false),
            new StructField("name", DataTypes.StringType, nullable: true),
        });
        ColumnBatch expected = TestData.RandomBatch(schema, rowCount: 500, _random);

        // Force several row groups so cross-boundary ordering is exercised.
        var writer = new ParquetFileWriter(rowGroupRowLimit: 128);
        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, schema, new[] { expected }, CancellationToken.None);

        ColumnBatch actual = await ReadAllConcatenatedAsync(stream, schema);

        TestData.AssertBatchesEqual(expected, actual);
    }

    private static async Task<ColumnBatch> WriteThenReadSingleBatchAsync(
        StructType writeSchema, ColumnBatch batch, StructType readSchema)
    {
        var writer = new ParquetFileWriter();
        using var stream = new MemoryStream();
        await writer.WriteAsync(stream, writeSchema, new[] { batch }, CancellationToken.None);
        return await ReadAllConcatenatedAsync(stream, readSchema);
    }

    private static async Task<ColumnBatch> ReadAllConcatenatedAsync(MemoryStream stream, StructType readSchema)
    {
        stream.Position = 0;
        var reader = new ParquetFileReader();
        var batches = new List<ColumnBatch>();
        await foreach (ColumnBatch batch in reader.ReadAsync(stream, readSchema, keepRowGroup: null, nullFillMissingColumns: false, CancellationToken.None))
        {
            batches.Add(batch);
        }

        return Concatenate(readSchema, batches);
    }

    private static ColumnBatch Concatenate(StructType schema, IReadOnlyList<ColumnBatch> batches)
    {
        int totalRows = batches.Sum(b => b.LogicalRowCount);
        var columns = new ColumnVector[schema.Count];
        for (int c = 0; c < schema.Count; c++)
        {
            MutableColumnVector vector = ColumnVectors.Create(schema[c].DataType, Math.Max(totalRows, 1));
            foreach (ColumnBatch batch in batches)
            {
                ColumnVector source = batch.SelectedColumn(c);
                for (int r = 0; r < batch.LogicalRowCount; r++)
                {
                    AppendCell(vector, schema[c].DataType, source, r);
                }
            }

            columns[c] = vector;
        }

        return new ManagedColumnBatch(schema, columns, totalRows);
    }

    private static void AppendCell(MutableColumnVector target, DataType type, ColumnVector source, int row)
    {
        if (source.IsNull(row))
        {
            target.AppendNull();
            return;
        }

        switch (type)
        {
            case StringType:
            case BinaryType:
                target.AppendBytes(source.GetBytes(row));
                break;
            case BooleanType:
                target.AppendValue(source.GetValue<bool>(row));
                break;
            case ByteType:
                target.AppendValue(source.GetValue<byte>(row));
                break;
            case ShortType:
                target.AppendValue(source.GetValue<short>(row));
                break;
            case IntegerType or DateType:
                target.AppendValue(source.GetValue<int>(row));
                break;
            case LongType or TimestampType:
                target.AppendValue(source.GetValue<long>(row));
                break;
            case FloatType:
                target.AppendValue(source.GetValue<float>(row));
                break;
            case DoubleType:
                target.AppendValue(source.GetValue<double>(row));
                break;
            case DecimalType { IsCompact: true }:
                target.AppendValue(source.GetValue<long>(row));
                break;
            case DecimalType:
                target.AppendValue(source.GetValue<Int128>(row));
                break;
            default:
                throw new NotSupportedException($"No concatenation support for '{type.SimpleString}'.");
        }
    }
}
