namespace DeltaSharp.Engine.Execution;

/// <summary>
/// A minimal scalar transform <c>y = (Multiplier * value) + Addend</c> over
/// <see cref="long"/>. It is the first <b>representative</b> kernel for the execution-backend
/// seam (ADR-0001): small enough to be obviously correct, yet a faithful stand-in for the
/// "fuse a scalar expression into a delegate" granularity ADR-0001 calls out for the optional
/// codegen tier.
/// </summary>
/// <remarks>
/// <para>
/// This kernel exists so the backend seam can be exercised end-to-end — and so the ADR-0001
/// differential parity oracle has something concrete to compare — before the general
/// expression / operator model lands in later EPIC-02 stories. It deliberately models only a
/// single fused arithmetic expression; it is <b>not</b> the public expression API and will be
/// generalized (not preserved) once real expressions exist.
/// </para>
/// <para>
/// Arithmetic is <see langword="unchecked"/> (wrapping) exactly like the C# <c>*</c> and
/// <c>+</c> operators and <see cref="System.Linq.Expressions.Expression.Multiply(System.Linq.Expressions.Expression,System.Linq.Expressions.Expression)"/>
/// / <see cref="System.Linq.Expressions.Expression.Add(System.Linq.Expressions.Expression,System.Linq.Expressions.Expression)"/>,
/// so the interpreted and compiled backends produce bit-for-bit identical results, including on
/// overflow.
/// </para>
/// </remarks>
/// <param name="Multiplier">The multiplicative coefficient applied to the input.</param>
/// <param name="Addend">The constant added after multiplication.</param>
public readonly record struct AffineInt64Kernel(long Multiplier, long Addend)
{
    /// <summary>
    /// The backend-independent reference evaluation. The interpreted backend is defined by
    /// ADR-0001 as the correctness ground truth, and this method is that ground truth for the
    /// affine kernel: every backend must match it for every input.
    /// </summary>
    /// <param name="value">The input value.</param>
    /// <returns><c>(Multiplier * value) + Addend</c>, with wrapping (unchecked) arithmetic.</returns>
    public long Evaluate(long value) => unchecked((Multiplier * value) + Addend);
}
