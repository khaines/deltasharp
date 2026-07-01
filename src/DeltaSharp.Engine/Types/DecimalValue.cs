using System.Globalization;

namespace DeltaSharp.Types;

/// <summary>
/// An exact fixed-point value: a 128-bit <see cref="Unscaled"/> mantissa and a non-negative
/// <see cref="Scale"/>. The vehicle for STORY-02.5.2 AC2/AC5 overflow + null-propagation
/// semantics — operations fit results into a target <see cref="DecimalType"/>, raising under
/// <see cref="AnsiMode.Ansi"/> and yielding null under <see cref="AnsiMode.Legacy"/>.
/// </summary>
public readonly struct DecimalValue : IEquatable<DecimalValue>
{
    /// <summary>Creates a value from an unscaled mantissa and scale.</summary>
    public DecimalValue(Int128 unscaled, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, DecimalArithmetic.MaxScale);
        Unscaled = unscaled;
        Scale = scale;
    }

    /// <summary>The unscaled mantissa (the integer of digits, sign included).</summary>
    public Int128 Unscaled { get; }

    /// <summary>The number of fractional digits.</summary>
    public int Scale { get; }

    /// <summary>Whether this value fits <paramref name="type"/>'s precision after rescaling to its scale.</summary>
    public bool Fits(DecimalType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return TryRescale(type.Scale, out Int128 unscaled) && Int128.Abs(unscaled) < Pow10(type.Precision);
    }

    /// <summary>Sum of two values, exact (no rounding); result scale is the wider operand scale.</summary>
    public static DecimalValue Add(DecimalValue a, DecimalValue b) => Combine(a, b, subtract: false);

    /// <summary>Difference of two values, exact (no rounding).</summary>
    public static DecimalValue Subtract(DecimalValue a, DecimalValue b) => Combine(a, b, subtract: true);

    /// <summary>Product of two values, exact; result scale is the sum of operand scales.</summary>
    /// <exception cref="ArithmeticOverflowException">The exact product cannot be represented (mantissa wraps Int128, or the scale sum exceeds <see cref="DecimalArithmetic.MaxScale"/>).</exception>
    public static DecimalValue Multiply(DecimalValue a, DecimalValue b)
    {
        int scale = a.Scale + b.Scale;
        if (scale > DecimalArithmetic.MaxScale)
        {
            throw new ArithmeticOverflowException(
                $"Decimal multiply scale {scale} exceeds {DecimalArithmetic.MaxScale}.");
        }

        try
        {
            return new DecimalValue(checked(a.Unscaled * b.Unscaled), scale);
        }
        catch (OverflowException ex)
        {
            throw new ArithmeticOverflowException("Decimal multiply mantissa overflow.", ex);
        }
    }

    /// <summary>
    /// Casts to <paramref name="target"/>, rounding to its scale (HALF_UP — Spark cast parity)
    /// and enforcing its precision. Overflow throws under <see cref="AnsiMode.Ansi"/>; under
    /// <see cref="AnsiMode.Legacy"/> the result is <c>null</c> (SQL <c>NULL</c>) — never a
    /// silently truncated or wrapped value.
    /// </summary>
    /// <exception cref="ArithmeticOverflowException">The value exceeds <paramref name="target"/> under ANSI mode.</exception>
    public DecimalValue? ToType(DecimalType target, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            Int128 rescaled = RescaleHalfUp(target.Scale);
            if (Int128.Abs(rescaled) >= Pow10(target.Precision))
            {
                return mode == AnsiMode.Ansi
                    ? throw new ArithmeticOverflowException(
                        $"Decimal value out of range for '{target.SimpleString}'.")
                    : null;
            }

            return new DecimalValue(rescaled, target.Scale);
        }
        catch (OverflowException ex)
        {
            return mode == AnsiMode.Ansi
                ? throw new ArithmeticOverflowException(
                    $"Decimal value out of range for '{target.SimpleString}'.", ex)
                : null;
        }
    }

    /// <summary>
    /// Applies a binary <paramref name="op"/>, fitting the exact result into its Spark result type.
    /// Any out-of-range condition — mantissa overflow, scale-sum &gt; 38, or precision overflow —
    /// is routed through the <paramref name="mode"/> contract: ANSI throws
    /// <see cref="ArithmeticOverflowException"/>, Legacy yields <c>null</c>. Divide/Remainder value
    /// rounding stays deferred (result <i>types</i> are defined; see type-system.md).
    /// </summary>
    public static DecimalValue? Apply(DecimalOp op, DecimalValue a, DecimalValue b, AnsiMode mode)
    {
        DecimalType result = DecimalArithmetic.ResultType(op, a.AsType(), b.AsType());
        DecimalValue exact;
        try
        {
            exact = op switch
            {
                DecimalOp.Add => Add(a, b),
                DecimalOp.Subtract => Subtract(a, b),
                DecimalOp.Multiply => Multiply(a, b),
                _ => throw new ArgumentOutOfRangeException(nameof(op), "Divide/Remainder need rounding context."),
            };
        }
        catch (ArithmeticOverflowException) when (mode == AnsiMode.Legacy)
        {
            return null;
        }

        return exact.ToType(result, mode);
    }

    /// <inheritdoc/>
    public bool Equals(DecimalValue other) => Unscaled == other.Unscaled && Scale == other.Scale;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DecimalValue v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Fold all 128 mantissa bits, not just the low 32 of (int)(long): distinct values that
        // share a low word (e.g. 1 and 1+2^64) must hash apart while equal values still agree.
        UInt128 bits = (UInt128)Unscaled;
        int hi = (int)((ulong)(bits >> 64) ^ ((ulong)(bits >> 64) >> 32));
        int lo = (int)((ulong)bits ^ ((ulong)bits >> 32));
        return StableHash.Combine(StableHash.Combine(hi, lo), Scale);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Unscaled}E-{Scale}");

    /// <summary>Equality.</summary>
    public static bool operator ==(DecimalValue left, DecimalValue right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(DecimalValue left, DecimalValue right) => !left.Equals(right);

    private static readonly Int128[] Pow10Table = BuildPow10Table();

    internal static Int128 Pow10(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, DecimalArithmetic.MaxScale);
        return Pow10Table[n];
    }

    private static Int128[] BuildPow10Table()
    {
        var table = new Int128[DecimalArithmetic.MaxScale + 1];
        Int128 r = Int128.One;
        for (int i = 0; i <= DecimalArithmetic.MaxScale; i++)
        {
            table[i] = r;
            if (i < DecimalArithmetic.MaxScale)
            {
                r *= 10;
            }
        }

        return table;
    }

    private DecimalType AsType() =>
        new(Math.Min(Math.Max(DecimalDigits(Unscaled), Scale + 1), DecimalType.MaxPrecision), Scale);

    private static int DecimalDigits(Int128 v)
    {
        v = Int128.Abs(v);
        int d = 1;
        while (v >= 10)
        {
            v /= 10;
            d++;
        }

        return d;
    }

    private static DecimalValue Combine(DecimalValue a, DecimalValue b, bool subtract)
    {
        int scale = Math.Max(a.Scale, b.Scale);
        try
        {
            Int128 av = a.RescaleChecked(scale), bv = b.RescaleChecked(scale);
            return new DecimalValue(checked(subtract ? av - bv : av + bv), scale);
        }
        catch (OverflowException ex)
        {
            throw new ArithmeticOverflowException("Decimal add/subtract mantissa overflow.", ex);
        }
    }

    private Int128 RescaleChecked(int targetScale) =>
        targetScale >= Scale ? checked(Unscaled * Pow10(targetScale - Scale)) : Unscaled / Pow10(Scale - targetScale);

    private Int128 RescaleHalfUp(int targetScale)
    {
        if (targetScale >= Scale)
        {
            return checked(Unscaled * Pow10(targetScale - Scale));
        }

        Int128 divisor = Pow10(Scale - targetScale);
        Int128 quotient = Unscaled / divisor;
        Int128 remainder = Int128.Abs(Unscaled % divisor);
        if (remainder >= divisor - remainder)
        {
            quotient += Unscaled < Int128.Zero ? Int128.NegativeOne : Int128.One;
        }

        return quotient;
    }

    private bool TryRescale(int targetScale, out Int128 value)
    {
        try
        {
            value = RescaleChecked(targetScale);
            return true;
        }
        catch (OverflowException)
        {
            value = Int128.Zero;
            return false;
        }
    }
}
