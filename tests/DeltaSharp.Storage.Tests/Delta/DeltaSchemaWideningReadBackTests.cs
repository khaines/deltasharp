using System.Collections.Generic;
using System.Linq;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Read-side type-widening <b>promotion</b> teeth (#495). #190/FIX-1 originally fail-closed logical widening
/// because <see cref="Parquet.ParquetFileReader"/> bound a requested column to the file's <b>exact physical
/// CLR type</b> — an <c>Int32</c> file was unreadable under a widened <c>long</c> schema. With the Delta
/// <c>typeWidening</c> table feature (#495) the reader now <b>promotes</b> a narrower physical type to the
/// current wide type per Delta PROTOCOL.md "Reader Requirements for Type Widening": "Readers must allow
/// reading data files written before the table underwent any supported type change, and must convert such
/// values to the current, wider type."
///
/// <para>This pins that an OLD narrow file reads back with its values promoted into the wide vector. The
/// enforcement teeth (widening applied on write + <c>delta.typeChanges</c> emitted) live in
/// <see cref="DeltaSchemaEnforcerTests"/>/<see cref="DeltaSchemaEvolutionWriterTests"/>; the exhaustive
/// per-width promotion matrix lives in <c>ParquetTypeWideningPromotionTests</c>.</para>
/// </summary>
public sealed class DeltaSchemaWideningReadBackTests
{
    [Fact]
    public async Task WidenedReadBack_OfInt32FileUnderLongSchema_PromotesValues()
    {
        // Write a plain Int32 column — the physical layout an int-typed table produces before widening.
        var writtenSchema = new StructType(new[] { new StructField("value", DataTypes.IntegerType, nullable: false) });
        MutableColumnVector column = ColumnVectors.Create(DataTypes.IntegerType, capacity: 3);
        column.AppendValue(1);
        column.AppendValue(2);
        column.AppendValue(3);
        var batch = new ManagedColumnBatch(writtenSchema, new ColumnVector[] { column }, rowCount: 3);
        byte[] bytes = await ParquetTestHelpers.WriteToBytesAsync(writtenSchema, new[] { batch });

        // Reading the SAME file under a widened `long` schema (what int->long evolution installs as the table
        // schema) now SUCCEEDS: the reader reads the narrow Int32 values and promotes them into a long vector.
        var widenedSchema = new StructType(new[] { new StructField("value", DataTypes.LongType, nullable: false) });
        List<ColumnBatch> promoted = await ParquetTestHelpers.ReadAllAsync(bytes, widenedSchema);

        ColumnVector vector = promoted.Single().Column(0);
        Assert.Equal(DataTypes.LongType, vector.Type);
        Assert.Equal(new long[] { 1L, 2L, 3L }, vector.GetValues<long>().ToArray());

        // Control: reading it back under its own (un-widened) schema still succeeds.
        List<ColumnBatch> ok = await ParquetTestHelpers.ReadAllAsync(bytes, writtenSchema);
        Assert.Equal(3, ok.Single().RowCount);
        Assert.Equal(new[] { 1, 2, 3 }, ok.Single().Column(0).GetValues<int>().ToArray());
    }
}
