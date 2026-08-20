using System.Diagnostics;
using System.Globalization;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Parquet;
using Parquet.Schema;
using ColumnSegment = DeltaSharp.Storage.Parquet.NestedColumnShredder.ColumnSegment;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Writes an ordered sequence of same-schema <see cref="ColumnBatch"/>es to a <see cref="Stream"/> as
/// one standards-compliant Parquet file (design §2.9.2, STORY-05.1.2 / #181). Logical rows are packed
/// into row groups of at most <see cref="RowGroupRowLimit"/> rows; the footer carries the Spark/Delta
/// schema JSON in <c>key_value_metadata</c>, and per-column <c>Statistics</c> (min/max/null) are
/// produced automatically by Parquet.Net from the written values (checklist 17 statistics bullets).
///
/// <para><see cref="WriteWithStatisticsAsync"/> additionally returns the write-time Delta
/// <see cref="FileStatistics"/> (record count + per-column min/max/nullCount) the caller records on the
/// <c>add</c> action (STORY-05.6.3 AC1), collected by <see cref="ParquetStatisticsCollector"/> under a
/// <see cref="StatisticsPolicy"/>.</para>
/// </summary>
internal class ParquetFileWriter
{
    /// <summary>The default maximum number of logical rows per row group. This is a <b>row-count
    /// proxy</b> for the design's ≈128&#160;MiB byte target (§2.9.2): a byte-aware flush that sizes row
    /// groups by encoded bytes directly (rather than by row count) is a tracked follow-up.</summary>
    public const int DefaultRowGroupRowLimit = 128 * 1024;

    private const string WriterIdentity = "DeltaSharp.Storage/0.1";

    // CF-8: cooperative-cancellation stride for the per-row string/binary build loops — check the token
    // every 16384 rows so a large single-row-group write stays cancellable without a per-row token read.
    // (Fixed-width schemas and every row-group boundary are already checked at the WriteAsync while loop.)
    private const int CancellationCheckMask = 0x3FFF;

    // The §2.4b footer reconciliation's reader. Stateless and hoisted to a single static instance so the
    // self-check adds no per-write allocation. It keeps the DEFAULT decode limits — the check must fail CLOSED
    // under the same wall-clock bounds every other footer read obeys, and a write whose own footer cannot be
    // read within its budget is precisely a write that must not be published.
    //
    // It runs on its OWN door (BoundedDecode.ReconciliationDecoder), not the process-wide data-file one. That
    // door admits UNTRUSTED reads; this read-back is DeltaSharp verifying bytes it just authored itself
    // (design §5: "no new external input on the write path"). Sharing would couple the two in both directions:
    // a flood of hostile reads saturating the door would fail otherwise-healthy WRITES with DecoderSaturated,
    // and every write would consume an admission slot sized for untrusted decodes. See the door's own
    // documentation for its dedicated-thread execution and footer-only sizing.
    private static readonly ParquetFileReader ReconciliationReader =
        new(dataFileDecoder: BoundedDecode.ReconciliationDecoder);

    private readonly int _rowGroupRowLimit;

    /// <summary>
    /// A TEST-ONLY extension point invoked after <see cref="CollectSegments"/> for each row group, returning
    /// the number of logical rows the (possibly perturbed) segments now describe. The base implementation is
    /// the identity, and NO production type derives from this writer, so a shipping <see cref="ParquetFileWriter"/>
    /// instance carries no mutable corruption switch (N1) — a test must author a subclass to perturb anything.
    /// The seam exists because §2.4b's post-write reconciliation cannot otherwise be driven from a real
    /// <see cref="WriteAsync"/>: every in-tree code path produces segments that trivially sum to the batch total.
    /// </summary>
    protected virtual int OnRowGroupSegmentsCollected(List<Segment> segments, int size) => size;

    /// <summary>Creates a writer with the given row-group row cap.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowGroupRowLimit"/> is not positive.</exception>
    public ParquetFileWriter(int rowGroupRowLimit = DefaultRowGroupRowLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowGroupRowLimit);
        _rowGroupRowLimit = rowGroupRowLimit;
    }

    /// <summary>The maximum number of logical rows written into a single row group.</summary>
    public int RowGroupRowLimit => _rowGroupRowLimit;

    /// <summary>Writes <paramref name="batches"/> (each conforming to <paramref name="schema"/>) to
    /// <paramref name="output"/> as one Parquet file.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A batch's schema does not match <paramref name="schema"/>.</exception>
    /// <exception cref="DeltaStorageException">A column's type has no supported Parquet mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>), or a non-nullable column holds a null
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    /// <exception cref="SchemaValidationException">#710/#711: <paramref name="schema"/> cannot be serialized
    /// into the Parquet footer's schema string — a field name / metadata key / metadata string value carries
    /// invalid UTF-16 (an unpaired surrogate <c>Utf8JsonWriter</c> would lossily transcode to U+FFFD), or the
    /// type tree nests deeper than the shared read/write JSON container bound. Fails closed before any bytes
    /// are written.</exception>
    public async Task WriteAsync(
        Stream output, StructType schema, IReadOnlyList<ColumnBatch> batches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);

        int columnCount = schema.Count;
        var fields = new Field[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            // honorReferenceNullability: the WRITTEN footer's physical repetition must match the
            // declared schemaString, so a "nullable":false string/binary column is emitted REQUIRED
            // rather than Parquet.Net's reference-type always-nullable default (#730). CreateField also
            // fails the out-of-scope nested shapes closed here, BEFORE any byte is written.
            fields[c] = ParquetTypeMapping.CreateField(schema[c], honorReferenceNullability: true);
        }

        // Apply any selection once and record each batch's logical row count, so row groups can be sized
        // independently of the input batch boundaries with a running cursor — no O(total-rows) per-row
        // index is materialized (M5).
        var selectedColumns = new List<ColumnVector[]>(batches.Count);
        var batchRowCounts = new int[batches.Count];
        long totalRows = 0;
        for (int b = 0; b < batches.Count; b++)
        {
            ColumnBatch batch = batches[b] ?? throw new ArgumentNullException(nameof(batches), $"Batch {b} is null.");
            if (!batch.Schema.Equals(schema))
            {
                // This is an internal-invariant guard: every in-tree caller derives `batch.Schema` and
                // `schema` from the same source, so a mismatch means a DeltaSharp bug. It is NOT dead code
                // though — InternalsVisibleTo makes WriteAsync directly callable, and the guard exists
                // precisely because the invariant can break. Both renders therefore go through
                // DescribeSchema: StructType.SimpleString embeds every field name verbatim and recurses, so
                // these two tokens rendered ~129,000 raw characters on a wide schema.
                throw new ArgumentException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Batch {b} has schema {DiagnosticText.DescribeSchema(batch.Schema)} but the writer "
                        + $"schema is {DiagnosticText.DescribeSchema(schema)}."),
                    nameof(batches));
            }

            // §2.8 selection-vector fail-closed PRE-PASS. SelectedColumn calls ColumnVector.Select, which
            // the nested vectors raise a raw NotSupportedException from (a selection cannot be pushed
            // through offsets without re-shredding the child). Reject BEFORE the loop so the door fails on
            // the typed, bounded contract rather than on a library-shaped exception mid-write.
            if (batch.Selection is not null)
            {
                for (int c = 0; c < columnCount; c++)
                {
                    if (schema[c].DataType is StructType or ArrayType or MapType)
                    {
                        throw DeltaStorageException.UnsupportedFeature(
                            $"Parquet write for nested column '{DiagnosticText.Sanitize(schema[c].Name)}' of "
                            + $"kind '{DiagnosticText.DescribeType(schema[c].DataType)}': writing a batch that "
                            + "carries a selection vector is not supported; materialize the selection first.");
                    }
                }
            }

            var columns = new ColumnVector[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                columns[c] = batch.SelectedColumn(c);
            }

            selectedColumns.Add(columns);
            batchRowCounts[b] = batch.LogicalRowCount;
            totalRows += batch.LogicalRowCount;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DeltaSchemaJson.SchemaMetadataKey] = DeltaSchemaJson.ToJson(schema),
            [DeltaSchemaJson.WriterMetadataKey] = WriterIdentity,
        };

        // §2.9 N9: build the ParquetSchema BEFORE opening the writer. Constructing it is what ATTACHES every
        // field and assigns the max definition/repetition levels the §2.3c guard bounds against (N4-c level
        // provenance), and ParquetWriter.CreateAsync emits the PAR1 magic the moment it is called — so every
        // nested reject has to be decided here, on an untouched stream.
        var parquetSchema = new ParquetSchema(fields);
        await ValidateNestedColumnsAsync(
            parquetSchema, schema, fields, selectedColumns, batchRowCounts, totalRows, cancellationToken)
            .ConfigureAwait(false);

        long startPosition = output.CanSeek ? output.Position : 0L;
        await using (ParquetWriter writer =
            await ParquetWriter.CreateAsync(parquetSchema, output, null, false, cancellationToken)
                .ConfigureAwait(false))
        {
            writer.CustomMetadata = metadata;

            // L2: a pre-test loop, so zero input rows produce ZERO row groups (never one empty group).
            int cursorBatch = 0;
            int cursorRow = 0;
            long emitted = 0;
            var segments = new List<Segment>();
            while (emitted < totalRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int size = (int)Math.Min(_rowGroupRowLimit, totalRows - emitted);
                CollectSegments(batchRowCounts, ref cursorBatch, ref cursorRow, size, segments);

                // §2.4b test seam. The base hook is the identity, so `written == size` in production. A test
                // SUBCLASS may drop or duplicate a segment (and report the row count the perturbed segments now
                // describe) to produce a file that is LOCALLY level-valid in every leaf yet whose footer NumRows
                // no longer equals the batch-derived total — the exact class §2.3c is blind to and only the
                // post-write reconciliation catches.
                int written = OnRowGroupSegmentsCollected(segments, size);

                using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
                for (int c = 0; c < columnCount; c++)
                {
                    if (schema[c].DataType is StructType or ArrayType or MapType)
                    {
                        // A nested column fans out to N leaf writes inside ONE row group — the shredder owns
                        // the Dremel encoding, the level guard, and the required-lane checks.
                        await NestedColumnShredder.WriteColumnAsync(
                            rowGroup, fields[c], schema[c],
                            BuildNestedSegments(selectedColumns, c, segments), written, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await WriteColumnAsync(
                        rowGroup, (DataField)fields[c], schema[c], selectedColumns, c, segments, written,
                        cancellationToken).ConfigureAwait(false);
                }

                emitted += size;
            }
        }

        // §2.4b POST-WRITE footer row-count reconciliation. The nested write door multiplies the ways a row
        // can be LOST or DUPLICATED without any exception: a slot-count/level defect writes a well-formed
        // file whose row count silently disagrees with the batches it came from, and the #497 schema door
        // only compares TYPES. So read the just-written footer back and reconcile its NumRows against the
        // batch-derived total. The reference is deliberately the LogicalRowCount sum computed above — never
        // statistics.NumRecords, which is nullable-by-policy and would degrade to a vacuous 0 == 0 check
        // exactly when statistics are disabled. Binding this inside WriteAsync's core binds all three
        // production write paths (DeltaWriteTarget.StageAsync, DeltaOptimize.WriteCompactedFileAsync,
        // ChangeDataWriter) at once.
        await ReconcileFooterRowCountAsync(output, startPosition, totalRows, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The §2.9 N9 PRE-PASS: runs the complete nested shredding pipeline — level computation, the §2.4a
    /// required-lane value guards, the §2.3c per-leaf <see cref="NestedLevelGuard"/> and both cross-leaf
    /// guards — over EVERY row-group segmentation this write will produce, writing nothing. Any reject
    /// therefore fires while <c>output</c> is still untouched, instead of after
    /// <c>ParquetWriter.CreateAsync</c> has already published the <c>PAR1</c> magic and a footer-less prefix.
    /// </summary>
    private async Task ValidateNestedColumnsAsync(
        ParquetSchema parquetSchema,
        StructType schema,
        Field[] fields,
        List<ColumnVector[]> selectedColumns,
        int[] batchRowCounts,
        long totalRows,
        CancellationToken cancellationToken)
    {
        _ = parquetSchema;
        bool anyNested = false;
        for (int c = 0; c < schema.Count; c++)
        {
            anyNested |= schema[c].DataType is StructType or ArrayType or MapType;
        }

        if (!anyNested)
        {
            return;
        }

        int cursorBatch = 0;
        int cursorRow = 0;
        long emitted = 0;
        var segments = new List<Segment>();
        while (emitted < totalRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = (int)Math.Min(_rowGroupRowLimit, totalRows - emitted);
            CollectSegments(batchRowCounts, ref cursorBatch, ref cursorRow, size, segments);
            int written = OnRowGroupSegmentsCollected(segments, size);
            for (int c = 0; c < schema.Count; c++)
            {
                if (schema[c].DataType is StructType or ArrayType or MapType)
                {
                    await NestedColumnShredder.ValidateColumnAsync(
                        fields[c], schema[c], BuildNestedSegments(selectedColumns, c, segments), written,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            emitted += size;
        }
    }

    // Projects the row group's (batch, start, length) segments onto ONE column's already-selection-resolved
    // vectors, which is the shape the shredder walks.
    private static ColumnSegment[] BuildNestedSegments(
        List<ColumnVector[]> selectedColumns, int columnIndex, List<Segment> segments)
    {
        var projected = new ColumnSegment[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            Segment segment = segments[i];
            projected[i] = new ColumnSegment(
                selectedColumns[segment.Batch][columnIndex], segment.Start, segment.Length);
        }

        return projected;
    }

    // Reads back the footer of the file just written into `output` (from `startPosition` to the current
    // position) and fails closed unless its NumRows equals `expectedRows`.
    internal static async Task ReconcileFooterRowCountAsync(
        Stream output, long startPosition, long expectedRows, CancellationToken cancellationToken)
    {
        if (!output.CanSeek)
        {
            // Parquet.Net itself requires a seekable output to write a footer, so this is unreachable in
            // practice — but an unverifiable write must fail CLOSED, never silently skip the check.
            throw DeltaStorageException.CorruptData(
                "The Parquet write could not be reconciled against its footer because the output stream is "
                + "not seekable.");
        }

        long endPosition = output.Position;
        long actualRows;
        try
        {
            // A NON-OWNING window over the just-written bytes: ParquetFileReader.OpenAsync constructs its
            // reader with leaveStreamOpen:false, so it would otherwise dispose the caller's output stream.
            var window = new NonOwningWindowStream(output, startPosition, endPosition - startPosition);
            actualRows = await ReconciliationReader.GetRowCountAsync(window, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // WriteWithStatisticsAsync measures byteSize as (Position - startPosition), so the read-back
            // must leave the stream exactly where the write left it.
            output.Position = endPosition;
        }

        if (actualRows != expectedRows)
        {
            throw DeltaStorageException.CorruptData(
                $"The written Parquet file reports {actualRows} row(s) in its footer but {expectedRows} "
                + "row(s) were written; the file is structurally inconsistent.");
        }
    }

    /// <summary>
    /// Writes <paramref name="batches"/> as one Parquet file (as <see cref="WriteAsync"/>) and returns the
    /// facts a Delta <c>add</c> action needs: the byte size, record count, and the write-time
    /// <see cref="FileStatistics"/> collected under <paramref name="policy"/> (STORY-05.6.3 AC1). The
    /// statistics describe exactly the rows written; the caller records them on the staged file so the
    /// commit carries <c>add.stats</c>.
    /// </summary>
    /// <remarks><see cref="WriteResult.ByteSize"/> is measured from <paramref name="output"/>'s advanced
    /// position and is <c>0</c> for a non-seekable stream (the caller measures bytes itself in that case);
    /// byte size and partition values are otherwise carried by the staged file, not this result.</remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A batch's schema does not match <paramref name="schema"/>.</exception>
    /// <exception cref="DeltaStorageException">A column's type has no supported Parquet mapping
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>), or a non-nullable column holds a null
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    /// <exception cref="SchemaValidationException">#710/#711: <paramref name="schema"/> cannot be serialized
    /// into the Parquet footer's schema string — a field name / metadata key / metadata string value carries
    /// invalid UTF-16 (an unpaired surrogate <c>Utf8JsonWriter</c> would lossily transcode to U+FFFD), or the
    /// type tree nests deeper than the shared read/write JSON container bound. Fails closed before any bytes
    /// are written.</exception>
    public async Task<WriteResult> WriteWithStatisticsAsync(
        Stream output,
        StructType schema,
        IReadOnlyList<ColumnBatch> batches,
        StatisticsPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(policy);

        long startPosition = output.CanSeek ? output.Position : 0L;

        // Write first: a failing write (e.g. a non-nullable null) never yields a spurious statistics pass.
        await WriteAsync(output, schema, batches, cancellationToken).ConfigureAwait(false);

        long byteSize = output.CanSeek ? output.Position - startPosition : 0L;
        FileStatistics statistics = ParquetStatisticsCollector.Collect(schema, batches, policy);

        // S11/§2.4b: the published row count is the LogicalRowCount sum — the SAME reference WriteAsync just
        // reconciled the footer's NumRows against — never `statistics.NumRecords ?? 0L`, which is null (and
        // would degrade to a published 0) exactly when the statistics policy disables record counting. With
        // this, `add.numRecords` is footer-reconciled whether or not statistics are collected.
        long rowCount = 0;
        for (int b = 0; b < batches.Count; b++)
        {
            rowCount += batches[b].LogicalRowCount;
        }

        if (statistics.NumRecords is long collected && collected != rowCount)
        {
            throw DeltaStorageException.CorruptData(
                $"The collected statistics report {collected} record(s) but {rowCount} row(s) were written.");
        }

        return new WriteResult(byteSize, rowCount, statistics);
    }

    // Advance the (batch, row) cursor by exactly `size` logical rows, recording the contiguous
    // (batch, start, length) segments spanned — which lets a row group straddle input batch boundaries
    // without a per-row index. Empty batches are skipped.
    private static void CollectSegments(
        int[] batchRowCounts, ref int cursorBatch, ref int cursorRow, int size, List<Segment> segments)
    {
        segments.Clear();
        int need = size;
        int b = cursorBatch;
        int r = cursorRow;
        while (need > 0)
        {
            int available = batchRowCounts[b] - r;
            if (available <= 0)
            {
                b++;
                r = 0;
                continue;
            }

            int take = Math.Min(available, need);
            segments.Add(new Segment(b, r, take));
            r += take;
            need -= take;
            if (r >= batchRowCounts[b])
            {
                b++;
                r = 0;
            }
        }

        cursorBatch = b;
        cursorRow = r;
    }

    private static async Task WriteColumnAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        StructField schemaField,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        CancellationToken cancellationToken)
    {
        // #730 backstop: this method's null-rejection flag and the Parquet field's REPETITION
        // (REQUIRED/OPTIONAL, chosen by ParquetTypeMapping.CreateField from the same
        // StructField.Nullable) are two readings of ONE decision. Parquet.Net 6.1.0 does not
        // cross-check them: writing a null-bearing (nullable) value array into a field the footer
        // declares REQUIRED — or a non-null array into an OPTIONAL field — produces a structurally
        // corrupt file with NO exception. So take the flag from the DataField that is actually
        // stamped into the footer (single source), and assert it against the declared schema so a
        // future divergence is loud in a Debug/test run rather than silent on disk. (MEASURED: with
        // the write flag flipped back to the read semantics, this fires with
        // "IsNullable=True ... Nullable=False" as a DebugAssertException the test host reports as a
        // normal failure. It rides the DATA path, so a zero-row write — which emits no row group —
        // does not reach it; the footer-shape guards in ParquetWriterTests cover that case.)
        Debug.Assert(
            field.IsNullable == schemaField.Nullable,
            $"Parquet field repetition (IsNullable={field.IsNullable}) diverges from the declared "
            + $"schema (Nullable={schemaField.Nullable}); writing would corrupt the file (#730).");
        bool nullable = field.IsNullable;
        switch (schemaField.DataType)
        {
            case BooleanType:
                await WriteValueAsync<bool>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<bool>(row), cancellationToken).ConfigureAwait(false);
                break;
            case ByteType:
                await WriteValueAsync<sbyte>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => unchecked((sbyte)vector.GetValue<byte>(row)), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShortType:
                await WriteValueAsync<short>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<short>(row), cancellationToken).ConfigureAwait(false);
                break;
            case IntegerType:
                await WriteValueAsync<int>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<int>(row), cancellationToken).ConfigureAwait(false);
                break;
            case LongType:
                await WriteValueAsync<long>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<long>(row), cancellationToken).ConfigureAwait(false);
                break;
            case FloatType:
                await WriteValueAsync<float>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<float>(row), cancellationToken).ConfigureAwait(false);
                break;
            case DoubleType:
                await WriteValueAsync<double>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => vector.GetValue<double>(row), cancellationToken).ConfigureAwait(false);
                break;
            case DateType:
                await WriteValueAsync<DateTime>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => ParquetTypeMapping.EpochDayToDateTime(vector.GetValue<int>(row)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TimestampType or TimestampNtzType:
                // Both TIMESTAMP (LTZ) and TIMESTAMP_NTZ store INT64 epoch-micros; the isAdjustedToUTC
                // annotation (set by ParquetTypeMapping.CreateField from the DataType) is the only wire
                // difference. The stored long is identical for both lanes (#533/#557).
                await WriteValueAsync<DateTime>(rowGroup, field, nullable, selectedColumns, columnIndex, segments, size,
                    static (vector, row) => ParquetTypeMapping.EpochMicrosToDateTime(vector.GetValue<long>(row)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecimalType decimalType:
                // L1: thread decimalType through a non-capturing static writer instead of a closure so no
                // per-column-chunk delegate allocation occurs (mirrors the static primitive delegates).
                await WriteDecimalAsync(
                    rowGroup, field, nullable, selectedColumns, columnIndex, segments, size, decimalType,
                    cancellationToken).ConfigureAwait(false);
                break;
            case StringType:
                await WriteStringAsync(
                    rowGroup, field, nullable, selectedColumns, columnIndex, segments, size, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case BinaryType:
                await WriteBinaryAsync(
                    rowGroup, field, nullable, selectedColumns, columnIndex, segments, size, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                // #705 predicate: the scalar kinds are cased above; nested types are routed to
                // NestedColumnShredder by WriteAsync (and the out-of-scope ones are rejected earlier by
                // ParquetTypeMapping.CreateField), so this arm is reached only for a scalar today. DescribeType
                // (== the atomic SimpleString literal here) bounds a defensively-reachable nested type instead of
                // recursing into raw nested field names.
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet write for column '{DiagnosticText.Sanitize(schemaField.Name)}' of type "
                    + $"'{DiagnosticText.DescribeType(schemaField.DataType)}' is not supported.");
        }
    }

    private static async Task WriteValueAsync<T>(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        Func<ColumnVector, int, T> read,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (nullable)
        {
            var values = new T?[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    values[idx++] = vector.IsNull(row) ? null : read(vector, row);
                }
            }

            await rowGroup.WriteAsync<T>(field, new ReadOnlyMemory<T?>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var values = new T[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (vector.IsNull(row))
                    {
                        throw DeltaStorageException.CorruptData(
                            $"Non-nullable column '{DiagnosticText.Sanitize(field.Name)}' holds a null at row {row}.");
                    }

                    values[idx++] = read(vector, row);
                }
            }

            await rowGroup.WriteAsync<T>(field, new ReadOnlyMemory<T>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteDecimalAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        DecimalType decimalType,
        CancellationToken cancellationToken)
    {
        if (nullable)
        {
            var values = new decimal?[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    values[idx++] = vector.IsNull(row) ? null : ParquetTypeMapping.ReadDecimal(vector, decimalType, row);
                }
            }

            await rowGroup.WriteAsync<decimal>(field, new ReadOnlyMemory<decimal?>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var values = new decimal[size];
            int idx = 0;
            foreach (Segment segment in segments)
            {
                ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
                for (int j = 0; j < segment.Length; j++)
                {
                    int row = segment.Start + j;
                    if (vector.IsNull(row))
                    {
                        throw DeltaStorageException.CorruptData(
                            $"Non-nullable column '{DiagnosticText.Sanitize(field.Name)}' holds a null at row {row}.");
                    }

                    values[idx++] = ParquetTypeMapping.ReadDecimal(vector, decimalType, row);
                }
            }

            await rowGroup.WriteAsync<decimal>(field, new ReadOnlyMemory<decimal>(values), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteStringAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        CancellationToken cancellationToken)
    {
        var values = new string?[size];
        int idx = 0;
        foreach (Segment segment in segments)
        {
            ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
            for (int j = 0; j < segment.Length; j++)
            {
                if ((idx & CancellationCheckMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int row = segment.Start + j;
                if (vector.IsNull(row))
                {
                    EnsureNullable(nullable, field, row);
                    values[idx++] = null;
                }
                else
                {
                    values[idx++] = System.Text.Encoding.UTF8.GetString(vector.GetBytes(row));
                }
            }
        }

        await rowGroup.WriteAsync(field, (IReadOnlyCollection<string>)values!, null).ConfigureAwait(false);
    }

    private static async Task WriteBinaryAsync(
        ParquetRowGroupWriter rowGroup,
        DataField field,
        bool nullable,
        List<ColumnVector[]> selectedColumns,
        int columnIndex,
        List<Segment> segments,
        int size,
        CancellationToken cancellationToken)
    {
        var values = new byte[]?[size];
        int idx = 0;
        foreach (Segment segment in segments)
        {
            ColumnVector vector = selectedColumns[segment.Batch][columnIndex];
            for (int j = 0; j < segment.Length; j++)
            {
                if ((idx & CancellationCheckMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int row = segment.Start + j;
                if (vector.IsNull(row))
                {
                    EnsureNullable(nullable, field, row);
                    values[idx++] = null;
                }
                else
                {
                    values[idx++] = vector.GetBytes(row).ToArray();
                }
            }
        }

        await rowGroup.WriteAsync(field, (IReadOnlyCollection<byte[]>)values!, null).ConfigureAwait(false);
    }

    private static void EnsureNullable(bool nullable, DataField field, int row)
    {
        if (!nullable)
        {
            throw DeltaStorageException.CorruptData(
                $"Non-nullable column '{DiagnosticText.Sanitize(field.Name)}' holds a null at row {row}.");
        }
    }

    /// <summary>The result of <see cref="WriteWithStatisticsAsync"/>: the file's byte
    /// <see cref="ByteSize"/> (0 for a non-seekable output), its <see cref="RowCount"/>, and the
    /// write-time <see cref="Statistics"/> to record on the Delta <c>add</c> action.</summary>
    public readonly record struct WriteResult(long ByteSize, long RowCount, FileStatistics Statistics);

    // A contiguous run of logical rows within a single input batch that a row group covers.
    internal readonly struct Segment
    {
        internal Segment(int batch, int start, int length)
        {
            Batch = batch;
            Start = start;
            Length = length;
        }

        internal int Batch { get; }

        internal int Start { get; }

        internal int Length { get; }
    }
}
