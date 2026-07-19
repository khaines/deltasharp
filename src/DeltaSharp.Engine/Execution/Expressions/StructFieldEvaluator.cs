using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Evaluates a <see cref="StructFieldExpression"/> by extracting one field from a struct column
/// (STORY-03.4.1 nested field access, #580). It evaluates the child to a
/// <see cref="StructColumnVector"/> and returns that struct's field child at the requested ordinal,
/// applying the struct's own validity: extracting a field of a <b>null struct</b> yields null (Spark
/// semantics), even where the field child holds a non-null slot at that row.
/// </summary>
/// <remarks>
/// When no row is a null struct (the common non-nullable-struct case) the field child already carries
/// the correct per-row validity, so it is returned <b>zero-copy</b> for any field type. Only when the
/// struct carries nulls does the result materialize: a flat/primitive field is copied lane-by-lane
/// with the combined (struct-null OR field-null) validity; a struct field is re-wrapped over its own
/// (shared) children with the combined validity; a nested <b>collection</b> (array/map) field shares
/// its element/entry buffers and masks only the top-level validity (#589), so a field of a null struct
/// reads as null. Under a batch
/// <see cref="ColumnBatch.Selection"/> the struct itself cannot row-gather (#546), so the field is
/// extracted over the unselected physical rows and the (flat) result is gathered through the
/// selection — a struct- or collection-<i>typed</i> field downstream of a selection still hits the #546
/// wall (<see cref="ColumnVector.Select"/> is unsupported for struct/list/map) when the gathered result
/// is itself nested.
/// </remarks>
internal sealed class StructFieldEvaluator : ExpressionEvaluator
{
    private readonly ExpressionEvaluator _child;
    private readonly int _ordinal;

    public StructFieldEvaluator(StructFieldExpression expression, ExpressionEvaluator child)
        : base(expression.Type, expression.Nullable)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
        _ordinal = expression.Ordinal;
    }

    /// <inheritdoc />
    public override ColumnVector Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(memory);

        // A struct column cannot row-gather under a selection (#546). Rather than fail a common shape
        // (`df.Filter(...).Select(col("s.f"))`), extract the field over the batch's UNSELECTED rows —
        // structs read in physical order — and gather the extracted result through the selection: the
        // extracted flat field gathers fine even though the struct itself cannot. (A struct-TYPED field
        // downstream of a selection still hits the #546 gather wall when the gathered result is a
        // struct; that boundary stays deterministic.)
        if (batch.Selection is { } selection)
        {
            ColumnVector fullField = ExtractField(Unselected(batch), memory, cancellationToken);
            return fullField.Select(selection);
        }

        return ExtractField(batch, memory, cancellationToken);
    }

    private ColumnVector ExtractField(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        ColumnVector childVector = _child.Evaluate(batch, memory, cancellationToken);
        if (childVector is not StructColumnVector structVector)
        {
            throw new InvalidOperationException(
                $"GetStructField expected a struct column but the child evaluated to '{childVector.Type.SimpleString}'.");
        }

        ColumnVector field = structVector.Child(_ordinal);

        // Fast path: no null structs, so the field child's own validity is authoritative — return it
        // directly (zero-copy), for any field type.
        if (!structVector.HasNulls)
        {
            return field;
        }

        // A null struct row makes the extracted field null, so combine the struct's validity with the
        // field's own before returning. A nested struct re-wraps over its (shared) children; a nested
        // collection (list/map) shares its element/entry buffers and masks only the top-level validity
        // (#589); a flat field copies lane-by-lane.
        return field switch
        {
            StructColumnVector nestedStruct => MaskNestedStruct(nestedStruct, structVector, memory, cancellationToken),
            ListColumnVector list => list.WithParentNulls(ParentNullMask(list.Length, structVector, memory, cancellationToken)),
            MapColumnVector map => map.WithParentNulls(ParentNullMask(map.Length, structVector, memory, cancellationToken)),
            _ => MaskFlatField(field, structVector, memory, cancellationToken),
        };
    }

    // The parent struct's per-row null flags [0, rows), used to mask a nested COLLECTION (list/map) field: a
    // field of a null struct is null (Spark semantics). The field's own nulls are preserved by WithParentNulls
    // (which ORs these in over the shared buffers), so only the parent's nulls are collected here. Reserves the
    // masked result like the flat/struct masking paths so a large masked field is bounded.
    private bool[] ParentNullMask(
        int rows, StructColumnVector parent, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        memory.ReserveVector(Type, rows);
        var mask = new bool[rows];
        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            mask[i] = parent.IsNull(i);
        }

        return mask;
    }

    // A selection-free view over the batch's physical rows (shared column vectors, no copy), so the
    // struct-typed child is read in physical order rather than being asked to row-gather (#546).
    private static ColumnBatch Unselected(ColumnBatch batch)
    {
        var columns = new ColumnVector[batch.ColumnCount];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = batch.Column(i);
        }

        return new ManagedColumnBatch(batch.Schema, columns, batch.RowCount);
    }

    private ColumnVector MaskFlatField(
        ColumnVector field,
        StructColumnVector structVector,
        BatchEvaluationMemory memory,
        CancellationToken cancellationToken)
    {
        int rows = field.Length;
        memory.ReserveVector(Type, rows);
        MutableColumnVector result = ColumnVectors.Create(Type, rows);
        bool fieldNulls = field.HasNulls;

        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            if (structVector.IsNull(i) || (fieldNulls && field.IsNull(i)))
            {
                result.AppendNull();
            }
            else
            {
                VectorMaterializer.CopyValue(result, field, i);
            }
        }

        return result;
    }

    private ColumnVector MaskNestedStruct(
        StructColumnVector field,
        StructColumnVector parent,
        BatchEvaluationMemory memory,
        CancellationToken cancellationToken)
    {
        int rows = field.Length;
        memory.ReserveVector(Type, rows);
        var nulls = new bool[rows];
        for (int i = 0; i < rows; i++)
        {
            CancellationPolicy.Poll(cancellationToken, i);
            nulls[i] = parent.IsNull(i) || field.IsNull(i);
        }

        var children = new ColumnVector[field.FieldCount];
        for (int f = 0; f < children.Length; f++)
        {
            children[f] = field.Child(f);
        }

        return new StructColumnVector((StructType)field.Type, children, nulls);
    }
}
