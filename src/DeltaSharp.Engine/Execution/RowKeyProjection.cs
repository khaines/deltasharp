using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution.Expressions;
using DeltaSharp.Engine.RowFormat;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// Projects a row's key expressions into the single canonical byte-sortable encoding (EPIC-02
/// <see cref="SortKeyEncoder"/>) that the relational operators share. One projection is built per
/// operator at Open; it owns the per-key scalar evaluators and the row-key encoder and does no row
/// work until <see cref="Evaluate"/> is pulled.
/// </summary>
/// <remarks>
/// <para>
/// The <b>orderings</b> decide how the bytes encode direction and null placement. Equality consumers
/// (grouping, join, exchange) pass <see langword="null"/> for a fixed ascending / nulls-first
/// canonical encoding — direction is irrelevant when two encodings are compared only for byte-equality,
/// so one ordering keeps them on the proven path that STORY-02.4.2 showed equal to
/// <see cref="RowOrderingComparer"/>. <b>Sort</b> passes one <see cref="SortKeyOrdering"/> per key so a
/// plain ascending <c>memcmp</c> of the encodings realizes the requested asc/desc × nulls-first/last
/// order across all keys.
/// </para>
/// <para>
/// <b>Allocation.</b> Encoding boxes each key value (<see cref="KeyBoxing"/>) and allocates a
/// right-sized key array per row — the documented v1 correctness-first cost. The boxing buffer is
/// reused across rows (<see cref="RowData"/> copies it), so only the key array escapes; a zero-box
/// typed-hash key path is deferred behind this same contract.
/// </para>
/// </remarks>
internal sealed class RowKeyProjection
{
    private readonly ExpressionEvaluator[] _keys;
    private readonly StructType _keySchema;
    private readonly SortKeyEncoder _encoder;
    private readonly object?[] _scratch;

    /// <summary>
    /// Builds a projection for <paramref name="keyExpressions"/> over <paramref name="inputSchema"/>.
    /// </summary>
    /// <param name="keyExpressions">The key expressions, in priority order.</param>
    /// <param name="inputSchema">The operator's input schema.</param>
    /// <param name="backendName">Backend attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <param name="kind">Operator kind attributed in any <see cref="UnsupportedOperatorException"/>.</param>
    /// <param name="orderings">
    /// One ordering per key (same length as <paramref name="keyExpressions"/>), or
    /// <see langword="null"/> for a fixed ascending / nulls-first canonical encoding.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="keyExpressions"/> is empty or mismatches <paramref name="orderings"/>.</exception>
    /// <exception cref="UnsupportedOperatorException">
    /// A key expression is not interpretable, or its resolved type is not byte-sortable (nested/void).
    /// </exception>
    internal RowKeyProjection(
        IReadOnlyList<PhysicalExpression> keyExpressions,
        StructType inputSchema,
        string backendName,
        OperatorKind kind,
        IReadOnlyList<SortKeyOrdering>? orderings = null)
    {
        ArgumentNullException.ThrowIfNull(keyExpressions);
        ArgumentNullException.ThrowIfNull(inputSchema);
        if (keyExpressions.Count == 0)
        {
            throw new ArgumentException("A key projection needs at least one key expression.", nameof(keyExpressions));
        }

        if (orderings is not null && orderings.Count != keyExpressions.Count)
        {
            throw new ArgumentException(
                $"orderings ({orderings.Count}) must match key count ({keyExpressions.Count}).", nameof(orderings));
        }

        _keys = new ExpressionEvaluator[keyExpressions.Count];
        var fields = new StructField[keyExpressions.Count];
        var keyFields = new int[keyExpressions.Count];
        var resolved = new SortKeyOrdering[keyExpressions.Count];
        for (int k = 0; k < keyExpressions.Count; k++)
        {
            // Build first: an unsupported key expression fails fast as UnsupportedOperatorException.
            _keys[k] = ExpressionEvaluators.Build(keyExpressions[k], inputSchema, backendName, kind);
            fields[k] = new StructField($"k{k}", _keys[k].Type, _keys[k].Nullable);
            keyFields[k] = k;
            resolved[k] = orderings?[k] ?? SortKeyOrdering.Ascending;
        }

        _keySchema = new StructType(fields);
        try
        {
            _encoder = new SortKeyEncoder(_keySchema, keyFields, resolved);
        }
        catch (RowFormatException ex)
        {
            // A non-byte-sortable key type (nested/void) is an unshipped shape, not a bad plan.
            throw new UnsupportedOperatorException(
                kind, backendName, $"a key type is not byte-sortable: {ex.Message}");
        }

        _scratch = new object?[keyExpressions.Count];
    }

    /// <summary>The number of key fields.</summary>
    internal int KeyCount => _keys.Length;

    /// <summary>
    /// Evaluates every key expression over <paramref name="batch"/>'s logical rows, returning one
    /// logical-order vector per key. The vectors are scratch owned by <paramref name="memory"/> and
    /// stay valid until the caller releases it.
    /// </summary>
    internal ColumnVector[] Evaluate(ColumnBatch batch, BatchEvaluationMemory memory, CancellationToken cancellationToken)
    {
        var vectors = new ColumnVector[_keys.Length];
        for (int k = 0; k < _keys.Length; k++)
        {
            vectors[k] = _keys[k].Evaluate(batch, memory, cancellationToken);
        }

        return vectors;
    }

    /// <summary>
    /// Encodes logical row <paramref name="row"/> of <paramref name="keyVectors"/>, setting
    /// <paramref name="anyNull"/> when any key field is SQL <c>NULL</c> (join uses it to drop null
    /// keys; grouping/exchange/sort ignore it).
    /// </summary>
    internal byte[] Encode(ColumnVector[] keyVectors, int row, out bool anyNull)
    {
        anyNull = false;
        for (int k = 0; k < _keys.Length; k++)
        {
            object? boxed = KeyBoxing.ToRowDataValue(keyVectors[k], row);
            if (boxed is null)
            {
                anyNull = true;
            }

            _scratch[k] = boxed;
        }

        // RowData copies _scratch, so reusing the buffer across rows is safe.
        return _encoder.Encode(new RowData(_keySchema, _scratch));
    }
}
