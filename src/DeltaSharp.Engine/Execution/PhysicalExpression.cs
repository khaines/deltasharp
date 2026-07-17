using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// The bound, physical-plan view of a scalar expression an operator carries (a predicate, a
/// projection element, a join/group/sort key). It is intentionally a <b>resolved</b> contract:
/// the result <see cref="Type"/> and <see cref="Nullable"/> are already decided, so the backend
/// only evaluates, it never resolves names or types. Evaluation is owned by the backend — this
/// contract names no kernel and emits no IL, so the interpreter path stays AOT-clean
/// (STORY-03.1.1 AC1, AC4).
/// </summary>
/// <remarks>
/// Only the minimal v1 leaves needed to express the operator contracts ship here
/// (<see cref="ColumnReference"/>); the general expression tree, function binding, and the
/// evaluation kernels arrive in later EPIC-03 stories. New leaves extend this base; none may
/// introduce a dynamic-code dependency for the interpreter.
/// </remarks>
public abstract class PhysicalExpression
{
    /// <summary>Initializes the resolved result type and nullability.</summary>
    /// <param name="type">The expression's value type (shared ADR-0008 contract).</param>
    /// <param name="nullable">Whether the expression may evaluate to null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    protected PhysicalExpression(DataType type, bool nullable)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Nullable = nullable;
    }

    /// <summary>The resolved result type of evaluating this expression over a batch row.</summary>
    public DataType Type { get; }

    /// <summary>Whether evaluating this expression may yield null (drives validity propagation).</summary>
    public bool Nullable { get; }

    /// <summary>The child expressions, in argument order (empty for a leaf).</summary>
    public virtual IReadOnlyList<PhysicalExpression> Children => Array.Empty<PhysicalExpression>();
}

/// <summary>
/// A leaf that reads a column by ordinal from the operator's input <see cref="ColumnBatch"/> — the
/// only expression leaf needed to wire the v1 operator contracts. Evaluating it returns
/// <c>batch.SelectedColumn(<see cref="Ordinal"/>)</c>, so it is null- and selection-aware without
/// any per-row work in the contract.
/// </summary>
public sealed class ColumnReference : PhysicalExpression
{
    /// <summary>Creates a reference to input column <paramref name="ordinal"/>.</summary>
    /// <param name="ordinal">The zero-based column ordinal in the input schema.</param>
    /// <param name="type">The column's type.</param>
    /// <param name="nullable">Whether the column is nullable.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative.</exception>
    public ColumnReference(int ordinal, DataType type, bool nullable)
        : base(type, nullable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        Ordinal = ordinal;
    }

    /// <summary>The zero-based ordinal of the referenced input column.</summary>
    public int Ordinal { get; }
}

/// <summary>
/// Extracts a field from a struct-typed <see cref="Child"/> — the physical form of Catalyst's
/// <c>GetStructField</c> (nested reference <c>s.f</c>, #580). Evaluating it reads the child's field
/// child at <see cref="Ordinal"/> and applies the struct's own validity: extracting a field of a
/// <b>null struct</b> yields null, so <see cref="PhysicalExpression.Nullable"/> is true whenever the
/// struct value may be null OR the field itself is nullable. Chaining (one node per path segment)
/// expresses a deeper path (<c>s.a.b</c>).
/// </summary>
public sealed class StructFieldExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates <c>child.field</c>, extracting the struct field at <paramref name="ordinal"/>.</summary>
    /// <param name="child">The struct-typed operand (its <see cref="PhysicalExpression.Type"/> must be a <see cref="StructType"/>).</param>
    /// <param name="ordinal">The zero-based field index within the child's struct type.</param>
    /// <param name="type">The extracted field's resolved value type.</param>
    /// <param name="nullable">Whether the extraction may yield null (null struct or nullable field).</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> or <paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is negative or outside the child's fields.</exception>
    /// <exception cref="ArgumentException">The child is not struct-typed, or <paramref name="type"/> disagrees with the field's declared type.</exception>
    public StructFieldExpression(PhysicalExpression child, int ordinal, DataType type, bool nullable)
        : base(type, nullable)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (child.Type is not StructType structType)
        {
            throw new ArgumentException(
                $"GetStructField requires a struct-typed child, but the child is '{child.Type.SimpleString}'.",
                nameof(child));
        }

        if (ordinal >= structType.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal), ordinal, $"Field ordinal must be in [0, {structType.Count}) for '{structType.SimpleString}'.");
        }

        DataType fieldType = structType[ordinal].DataType;
        if (!type.Equals(fieldType))
        {
            throw new ArgumentException(
                $"GetStructField declares result type '{type.SimpleString}' but field {ordinal} "
                + $"('{structType[ordinal].Name}') is '{fieldType.SimpleString}'.", nameof(type));
        }

        _children = [child];
        Ordinal = ordinal;
    }

    /// <summary>The struct-typed operand the field is extracted from.</summary>
    public PhysicalExpression Child => _children[0];

    /// <summary>The zero-based field index within the child's struct type.</summary>
    public int Ordinal { get; }

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;
}
