using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// Encodes the in-memory <see cref="Row"/> sequence a Core <c>LocalRelation</c> (the STORY-04.1.2 /
/// #158 read-door for local data) carries into EPIC-03 <see cref="ColumnBatch"/>es, so the physical
/// planner can plan a scan over it without the <see cref="IScanSource"/> catalog seam. It is the exact
/// inverse of <see cref="RowMaterializer"/>: it reads each row's natural CLR value positionally and
/// writes it onto the typed <see cref="MutableColumnVector"/> lane for the column's ADR-0008
/// <see cref="DataType"/> (null-aware), recursing through nested columns (#608) — a struct value is a
/// nested <see cref="Row"/>, an array value a non-string <see cref="System.Collections.IEnumerable"/>,
/// a map value an <see cref="System.Collections.IDictionary"/>. Any row whose shape or a value's CLR
/// type does not match the schema — or a value that cannot be encoded (overflow, unrepresentable
/// date/timestamp/decimal) — raises the deterministic <see cref="UnsupportedPlanException"/> attributed to
/// <see cref="QueryExecutionStage.Scan"/> (this deferred row→batch encode runs inside
/// <c>ScanPlan.Execute</c> — a scan/data-in failure, not a planning one, now that #158 is merged)
/// rather than a raw failure.
/// </summary>
internal static class LocalRelationBatches
{
    /// <summary>Encodes <paramref name="data"/> into a single columnar batch conforming to
    /// <paramref name="schema"/>. Enumerates <paramref name="data"/> exactly once (this is the point at
    /// which a lazily-created DataFrame's rows are finally read).</summary>
    /// <param name="schema">The authoritative relation schema.</param>
    /// <param name="data">The in-memory rows, read positionally against <paramref name="schema"/>.</param>
    /// <param name="cancellationToken">The run's effective token (user cancellation linked with any
    /// timeout). Threaded into the <b>source drain</b> so a slow/large/unbounded source honors
    /// cancel/timeout: a <c>LocalRelation</c>'s rows are a <c>MemoizedRowSequence</c> that snapshots the
    /// user <see cref="IEnumerable{T}"/> <b>eagerly</b> (in Core) on first read, so its token-aware
    /// <c>Snapshot</c> polls per source row while draining; a raw sequence is drained here the same way.
    /// This deferred encode runs in <c>ScanPlan.Execute</c> — between the driver's upfront gate and
    /// <c>PhysicalRuntime.Run</c>'s per-batch poll — so without this the source drain would run past a
    /// cancellation/timeout (STORY-04.6.4 AC2). Encoding also polls every 1024 rows for a large relation.</param>
    /// <returns>A single-element list holding the encoded batch (a zero-row batch when empty).</returns>
    /// <exception cref="UnsupportedPlanException">A row's arity or a value's CLR type does not match the
    /// schema, or a value cannot be encoded onto its lane.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled
    /// (surfaced by the driver as an <see cref="OperationCanceledException"/> or a timeout).</exception>
    public static IReadOnlyList<ColumnBatch> Build(
        StructType schema, IEnumerable<Row> data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(data);

        IReadOnlyList<Row> rows = Drain(data, cancellationToken);

        int rowCount = rows.Count;
        int columnCount = schema.Count;

        // Validate arity/null once up front (a Stage=Scan data error), polling every 1024 rows so a very
        // large relation stays cancellable during validation too.
        for (int r = 0; r < rowCount; r++)
        {
            if ((r & CancellationPollMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Row current = rows[r];
            if (current is null)
            {
                throw new UnsupportedPlanException(
                    QueryExecutionStage.Scan,
                    "A LocalRelation row is null; every row supplied to CreateDataFrame must be a Row.");
            }

            if (current.Length != columnCount)
            {
                throw new UnsupportedPlanException(
                    QueryExecutionStage.Scan,
                    $"A LocalRelation row has {current.Length} value(s) but the schema declares "
                    + $"{columnCount} column(s); every row must match the schema arity.");
            }
        }

        var columns = new ColumnVector[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            StructField field = schema[c];
            MutableColumnVector vector = ColumnVectors.Create(field.DataType, Math.Max(rowCount, 1));
            for (int r = 0; r < rowCount; r++)
            {
                // The source is fully drained (in memory) by here, so a coarser every-1024-rows poll
                // suffices to bound the uncancellable encode of a very large collected relation.
                if ((r & CancellationPollMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                object? value = rows[r][c];
                EncodeValue(vector, field.DataType, field.Name, value);
            }

            columns[c] = vector;
        }

        return new ColumnBatch[] { new ManagedColumnBatch(schema, columns, rowCount) };
    }

    // Drains the LocalRelation's rows cancellation-aware. The rows are normally a MemoizedRowSequence whose
    // snapshot drains the user IEnumerable EAGERLY on first read (in Core); its Snapshot(token) overload
    // threads the per-row poll INTO that drain, which is the actual point a slow/large/unbounded source is
    // pulled. A raw sequence (a direct Build call, e.g. a test) is drained here with the same per-row poll.
    private static IReadOnlyList<Row> Drain(IEnumerable<Row> data, CancellationToken cancellationToken)
    {
        if (data is MemoizedRowSequence memoized)
        {
            return memoized.Snapshot(cancellationToken);
        }

        var rows = new List<Row>();
        foreach (Row row in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(row);
        }

        return rows;
    }

    // Poll cancellation every 1024 rows (power-of-two mask) while encoding the already-collected rows,
    // matching RowMaterializer.CancellationPollMask so the two inverse encode/decode paths bound
    // uncancellable in-memory work identically.
    private const int CancellationPollMask = 1023;

    // Recursively encodes one value onto its column vector for the given DataType, null- and
    // nesting-aware. A null value is a SQL NULL: a flat/list/map vector commits a null row directly; a
    // null struct must still advance every field child by one (a null placeholder) before committing the
    // null struct row, so the children stay length-aligned (StructColumnVector's build contract). Nested
    // values follow the CreateDataFrame nested CLR convention (#608): a StructType value is a nested
    // <see cref="Row"/>, an ArrayType value is any non-string IEnumerable, a MapType value is any
    // IDictionary. This is the exact inverse of RowMaterializer.ReadValue.
    private static void EncodeValue(MutableColumnVector vector, DataType type, string path, object? value)
    {
        switch (type)
        {
            case StructType structType:
                EncodeStruct((StructColumnVector)vector, structType, path, value);
                break;

            case ArrayType arrayType:
                EncodeList((ListColumnVector)vector, arrayType, path, value);
                break;

            case MapType mapType:
                EncodeMap((MapColumnVector)vector, mapType, path, value);
                break;

            default:
                if (value is null)
                {
                    vector.AppendNull();
                }
                else
                {
                    EncodeScalar(vector, type, path, value);
                }

                break;
        }
    }

    // Encodes a struct value: a null struct advances every field child by one (null) then commits a null
    // struct row; a non-null struct requires a nested Row of the field's arity and recurses per field.
    private static void EncodeStruct(StructColumnVector vector, StructType type, string path, object? value)
    {
        if (value is null)
        {
            for (int i = 0; i < type.Count; i++)
            {
                EncodeValue((MutableColumnVector)vector.Child(i), type[i].DataType, FieldPath(path, type[i].Name), null);
            }

            vector.AppendNull();
            return;
        }

        if (value is not Row row)
        {
            throw NestedTypeMismatch(path, type, "a DeltaSharp.Row", value);
        }

        if (row.Length != type.Count)
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Scan,
                $"Struct column '{path}' of type '{type.SimpleString}' expects a Row with {type.Count} field(s), "
                + $"but a row supplied {row.Length}.");
        }

        for (int i = 0; i < type.Count; i++)
        {
            EncodeValue((MutableColumnVector)vector.Child(i), type[i].DataType, FieldPath(path, type[i].Name), row[i]);
        }

        vector.EndStruct();
    }

    // Encodes an array value: a null array commits a null list row (no elements); a non-null array is any
    // non-string IEnumerable whose items encode into the element child in order, then closes the list.
    private static void EncodeList(ListColumnVector vector, ArrayType type, string path, object? value)
    {
        if (value is null)
        {
            vector.AppendNull();
            return;
        }

        IEnumerable elements = AsSequence(path, type, value);
        var elementChild = (MutableColumnVector)vector.Elements;
        foreach (object? element in elements)
        {
            EncodeValue(elementChild, type.ElementType, ElementPath(path), element);
        }

        vector.EndList();
    }

    // Encodes a map value: a null map commits a null map row (no entries); a non-null map is any
    // IDictionary whose entries encode into the parallel key/value children, then closes the map. A null
    // key is rejected fail-closed by the vector (MapType keys are structurally non-null).
    private static void EncodeMap(MapColumnVector vector, MapType type, string path, object? value)
    {
        if (value is null)
        {
            vector.AppendNull();
            return;
        }

        if (value is not IDictionary dictionary)
        {
            throw NestedTypeMismatch(path, type, "a System.Collections.IDictionary", value);
        }

        var keyChild = (MutableColumnVector)vector.Keys;
        var valueChild = (MutableColumnVector)vector.Values;
        foreach (DictionaryEntry entry in dictionary)
        {
            EncodeValue(keyChild, type.KeyType, KeyPath(path), entry.Key);
            EncodeValue(valueChild, type.ValueType, ValuePath(path), entry.Value);
        }

        vector.EndMap();
    }

    // Treats an array value as a sequence of items. Any IEnumerable is accepted (object?[], List<object?>,
    // a typed collection like int[], …) EXCEPT a string: a string is a scalar StringType value, not a
    // char sequence, so accepting it here would silently mis-encode a mistyped value.
    private static IEnumerable AsSequence(string path, ArrayType type, object value)
    {
        if (value is string || value is not IEnumerable sequence)
        {
            throw NestedTypeMismatch(path, type, "a non-string System.Collections.IEnumerable", value);
        }

        return sequence;
    }

    private static UnsupportedPlanException NestedTypeMismatch(string path, DataType type, string expected, object value) =>
        new(QueryExecutionStage.Scan,
            $"Column '{path}' is '{type.SimpleString}', which expects {expected} value, but a row supplied a "
            + $"{value.GetType().Name}.");

    private static string FieldPath(string path, string field) => $"{path}.{field}";

    private static string ElementPath(string path) => $"{path}[]";

    private static string KeyPath(string path) => $"{path}.key";

    private static string ValuePath(string path) => $"{path}.value";

    private static void EncodeScalar(MutableColumnVector vector, DataType type, string path, object value)
    {
        switch (type)
        {
            case BooleanType:
                vector.AppendValue(Expect<bool>(path, type, value));
                break;

            // Spark ByteType is a signed tinyint; the Engine stores it on an unsigned byte lane.
            case ByteType:
                vector.AppendValue(unchecked((byte)Expect<sbyte>(path, type, value)));
                break;

            case ShortType:
                vector.AppendValue(Expect<short>(path, type, value));
                break;

            case IntegerType:
                vector.AppendValue(Expect<int>(path, type, value));
                break;

            case LongType:
                vector.AppendValue(Expect<long>(path, type, value));
                break;

            case FloatType:
                vector.AppendValue(Expect<float>(path, type, value));
                break;

            case DoubleType:
                vector.AppendValue(Expect<double>(path, type, value));
                break;

            case StringType:
                vector.AppendBytes(Encoding.UTF8.GetBytes(Expect<string>(path, type, value)));
                break;

            case BinaryType:
                vector.AppendBytes(Expect<byte[]>(path, type, value));
                break;

            case DateType:
                vector.AppendValue(EncodeDate(Expect<DateOnly>(path, type, value)));
                break;

            case TimestampType:
                vector.AppendValue(EncodeTimestamp(Expect<DateTime>(path, type, value)));
                break;

            case TimestampNtzType:
                vector.AppendValue(EncodeTimestampNtz(Expect<DateTime>(path, type, value)));
                break;

            case DecimalType decimalType:
                AppendDecimal(vector, path, decimalType, Expect<decimal>(path, type, value));
                break;

            default:
                throw new UnsupportedPlanException(
                    QueryExecutionStage.Scan,
                    $"CreateDataFrame has no CLR encoding for column '{path}' of type '{type.SimpleString}'.");
        }
    }

    // Requires the row value's CLR type to match the expected encoding type EXACTLY (an int is not
    // accepted for a LongType lane, etc.). This mirrors Spark's createDataFrame, which does not silently
    // widen local values, and keeps the failure deterministic and path-named: a caller must supply the
    // ADR-0008 CLR type for the declared column type. Widening is intentionally NOT performed so a
    // mismatched literal (e.g. `1` where a `long` is declared) fails loudly rather than masking a
    // schema/value drift.
    private static T Expect<T>(string path, DataType type, object value)
    {
        if (value is T typed)
        {
            return typed;
        }

        throw new UnsupportedPlanException(
            QueryExecutionStage.Scan,
            $"Column '{path}' is '{type.SimpleString}', which expects a "
            + $"{typeof(T).Name} value, but a row supplied a {value.GetType().Name}.");
    }

    // A DateType lane stores the Spark epoch-day (days since 1970-01-01) as an int — the exact inverse
    // of RowMaterializer.ReadDate. DateOnly's whole representable range fits comfortably in an int.
    private static int EncodeDate(DateOnly value) =>
        value.DayNumber - UnixEpochDate.DayNumber;

    // A TimestampType lane stores the Spark epoch-microsecond instant as a long — the inverse of
    // RowMaterializer.ReadTimestamp (which yields a UTC DateTime). A Local DateTime is converted to UTC;
    // a Utc/Unspecified DateTime is treated as the instant. Sub-microsecond ticks are truncated
    // (Spark timestamps carry microsecond precision).
    private static long EncodeTimestamp(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        long ticksFromEpoch = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return ticksFromEpoch / TimeSpan.TicksPerMicrosecond;
    }

    // A TimestampNtzType lane stores the Spark epoch-microsecond WALL-CLOCK value as a long — the inverse of
    // RowMaterializer.ReadTimestampNtz (which yields an Unspecified-kind DateTime). Unlike EncodeTimestamp,
    // the DateTime is taken AS-IS: a Local kind is deliberately NOT converted to UTC, because timestamp_ntz is
    // timezone-less (#558) and applying value.ToUniversalTime() here would shift the stored value by the host
    // offset and make read-back off by that offset. Sub-microsecond ticks are truncated (Spark timestamps
    // carry microsecond precision).
    private static long EncodeTimestampNtz(DateTime value)
    {
        long ticksFromEpoch = value.Ticks - DateTime.UnixEpoch.Ticks;
        return ticksFromEpoch / TimeSpan.TicksPerMicrosecond;
    }

    private static readonly DateOnly UnixEpochDate = new(1970, 1, 1);

    private static void AppendDecimal(
        MutableColumnVector vector, string path, DecimalType type, decimal value)
    {
        // Encode value as an unscaled integer at the column's declared scale — the inverse of
        // RowMaterializer.ReadDecimal (which reconstructs a System.Decimal from the unscaled lane). A
        // System.Decimal carries at most 28 fractional digits, so a scale beyond that is unrepresentable.
        if (type.Scale > MaxDecimalScale)
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Scan,
                $"Column '{path}' is '{type.SimpleString}': scale {type.Scale.ToString(CultureInfo.InvariantCulture)} exceeds the "
                + $"System.Decimal maximum of {MaxDecimalScale.ToString(CultureInfo.InvariantCulture)}, so a decimal value cannot be encoded.");
        }

        decimal scaled;
        try
        {
            scaled = value * Pow10Decimal(type.Scale);
        }
        catch (OverflowException)
        {
            throw DecimalOutOfRange(path, type, value);
        }

        if (scaled != decimal.Truncate(scaled))
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Scan,
                $"Decimal value {Invariant(value)} for column '{path}' cannot be represented at scale "
                + $"{type.Scale.ToString(CultureInfo.InvariantCulture)} without loss of precision.");
        }

        Int128 unscaled;
        try
        {
            unscaled = Int128.CreateChecked(scaled);
        }
        catch (OverflowException)
        {
            throw DecimalOutOfRange(path, type, value);
        }

        Int128 magnitude = unscaled < 0 ? -unscaled : unscaled;
        if (magnitude >= Pow10Int128(type.Precision))
        {
            throw new UnsupportedPlanException(
                QueryExecutionStage.Scan,
                $"Decimal value {Invariant(value)} for column '{path}' does not fit in precision "
                + $"{type.Precision.ToString(CultureInfo.InvariantCulture)} (type '{type.SimpleString}').");
        }

        if (type.IsCompact)
        {
            vector.AppendValue((long)unscaled);
        }
        else
        {
            vector.AppendValue(unscaled);
        }
    }

    private static UnsupportedPlanException DecimalOutOfRange(
        string path, DecimalType type, decimal value) =>
        new(QueryExecutionStage.Scan,
            $"Decimal value {Invariant(value)} for column '{path}' is out of range for type "
            + $"'{type.SimpleString}'.");

    private static string Invariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal Pow10Decimal(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static Int128 Pow10Int128(int exponent)
    {
        Int128 result = Int128.One;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    // System.Decimal supports at most 28 fractional digits (mirrors RowMaterializer.MaxDecimalScale).
    private const int MaxDecimalScale = 28;
}
