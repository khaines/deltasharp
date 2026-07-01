namespace DeltaSharp.Types;

/// <summary>
/// Spark-parity type coercion for v1 (STORY-02.5.2). Owns numeric width precedence and the
/// common-type matrix (AC1), nested-type coercion with precise error paths (AC4), and the
/// null-type promotion that keeps SQL null propagation distinct from CLR defaults (AC5).
/// Mirrors Apache Spark's <c>TypeCoercion.findTightestCommonType</c>/<c>findWiderTypeForTwo</c>.
/// </summary>
public static class TypeCoercion
{
    /// <summary>
    /// Numeric width precedence, widest last (Spark <c>numericPrecedence</c>). The common type
    /// of two members is whichever appears later; <see cref="DecimalType"/> sits outside this
    /// list and is handled by the decimal widening rules.
    /// </summary>
    public static IReadOnlyList<DataType> NumericPrecedence { get; } = new DataType[]
    {
        ByteType.Instance,
        ShortType.Instance,
        IntegerType.Instance,
        LongType.Instance,
        FloatType.Instance,
        DoubleType.Instance,
    };

    /// <summary>Whether <paramref name="type"/> is a v1 numeric type (integral, floating, or decimal).</summary>
    public static bool IsNumeric(DataType type) =>
        type is ByteType or ShortType or IntegerType or LongType or FloatType or DoubleType or DecimalType;

    /// <summary>True for fixed-point integral types; false for float/double/decimal.</summary>
    public static bool IsIntegral(DataType type) =>
        type is ByteType or ShortType or IntegerType or LongType;

    /// <summary>
    /// The tightest common type without lossy promotion (Spark <c>findTightestCommonType</c>):
    /// equal types, null promotion, integer widening, exact decimals, and <c>date→timestamp</c>.
    /// An integer promotes to a decimal only when that decimal losslessly holds it (Spark
    /// <c>DecimalType.isWiderThan</c>); a narrower decimal has no tightest common type (returns
    /// null). Returns null when there is no lossless common type.
    /// </summary>
    public static DataType? FindTightestCommonType(DataType left, DataType right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Equals(right))
        {
            return left;
        }

        if (left is NullType)
        {
            return right;
        }

        if (right is NullType)
        {
            return left;
        }

        // Integer⊕decimal stays tight only when the decimal loses nothing holding the integer.
        if (left is DecimalType ld && IsIntegral(right) && DecimalHolds(ld, right))
        {
            return ld;
        }

        if (right is DecimalType rd && IsIntegral(left) && DecimalHolds(rd, left))
        {
            return rd;
        }

        int li = PrecedenceIndex(left), ri = PrecedenceIndex(right);
        if (li >= 0 && ri >= 0)
        {
            return NumericPrecedence[Math.Max(li, ri)];
        }

        if ((left is DateType && right is TimestampType) || (left is TimestampType && right is DateType))
        {
            return TimestampType.Instance;
        }

        return null;
    }

    /// <summary>
    /// The wider common type, allowing decimal promotion (Spark <c>findWiderTypeForTwo</c>):
    /// the AC1 matrix for mixed numeric widths. Integers widen into decimals, decimals widen to
    /// the smallest decimal that holds both, and decimal⊕float/double widens to <c>double</c>.
    /// Returns null for non-coercible pairs.
    /// </summary>
    public static DataType? FindWiderTypeForTwo(DataType left, DataType right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        DataType? tight = FindTightestCommonType(left, right);
        if (tight is not null)
        {
            return tight;
        }

        if (left is DecimalType || right is DecimalType)
        {
            return WiderDecimal(left, right);
        }

        return null;
    }

    /// <summary>The wider common type of a non-empty sequence (left-folded), or null if any pair is incompatible.</summary>
    /// <exception cref="ArgumentException"><paramref name="types"/> is empty.</exception>
    public static DataType? FindWiderCommonType(IEnumerable<DataType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        DataType? acc = null;
        bool any = false;
        foreach (DataType t in types)
        {
            ArgumentNullException.ThrowIfNull(t);
            any = true;
            if (acc is null)
            {
                acc = t;
                continue;
            }

            acc = FindWiderTypeForTwo(acc, t);
            if (acc is null)
            {
                return null;
            }
        }

        return any ? acc : throw new ArgumentException("At least one type is required.", nameof(types));
    }

    /// <summary>
    /// Verifies <paramref name="source"/> can be implicitly coerced to <paramref name="target"/>,
    /// recursing structurally and throwing <see cref="TypeCoercionException"/> that names the
    /// source, target, and dotted expression <paramref name="path"/> on the first mismatch (AC4).
    /// </summary>
    /// <exception cref="TypeCoercionException">No implicit coercion exists at some path.</exception>
    public static void EnsureCoercible(DataType source, DataType target, string path = "value")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(path);
        TryCoerce(source, target, path, out string failPath, out DataType failSource, out DataType failTarget);
        if (failSource is not null)
        {
            throw TypeCoercionException.ForPath(failSource, failTarget, failPath);
        }
    }

    /// <summary>Whether <paramref name="source"/> can be implicitly coerced to <paramref name="target"/> (no throw).</summary>
    public static bool CanCoerce(DataType source, DataType target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return TryCoerce(source, target, "value", out _, out _, out _);
    }

    private static bool TryCoerce(
        DataType source, DataType target, string path, out string failPath, out DataType failSource, out DataType failTarget)
    {
        failPath = path;
        failSource = source;
        failTarget = target;
        if (source.Equals(target) || source is NullType)
        {
            failSource = null!;
            failTarget = null!;
            return true; // identity and null-literal widening are always allowed (AC5).
        }

        switch (source, target)
        {
            case (ArrayType s, ArrayType t):
                return TryCoerce(s.ElementType, t.ElementType, path + ".element", out failPath, out failSource, out failTarget);
            case (MapType s, MapType t):
                return TryCoerce(s.KeyType, t.KeyType, path + ".key", out failPath, out failSource, out failTarget)
                    && TryCoerce(s.ValueType, t.ValueType, path + ".value", out failPath, out failSource, out failTarget);
            case (StructType s, StructType t):
                if (s.Count != t.Count)
                {
                    return false;
                }

                for (int i = 0; i < s.Count; i++)
                {
                    if (!TryCoerce(s[i].DataType, t[i].DataType, $"{path}.{t[i].Name}", out failPath, out failSource, out failTarget))
                    {
                        return false;
                    }
                }

                failSource = null!;
                failTarget = null!;
                return true;
            default:
                if (IsNumeric(source) && IsNumeric(target) && FindWiderTypeForTwo(source, target)!.Equals(target))
                {
                    failSource = null!;
                    failTarget = null!;
                    return true; // implicit widening to target
                }

                return false;
        }
    }

    private static int PrecedenceIndex(DataType type)
    {
        for (int i = 0; i < NumericPrecedence.Count; i++)
        {
            if (NumericPrecedence[i].Equals(type))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether <paramref name="dec"/> holds every value of an integral type with no loss (Spark <c>isWiderThan</c>).</summary>
    private static bool DecimalHolds(DecimalType dec, DataType integral)
    {
        DecimalType need = DecimalArithmetic.ForType(integral); // integral.forType has scale 0
        return dec.Precision - dec.Scale >= need.Precision - need.Scale && dec.Scale >= need.Scale;
    }

    private static DataType? WiderDecimal(DataType left, DataType right)
    {
        // float/double dominate decimal (Spark widens decimal⊕float and decimal⊕double to double).
        if (left is FloatType or DoubleType || right is FloatType or DoubleType)
        {
            return DoubleType.Instance;
        }

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            return null;
        }

        DecimalType a = DecimalArithmetic.ForType(left), b = DecimalArithmetic.ForType(right);
        int scale = Math.Max(a.Scale, b.Scale);
        int range = Math.Max(a.Precision - a.Scale, b.Precision - b.Scale);
        return DecimalArithmetic.Bounded(range + scale, scale);
    }
}
