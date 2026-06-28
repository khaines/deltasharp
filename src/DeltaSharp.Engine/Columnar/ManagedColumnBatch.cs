using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// A managed reference <see cref="ColumnBatch"/>: a schema plus one equal-length
/// <see cref="ColumnVector"/> per field, optionally carrying a <see cref="SelectionVector"/>.
/// </summary>
public sealed class ManagedColumnBatch : ColumnBatch
{
    private readonly ColumnVector[] _columns;
    private readonly int _rowCount;
    private readonly SelectionVector? _selection;

    /// <summary>Creates a batch from a schema and its columns.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The column count, a column's type, or a column's length disagrees with the schema/row count.
    /// </exception>
    public ManagedColumnBatch(StructType schema, IReadOnlyList<ColumnVector> columns, int rowCount)
        : this(schema, ToValidatedArray(schema, columns, rowCount), rowCount, selection: null)
    {
    }

    private ManagedColumnBatch(StructType schema, ColumnVector[] columns, int rowCount, SelectionVector? selection)
    {
        Schema = schema;
        _columns = columns;
        _rowCount = rowCount;
        _selection = selection;
    }

    /// <inheritdoc/>
    public override StructType Schema { get; }

    /// <inheritdoc/>
    public override int RowCount => _rowCount;

    /// <inheritdoc/>
    public override int ColumnCount => _columns.Length;

    /// <inheritdoc/>
    public override SelectionVector? Selection => _selection;

    /// <inheritdoc/>
    public override ColumnVector Column(int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal), ordinal, $"Ordinal must be in [0, {_columns.Length}).");
        }

        return _columns[ordinal];
    }

    /// <inheritdoc/>
    public override ColumnBatch Slice(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > _rowCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length), length, $"Slice [{offset}, {offset + length}) exceeds row count {_rowCount}.");
        }

        var sliced = new ColumnVector[_columns.Length];
        for (int i = 0; i < _columns.Length; i++)
        {
            sliced[i] = _columns[i].Slice(offset, length);
        }

        return new ManagedColumnBatch(Schema, sliced, length, selection: null);
    }

    /// <inheritdoc/>
    public override ColumnBatch WithSelection(SelectionVector selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ReadOnlySpan<int> indices = selection.Indices;
        for (int i = 0; i < indices.Length; i++)
        {
            if ((uint)indices[i] >= (uint)_rowCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(selection), indices[i], $"Selected index must be in [0, {_rowCount}).");
            }
        }

        return new ManagedColumnBatch(Schema, _columns, _rowCount, selection);
    }

    private static ColumnVector[] ToValidatedArray(StructType schema, IReadOnlyList<ColumnVector> columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        if (columns.Count != schema.Count)
        {
            throw new ArgumentException(
                $"Schema has {schema.Count} fields but {columns.Count} columns were supplied.", nameof(columns));
        }

        var array = new ColumnVector[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnVector column = columns[i]
                ?? throw new ArgumentNullException(nameof(columns), $"Column {i} is null.");

            if (!column.Type.Equals(schema[i].DataType))
            {
                throw new ArgumentException(
                    $"Column {i} ('{schema[i].Name}') has type '{column.Type.SimpleString}' but the schema "
                    + $"declares '{schema[i].DataType.SimpleString}'.", nameof(columns));
            }

            if (column.Length != rowCount)
            {
                throw new ArgumentException(
                    $"Column {i} ('{schema[i].Name}') has length {column.Length} but the batch row count is {rowCount}.",
                    nameof(columns));
            }

            array[i] = column;
        }

        return array;
    }
}
