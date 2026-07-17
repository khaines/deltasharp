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
/// (shared) children with the combined validity. A nested <b>collection</b> (array/map) field is
/// rejected at build time (see <see cref="ExpressionEvaluators.Build"/>) so behavior is deterministic
/// rather than data-dependent on the presence of a null struct. Row gather over a struct is still a
/// wall — with a batch <see cref="ColumnBatch.Selection"/> present, evaluating the struct-typed child
/// throws <see cref="NotSupportedException"/> upstream (#546), so this evaluator only runs over
/// unselected batches.
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
        // field's own before returning.
        return field is StructColumnVector nestedStruct
            ? MaskNestedStruct(nestedStruct, structVector, memory, cancellationToken)
            : MaskFlatField(field, structVector, memory, cancellationToken);
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
