using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Materializes an execution <see cref="BatchResult"/> into Core <see cref="Row"/>s, converting each
/// EPIC-03 <see cref="ColumnVector"/> lane to the natural CLR value for its logical
/// <see cref="DataType"/> (ADR-0002 column format → Spark-compatible <see cref="Row"/> values),
/// null-aware. It reads through <see cref="ColumnBatch.SelectedColumn"/> so any selection vector a
/// filter/limit left on a batch is honored.
/// </summary>
internal static class RowMaterializer
{
    /// <summary>Materializes every logical row of every batch into a <see cref="Row"/>, honoring result bounds.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <param name="maxRows">The maximum rows to materialize, or <see langword="null"/> for unbounded.</param>
    /// <param name="maxBytes">The maximum estimated bytes to materialize, or <see langword="null"/> for unbounded.</param>
    /// <param name="cancellationToken">The effective cancellation token (user cancel linked with any timeout).</param>
    /// <returns>All rows, in batch-then-row order.</returns>
    /// <exception cref="ResultLimitExceededException">A configured row/byte bound would be exceeded (checked
    /// <b>before</b> the offending batch is materialized — bounded, not OOM).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<Row> Materialize(
        BatchResult result, long? maxRows, long? maxBytes, CancellationToken cancellationToken)
    {
        StructType schema = result.Schema;

        // Fail-close an adversarially deep nested result type before the recursive per-row decode can
        // overflow the stack (symmetric with the encode door). Checked once per column (schema is fixed).
        for (int c = 0; c < schema.Count; c++)
        {
            NestedTypeDepth.Ensure(schema[c].DataType, QueryExecutionStage.Materialize, schema[c].Name);
        }

        // Bind one decode-plan reader per column ONCE, before the per-row loop: struct child views are
        // hoisted and array/map element/key/value fields are pre-synthesized, so the per-row decode adds no
        // structural allocation (#610). Readers carry a per-vector memo and are single-threaded within this call.
        var readers = new ColumnReader[schema.Count];
        for (int c = 0; c < schema.Count; c++)
        {
            readers[c] = ColumnReader.For(schema[c].DataType, schema[c].Name);
        }

        var rows = new List<Row>();
        long rowsSoFar = 0;
        long bytesSoFar = 0;
        foreach (ColumnBatch batch in result.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int rowCount = batch.LogicalRowCount;
            int columnCount = schema.Count;
            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            // Enforce the result bounds BEFORE materializing this batch's rows, so the row list is never
            // grown past the bound: a deterministic fail-fast rather than an OOM (criterion 3).
            if (maxRows is { } rowCap && rowsSoFar + rowCount > rowCap)
            {
                throw ResultLimitExceededException.Rows(rowCap, rowsSoFar + rowCount);
            }

            if (maxBytes is { } byteCap)
            {
                long batchBytes = EstimateBatchBytes(columns, schema, rowCount);
                if (bytesSoFar + batchBytes > byteCap)
                {
                    throw ResultLimitExceededException.Bytes(byteCap, bytesSoFar + batchBytes);
                }

                bytesSoFar += batchBytes;
            }

            for (int r = 0; r < rowCount; r++)
            {
                if ((r & CancellationPollMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var values = new object?[columnCount];
                for (int c = 0; c < columnCount; c++)
                {
                    values[c] = columns[c].IsNull(r) ? null : readers[c].Read(columns[c], r, cancellationToken);
                }

                rows.Add(new Row(schema, values));
            }

            rowsSoFar += rowCount;
        }

        return rows;
    }

    /// <summary>Sums the logical row counts across the result's batches without materializing values.</summary>
    /// <param name="result">The executed schema + batches.</param>
    /// <param name="cancellationToken">The effective cancellation token, polled per batch so a
    /// cancel/timeout stops a long count promptly (criterion 1).</param>
    /// <returns>The total logical row count.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static long CountRows(BatchResult result, CancellationToken cancellationToken = default)
    {
        long count = 0;
        foreach (ColumnBatch batch in result.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count += batch.LogicalRowCount;
        }

        return count;
    }

    // A decode-plan node bound once per schema position (before the per-row loop), so the per-row decode
    // allocates nothing structural. Struct child views are hoisted (Child(i) is a pure function of the vector
    // instance — for a sliced struct it returns the identical slice for every row), and array/map
    // element/key/value names are constant, so no per-row StructField is synthesized (#610). Reader instances
    // carry a mutable per-vector memo and are used single-threaded within one Materialize call.
    private abstract class ColumnReader
    {
        // Reads column[index] as its natural CLR value. The caller has already gated on IsNull for this
        // position (top-level columns, struct children, and array/map elements each gate their own nulls).
        public abstract object Read(ColumnVector column, int index, CancellationToken cancellationToken);

        // Builds the reader for a logical type ONCE (plan-build time), so the per-cell decode is a single
        // virtual call into a type-specialized leaf with no per-cell type switch: nested types get a hoisting
        // reader; each scalar type gets its own leaf reader. The field name is captured only for the
        // out-of-range date/timestamp diagnostics.
        public static ColumnReader For(DataType type, string name) => type switch
        {
            BooleanType => BooleanReader.Instance,
            ByteType => ByteReader.Instance,
            ShortType => ShortReader.Instance,
            IntegerType => Int32Reader.Instance,
            LongType => Int64Reader.Instance,
            FloatType => FloatReader.Instance,
            DoubleType => DoubleReader.Instance,
            StringType => StringReader.Instance,
            BinaryType => BinaryReader.Instance,
            DecimalType decimalType => new DecimalReader(decimalType),
            DateType => new DateReader(name, type),
            TimestampType => new TimestampReader(name, type, DateTimeKind.Utc),
            TimestampNtzType => new TimestampReader(name, type, DateTimeKind.Unspecified),
            StructType structType => new StructReader(structType),
            ArrayType arrayType => new ListReader(arrayType),
            MapType mapType => new MapReader(mapType),
            _ => throw new UnsupportedPlanException(
                QueryExecutionStage.Materialize,
                $"Row materialization has no CLR mapping for type '{type.SimpleString}'."),
        };
    }

    // Type-specialized scalar leaf readers. Each decodes one lane to its natural CLR value with no per-cell
    // type switch (the switch is resolved once in ColumnReader.For). The stateless leaves are shared singletons
    // (no per-column allocation); readers that carry state (decimal scale, the field name for diagnostics, the
    // timestamp kind) are built once per column. Null is gated by the caller before Read is invoked.
    private sealed class BooleanReader : ColumnReader
    {
        public static readonly BooleanReader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetValue<bool>(index);
    }

    private sealed class ByteReader : ColumnReader
    {
        public static readonly ByteReader Instance = new();

        // Spark ByteType is a signed tinyint; the Engine stores it as an unsigned byte lane.
        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            unchecked((sbyte)column.GetValue<byte>(index));
    }

    private sealed class ShortReader : ColumnReader
    {
        public static readonly ShortReader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetValue<short>(index);
    }

    private sealed class Int32Reader : ColumnReader
    {
        public static readonly Int32Reader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetValue<int>(index);
    }

    private sealed class Int64Reader : ColumnReader
    {
        public static readonly Int64Reader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetValue<long>(index);
    }

    private sealed class FloatReader : ColumnReader
    {
        public static readonly FloatReader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetValue<float>(index);
    }

    private sealed class DoubleReader : ColumnReader
    {
        public static readonly DoubleReader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetValue<double>(index);
    }

    private sealed class StringReader : ColumnReader
    {
        public static readonly StringReader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            Encoding.UTF8.GetString(column.GetBytes(index));
    }

    private sealed class BinaryReader : ColumnReader
    {
        public static readonly BinaryReader Instance = new();

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            column.GetBytes(index).ToArray();
    }

    private sealed class DecimalReader : ColumnReader
    {
        private readonly DecimalType _type;

        public DecimalReader(DecimalType type) => _type = type;

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            ReadDecimal(column, _type, index);
    }

    private sealed class DateReader : ColumnReader
    {
        private readonly string _name;
        private readonly DataType _type;

        public DateReader(string name, DataType type)
        {
            _name = name;
            _type = type;
        }

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            ReadDate(column, _name, _type, index);
    }

    // Handles both TimestampType (kind Utc) and TimestampNtzType (kind Unspecified, timezone-less #533); the
    // two differ only by the DateTimeKind stamped on the reconstructed instant.
    private sealed class TimestampReader : ColumnReader
    {
        private readonly string _name;
        private readonly DataType _type;
        private readonly DateTimeKind _kind;

        public TimestampReader(string name, DataType type, DateTimeKind kind)
        {
            _name = name;
            _type = type;
            _kind = kind;
        }

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken) =>
            ReadTimestampInstant(column, _name, _type, index, _kind);
    }

    // Reads a struct value as a nested Row (the exact inverse of LocalRelationBatches' struct encode, and the
    // CreateDataFrame nested CLR convention #608): each field is read from its child at the same logical index,
    // null-aware. A null struct row never reaches here — the caller gates on IsNull. Child views are hoisted:
    // Child(i) depends only on the struct vector's window (offset/length), not the row, so a one-entry memo
    // keyed by vector identity computes them once (#610) — for a sliced (limit) result this replaces
    // O(rows x fields) slice allocations with O(fields) per distinct vector; for a whole/offset-0 vector
    // Child returns the raw child (no allocation) and the memo is a free no-op.
    private sealed class StructReader : ColumnReader
    {
        private readonly StructType _type;
        private readonly ColumnReader[] _fields;
        private StructColumnVector? _boundVector;
        private ColumnVector[]? _boundChildren;

        public StructReader(StructType type)
        {
            _type = type;
            _fields = new ColumnReader[type.Count];
            for (int i = 0; i < type.Count; i++)
            {
                _fields[i] = For(type[i].DataType, type[i].Name);
            }
        }

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken)
        {
            var vector = (StructColumnVector)column;
            ColumnVector[] children = BindChildren(vector);
            var values = new object?[_type.Count];
            for (int i = 0; i < _type.Count; i++)
            {
                ColumnVector child = children[i];
                values[i] = child.IsNull(index) ? null : _fields[i].Read(child, index, cancellationToken);
            }

            return new Row(_type, values);
        }

        // Returns the per-field child views for this vector, computing them once per distinct vector instance.
        // Correct because Child(i) is a pure function of the vector instance (its offset/length window), so a
        // reference-equal vector yields identical children; a different instance (the next batch's column, or a
        // per-row array/map element view) misses and rebinds. Single-entry: struct positions are read in
        // vector-stable runs (all rows of a top-level/nested struct share one vector; all elements of one
        // array/map cell share one element view), so a single slot captures every reuse without thrashing.
        private ColumnVector[] BindChildren(StructColumnVector vector)
        {
            if (ReferenceEquals(vector, _boundVector))
            {
                return _boundChildren!;
            }

            var children = new ColumnVector[_type.Count];
            for (int i = 0; i < _type.Count; i++)
            {
                children[i] = vector.Child(i);
            }

            _boundVector = vector;
            _boundChildren = children;
            return children;
        }
    }

    // Reads an array value as an object?[] (an IReadOnlyList<object?>/IEnumerable, the inverse of the
    // non-string-IEnumerable array encode): the row's elements are read from the per-row element view,
    // null-aware. A null array row never reaches here — the caller gates on IsNull; an empty array yields an
    // empty array. The element reader is built ONCE with a constant "element" name, so no per-row StructField is
    // synthesized (#610). The per-cell element VIEW (ElementsAt) is inherently row-scoped — it is the row's own
    // offset window, so it is resolved per row (never hoisted); a struct-typed element reader still hoists its
    // child views WITHIN a cell (ElementsAt returns one vector for all the cell's elements, so the struct memo
    // binds once and hits across elements) and rebinds across cells. The token is polled per element so a single
    // row carrying a huge collection stays cancellable (the row-level poll alone would not interrupt one giant cell).
    private sealed class ListReader : ColumnReader
    {
        private readonly ColumnReader _element;

        public ListReader(ArrayType type) => _element = For(type.ElementType, "element");

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken)
        {
            var vector = (ListColumnVector)column;
            ColumnVector elements = vector.ElementsAt(index);
            int count = elements.Length;
            var items = new object?[count];
            for (int j = 0; j < count; j++)
            {
                if ((j & CancellationPollMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                items[j] = elements.IsNull(j) ? null : _element.Read(elements, j, cancellationToken);
            }

            return items;
        }
    }

    // Reads a map value as a Dictionary<object, object?> (an IDictionary, the inverse of the IDictionary map
    // encode): the row's entries are read from the parallel per-row key/value views. Keys are non-null (MapType
    // invariant); values are null-aware. A null map row never reaches here — the caller gates on IsNull; an
    // empty map yields an empty dictionary. On the rare stored duplicate key, last value wins. The key/value
    // readers are built ONCE with constant "key"/"value" names, so no per-row StructField is synthesized (#610).
    // The token is polled per entry so a huge map row stays cancellable.
    private sealed class MapReader : ColumnReader
    {
        private readonly ColumnReader _key;
        private readonly ColumnReader _value;

        public MapReader(MapType type)
        {
            _key = For(type.KeyType, "key");
            _value = For(type.ValueType, "value");
        }

        public override object Read(ColumnVector column, int index, CancellationToken cancellationToken)
        {
            var vector = (MapColumnVector)column;
            ColumnVector keys = vector.KeysAt(index);
            ColumnVector values = vector.ValuesAt(index);
            int count = keys.Length;
            var map = new Dictionary<object, object?>(count);
            for (int j = 0; j < count; j++)
            {
                if ((j & CancellationPollMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                object key = _key.Read(keys, j, cancellationToken);
                map[key] = values.IsNull(j) ? null : _value.Read(values, j, cancellationToken);
            }

            return map;
        }
    }

    // The driver polls the effective cancellation token every 1024 materialized rows (a power-of-two
    // mask keeps the check branch-cheap) so a cancel/timeout stops a large single-batch result promptly.
    private const int CancellationPollMask = 1023;

    // A best-effort estimate of the driver bytes materializing this batch would hold. It sums the columnar
    // value bytes (fixed-width: rows × width; variable-width: sum of non-null value lengths, mirroring the
    // Engine scan's EstimateBatchBytes) PLUS a conservative per-row/per-value CLR object-overhead term:
    // Materialize builds a List<Row> of BOXED CLR values (each value a heap object with a header, each Row
    // an object plus an object?[] array), which the coarse columnar figure alone under-counts by roughly an
    // order of magnitude. This is NOT an exact driver-heap figure (see MaxResultBytes doc / #176 #9) — it
    // is a deliberately pessimistic safety proxy the result byte-bound (criterion 3) is enforced against so
    // the cap trips before, not after, an over-optimistic estimate would have let the heap balloon.
    private static long EstimateBatchBytes(ColumnVector[] columns, StructType schema, int rowCount)
    {
        long bytes = 0;
        for (int c = 0; c < columns.Length; c++)
        {
            ColumnVector column = columns[c];
            DataType type = schema[c].DataType;
            if (type is StringType or BinaryType)
            {
                for (int r = 0; r < rowCount; r++)
                {
                    if (!column.IsNull(r))
                    {
                        bytes += column.GetBytes(r).Length;
                    }
                }
            }
            else
            {
                bytes += (long)rowCount * FixedWidthBytes(type);
            }
        }

        // Add the boxed-value / Row-object overhead the columnar figure omits (per value: a boxed heap
        // object + its object?[] slot; per row: the Row object + its array). Conservative on a 64-bit CLR.
        bytes += (long)rowCount * columns.Length * PerValueObjectOverheadBytes;
        bytes += (long)rowCount * PerRowObjectOverheadBytes;
        return bytes;
    }

    // Conservative 64-bit CLR overhead for a boxed value (object header + padding + the enclosing
    // object?[] reference slot) and for a materialized Row (the Row object + its object?[] array header).
    // These make the byte estimate pessimistic rather than wildly optimistic (#176 #9); they are a safety
    // proxy, not an exact driver-heap measurement.
    private const long PerValueObjectOverheadBytes = 32;
    private const long PerRowObjectOverheadBytes = 48;

    private static int FixedWidthBytes(DataType type) => type switch
    {
        BooleanType or ByteType => 1,
        ShortType => 2,
        IntegerType or FloatType or DateType => 4,
        LongType or DoubleType or TimestampType or TimestampNtzType => 8,
        DecimalType decimalType => decimalType.IsCompact ? 8 : 16,
        _ => 8,
    };

    // A DateType lane stores the Spark epoch-day (days since 1970-01-01) as an int; surface it as the
    // CLR DateOnly that lit(DateOnly) round-trips (Functions.DateLiteral uses the same epoch), so
    // Collect()/GetAs<DateOnly> and Show render a calendar date rather than the raw epoch number.
    // An epoch-day whose date falls outside DateOnly's representable range (0001-01-01..9999-12-31) is a
    // deterministic UnsupportedPlanException (mirrors the timestamp/decimal paths) rather than a raw
    // ArgumentOutOfRangeException leaked from DateOnly.AddDays.
    private static DateOnly ReadDate(ColumnVector column, string name, DataType type, int index)
    {
        int epochDay = column.GetValue<int>(index);
        try
        {
            return UnixEpochDate.AddDays(epochDay);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw OutOfRangeDate(name, type);
        }
    }

    // The out-of-range message names the offending column and type but deliberately does NOT embed the raw
    // cell value (the epoch-day), which would leak row-level data into logs/diagnostics (#176 #8).
    private static UnsupportedPlanException OutOfRangeDate(string name, DataType type) =>
        new(QueryExecutionStage.Materialize,
            $"Row materialization cannot surface column '{name}' of type "
            + $"'{type.SimpleString}' as System.DateOnly: the date falls outside the "
            + "representable DateOnly range.");

    // A TimestampType lane stores the Spark epoch-microsecond instant as a long; TimestampReader surfaces it
    // as a UTC DateTime — the inverse of lit(DateTime)/lit(DateTimeOffset), which normalize to epoch-micros —
    // so Collect()/GetAs<DateTime> round-trips and Show renders an instant, not the raw epoch number. A
    // TimestampNtzType lane stores the same epoch-microsecond long but is timezone-LESS (#533): surfaced as a
    // DateTime of kind Unspecified (a wall-clock instant with no offset) so Collect()/Show renders the stored
    // local-datetime, never a UTC-adjusted one. A micros value whose ticks overflow long, or whose instant
    // falls outside DateTime's range, is a deterministic UnsupportedPlanException (mirrors the decimal path)
    // rather than a raw ArgumentOutOfRangeException or a silent mis-decode.
    private static DateTime ReadTimestampInstant(ColumnVector column, string name, DataType type, int index, DateTimeKind kind)
    {
        long micros = column.GetValue<long>(index);
        long ticks;
        try
        {
            ticks = checked(micros * TimeSpan.TicksPerMicrosecond);
            ticks = checked(DateTime.UnixEpoch.Ticks + ticks);
        }
        catch (OverflowException)
        {
            throw OutOfRangeTimestamp(name, type);
        }

        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw OutOfRangeTimestamp(name, type);
        }

        return new DateTime(ticks, kind);
    }

    // Names the offending column and type but deliberately does NOT embed the raw epoch-microsecond cell
    // value, which would leak row-level data into logs/diagnostics (#176 #8).
    private static UnsupportedPlanException OutOfRangeTimestamp(string name, DataType type) =>
        new(QueryExecutionStage.Materialize,
            $"Row materialization cannot surface column '{name}' of type "
            + $"'{type.SimpleString}' as System.DateTime: the instant falls outside the "
            + "representable DateTime range.");

    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    private static decimal ReadDecimal(ColumnVector column, DecimalType type, int index)
    {
        // System.Decimal is a sign + 96-bit magnitude + scale in [0, 28]. Reconstruct it directly from
        // the unscaled integer preserving the declared scale (so decimal(5,2) 100.00 keeps scale 2 and
        // renders "100.00", instead of dividing by 10^scale — which both overflows for wide values
        // representable at their scale and normalizes trailing zeros away). A genuinely unrepresentable
        // value (scale > 28, or a magnitude wider than 96 bits) is a deterministic UnsupportedPlanException.
        if (type.Scale > MaxDecimalScale)
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Materialize,
                $"Row materialization cannot surface '{type.SimpleString}' as System.Decimal: scale "
                + $"{type.Scale} exceeds the System.Decimal maximum of {MaxDecimalScale}.");
        }

        Int128 unscaled = type.IsCompact ? column.GetValue<long>(index) : column.GetValue<Int128>(index);
        bool isNegative = unscaled < 0;
        UInt128 magnitude = isNegative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude > MaxDecimalMagnitude)
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Materialize,
                $"Row materialization cannot surface a '{type.SimpleString}' value as System.Decimal: "
                + "its unscaled magnitude exceeds the 96-bit System.Decimal range.");
        }

        int lo = unchecked((int)(uint)magnitude);
        int mid = unchecked((int)(uint)(magnitude >> 32));
        int hi = unchecked((int)(uint)(magnitude >> 64));
        return new decimal(lo, mid, hi, isNegative, (byte)type.Scale);
    }

    // System.Decimal supports at most 28 fractional digits and a 96-bit magnitude.
    private const int MaxDecimalScale = 28;
    private static readonly UInt128 MaxDecimalMagnitude = UInt128.MaxValue >> 32;
}
