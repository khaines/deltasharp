using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// Vector builders for the nested write tests. Each builds an <b>immutable</b> nested
/// <see cref="ColumnVector"/> from a plain CLR model, so a test states its rows declaratively and the
/// round-trip oracle can compare the decoded vector against the same model.
/// </summary>
internal static class NestedVectors
{
    /// <summary>Builds a <c>struct&lt;a:int, b:string&gt;</c> vector. A null row is a null struct; a null
    /// component is a null field within a present struct.</summary>
    public static StructColumnVector IntStringStruct(
        StructType type, IReadOnlyList<(int? A, string? B)?> rows)
    {
        MutableColumnVector a = ColumnVectors.Create(DataTypes.IntegerType, rows.Count);
        MutableColumnVector b = ColumnVectors.Create(DataTypes.StringType, rows.Count);
        var nulls = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            (int? A, string? B)? row = rows[i];
            nulls[i] = row is null;
            if (row is null || row.Value.A is null)
            {
                a.AppendNull();
            }
            else
            {
                a.AppendValue(row.Value.A.Value);
            }

            if (row is null || row.Value.B is null)
            {
                b.AppendNull();
            }
            else
            {
                b.AppendBytes(Encoding.UTF8.GetBytes(row.Value.B));
            }
        }

        return new StructColumnVector(type, new ColumnVector[] { a, b }, nulls);
    }

    /// <summary>Builds an <c>array&lt;int&gt;</c> vector. A null row is a null list; an empty inner array is
    /// an empty list; a null element is a null within a present list.</summary>
    public static ListColumnVector IntList(ArrayType type, IReadOnlyList<int?[]?> rows)
    {
        MutableColumnVector elements = ColumnVectors.Create(DataTypes.IntegerType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            int?[]? row = rows[i];
            nulls[i] = row is null;
            if (row is not null)
            {
                foreach (int? value in row)
                {
                    if (value is null)
                    {
                        elements.AppendNull();
                    }
                    else
                    {
                        elements.AppendValue(value.Value);
                    }

                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        return new ListColumnVector(type, elements, offsets, nulls);
    }

    /// <summary>Builds a <c>map&lt;string,int&gt;</c> vector. A null row is a null map; an empty entry list is
    /// an empty map; a null value is a null within a present entry.</summary>
    public static MapColumnVector StringIntMap(
        MapType type, IReadOnlyList<IReadOnlyList<(string Key, int? Value)>?> rows)
    {
        MutableColumnVector keys = ColumnVectors.Create(DataTypes.StringType, 16);
        MutableColumnVector values = ColumnVectors.Create(DataTypes.IntegerType, 16);
        var offsets = new int[rows.Count + 1];
        var nulls = new bool[rows.Count];
        int cursor = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            offsets[i] = cursor;
            IReadOnlyList<(string Key, int? Value)>? row = rows[i];
            nulls[i] = row is null;
            if (row is not null)
            {
                foreach ((string key, int? value) in row)
                {
                    keys.AppendBytes(Encoding.UTF8.GetBytes(key));
                    if (value is null)
                    {
                        values.AppendNull();
                    }
                    else
                    {
                        values.AppendValue(value.Value);
                    }

                    cursor++;
                }
            }
        }

        offsets[rows.Count] = cursor;
        return new MapColumnVector(type, keys, values, offsets, nulls);
    }

    /// <summary>Reads a decoded <c>struct&lt;a:int, b:string&gt;</c> back into the model shape.</summary>
    public static List<(int? A, string? B)?> ReadIntStringStruct(StructColumnVector vector)
    {
        ColumnVector a = vector.Child(0);
        ColumnVector b = vector.Child(1);
        var result = new List<(int? A, string? B)?>(vector.Length);
        for (int i = 0; i < vector.Length; i++)
        {
            if (vector.IsNull(i))
            {
                result.Add(null);
                continue;
            }

            result.Add((
                a.IsNull(i) ? null : a.GetValue<int>(i),
                b.IsNull(i) ? null : Encoding.UTF8.GetString(b.GetBytes(i))));
        }

        return result;
    }

    /// <summary>Reads a decoded <c>array&lt;int&gt;</c> back into the model shape.</summary>
    public static List<int?[]?> ReadIntList(ListColumnVector vector)
    {
        var result = new List<int?[]?>(vector.Length);
        for (int i = 0; i < vector.Length; i++)
        {
            if (vector.IsNull(i))
            {
                result.Add(null);
                continue;
            }

            ColumnVector elements = vector.ElementsAt(i);
            var row = new int?[elements.Length];
            for (int e = 0; e < elements.Length; e++)
            {
                row[e] = elements.IsNull(e) ? null : elements.GetValue<int>(e);
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>Reads a decoded <c>map&lt;string,int&gt;</c> back into the model shape.</summary>
    public static List<List<(string Key, int? Value)>?> ReadStringIntMap(MapColumnVector vector)
    {
        var result = new List<List<(string Key, int? Value)>?>(vector.Length);
        for (int i = 0; i < vector.Length; i++)
        {
            if (vector.IsNull(i))
            {
                result.Add(null);
                continue;
            }

            ColumnVector keys = vector.KeysAt(i);
            ColumnVector values = vector.ValuesAt(i);
            var row = new List<(string Key, int? Value)>(keys.Length);
            for (int e = 0; e < keys.Length; e++)
            {
                row.Add((
                    Encoding.UTF8.GetString(keys.GetBytes(e)),
                    values.IsNull(e) ? null : values.GetValue<int>(e)));
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>Asserts two struct models are equal, distinguishing a null struct from a null field.</summary>
    public static void AssertStructsEqual(
        IReadOnlyList<(int? A, string? B)?> expected, IReadOnlyList<(int? A, string? B)?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] is null, actual[i] is null);
            if (expected[i] is not null)
            {
                Assert.Equal(expected[i]!.Value.A, actual[i]!.Value.A);
                Assert.Equal(expected[i]!.Value.B, actual[i]!.Value.B);
            }
        }
    }

    /// <summary>Asserts two list models are equal, distinguishing a null list from an empty list and a null
    /// element.</summary>
    public static void AssertListsEqual(IReadOnlyList<int?[]?> expected, IReadOnlyList<int?[]?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] is null, actual[i] is null);
            if (expected[i] is not null)
            {
                Assert.Equal(expected[i]!.Length, actual[i]!.Length);
                for (int e = 0; e < expected[i]!.Length; e++)
                {
                    Assert.Equal(expected[i]![e], actual[i]![e]);
                }
            }
        }
    }

    /// <summary>Asserts two map models are equal, distinguishing a null map from an empty map and a null
    /// value, and preserving entry order.</summary>
    public static void AssertMapsEqual(
        IReadOnlyList<IReadOnlyList<(string Key, int? Value)>?> expected,
        IReadOnlyList<List<(string Key, int? Value)>?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] is null, actual[i] is null);
            if (expected[i] is not null)
            {
                Assert.Equal(expected[i]!.Count, actual[i]!.Count);
                for (int e = 0; e < expected[i]!.Count; e++)
                {
                    Assert.Equal(expected[i]![e].Key, actual[i]![e].Key);
                    Assert.Equal(expected[i]![e].Value, actual[i]![e].Value);
                }
            }
        }
    }
}
