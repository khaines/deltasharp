using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// The fixed geometry of a binary row for a given <see cref="StructType"/> (an <c>UnsafeRow</c>
/// analog, ADR-0008): a leading word-aligned <see cref="NullBitSetBytes"/> null bitset followed
/// by a <see cref="FixedRegionBytes"/> fixed region of one 8-byte slot per field. Anything
/// variable-length (string, binary, large decimal, array, map, struct) is stored after the
/// header in the row's variable region, with its 8-byte slot holding a packed
/// (offset, length) reference. The header is always 8-byte aligned and the encoder pads the
/// variable region so the total row size stays 8-byte aligned (STORY-02.4.1 AC1).
/// </summary>
public sealed class RowLayout
{
    /// <summary>Every field occupies exactly one 8-byte slot in the fixed region (inline value or (offset,length) ref).</summary>
    public const int SlotBytes = 8;

    private readonly bool[] _fixedWidth;

    /// <summary>Builds the geometry for <paramref name="schema"/>.</summary>
    /// <param name="schema">The row schema (a top-level struct).</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="UnsupportedTypeException">A field type has no physical layout (for example <c>void</c>).</exception>
    public RowLayout(StructType schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        FieldCount = schema.Count;
        _fixedWidth = new bool[FieldCount];
        for (int i = 0; i < FieldCount; i++)
        {
            // Throws UnsupportedTypeException for void; classifies everything else.
            _fixedWidth[i] = IsInlineFixedWidth(schema[i].DataType);
        }

        NullBitSetBytes = RowNullBitSet.ByteSize(FieldCount);
        FixedRegionBytes = FieldCount * SlotBytes;
    }

    /// <summary>The schema this geometry describes.</summary>
    public StructType Schema { get; }

    /// <summary>The number of fields (and 8-byte fixed-region slots).</summary>
    public int FieldCount { get; }

    /// <summary>The byte size of the leading null bitset (a multiple of 8).</summary>
    public int NullBitSetBytes { get; }

    /// <summary>The byte size of the fixed region (<see cref="FieldCount"/> × 8).</summary>
    public int FixedRegionBytes { get; }

    /// <summary>The size of the fixed header (null bitset + fixed region); the variable region starts here. Always a multiple of 8.</summary>
    public int HeaderBytes => NullBitSetBytes + FixedRegionBytes;

    /// <summary>The byte offset of field <paramref name="index"/>'s 8-byte slot within a row.</summary>
    public int SlotOffset(int index) => NullBitSetBytes + (index * SlotBytes);

    /// <summary>Whether field <paramref name="index"/> stores its value inline in its 8-byte slot (vs. a variable-region reference).</summary>
    public bool IsInline(int index) => _fixedWidth[index];

    /// <summary>
    /// Whether <paramref name="type"/> stores its value inline in an 8-byte slot. True for the
    /// ≤8-byte fixed-width types and compact decimals; false for variable-width, 16-byte
    /// decimals, and nested types — which live in the variable region.
    /// </summary>
    /// <exception cref="UnsupportedTypeException"><paramref name="type"/> has no physical layout (for example <c>void</c>).</exception>
    public static bool IsInlineFixedWidth(DataType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        PhysicalLayout layout = type.GetPhysicalLayout();
        return layout.Kind == PhysicalLayoutKind.FixedWidth && layout.FixedWidthBytes <= SlotBytes;
    }
}
