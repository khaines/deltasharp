namespace DeltaSharp.Engine.Columnar;

/// <summary>
/// Null-propagation contracts for vectorized expression evaluation (STORY-02.6.1 AC3): how an
/// expression's output <see cref="Validity"/> is derived from its inputs' validity under SQL
/// three-valued logic (3VL). Two families are modeled:
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Propagate-on-any-null</b> — the rule for arithmetic, comparison, and most scalar functions:
/// the output is null wherever <i>any</i> input is null, independent of the input values.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Kleene AND / OR / NOT</b> — the value-aware rule for boolean connectives: a <c>FALSE</c> rescues
/// <c>AND</c> and a <c>TRUE</c> rescues <c>OR</c> even when the other operand is null.
/// </description>
/// </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The single-lane <see cref="bool"/><c>?</c> methods (<c>null</c> = SQL <c>UNKNOWN</c>) are the
/// scalar <b>reference</b>; the bulk span methods compute the same result lane-by-lane and exist so
/// a later SIMD/branchless path (STORY-02.6.2) has a parity oracle. Output validity is written into
/// a caller-provided <see cref="Span{Byte}"/> (Arrow LSB-first; set bit = valid). When every input
/// is all-valid the result is all-valid, so callers gate allocation with
/// <see cref="NeedsValidityBitmap(Validity)"/> / <see cref="NeedsValidityBitmap(Validity, Validity)"/>
/// and keep the no-null fast path allocation-free (AC1).
/// </para>
/// </remarks>
public static class NullPropagation
{
    // ----------------------------------------------------------------------------------------
    // Scalar three-valued-logic reference (null = SQL UNKNOWN). These are the fixture oracle.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Kleene <c>AND</c>: <c>FALSE</c> dominates (so <c>FALSE AND NULL = FALSE</c>); otherwise a null
    /// operand yields null (<c>TRUE AND NULL = NULL</c>); else <c>TRUE</c>.
    /// </summary>
    public static bool? KleeneAnd(bool? left, bool? right)
    {
        if (left is false || right is false)
        {
            return false;
        }

        if (left is null || right is null)
        {
            return null;
        }

        return true;
    }

    /// <summary>
    /// Kleene <c>OR</c>: <c>TRUE</c> dominates (so <c>TRUE OR NULL = TRUE</c>); otherwise a null
    /// operand yields null (<c>FALSE OR NULL = NULL</c>); else <c>FALSE</c>.
    /// </summary>
    public static bool? KleeneOr(bool? left, bool? right)
    {
        if (left is true || right is true)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return null;
        }

        return false;
    }

    /// <summary>Kleene <c>NOT</c>: <c>NOT NULL = NULL</c>; otherwise the boolean negation.</summary>
    public static bool? KleeneNot(bool? value) => value is bool b ? !b : value;

    // ----------------------------------------------------------------------------------------
    // Propagate-on-any-null validity (arithmetic, comparison, most scalar functions).
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a unary propagate-on-any-null result needs a validity bitmap at all. <c>false</c> is
    /// the all-valid fast path: the caller materializes no output bitmap (AC1).
    /// </summary>
    public static bool NeedsValidityBitmap(Validity input) => input.HasBitmap;

    /// <summary>
    /// Whether a binary propagate-on-any-null result needs a validity bitmap at all. <c>false</c>
    /// (both operands all-valid) is the no-null, no-allocation fast path (AC1).
    /// </summary>
    public static bool NeedsValidityBitmap(Validity left, Validity right) => left.HasBitmap || right.HasBitmap;

    /// <summary>
    /// Writes the unary propagate-on-any-null output validity (<c>out_valid = in_valid</c>) into
    /// <paramref name="output"/> and returns the null count.
    /// </summary>
    /// <param name="input">The operand's validity.</param>
    /// <param name="output">A bitmap of at least <c>ByteCount(input.Length)</c> bytes; fully written.</param>
    /// <returns>The number of null output rows.</returns>
    /// <exception cref="ArgumentException"><paramref name="output"/> is too small.</exception>
    public static int PropagateUnary(Validity input, Span<byte> output)
    {
        int length = input.Length;
        int byteCount = Bitmap.ByteCount(length);
        RequireOutput(output.Length, byteCount);

        output[..byteCount].Clear();
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            if (input.IsValid(i))
            {
                Bitmap.Set(output, i, true);
            }
            else
            {
                nulls++;
            }
        }

        return nulls;
    }

    /// <summary>
    /// Writes the binary propagate-on-any-null output validity (<c>out_valid = left_valid AND
    /// right_valid</c>) into <paramref name="output"/> and returns the null count.
    /// </summary>
    /// <param name="left">The left operand's validity.</param>
    /// <param name="right">The right operand's validity; must have the same length as <paramref name="left"/>.</param>
    /// <param name="output">A bitmap of at least <c>ByteCount(length)</c> bytes; fully written.</param>
    /// <returns>The number of null output rows.</returns>
    /// <exception cref="ArgumentException">The operand lengths differ, or <paramref name="output"/> is too small.</exception>
    public static int PropagateBinary(Validity left, Validity right, Span<byte> output)
    {
        int length = left.Length;
        RequireSameLength(length, right.Length, "operand validity");
        int byteCount = Bitmap.ByteCount(length);
        RequireOutput(output.Length, byteCount);

        output[..byteCount].Clear();
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            if (left.IsValid(i) && right.IsValid(i))
            {
                Bitmap.Set(output, i, true);
            }
            else
            {
                nulls++;
            }
        }

        return nulls;
    }

    // ----------------------------------------------------------------------------------------
    // Kleene bulk kernels (value-aware: a valid FALSE/TRUE can rescue a null operand).
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Kleene <c>AND</c> over boolean value spans and their validity, writing the output values and
    /// validity. Matches the single-lane <see cref="KleeneAnd(bool?, bool?)"/> reference per row.
    /// </summary>
    /// <returns>The number of null output rows.</returns>
    /// <exception cref="ArgumentException">Any span length or validity length is inconsistent.</exception>
    public static int KleeneAnd(
        ReadOnlySpan<bool> leftValues,
        Validity leftValidity,
        ReadOnlySpan<bool> rightValues,
        Validity rightValidity,
        Span<bool> resultValues,
        Span<byte> resultValidity)
    {
        int length = CheckBinaryBool(
            leftValues.Length, rightValues.Length, leftValidity, rightValidity, resultValues.Length, resultValidity.Length);

        resultValidity[..Bitmap.ByteCount(length)].Clear();
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            bool? a = leftValidity.IsNull(i) ? null : leftValues[i];
            bool? b = rightValidity.IsNull(i) ? null : rightValues[i];
            nulls += WriteLane(KleeneAnd(a, b), i, resultValues, resultValidity);
        }

        return nulls;
    }

    /// <summary>
    /// Kleene <c>OR</c> over boolean value spans and their validity, writing the output values and
    /// validity. Matches the single-lane <see cref="KleeneOr(bool?, bool?)"/> reference per row.
    /// </summary>
    /// <returns>The number of null output rows.</returns>
    /// <exception cref="ArgumentException">Any span length or validity length is inconsistent.</exception>
    public static int KleeneOr(
        ReadOnlySpan<bool> leftValues,
        Validity leftValidity,
        ReadOnlySpan<bool> rightValues,
        Validity rightValidity,
        Span<bool> resultValues,
        Span<byte> resultValidity)
    {
        int length = CheckBinaryBool(
            leftValues.Length, rightValues.Length, leftValidity, rightValidity, resultValues.Length, resultValidity.Length);

        resultValidity[..Bitmap.ByteCount(length)].Clear();
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            bool? a = leftValidity.IsNull(i) ? null : leftValues[i];
            bool? b = rightValidity.IsNull(i) ? null : rightValues[i];
            nulls += WriteLane(KleeneOr(a, b), i, resultValues, resultValidity);
        }

        return nulls;
    }

    /// <summary>
    /// Kleene <c>NOT</c> over a boolean value span and its validity, writing the output values and
    /// validity. Nulls propagate unchanged; matches <see cref="KleeneNot(bool?)"/> per row.
    /// </summary>
    /// <returns>The number of null output rows.</returns>
    /// <exception cref="ArgumentException">Any span length or validity length is inconsistent.</exception>
    public static int KleeneNot(
        ReadOnlySpan<bool> values,
        Validity validity,
        Span<bool> resultValues,
        Span<byte> resultValidity)
    {
        int length = values.Length;
        RequireSameLength(length, validity.Length, "validity");
        if (resultValues.Length < length)
        {
            throw new ArgumentException($"Result values span needs {length} rows but has {resultValues.Length}.", nameof(resultValues));
        }

        int byteCount = Bitmap.ByteCount(length);
        RequireOutput(resultValidity.Length, byteCount);

        resultValidity[..byteCount].Clear();
        int nulls = 0;
        for (int i = 0; i < length; i++)
        {
            bool? v = validity.IsNull(i) ? null : values[i];
            nulls += WriteLane(KleeneNot(v), i, resultValues, resultValidity);
        }

        return nulls;
    }

    // ----------------------------------------------------------------------------------------
    // Internals.
    // ----------------------------------------------------------------------------------------

    private static int WriteLane(bool? result, int index, Span<bool> resultValues, Span<byte> resultValidity)
    {
        if (result is bool value)
        {
            resultValues[index] = value;
            Bitmap.Set(resultValidity, index, true);
            return 0;
        }

        // Null output: a deterministic placeholder value with the validity bit left cleared.
        resultValues[index] = false;
        return 1;
    }

    private static int CheckBinaryBool(
        int leftLength,
        int rightLength,
        Validity leftValidity,
        Validity rightValidity,
        int resultValuesLength,
        int resultValidityLength)
    {
        RequireSameLength(leftLength, rightLength, "operand values");
        RequireSameLength(leftLength, leftValidity.Length, "left validity");
        RequireSameLength(leftLength, rightValidity.Length, "right validity");
        if (resultValuesLength < leftLength)
        {
            throw new ArgumentException($"Result values span needs {leftLength} rows but has {resultValuesLength}.");
        }

        RequireOutput(resultValidityLength, Bitmap.ByteCount(leftLength));
        return leftLength;
    }

    private static void RequireSameLength(int expected, int actual, string what)
    {
        if (expected != actual)
        {
            throw new ArgumentException($"Length mismatch for {what}: expected {expected} but got {actual}.");
        }
    }

    private static void RequireOutput(int outputLength, int requiredBytes)
    {
        if (outputLength < requiredBytes)
        {
            throw new ArgumentException(
                $"Output validity bitmap needs at least {requiredBytes} byte(s) but has {outputLength}.");
        }
    }
}
