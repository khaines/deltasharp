using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar;

public class StructColumnVectorTests
{
    private static readonly StructType PersonType = new(new[]
    {
        new StructField("id", IntegerType.Instance),
        new StructField("name", StringType.Instance),
    });

    private static MutableColumnVector Ints(params int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(IntegerType.Instance, Math.Max(values.Length, 1));
        foreach (int? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendValue(value.Value);
            }
        }

        return v;
    }

    private static MutableColumnVector Strings(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(StringType.Instance, Math.Max(values.Length, 1));
        foreach (string? value in values)
        {
            if (value is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(value));
            }
        }

        return v;
    }

    private static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void FromChildren_ExposesFieldsAndReportsStructType()
    {
        var s = new StructColumnVector(PersonType, new ColumnVector[] { Ints(1, 2, 3), Strings("a", "b", "c") });

        Assert.Equal(PersonType, s.Type);
        Assert.Equal(3, s.Length);
        Assert.Equal(0, s.Offset);
        Assert.Equal(2, s.FieldCount);
        Assert.False(s.HasNulls);
        Assert.Equal(0, s.NullCount);

        // Field access by ordinal and by name returns the correct child values (mutation-sensitive:
        // an off-by-one in child indexing changes these).
        Assert.Equal(1, s.Child(0).GetValue<int>(0));
        Assert.Equal(3, s.Child(0).GetValue<int>(2));
        Assert.Equal("a", Utf8(s.Child("name").GetBytes(0)));
        Assert.Equal("c", Utf8(s.Child("name").GetBytes(2)));
    }

    [Fact]
    public void NullStructRow_IsDistinctFromNullField()
    {
        // Row 1: valid struct whose "name" FIELD is null. Row 2: the whole STRUCT is null while the
        // child slots still carry (masked) values. This is the load-bearing null distinction.
        var s = new StructColumnVector(
            PersonType,
            new ColumnVector[] { Ints(1, 2, 3), Strings("a", null, "c") },
            nulls: new[] { false, false, true });

        Assert.True(s.HasNulls);
        Assert.Equal(1, s.NullCount);

        Assert.False(s.IsNull(0));
        Assert.False(s.IsNull(1)); // struct valid, only its name field is null
        Assert.True(s.IsNull(2)); // whole struct null

        // A null field inside a valid struct.
        Assert.False(s.Child("name").IsNull(0));
        Assert.True(s.Child("name").IsNull(1));

        // The null struct row masks the row logically, but the child slots keep their values.
        Assert.False(s.Child("id").IsNull(2));
        Assert.Equal(3, s.Child("id").GetValue<int>(2));
        Assert.Equal("c", Utf8(s.Child("name").GetBytes(2)));
    }

    [Fact]
    public void Builder_AppendsRowsThroughChildrenThenCommits()
    {
        var s = new StructColumnVector(PersonType, capacity: 4);
        var id = (MutableColumnVector)s.Child(0);
        var name = (MutableColumnVector)s.Child(1);

        // Row 0: valid.
        id.AppendValue(10);
        name.AppendBytes("x"u8);
        s.EndStruct();

        // Row 1: null struct (children still advanced to stay aligned).
        id.AppendNull();
        name.AppendNull();
        s.AppendNull();

        // Row 2: valid.
        id.AppendValue(30);
        name.AppendBytes("z"u8);
        s.EndStruct();

        Assert.Equal(3, s.Length);
        Assert.Equal(1, s.NullCount);
        Assert.False(s.IsNull(0));
        Assert.True(s.IsNull(1));
        Assert.False(s.IsNull(2));
        Assert.Equal(10, s.Child(0).GetValue<int>(0));
        Assert.Equal(30, s.Child(0).GetValue<int>(2));
        Assert.Equal("z", Utf8(s.Child("name").GetBytes(2)));
    }

    [Fact]
    public void Commit_RejectsChildrenThatAreNotAdvancedByExactlyOne()
    {
        var s = new StructColumnVector(PersonType, capacity: 2);
        var id = (MutableColumnVector)s.Child(0);

        // Advanced "id" but not "name": committing must fail rather than desync the children.
        id.AppendValue(1);
        Assert.Throws<InvalidOperationException>(() => s.EndStruct());
        Assert.Throws<InvalidOperationException>(() => s.AppendNull());
        Assert.Equal(0, s.Length);
    }

    [Fact]
    public void Slice_ReBasesRowsChildrenAndValidity()
    {
        // Rows: 0..4 with the struct null at rows 1 and 4.
        var s = new StructColumnVector(
            PersonType,
            new ColumnVector[] { Ints(0, 1, 2, 3, 4), Strings("n0", "n1", "n2", "n3", "n4") },
            nulls: new[] { false, true, false, false, true });

        ColumnVector slice = s.Slice(1, 3); // logical rows 1..3 of the parent
        var st = Assert.IsType<StructColumnVector>(slice);

        Assert.Equal(3, st.Length);
        Assert.Equal(1, st.Offset);
        Assert.Equal(1, st.NullCount); // only parent row 1 (now slice row 0) is null in [1,4)
        Assert.True(st.IsNull(0)); // parent row 1
        Assert.False(st.IsNull(1)); // parent row 2

        // The children re-base too: slice row 0 == parent row 1.
        Assert.Equal(1, st.Child(0).GetValue<int>(0));
        Assert.Equal(3, st.Child(0).GetValue<int>(2));
        Assert.Equal("n2", Utf8(st.Child("name").GetBytes(1)));
    }

    [Fact]
    public void Select_ThrowsNotSupportedNamingTheStruct()
    {
        var s = new StructColumnVector(PersonType, new ColumnVector[] { Ints(1, 2), Strings("a", "b") });
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => s.Select(new SelectionVector(new[] { 1, 0 })));
        Assert.Contains("struct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScalarAndTopLevelMutators_AreUnavailable()
    {
        var s = new StructColumnVector(PersonType, new ColumnVector[] { Ints(1), Strings("a") });
        Assert.Throws<InvalidOperationException>(() => s.GetValues<int>().Length);
        Assert.Throws<InvalidOperationException>(() => s.GetBytes(0));

        var builder = new StructColumnVector(PersonType, capacity: 1);
        Assert.Throws<InvalidOperationException>(() => builder.AppendValue(1));
        Assert.Throws<InvalidOperationException>(() => builder.AppendBytes("x"u8));
        Assert.Throws<InvalidOperationException>(() => builder.SetValue(0, 1));
    }

    [Fact]
    public void FromChildren_RejectsInconsistentChildren()
    {
        // Wrong field count.
        Assert.Throws<ArgumentException>(() => new StructColumnVector(PersonType, new ColumnVector[] { Ints(1) }));

        // Wrong child type (name declared string, supplied int).
        Assert.Throws<ArgumentException>(() =>
            new StructColumnVector(PersonType, new ColumnVector[] { Ints(1), Ints(2) }));

        // Unequal child lengths.
        Assert.Throws<ArgumentException>(() =>
            new StructColumnVector(PersonType, new ColumnVector[] { Ints(1, 2), Strings("a") }));

        // Null-flag length disagrees with the rows.
        Assert.Throws<ArgumentException>(() =>
            new StructColumnVector(PersonType, new ColumnVector[] { Ints(1, 2), Strings("a", "b") }, nulls: new[] { true }));
    }

    [Fact]
    public void Child_RejectsUnknownOrdinalAndName()
    {
        var s = new StructColumnVector(PersonType, new ColumnVector[] { Ints(1), Strings("a") });
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Child(2));
        Assert.Throws<KeyNotFoundException>(() => s.Child("missing"));
    }
}
