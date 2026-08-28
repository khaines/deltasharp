using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// A generic, recursively-built nested value model for the #873 nested-within-nested write→read round-trip
/// oracle (§3.3). A test expresses its fixture as a tree of <see cref="StructVal"/>/<see cref="ArrVal"/>/
/// <see cref="MapVal"/> and scalar CLR values (with <see langword="null"/> for a null container/leaf at any
/// level); <see cref="Build"/> shreds that tree into an immutable nested <see cref="ColumnVector"/>, and
/// <see cref="ReadValue"/> reconstructs the same tree from the decoded vector. The round-trip through the
/// shipped 585a reader is the correctness oracle: value + null-structure identity at every level.
/// </summary>
internal static class NestedValueModel
{
    internal sealed record StructVal(object?[] Children);

    internal sealed record ArrVal(object?[] Items);

    internal sealed record MapVal((object? Key, object? Value)[] Entries);

    internal static StructVal Struct(params object?[] children) => new(children);

    internal static ArrVal Arr(params object?[] items) => new(items);

    internal static MapVal Map(params (object? Key, object? Value)[] entries) => new(entries);

    // ----- build -----

    public static ColumnVector Build(DataType type, IReadOnlyList<object?> rows)
    {
        switch (type)
        {
            case StructType structType:
                {
                    var nulls = new bool[rows.Count];
                    var childRows = new List<object?>[structType.Count];
                    for (int c = 0; c < structType.Count; c++)
                    {
                        childRows[c] = new List<object?>(rows.Count);
                    }

                    for (int i = 0; i < rows.Count; i++)
                    {
                        object? row = rows[i];
                        if (row is null)
                        {
                            nulls[i] = true;
                            for (int c = 0; c < structType.Count; c++)
                            {
                                childRows[c].Add(null);
                            }

                            continue;
                        }

                        var sv = (StructVal)row;
                        Assert.Equal(structType.Count, sv.Children.Length);
                        for (int c = 0; c < structType.Count; c++)
                        {
                            childRows[c].Add(sv.Children[c]);
                        }
                    }

                    var children = new ColumnVector[structType.Count];
                    for (int c = 0; c < structType.Count; c++)
                    {
                        children[c] = Build(structType[c].DataType, childRows[c]);
                    }

                    return new StructColumnVector(structType, children, nulls);
                }

            case ArrayType array:
                {
                    var offsets = new int[rows.Count + 1];
                    var nulls = new bool[rows.Count];
                    var elementRows = new List<object?>();
                    int cursor = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        offsets[i] = cursor;
                        object? row = rows[i];
                        if (row is null)
                        {
                            nulls[i] = true;
                            continue;
                        }

                        var av = (ArrVal)row;
                        foreach (object? item in av.Items)
                        {
                            elementRows.Add(item);
                            cursor++;
                        }
                    }

                    offsets[rows.Count] = cursor;
                    ColumnVector elements = Build(array.ElementType, elementRows);
                    return new ListColumnVector(array, elements, offsets, nulls);
                }

            case MapType map:
                {
                    var offsets = new int[rows.Count + 1];
                    var nulls = new bool[rows.Count];
                    var keyRows = new List<object?>();
                    var valueRows = new List<object?>();
                    int cursor = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        offsets[i] = cursor;
                        object? row = rows[i];
                        if (row is null)
                        {
                            nulls[i] = true;
                            continue;
                        }

                        var mv = (MapVal)row;
                        foreach ((object? key, object? value) in mv.Entries)
                        {
                            keyRows.Add(key);
                            valueRows.Add(value);
                            cursor++;
                        }
                    }

                    offsets[rows.Count] = cursor;
                    ColumnVector keys = Build(map.KeyType, keyRows);
                    ColumnVector values = Build(map.ValueType, valueRows);
                    return new MapColumnVector(map, keys, values, offsets, nulls);
                }

            default:
                {
                    MutableColumnVector vector = ColumnVectors.Create(type, Math.Max(rows.Count, 1));
                    foreach (object? row in rows)
                    {
                        AppendScalar(vector, type, row);
                    }

                    return vector;
                }
        }
    }

    private static void AppendScalar(MutableColumnVector vector, DataType type, object? value)
    {
        if (value is null)
        {
            vector.AppendNull();
            return;
        }

        switch (type)
        {
            case IntegerType:
                vector.AppendValue((int)value);
                break;
            case LongType:
                vector.AppendValue((long)value);
                break;
            case DoubleType:
                vector.AppendValue((double)value);
                break;
            case StringType:
                vector.AppendBytes(Encoding.UTF8.GetBytes((string)value));
                break;
            default:
                throw new NotSupportedException($"NestedValueModel has no scalar lane for {type.TypeName}.");
        }
    }

    // ----- read -----

    public static object? ReadValue(ColumnVector vector, int row, DataType type)
    {
        switch (type)
        {
            case StructType structType:
                {
                    var sv = (StructColumnVector)vector;
                    if (sv.IsNull(row))
                    {
                        return null;
                    }

                    var children = new object?[structType.Count];
                    for (int c = 0; c < structType.Count; c++)
                    {
                        children[c] = ReadValue(sv.Child(c), row, structType[c].DataType);
                    }

                    return new StructVal(children);
                }

            case ArrayType array:
                {
                    var lv = (ListColumnVector)vector;
                    if (lv.IsNull(row))
                    {
                        return null;
                    }

                    (int start, int length) = lv.RawElementSpan(row);
                    ColumnVector elements = lv.Elements;
                    var items = new object?[length];
                    for (int e = 0; e < length; e++)
                    {
                        items[e] = ReadValue(elements, start + e, array.ElementType);
                    }

                    return new ArrVal(items);
                }

            case MapType map:
                {
                    var mv = (MapColumnVector)vector;
                    if (mv.IsNull(row))
                    {
                        return null;
                    }

                    (int start, int length) = mv.RawEntrySpan(row);
                    ColumnVector keys = mv.Keys;
                    ColumnVector values = mv.Values;
                    var entries = new (object? Key, object? Value)[length];
                    for (int e = 0; e < length; e++)
                    {
                        entries[e] = (
                            ReadValue(keys, start + e, map.KeyType),
                            ReadValue(values, start + e, map.ValueType));
                    }

                    return new MapVal(entries);
                }

            default:
                return vector.IsNull(row) ? null : ReadScalar(vector, row, type);
        }
    }

    private static object ReadScalar(ColumnVector vector, int row, DataType type) => type switch
    {
        IntegerType => vector.GetValue<int>(row),
        LongType => vector.GetValue<long>(row),
        DoubleType => vector.GetValue<double>(row),
        StringType => Encoding.UTF8.GetString(vector.GetBytes(row)),
        _ => throw new NotSupportedException($"NestedValueModel has no scalar lane for {type.TypeName}."),
    };

    // ----- compare (null-structure sensitive at every level) -----

    public static void AssertEqual(object? expected, object? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        switch (expected)
        {
            case StructVal s:
                {
                    var a = Assert.IsType<StructVal>(actual);
                    Assert.Equal(s.Children.Length, a.Children.Length);
                    for (int i = 0; i < s.Children.Length; i++)
                    {
                        AssertEqual(s.Children[i], a.Children[i]);
                    }

                    break;
                }

            case ArrVal ar:
                {
                    var a = Assert.IsType<ArrVal>(actual);
                    Assert.Equal(ar.Items.Length, a.Items.Length);
                    for (int i = 0; i < ar.Items.Length; i++)
                    {
                        AssertEqual(ar.Items[i], a.Items[i]);
                    }

                    break;
                }

            case MapVal m:
                {
                    var a = Assert.IsType<MapVal>(actual);
                    Assert.Equal(m.Entries.Length, a.Entries.Length);
                    for (int i = 0; i < m.Entries.Length; i++)
                    {
                        AssertEqual(m.Entries[i].Key, a.Entries[i].Key);
                        AssertEqual(m.Entries[i].Value, a.Entries[i].Value);
                    }

                    break;
                }

            default:
                Assert.Equal(expected, actual);
                break;
        }
    }
}
