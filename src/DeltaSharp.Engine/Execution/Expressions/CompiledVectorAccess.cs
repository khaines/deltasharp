using System.Reflection;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution.Expressions;

/// <summary>
/// Non-generic, statically-analyzable wrappers over <see cref="ColumnVector.GetValue{T}"/> /
/// <see cref="ColumnVector.IsNull"/> / <see cref="MutableColumnVector.AppendValue{T}"/> used by the
/// compiled lowering (STORY-03.4.2). The lowering needs a <see cref="MethodInfo"/> for each storage
/// read/write to emit an <c>Expression.Call</c>; obtaining those handles via a delegate method-group
/// (see the <c>…Method</c> fields) keeps the codegen tier <b>trim- and AOT-analyzer clean</b>
/// (no <c>GetMethod(string)</c>, no <c>MakeGenericMethod</c>, which would trip IL2060/IL2070/IL2075).
/// </summary>
/// <remarks>
/// Reads mirror <see cref="ScalarReader"/> exactly: <see cref="ByteType"/> is signed Spark
/// <c>tinyint</c> read through <see cref="sbyte"/> (handled by callers widening to 64-bit), and a
/// decimal mantissa is a <see cref="long"/> when <see cref="DecimalType.IsCompact"/> else an
/// <see cref="Int128"/>. Writes mirror <see cref="VectorMaterializer"/>: the integral-narrowing and
/// compact/wide-decimal append rules are reused verbatim so the compiled output is byte-identical.
/// These methods emit no IL and use no reflection, so they are AOT-safe even though they are only
/// ever reached from the elided compiled tier.
/// </remarks>
internal static class CompiledVectorAccess
{
    // ---- Validity -------------------------------------------------------------------------------

    /// <summary>Reads the validity bit of one logical row (<c>true</c> = SQL NULL).</summary>
    public static bool IsRowNull(ColumnVector vector, int row) => vector.IsNull(row);

    // ---- Storage reads (element type == physical storage type) ----------------------------------

    public static bool ReadBoolean(ColumnVector vector, int row) => vector.GetValue<bool>(row);

    public static byte ReadByte(ColumnVector vector, int row) => vector.GetValue<byte>(row);

    public static short ReadInt16(ColumnVector vector, int row) => vector.GetValue<short>(row);

    public static int ReadInt32(ColumnVector vector, int row) => vector.GetValue<int>(row);

    public static long ReadInt64(ColumnVector vector, int row) => vector.GetValue<long>(row);

    public static float ReadSingle(ColumnVector vector, int row) => vector.GetValue<float>(row);

    public static double ReadDouble(ColumnVector vector, int row) => vector.GetValue<double>(row);

    /// <summary>Reads a compact (<see cref="DecimalType.IsCompact"/>) decimal mantissa.</summary>
    public static long ReadDecimalCompact(ColumnVector vector, int row) => vector.GetValue<long>(row);

    /// <summary>Reads a wide decimal mantissa.</summary>
    public static Int128 ReadDecimalWide(ColumnVector vector, int row) => vector.GetValue<Int128>(row);

    // ---- Storage writes (carrier type == physical storage type) ---------------------------------

    public static void AppendBoolean(MutableColumnVector output, bool value) => output.AppendValue(value);

    public static void AppendByte(MutableColumnVector output, byte value) => output.AppendValue(value);

    public static void AppendInt16(MutableColumnVector output, short value) => output.AppendValue(value);

    public static void AppendInt32(MutableColumnVector output, int value) => output.AppendValue(value);

    public static void AppendInt64(MutableColumnVector output, long value) => output.AppendValue(value);

    public static void AppendSingle(MutableColumnVector output, float value) => output.AppendValue(value);

    public static void AppendDouble(MutableColumnVector output, double value) => output.AppendValue(value);

    /// <summary>Appends a decimal carrier, reusing the compact/wide mantissa rule of the interpreter.</summary>
    public static void AppendDecimal(MutableColumnVector output, DecimalValue value) =>
        VectorMaterializer.AppendDecimal(output, value.Unscaled);

    /// <summary>Appends a SQL NULL lane.</summary>
    public static void AppendNull(MutableColumnVector output) => output.AppendNull();

    // ---- Cached method handles (method-group => MethodInfo; analyzer-safe) -----------------------

    public static readonly MethodInfo IsRowNullMethod = ((Func<ColumnVector, int, bool>)IsRowNull).Method;
    public static readonly MethodInfo ReadBooleanMethod = ((Func<ColumnVector, int, bool>)ReadBoolean).Method;
    public static readonly MethodInfo ReadByteMethod = ((Func<ColumnVector, int, byte>)ReadByte).Method;
    public static readonly MethodInfo ReadInt16Method = ((Func<ColumnVector, int, short>)ReadInt16).Method;
    public static readonly MethodInfo ReadInt32Method = ((Func<ColumnVector, int, int>)ReadInt32).Method;
    public static readonly MethodInfo ReadInt64Method = ((Func<ColumnVector, int, long>)ReadInt64).Method;
    public static readonly MethodInfo ReadSingleMethod = ((Func<ColumnVector, int, float>)ReadSingle).Method;
    public static readonly MethodInfo ReadDoubleMethod = ((Func<ColumnVector, int, double>)ReadDouble).Method;
    public static readonly MethodInfo ReadDecimalCompactMethod = ((Func<ColumnVector, int, long>)ReadDecimalCompact).Method;
    public static readonly MethodInfo ReadDecimalWideMethod = ((Func<ColumnVector, int, Int128>)ReadDecimalWide).Method;

    public static readonly MethodInfo AppendBooleanMethod = ((Action<MutableColumnVector, bool>)AppendBoolean).Method;
    public static readonly MethodInfo AppendByteMethod = ((Action<MutableColumnVector, byte>)AppendByte).Method;
    public static readonly MethodInfo AppendInt16Method = ((Action<MutableColumnVector, short>)AppendInt16).Method;
    public static readonly MethodInfo AppendInt32Method = ((Action<MutableColumnVector, int>)AppendInt32).Method;
    public static readonly MethodInfo AppendInt64Method = ((Action<MutableColumnVector, long>)AppendInt64).Method;
    public static readonly MethodInfo AppendSingleMethod = ((Action<MutableColumnVector, float>)AppendSingle).Method;
    public static readonly MethodInfo AppendDoubleMethod = ((Action<MutableColumnVector, double>)AppendDouble).Method;
    public static readonly MethodInfo AppendDecimalMethod = ((Action<MutableColumnVector, DecimalValue>)AppendDecimal).Method;
    public static readonly MethodInfo AppendNullMethod = ((Action<MutableColumnVector>)AppendNull).Method;
}
