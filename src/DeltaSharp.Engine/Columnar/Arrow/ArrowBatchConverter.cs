using Apache.Arrow;
using DeltaSharp.Engine.Types;
using ArrowField = Apache.Arrow.Field;
using ArrowSchema = Apache.Arrow.Schema;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// Converts between Apache Arrow <see cref="RecordBatch"/>es and the DeltaSharp
/// <see cref="ColumnBatch"/> contract (STORY-02.2.2, #136) — the batch-level "Arrow at the edges"
/// boundary from ADR-0002. It is the single seam a Parquet/Flight reader or writer crosses:
/// everything upstream speaks <c>Apache.Arrow</c>, everything downstream binds to
/// <see cref="ColumnBatch"/>/<see cref="ColumnVector"/>, and the conversion preserves schema, values,
/// validity, and (for zero-copy columns) physical offset so a round trip is identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Import (<see cref="FromArrow"/>)</b> is zero-copy for the broad set of supported primitive,
/// string/binary, and nested types (offset and validity preserved), and materializes only the two
/// types whose physical layout differs from DeltaSharp's — bit-packed <c>boolean</c> and
/// <c>decimal128</c> — copying them in logical row order (offset resets to <c>0</c>; values and
/// validity preserved). Ownership of the source buffers is governed by <see cref="ArrowImportOwnership"/>.
/// </para>
/// <para>
/// <b>Export (<see cref="ToArrow"/>)</b> reads through the logical <see cref="ColumnVector"/> contract
/// (so slices and selections export correctly) and emits fresh managed buffers for flat columns, so
/// the returned <see cref="RecordBatch"/> independently owns its memory; nested columns are passed
/// through with an independent retained reference. The Arrow field type is taken from the built
/// array, capturing decimal precision/scale and the microsecond timestamp unit.
/// </para>
/// <para>
/// Anything outside the v1 capability matrix (unsigned/half-float, non-microsecond timestamps,
/// <c>date64</c>/<c>time</c>/<c>decimal256</c>, the null type, large/view layouts) raises
/// <see cref="UnsupportedTypeException"/> with the exact gap named — never a silent coercion. See
/// <c>docs/engineering/design/arrow-backed-vector.md</c> for the full matrix and residual caveats.
/// </para>
/// </remarks>
public static class ArrowBatchConverter
{
    /// <summary>
    /// Imports an Arrow <paramref name="recordBatch"/> as a <see cref="ColumnBatch"/>, wrapping each
    /// column zero-copy where the layout matches and materializing boolean/decimal columns otherwise.
    /// The DeltaSharp schema mirrors the Arrow field names and nullability; each column's logical type
    /// is the wrapped vector's type.
    /// </summary>
    /// <param name="recordBatch">The Arrow batch to import.</param>
    /// <param name="ownership">
    /// Who owns the source buffers (default <see cref="ArrowImportOwnership.Borrowed"/>); see
    /// <see cref="ArrowImportOwnership"/> for the disposal contract.
    /// </param>
    /// <returns>
    /// A disposable <see cref="ArrowColumnBatch"/> over the imported columns. Dispose it to release the
    /// source under <see cref="ArrowImportOwnership.Transfer"/>; under
    /// <see cref="ArrowImportOwnership.Borrowed"/> the caller still owns and must dispose the source.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="recordBatch"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">A column's Arrow type has no v1 DeltaSharp mapping.</exception>
    public static ArrowColumnBatch FromArrow(
        RecordBatch recordBatch, ArrowImportOwnership ownership = ArrowImportOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(recordBatch);

        int columnCount = recordBatch.ColumnCount;
        int rowCount = recordBatch.Length;
        var columns = new ColumnVector[columnCount];
        var fields = new List<StructField>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            ColumnVector vector = ArrowColumnReader.WrapColumn(recordBatch.Column(i));
            ArrowField field = recordBatch.Schema.GetFieldByIndex(i);

            // The DeltaSharp field type is the wrapped vector's type, so the composed batch's schema
            // and its columns agree by construction (ManagedColumnBatch validates this invariant).
            fields.Add(new StructField(field.Name, vector.Type, field.IsNullable));
            columns[i] = vector;
        }

        var schema = new StructType(fields);
        var inner = new ManagedColumnBatch(schema, columns, rowCount);
        return new ArrowColumnBatch(inner, recordBatch, ownership);
    }

    /// <summary>
    /// Exports a <paramref name="batch"/> as an Arrow <see cref="RecordBatch"/> that independently owns
    /// its buffers. Any <see cref="ColumnBatch.Selection"/> is applied (logical rows are exported in
    /// selection order), validity and values are preserved, and the Arrow field type/nullability mirror
    /// the batch schema.
    /// </summary>
    /// <param name="batch">The batch to export (managed, Arrow-backed, sliced, or selected).</param>
    /// <returns>A new <see cref="RecordBatch"/> the caller owns and should dispose.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The batch carries a <see cref="ColumnBatch.Selection"/> and a nested (struct/list/map) column;
    /// a selection over a nested column is not exportable in v1 (materialize it first).
    /// </exception>
    /// <exception cref="UnsupportedTypeException">A column's logical type has no v1 Arrow export.</exception>
    public static RecordBatch ToArrow(ColumnBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        StructType schema = batch.Schema;
        int columnCount = batch.ColumnCount;
        int length = batch.LogicalRowCount;
        bool hasSelection = batch.Selection is not null;

        var arrays = new IArrowArray[columnCount];
        var fields = new ArrowField[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            IArrowArray array;
            if (batch.Column(i) is ArrowNestedColumnVector nested)
            {
                if (hasSelection)
                {
                    throw new NotSupportedException(
                        "Exporting a nested (struct/list/map) column under a selection is not supported in "
                        + "v1; materialize the selection before converting to Arrow.");
                }

                // Retain an independent reference so the exported batch owns its buffers and disposing
                // it never frees a borrowed source array.
                array = ArrowArrayFactory.SliceShared(nested.Array, 0, nested.Array.Length);
            }
            else
            {
                // SelectedColumn applies any selection for flat columns (a no-op when none is present),
                // so the writer sees logical rows in order.
                array = ArrowColumnWriter.BuildArray(batch.SelectedColumn(i));
            }

            arrays[i] = array;
            StructField field = schema[i];

            // Take the Arrow type from the built array so decimal precision/scale and the timestamp
            // unit are exact; carry the schema's nullability.
            fields[i] = new ArrowField(field.Name, array.Data.DataType, field.Nullable);
        }

        var arrowSchema = new ArrowSchema(fields, metadata: null);
        return new RecordBatch(arrowSchema, arrays, length);
    }
}
