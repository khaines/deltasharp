using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Types;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// Reads a Parquet <see cref="Stream"/> into <see cref="ColumnBatch"/>es — one batch per row group
/// (design §2.9.1, STORY-05.1.1 / #180). It supports <b>projection</b> (only the requested columns'
/// chunks are read) and <b>row-group pruning</b> (a caller predicate over each group's column
/// statistics may skip a group as a <i>hint</i>; the residual predicate stays the engine's job, so a
/// kept row group is always fully correct).
/// </summary>
/// <remarks>
/// <para><b>Streaming corruption contract (H3, design §3.2 EE-01/EE-02).</b> The reader is
/// <i>batch-atomic</i>, not file-atomic: every yielded <see cref="ColumnBatch"/> is <b>always</b>
/// complete — a torn/partial batch is never produced. Concretely:
/// <list type="bullet">
/// <item>Structural/footer/metadata corruption fails <b>before any batch is yielded</b>: the footer
/// (schema + row-group metadata) is read at open, so a malformed/truncated file throws a deterministic
/// <see cref="DeltaStorageException"/> up front.</item>
/// <item>A page-level defect inside row group <c>K</c> surfaces as a deterministic error <b>at batch
/// <c>K</c></b>: batches for groups <c>0..K-1</c> were already returned complete, then enumerating the
/// defective group throws — it never yields a partial batch <c>K</c>.</item>
/// </list>
/// The reader deliberately keeps streaming (it does not buffer every row group to make the whole file
/// atomic): the honest, design-consistent guarantee is that a batch is never torn, not that the whole
/// file is validated before the first batch.</para>
/// <para><b>Eager-decode memory ceiling (H4/CF-1, design §5.4 C-DECODE).</b> Before the eager
/// per-column allocation, each projected row group's declared metadata is validated via
/// <see cref="EnsureDecodeCeiling"/>: a per-chunk decompression-ratio ceiling, an absolute
/// decompressed-size ceiling, and a row-count bound on the bytes the declared row count would eagerly
/// materialize. This is fundamentally a bound on the <b>transient memory this reader allocates</b>
/// because it decodes each row group eagerly and whole, not purely a "decompression bomb" guard: a
/// crafted footer that inflates the decompressed size or row count fails closed rather than driving an
/// out-of-memory allocation, and a <i>legitimate</i> row group whose projected columns would
/// materialize past the cap is likewise rejected because the eager decode cannot fit it. Legitimately
/// compressible data (constant/RLE/all-null columns that encode millions of rows in a few hundred
/// bytes) passes, since the bound is on the decoded footprint rather than a physical rows/byte proxy.
/// Chunked/streaming page-at-a-time decode that would lift the cap is a tracked follow-up.</para>
/// </remarks>
internal sealed class ParquetFileReader
{
    /// <summary>The default maximum plausible ratio of a column chunk's declared decompressed bytes to its
    /// declared compressed bytes (design §5.4 C-DECODE). Real Parquet encodings — even Snappy over
    /// constant/RLE/all-null data — stay well under this (empirically ≈ 20:1 at the column-chunk level),
    /// so a declared ratio beyond it is a crafted footer, not a real file, and is rejected as a
    /// decompression bomb. Configurable per reader via <see cref="ParquetDecodeLimits"/>; this constant is
    /// the default (and the reference used by the checkpoint reader's own guard).</summary>
    internal const long MaxDecompressionRatio = ParquetDecodeLimits.DefaultMaxDecompressionRatio;

    /// <summary>The default absolute per-row-group <b>eager-decode</b> memory ceiling (4&#160;GiB), applied
    /// to both the declared decompressed bytes AND the bytes a row group's declared row count would eagerly
    /// materialize (design §5.4 C-DECODE). Because this reader decodes each row group whole, it bounds the
    /// transient decode/allocation memory: a crafted footer (inflating the decompressed size or row count)
    /// fails closed rather than driving an out-of-memory allocation, and a legitimate row group whose
    /// projected columns exceed the cap is likewise rejected until chunked/streaming decode lifts the limit
    /// (tracked follow-up). Configurable per reader via <see cref="ParquetDecodeLimits"/> — lower it below a
    /// constrained executor budget, or raise it for a trusted large-row-group workload.</summary>
    internal const long MaxRowGroupDecodedBytes = ParquetDecodeLimits.DefaultMaxRowGroupDecodedBytes;

    private readonly ParquetDecodeLimits _limits;

    /// <summary>Creates a reader whose eager-decode guard uses <paramref name="limits"/> (or the safe
    /// <see cref="ParquetDecodeLimits.Default"/> when unset).</summary>
    public ParquetFileReader(ParquetDecodeLimits? limits = null) => _limits = limits ?? ParquetDecodeLimits.Default;

    /// <summary>A row-group pruning hint: return <see langword="false"/> to skip a row group whose
    /// <see cref="RowGroupStatistics"/> prove it cannot match. Pruning is a hint only — a kept group is
    /// still read in full and the residual predicate is the engine's responsibility (design §2.9.1).</summary>
    public delegate bool RowGroupPredicate(RowGroupStatistics statistics);

    /// <summary>Reads <paramref name="input"/>, projecting to <paramref name="requested"/> (a subset of
    /// the file schema by field name) and optionally skipping row groups via
    /// <paramref name="keepRowGroup"/>.</summary>
    /// <param name="input">The Parquet byte stream.</param>
    /// <param name="requested">The projection: the columns to read, by field name, in output order.</param>
    /// <param name="keepRowGroup">An optional row-group pruning hint (see <see cref="RowGroupPredicate"/>).</param>
    /// <param name="nullFillMissingColumns">When <see langword="true"/>, a <paramref name="requested"/> column
    /// that is <b>absent</b> from the file <i>and</i> <b>nullable</b> is materialized as an all-<c>null</c>
    /// column instead of failing — the additive schema-evolution (#190) read-side null-fill (#497): a file
    /// written under an older, narrower schema reads back through the current schema with the later-added
    /// columns null-filled. It never masks a genuine incompatibility: an absent <b>non-nullable</b> requested
    /// column still fails closed (a required column cannot be null-filled), and a <i>present</i> column whose
    /// physical type/nullability disagrees with the request is still rejected as a
    /// <see cref="StorageErrorKind.SchemaMismatch"/>. When <see langword="false"/> (the default for the
    /// general reader), an absent column of any nullability fails closed, preserving the reader's strict
    /// projection contract for callers that must not silently null-fill.</param>
    /// <param name="allowTypeWideningPromotion">When <see langword="true"/>, a <paramref name="requested"/>
    /// column whose physical (file) type is a NARROWER Delta-sanctioned widening of the requested type is
    /// PROMOTED into the requested wide type on read (Delta PROTOCOL.md "Reader Requirements for Type
    /// Widening", #495). When <see langword="false"/> (the strict default), a narrower physical type is NOT
    /// promotable — the exact physical/engine type mismatch fails closed as
    /// <see cref="StorageErrorKind.SchemaMismatch"/>. This gate is the READ-side counterpart to the write-side
    /// enablement check: only a caller that KNOWS the table declares the <c>typeWidening</c> feature in its
    /// snapshot protocol may pass <see langword="true"/>, so a tampered/malformed external log (a wide schema
    /// over narrow files with NO <c>typeWidening</c> feature) fails closed rather than being silently
    /// "repaired". Promotion trusts this caller-supplied gate: the scan layer knows the protocol, the
    /// stream-level reader does not.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DeltaStorageException">A requested column type is unsupported
    /// (<see cref="StorageErrorKind.UnsupportedFeature"/>); the resolved file column's physical type or
    /// nullability does not match the requested engine type
    /// (<see cref="StorageErrorKind.SchemaMismatch"/>); or the file is malformed/truncated, a requested
    /// column is absent (and not null-filled per <paramref name="nullFillMissingColumns"/>), or a row group's
    /// declared size exceeds the decode ceiling (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    public async IAsyncEnumerable<ColumnBatch> ReadAsync(
        Stream input,
        StructType requested,
        RowGroupPredicate? keepRowGroup,
        bool nullFillMissingColumns,
        bool allowTypeWideningPromotion,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(requested);

        // Validate every requested column maps to a supported Parquet type BEFORE any decode, so an
        // unsupported/nested projection fails deterministically without materializing a partial batch.
        for (int c = 0; c < requested.Count; c++)
        {
            _ = ParquetTypeMapping.CreateField(requested[c]);
        }

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            // Structural validation happens here (footer read at open) — schema/type mismatches fail
            // before any batch is yielded (H3). A null slot marks a requested column absent from the file
            // that will be null-filled (nullFillMissingColumns; #497).
            DataField?[] fileFields = ResolveFileFields(reader.Schema, requested, nullFillMissingColumns, allowTypeWideningPromotion);
            for (int group = 0; group < reader.RowGroupCount; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ColumnBatch? batch = await ReadRowGroupAsync(
                    reader, group, requested, fileFields, keepRowGroup, _limits, allowTypeWideningPromotion, cancellationToken)
                    .ConfigureAwait(false);
                if (batch is not null)
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>
    /// Reads only the Parquet footer and returns the file's total PHYSICAL row count (summed across row
    /// groups) — decoding no data pages. This is the file's real row count, used to bound a deletion vector's
    /// decoded positions by the truth on disk (never an attacker-controlled descriptor/stats field), so a
    /// poisoned DV can neither reference a row beyond the file nor force an oversized allocation.
    /// </summary>
    /// <exception cref="DeltaStorageException">The Parquet footer is malformed/truncated, or a row group
    /// declares a negative row count (fail closed).</exception>
    public async Task<long> GetRowCountAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            long total = 0;
            for (int group = 0; group < reader.RowGroupCount; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);
                long rows = rowGroup.RowCount;
                if (rows < 0)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Row group {group} declares a negative row count ({rows}).");
                }

                total = checked(total + rows);
            }

            return total;
        }
    }

    /// <summary>
    /// Reads only the Parquet footer and reconstructs the file's <b>actual physical data schema</b> (field
    /// names + DeltaSharp types via <see cref="ParquetTypeMapping.ToDataSchema"/>), decoding no data pages.
    /// The write-door records this on each staged file so schema enforcement gates the <b>real written
    /// bytes</b>, not the caller's declared write schema (#497). Nullability is the footer's view (Parquet
    /// models string/binary as nullable); callers compare the returned schema by name + type only.
    /// </summary>
    /// <exception cref="DeltaStorageException">The footer is malformed/truncated
    /// (<see cref="StorageErrorKind.CorruptData"/>), or a footer field has no supported DeltaSharp type
    /// mapping (<see cref="StorageErrorKind.UnsupportedFeature"/>).</exception>
    public async Task<StructType> ReadDataSchemaAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        ParquetReader reader = await OpenAsync(input, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return ParquetTypeMapping.ToDataSchema(reader.Schema);
        }
    }

    /// <summary>The declared compressed/decompressed footprint of one projected column chunk, plus the
    /// per-row width of the read buffer the reader will eagerly allocate for it (including any
    /// <see cref="Nullable{T}"/> overhead) — the inputs to <see cref="EnsureDecodeCeiling"/>.
    /// <paramref name="Absent"/> marks a column that is <b>not present</b> in the file and will be
    /// null-filled (#497): it declares no compressed/decompressed bytes (nothing is decoded) but still
    /// carries its element width so the null-fill allocation is bounded by the same row-count ceiling.
    /// </summary>
    internal readonly record struct ColumnChunkFootprint(
        long CompressedBytes, long UncompressedBytes, int ElementBytes, bool Absent = false);

    /// <summary>Fails closed when a row group's declared metadata would exceed this reader's
    /// <b>eager-decode</b> memory ceiling (design §5.4 C-DECODE), so neither a crafted footer nor a
    /// genuinely oversized row group can drive an out-of-memory allocation. Over the projected column
    /// chunks it enforces: (0) a <b>footprint-0 guard</b> — a projected chunk declaring zero decompressed
    /// bytes for a non-empty row group is a stripped/absent footer whose real pages would still decode
    /// unbounded, so it is rejected; (i) a per-chunk decompression-<b>ratio</b> ceiling
    /// (<see cref="MaxDecompressionRatio"/>); (ii) an <b>absolute</b> decompressed-size ceiling
    /// (<see cref="MaxRowGroupDecodedBytes"/>); and (iii) a <b>row-count</b> bound — the bytes the declared
    /// <paramref name="rowCount"/> would eagerly materialize, at the column's <b>actual</b> allocated
    /// element width (including any <see cref="Nullable{T}"/> overhead), must not exceed the same absolute
    /// ceiling. A negative row count or a negative declared size is likewise rejected. These declared-size
    /// bounds are sound where a physical rows/byte proxy is not: legitimately compressible data
    /// (constant/RLE/all-null columns, which encode millions of rows in a few hundred bytes) passes, while
    /// an inflated decompressed size or row count is caught. A <b>page-header-inflated</b> size within a
    /// non-zero footer is <i>not</i> caught here — that needs page-level streaming decode (Parquet.Net's
    /// public API does not expose it), tracked in #472.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="projectedChunks"/> is null.</exception>
    /// <exception cref="DeltaStorageException">A declared value is negative, or a ceiling is exceeded
    /// (<see cref="StorageErrorKind.CorruptData"/>).</exception>
    internal static void EnsureDecodeCeiling(
        long rowCount, IReadOnlyList<ColumnChunkFootprint> projectedChunks, int group, ParquetDecodeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(projectedChunks);
        limits ??= ParquetDecodeLimits.Default;
        if (rowCount < 0)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares a negative row count ({rowCount}).");
        }

        long totalDecompressedBytes = 0;
        for (int c = 0; c < projectedChunks.Count; c++)
        {
            ColumnChunkFootprint chunk = projectedChunks[c];

            // A null-filled absent column (#497) is never decoded: it declares no bytes and skips the
            // decompression guards (0)/(i)/(ii) below, but still applies the (iii) row-count element-width
            // bound further down so its all-null materialization stays inside the same eager-decode ceiling.
            if (chunk.Absent)
            {
                if (chunk.ElementBytes > 0 && rowCount > limits.MaxRowGroupDecodedBytes / chunk.ElementBytes)
                {
                    throw DeltaStorageException.CorruptData(
                        $"Row group {group} declares {rowCount} rows, whose null-fill of an absent column would "
                        + $"eagerly materialize more than the {limits.MaxRowGroupDecodedBytes}-byte eager-decode "
                        + $"ceiling for a {chunk.ElementBytes}-byte column.");
                }

                continue;
            }

            if (chunk.CompressedBytes < 0 || chunk.UncompressedBytes < 0)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} declares a negative column-chunk size (compressed "
                    + $"{chunk.CompressedBytes}, decompressed {chunk.UncompressedBytes}).");
            }

            // (0) Metadata-stripped-footer / footprint-0 guard (design §5.4 C-DECODE). A projected column
            // chunk that declares ZERO decompressed bytes while the row group has rows is malformed: the
            // Parquet spec defines TotalUncompressedSize as the size of all pages *including page headers*,
            // so any present column with rows has >= 1 page and therefore declares a strictly positive size
            // (this holds for required columns with no definition levels too). A zero is thus a
            // stripped/absent/missing-metadata footer. Its real data pages would still decode — Parquet.Net
            // reads pages by offset and decompresses each to its page-header size — so the declared-size
            // ceiling below cannot bound it. Fail closed rather than hand an unbounded chunk to the decoder.
            // (A per-PAGE declared-vs-produced control that also catches a page-header-inflated size WITHIN a
            // non-zero footer needs page-level streaming decode, which Parquet.Net's public API does not
            // expose — tracked in #472.)
            if (rowCount > 0 && chunk.UncompressedBytes == 0)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} projects a column chunk that declares zero decompressed bytes for "
                    + $"{rowCount} rows (a metadata-stripped or absent footer); the reader cannot bound its "
                    + "decode and fails closed.");
            }

            // (i) Decompression-ratio ceiling: a chunk claiming far more decompressed than compressed
            // bytes is a decompression bomb. The floor of 1 avoids a divide-by/zero-compressed edge. The
            // product is widened to Int128 so a large declared compressed size cannot overflow the 64-bit
            // multiply into a spurious verdict (wrapping to a negative or small-positive threshold that would
            // either wrongly reject a legitimate chunk or wrongly accept a bomb).
            long compressedFloor = Math.Max(chunk.CompressedBytes, 1);
            if (chunk.UncompressedBytes > (Int128)compressedFloor * limits.MaxDecompressionRatio)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} declares {chunk.UncompressedBytes} decompressed bytes for "
                    + $"{chunk.CompressedBytes} compressed, exceeding the {limits.MaxDecompressionRatio}:1 "
                    + "decompression-ratio ceiling (possible decompression bomb).");
            }

            // (iii) Row-count bound: reject BEFORE the eager per-column allocation if this row count would
            // materialize more than the absolute eager-decode ceiling. ElementBytes is the ACTUAL read
            // buffer width (Nullable<T>-aware, RF-4a). Dividing (rather than multiplying rowCount by the
            // width) keeps the check overflow-safe for an attacker-sized row count.
            if (chunk.ElementBytes > 0 && rowCount > limits.MaxRowGroupDecodedBytes / chunk.ElementBytes)
            {
                throw DeltaStorageException.CorruptData(
                    $"Row group {group} declares {rowCount} rows, which would eagerly materialize more "
                    + $"than the {limits.MaxRowGroupDecodedBytes}-byte eager-decode ceiling for a "
                    + $"{chunk.ElementBytes}-byte column.");
            }

            totalDecompressedBytes = SaturatingAdd(totalDecompressedBytes, chunk.UncompressedBytes);
        }

        // (ii) Absolute decompressed-size ceiling over the row group's projected chunks.
        if (totalDecompressedBytes > limits.MaxRowGroupDecodedBytes)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares {totalDecompressedBytes} decompressed bytes across its "
                + $"projected columns, exceeding the {limits.MaxRowGroupDecodedBytes}-byte eager-decode ceiling.");
        }
    }

    private static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;

    // The declared footprint the reader will decode/materialize for each projected column, pulled from
    // the row group's Parquet metadata (CF-1). A PRESENT chunk with missing/encrypted chunk metadata
    // contributes a zero decompressed footprint, which EnsureDecodeCeiling rejects fail-closed for a
    // non-empty row group (guard (0)): a present column with rows always declares a positive decompressed
    // size, so a zero is a stripped/absent footer whose real pages would still decode unbounded (§5.4). A
    // requested column ABSENT from the file (null slot; #497 null-fill) is marked Absent so it skips the
    // decompression guards but keeps its element width for the (iii) null-fill allocation bound.
    private static List<ColumnChunkFootprint> ProjectedFootprints(
        ParquetRowGroupReader rowGroup, StructType requested, DataField?[] fileFields)
    {
        var footprints = new List<ColumnChunkFootprint>(requested.Count);
        for (int c = 0; c < requested.Count; c++)
        {
            if (fileFields[c] is not { } fileField)
            {
                footprints.Add(
                    new ColumnChunkFootprint(
                        CompressedBytes: 0,
                        UncompressedBytes: 0,
                        AllocatedElementByteWidth(requested[c].DataType, nullable: true),
                        Absent: true));
                continue;
            }

            long compressed = 0;
            long uncompressed = 0;
            if (rowGroup.ColumnExists(fileField))
            {
                global::Parquet.Meta.ColumnMetaData? meta = rowGroup.GetMetadata(fileField)?.MetaData;
                if (meta is not null)
                {
                    compressed = meta.TotalCompressedSize;
                    uncompressed = meta.TotalUncompressedSize;
                }
            }

            footprints.Add(
                new ColumnChunkFootprint(
                    compressed,
                    uncompressed,
                    // For a type-widening promotion (#495) the file column is physically NARROWER than the
                    // requested (wide) type, yet the reader materializes the WIDE vector (ColumnVectors.Create
                    // over the requested type) — so the requested width is the dominant eager allocation and a
                    // safe (never under-counting) bound. The physical narrow read buffer is smaller and
                    // additive; the requested width upper-bounds the transient either way.
                    AllocatedElementByteWidth(requested[c].DataType, fileField.IsNullable)));
        }

        return footprints;
    }

    // The per-row byte width of the read buffer the reader eagerly allocates for a column — used only to
    // bound that allocation (CF-1 (iii)/RF-4a). It is the ACTUAL element size of the `new T[]`/`new T?[]`
    // read buffer, INCLUDING Nullable<T> overhead (long? is 16 bytes, decimal? is 24), so the (iii) bound
    // reflects the true transient rather than the unwrapped width. String/binary read into a per-row
    // managed reference (IntPtr.Size); their backing bytes are separately bounded by the decompressed
    // ceiling.
    internal static int AllocatedElementByteWidth(DataType type, bool nullable) => type switch
    {
        BooleanType => nullable ? Unsafe.SizeOf<bool?>() : Unsafe.SizeOf<bool>(),
        ByteType => nullable ? Unsafe.SizeOf<sbyte?>() : Unsafe.SizeOf<sbyte>(),
        ShortType => nullable ? Unsafe.SizeOf<short?>() : Unsafe.SizeOf<short>(),
        IntegerType => nullable ? Unsafe.SizeOf<int?>() : Unsafe.SizeOf<int>(),
        LongType => nullable ? Unsafe.SizeOf<long?>() : Unsafe.SizeOf<long>(),
        FloatType => nullable ? Unsafe.SizeOf<float?>() : Unsafe.SizeOf<float>(),
        DoubleType => nullable ? Unsafe.SizeOf<double?>() : Unsafe.SizeOf<double>(),
        DateType or TimestampType => nullable ? Unsafe.SizeOf<DateTime?>() : Unsafe.SizeOf<DateTime>(),
        DecimalType => nullable ? Unsafe.SizeOf<decimal?>() : Unsafe.SizeOf<decimal>(),
        StringType or BinaryType => IntPtr.Size,
        _ => IntPtr.Size,
    };

    private static async Task<ParquetReader> OpenAsync(Stream input, CancellationToken cancellationToken)
    {
        try
        {
            return await ParquetReader.CreateAsync(input, null, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsParquetDefect(ex))
        {
            throw DeltaStorageException.CorruptData(
                $"The Parquet stream is malformed or truncated: {ex.Message}", ex);
        }
    }

    // Resolves each requested column to the matching file DataField (validating physical type/nullability),
    // or a null slot for a column ABSENT from the file when it may be null-filled (#497). A null slot is
    // produced ONLY when nullFillMissingColumns is set AND the requested column is nullable: an
    // additively-added column (#190) is always nullable, and older files written before it lack it, so it
    // reads back as all-null. An absent NON-nullable column can never be null-filled (a required lane cannot
    // carry null), and an absent column with null-fill disabled preserves the strict projection contract —
    // both fail closed with the dedicated ColumnNotPresentInFile kind the OPTIMIZE/read guards match on
    // (#513). A PRESENT column with a disagreeing physical type/nullability is still rejected by
    // ValidateFileField as a distinct SchemaMismatch (never silently coerced or null-filled).
    private static DataField?[] ResolveFileFields(
        ParquetSchema fileSchema, StructType requested, bool nullFillMissingColumns, bool allowTypeWideningPromotion)
    {
        var byName = new Dictionary<string, DataField>(StringComparer.Ordinal);
        foreach (DataField field in fileSchema.DataFields)
        {
            byName[field.Name] = field;
        }

        var resolved = new DataField?[requested.Count];
        for (int c = 0; c < requested.Count; c++)
        {
            StructField requestedField = requested[c];
            string name = requestedField.Name;
            if (!byName.TryGetValue(name, out DataField? field))
            {
                if (nullFillMissingColumns && requestedField.Nullable)
                {
                    // Absent + nullable + null-fill enabled: a null slot the read path materializes as an
                    // all-null column (evolved-column read null-fill, #497).
                    resolved[c] = null;
                    continue;
                }

                throw DeltaStorageException.ColumnNotPresentInFile(name);
            }

            ValidateFileField(field, requestedField, allowTypeWideningPromotion);
            resolved[c] = field;
        }

        return resolved;
    }

    // M2: cross-check the resolved file field's physical type, temporal annotation, decimal
    // precision/scale, and nullability against the requested engine type. A mismatch is a DISTINCT
    // SchemaMismatch error (not a generic "malformed" one), so a schema-evolution/type surprise is
    // never silently coerced or masked as corruption.
    //
    // Type-widening promotion (Delta PROTOCOL.md "Reader Requirements for Type Widening", #495): an OLDER
    // file physically stores a NARROWER type than the current (widened) requested type — e.g. an Int32 file
    // read under a widened `long` schema, or a decimal(6,2) file under a widened decimal(10,4) schema. When
    // the file's physical type is a Delta-SANCTIONED widening OF the requested type
    // (TypeWidening.IsSanctionedWidening) AND the caller opened this promotion gate
    // (allowTypeWideningPromotion — the scan layer proved the table's protocol declares the `typeWidening`
    // feature; the stream-level reader cannot see the protocol, so it TRUSTS this flag), accept it: the read
    // path (ReadColumnAsync/ReadPromotedColumnAsync) reads the narrow physical values and PROMOTES them into
    // the requested wide vector. When the gate is closed (allowTypeWideningPromotion == false) a narrower
    // physical type is NOT promotable — the exact-type mismatch below fires and the read fails closed
    // (SchemaMismatch), so a tampered/malformed log (wide schema, narrow files, no `typeWidening` feature) is
    // never silently "repaired". Any OTHER physical divergence (a narrowing on read, an unrelated type) is
    // still rejected fail-closed.
    private static void ValidateFileField(DataField fileField, StructField requestedField, bool allowTypeWideningPromotion)
    {
        DataField expected = ParquetTypeMapping.CreateField(requestedField);

        // Read-side promotion gate: a narrower physical type that is a sanctioned widening of the request is
        // accepted (the values are widened on read) ONLY when the caller opened the promotion gate. This is
        // lossless and matches what the writer recorded in `delta.typeChanges`. Nullability is still checked
        // below against the physical column.
        bool promotable = allowTypeWideningPromotion
            && ParquetTypeMapping.TryToDataType(fileField, out DataType? physicalType)
            && !physicalType.Equals(requestedField.DataType)
            && TypeWidening.IsSanctionedWidening(physicalType, requestedField.DataType);

        if (!promotable && fileField.ClrType != expected.ClrType)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{requestedField.Name}': file physical type '{fileField.ClrType.Name}' does not "
                + $"match the requested engine type '{requestedField.DataType.SimpleString}' "
                + $"(expected '{expected.ClrType.Name}').");
        }

        // A nullable file column cannot be read into a column the writer would have emitted as
        // non-nullable without risking a null in a required lane; reject rather than coerce. We compare
        // against the EXPECTED field's nullability (not the requested engine flag) because Parquet.Net
        // always models string/binary as nullable, so a required string legitimately maps to a nullable
        // physical column and must not trip this guard.
        if (fileField.IsNullable && !expected.IsNullable)
        {
            throw DeltaStorageException.SchemaMismatch(
                $"Column '{requestedField.Name}': the file column is nullable but the requested engine "
                + "type is non-nullable.");
        }

        // A promoted (narrower physical) column has already been validated as a sanctioned widening; its
        // physical decimal/temporal annotation legitimately differs from the requested wide type, so skip the
        // exact annotation cross-checks below (which assume physical == requested).
        if (promotable)
        {
            return;
        }

        switch (requestedField.DataType)
        {
            case DateType:
                if (fileField is not DateTimeDataField { DateTimeFormat: DateTimeFormat.Date })
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{requestedField.Name}': expected a DATE column but the file annotation "
                        + "is not DATE.");
                }

                break;

            case TimestampType:
                if (fileField is not DateTimeDataField timestampField
                    || timestampField.DateTimeFormat == DateTimeFormat.Date)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{requestedField.Name}': expected a TIMESTAMP column but the file "
                        + "annotation is DATE or not a temporal type.");
                }

                break;

            case DecimalType decimalType:
                if (fileField is not DecimalDataField decimalField
                    || decimalField.Precision != decimalType.Precision
                    || decimalField.Scale != decimalType.Scale)
                {
                    throw DeltaStorageException.SchemaMismatch(
                        $"Column '{requestedField.Name}': file decimal type does not match the requested "
                        + $"'{decimalType.SimpleString}' (precision/scale differ).");
                }

                break;

            default:
                break;
        }
    }

    private static async Task<ColumnBatch?> ReadRowGroupAsync(
        ParquetReader reader,
        int group,
        StructType requested,
        DataField?[] fileFields,
        RowGroupPredicate? keepRowGroup,
        ParquetDecodeLimits limits,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(group);

        if (keepRowGroup is not null)
        {
            var statistics = new RowGroupStatistics(rowGroup, requested, fileFields);
            if (!keepRowGroup(statistics))
            {
                // Pruned: return without reading any column chunk for this group.
                return null;
            }
        }

        // CF-1/H4/L3: reject an implausible or out-of-range row group BEFORE the eager allocation below,
        // so a crafted footer (inflated decompressed size or row count) surfaces as a deterministic
        // CorruptData error rather than an OOM or a raw OverflowException escaping the codec contract.
        long declaredRows = rowGroup.RowCount;
        EnsureDecodeCeiling(declaredRows, ProjectedFootprints(rowGroup, requested, fileFields), group, limits);
        int rowCount;
        try
        {
            rowCount = checked((int)declaredRows);
        }
        catch (OverflowException ex)
        {
            throw DeltaStorageException.CorruptData(
                $"Row group {group} declares {declaredRows} rows, exceeding Int32.MaxValue.", ex);
        }

        var columns = new ColumnVector[requested.Count];
        try
        {
            for (int c = 0; c < requested.Count; c++)
            {
                MutableColumnVector vector = ColumnVectors.Create(requested[c].DataType, Math.Max(rowCount, 1));
                if (fileFields[c] is { } fileField)
                {
                    await ReadColumnAsync(rowGroup, fileField, requested[c], vector, rowCount, allowTypeWideningPromotion, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // The requested column is absent from this (older, narrower) file: materialize it as an
                    // all-null column (evolved-column read null-fill, #497). Only nullable columns reach here
                    // (ResolveFileFields fails closed on an absent non-nullable column).
                    for (int r = 0; r < rowCount; r++)
                    {
                        vector.AppendNull();
                    }
                }

                columns[c] = vector;
            }
        }
        catch (Exception ex) when (IsParquetDefect(ex))
        {
            throw DeltaStorageException.CorruptData(
                $"Failed to decode Parquet row group {group}: {ex.Message}", ex);
        }

        return new ManagedColumnBatch(requested, columns, rowCount);
    }

    // M9: for fixed-width primitive columns the read path materializes into the ColumnVector's backing
    // array with no per-row object allocation (AC #180.3). String/binary columns still materialize one
    // managed object per row via Parquet.Net's decoder; a native UTF-8/byte decode that removes that
    // per-row allocation is a documented, tracked follow-up (not in FEAT-05.1 scope).
    private static async Task ReadColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        bool allowTypeWideningPromotion,
        CancellationToken cancellationToken)
    {
        // Type-widening promotion (#495): when the file column's physical type is a NARROWER sanctioned
        // widening of the requested type, read the file's physical values and widen them into the requested
        // (wide) vector. ValidateFileField has already proven this is a sanctioned widening AND that the
        // caller opened the promotion gate; here we re-check the gate before dispatching the physical read +
        // upcast (defense-in-depth — with the gate closed a mismatched field never reaches here). A physical
        // type equal to the request takes the normal path below.
        if (allowTypeWideningPromotion
            && ParquetTypeMapping.TryToDataType(fileField, out DataType? physicalType)
            && !physicalType.Equals(requestedField.DataType))
        {
            await ReadPromotedColumnAsync(
                rowGroup, fileField, physicalType, requestedField, vector, rowCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        switch (requestedField.DataType)
        {
            case BooleanType:
                await ReadValueAsync<bool>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case ByteType:
                await ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(unchecked((byte)value)), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ShortType:
                await ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case IntegerType:
                await ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case LongType:
                await ReadValueAsync<long>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case FloatType:
                await ReadValueAsync<float>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case DoubleType:
                await ReadValueAsync<double>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(value), cancellationToken).ConfigureAwait(false);
                break;
            case DateType:
                await ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochDay(value)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TimestampType:
                await ReadValueAsync<DateTime>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue(ParquetTypeMapping.DateTimeToEpochMicros(value)),
                    cancellationToken).ConfigureAwait(false);
                break;
            case DecimalType decimalType:
                // L1: thread decimalType through a non-capturing static reader instead of a closure so
                // no per-column-chunk delegate allocation occurs (mirrors the static primitive delegates).
                await ReadDecimalAsync(rowGroup, fileField, vector, rowCount, decimalType, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case StringType:
                await ReadStringAsync(rowGroup, fileField, vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case BinaryType:
                await ReadBinaryAsync(rowGroup, fileField, vector, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw DeltaStorageException.UnsupportedFeature(
                    $"Parquet read for column '{requestedField.Name}' of type "
                    + $"'{requestedField.DataType.SimpleString}' is not supported.");
        }
    }

    // Type-widening read promotion (#495, #535 / Delta PROTOCOL.md "Reader Requirements for Type Widening"):
    // reads the file's NARROW physical values and widens each into the requested WIDE vector. The dispatch is
    // by (physical, requested) — ValidateFileField already proved the pair is a sanctioned widening, so every
    // arm here is a lossless promotion: an integral sign-extend (byte/short/int → wider integral), float→
    // double, a decimal rescale (grow-only decimal), or a cross-family integral→double / integral→decimal
    // (#535) computed in managed code (Parquet.Net stores the file value as the physical integral, so we read
    // the integral and convert — no Parquet.Net cross-type coercion is required). Every append targets the
    // requested vector's storage width, and each lambda is `static` (non-capturing) so no per-column-chunk
    // delegate is allocated.
    private static Task ReadPromotedColumnAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        DataType physicalType,
        StructField requestedField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        if (requestedField.DataType is DecimalType requestedDecimal)
        {
            // decimal(p,s) → decimal(p',s') grow-only (#495): read at the file's scale, rescale to the
            // requested type.
            if (physicalType is DecimalType)
            {
                return ReadDecimalAsync(rowGroup, fileField, vector, rowCount, requestedDecimal, cancellationToken);
            }

            // Cross-family integral → decimal (#535): read the narrow integral physical values and widen each
            // into the requested decimal lane. ValidateFileField proved the decimal holds the full integral
            // range (its integer-digit capacity p − s ≥ the source's digits), so AppendDecimal never truncates.
            return ReadIntegralAsDecimalAsync(
                rowGroup, fileField, physicalType, vector, rowCount, requestedDecimal, cancellationToken);
        }

        if (requestedField.DataType is DoubleType)
        {
            // float → double (#495).
            if (physicalType is FloatType)
            {
                return ReadValueAsync<float>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken);
            }

            // Cross-family byte/short/int → double (#535). long → double is lossy and NOT sanctioned, so it
            // never reaches here; the physical is byte/short/int only.
            return physicalType switch
            {
                ByteType => ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken),
                ShortType => ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken),
                _ => ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((double)value), cancellationToken),
            };
        }

        // Integral widening: byte(sbyte) → short → int → long. The requested vector's storage width is the
        // target of the upcast; the file's physical width is the read buffer's element type.
        switch (requestedField.DataType)
        {
            case ShortType:
                // Only byte → short reaches here (a sanctioned narrower integral).
                return ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                    static (v, value) => v.AppendValue((short)value), cancellationToken);

            case IntegerType:
                return physicalType is ByteType
                    ? ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((int)value), cancellationToken)
                    : ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((int)value), cancellationToken);

            case LongType:
                return physicalType switch
                {
                    ByteType => ReadValueAsync<sbyte>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((long)value), cancellationToken),
                    ShortType => ReadValueAsync<short>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((long)value), cancellationToken),
                    _ => ReadValueAsync<int>(rowGroup, fileField, vector, rowCount,
                        static (v, value) => v.AppendValue((long)value), cancellationToken),
                };

            default:
                // Unreachable: ValidateFileField only admits the sanctioned widenings handled above.
                throw DeltaStorageException.SchemaMismatch(
                    $"Column '{requestedField.Name}': cannot promote physical type "
                    + $"'{physicalType.SimpleString}' to requested '{requestedField.DataType.SimpleString}'.");
        }
    }

    // Cross-family integral → decimal read promotion (#535): reads the file's NARROW integral physical values
    // (byte/short/int/long) and widens each into the requested decimal lane. The scale factors are hoisted
    // ONCE per column chunk (like ReadDecimalAsync). ValidateFileField already proved the requested decimal's
    // integer-digit capacity (precision − scale) holds the full source range, so AppendDecimal's over-precision
    // guard never trips for an in-range integral value.
    private static Task ReadIntegralAsDecimalAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        DataType physicalType,
        MutableColumnVector vector,
        int rowCount,
        DecimalType requestedDecimal,
        CancellationToken cancellationToken)
    {
        ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(requestedDecimal);
        return physicalType switch
        {
            ByteType => ReadDecimalFromIntegralAsync<sbyte>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
            ShortType => ReadDecimalFromIntegralAsync<short>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
            IntegerType => ReadDecimalFromIntegralAsync<int>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
            _ => ReadDecimalFromIntegralAsync<long>(
                rowGroup, fileField, vector, rowCount, requestedDecimal, factors, static v => v, cancellationToken),
        };
    }

    private static async Task ReadDecimalFromIntegralAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        DecimalType requestedDecimal,
        ParquetTypeMapping.DecimalScaleFactors factors,
        Func<T, decimal> toDecimal,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (fileField.IsNullable)
        {
            var buffer = new T?[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    ParquetTypeMapping.AppendDecimal(vector, requestedDecimal, toDecimal(value), factors);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new T[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                ParquetTypeMapping.AppendDecimal(vector, requestedDecimal, toDecimal(buffer[i]), factors);
            }
        }
    }

    private static async Task ReadValueAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        Action<MutableColumnVector, T> append,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        if (fileField.IsNullable)
        {
            var buffer = new T?[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    append(vector, value);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new T[rowCount];
            await rowGroup.ReadAsync<T>(fileField, new Memory<T>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                append(vector, buffer[i]);
            }
        }
    }

    private static async Task ReadDecimalAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        DecimalType decimalType,
        CancellationToken cancellationToken)
    {
        // L1/CF-5: the scale factor (10^scale) and over-precision ceiling (10^precision) are invariant
        // across the whole column chunk, so compute them ONCE here instead of per value in AppendDecimal.
        ParquetTypeMapping.DecimalScaleFactors factors = ParquetTypeMapping.DecimalScaleFactors.For(decimalType);
        if (fileField.IsNullable)
        {
            var buffer = new decimal?[rowCount];
            await rowGroup.ReadAsync<decimal>(fileField, new Memory<decimal?>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                if (buffer[i] is { } value)
                {
                    ParquetTypeMapping.AppendDecimal(vector, decimalType, value, factors);
                }
                else
                {
                    vector.AppendNull();
                }
            }
        }
        else
        {
            var buffer = new decimal[rowCount];
            await rowGroup.ReadAsync<decimal>(fileField, new Memory<decimal>(buffer), null, cancellationToken)
                .ConfigureAwait(false);
            for (int i = 0; i < rowCount; i++)
            {
                ParquetTypeMapping.AppendDecimal(vector, decimalType, buffer[i], factors);
            }
        }
    }

    private static async Task ReadStringAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var buffer = new string?[rowCount];
        await rowGroup.ReadAsync(fileField, new Memory<string?>(buffer), null, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < rowCount; i++)
        {
            if (buffer[i] is { } value)
            {
                vector.AppendBytes(Encoding.UTF8.GetBytes(value));
            }
            else
            {
                vector.AppendNull();
            }
        }
    }

    private static async Task ReadBinaryAsync(
        ParquetRowGroupReader rowGroup,
        DataField fileField,
        MutableColumnVector vector,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[]?[rowCount];
        await rowGroup.ReadAsync(fileField, new Memory<byte[]?>(buffer), null, cancellationToken)
            .ConfigureAwait(false);
        for (int i = 0; i < rowCount; i++)
        {
            if (buffer[i] is { } value)
            {
                vector.AppendBytes(value);
            }
            else
            {
                vector.AppendNull();
            }
        }
    }

    // M2/RF-4b: the narrow set of exceptions the read path maps to a deterministic CorruptData — the
    // faults Parquet.Net actually raises for a malformed, truncated, or otherwise undecodable stream
    // (I/O/format faults plus Parquet.Net's own format exceptions), PLUS the two failure modes an eager
    // per-column allocation can raise if a row group slips past the decode ceiling: OutOfMemoryException
    // and OverflowException. ADR-0013 mandates the reader never let a raw OutOfMemoryException (nor an
    // unchecked-arithmetic OverflowException) escape the decode boundary, so both fail closed as
    // CorruptData rather than escaping raw. Other broad CLR exceptions (InvalidOperationException/
    // ArgumentException/IndexOutOfRangeException/NotSupportedException/FormatException) are deliberately
    // NOT swallowed here, so a genuine bug in our own decode path surfaces as itself instead of being
    // masked as "corrupt data".
    internal static bool IsParquetDefect(Exception ex) => ex is
        IOException or
        InvalidDataException or
        EndOfStreamException or
        OutOfMemoryException or
        OverflowException or
        global::Parquet.ParquetException or
        global::Parquet.Meta.Proto.ThriftProtocolException;

    /// <summary>
    /// A read-only view of one row group's per-column statistics, exposed to a
    /// <see cref="RowGroupPredicate"/> for pruning.
    /// </summary>
    /// <remarks>
    /// <para><b>Min/Max are ENGINE-LANE-normalized (M3).</b> <see cref="Min"/>/<see cref="Max"/> return
    /// values in the same physical lane space the reader decodes columns into — <c>int</c> epoch-day for
    /// DATE, <c>long</c> epoch-microseconds for TIMESTAMP, unscaled <c>Int128</c> for DECIMAL — so a
    /// lane-space pruning predicate compares apples to apples and can never silently drop a matching row.
    /// The raw Parquet-space values remain available via <see cref="RawMin"/>/<see cref="RawMax"/>.</para>
    /// <para><b>Pruning is safe-by-construction (M6).</b> When a statistic is missing, cannot be
    /// lane-normalized without loss (a wide DECIMAL whose stat is a logical <c>BigDecimal</c>), or is a
    /// <c>NaN</c>-poisoned float/double bound, <see cref="Min"/>/<see cref="Max"/> return
    /// <see langword="null"/> — meaning "cannot prune", so the residual predicate is always evaluated and
    /// no row is ever wrongly skipped. TIMESTAMP bounds are additionally widened by ±1&#160;ms because
    /// Parquet's timestamp statistics are millisecond-truncated toward zero, keeping <see cref="Min"/> a
    /// true lower bound and <see cref="Max"/> a true upper bound.</para>
    /// </remarks>
    internal sealed class RowGroupStatistics
    {
        private readonly Dictionary<string, ColumnEntry> _byColumn;

        internal RowGroupStatistics(ParquetRowGroupReader rowGroup, StructType requested, DataField?[] fileFields)
        {
            RowCount = rowGroup.RowCount;
            _byColumn = new Dictionary<string, ColumnEntry>(StringComparer.Ordinal);
            for (int c = 0; c < requested.Count; c++)
            {
                // A column absent from the file (null slot; #497 null-fill) carries no statistics, so pruning
                // on it always returns "cannot prune" — an all-null column has no useful bounds anyway.
                DataColumnStatistics? statistics =
                    fileFields[c] is { } fileField && rowGroup.ColumnExists(fileField)
                        ? rowGroup.GetStatistics(fileField)
                        : null;
                _byColumn[requested[c].Name] = new ColumnEntry(requested[c].DataType, statistics);
            }
        }

        /// <summary>The number of rows in the row group.</summary>
        public long RowCount { get; }

        /// <summary>The raw Parquet.Net statistics for <paramref name="column"/>, or
        /// <see langword="null"/> if absent.</summary>
        public DataColumnStatistics? ForColumn(string column) =>
            _byColumn.TryGetValue(column, out ColumnEntry entry) ? entry.Statistics : null;

        /// <summary>The engine-lane-normalized minimum for <paramref name="column"/>, or
        /// <see langword="null"/> when pruning is not safe (see the type remarks).</summary>
        public object? Min(string column) => LaneBound(column, isMin: true);

        /// <summary>The engine-lane-normalized maximum for <paramref name="column"/>, or
        /// <see langword="null"/> when pruning is not safe (see the type remarks).</summary>
        public object? Max(string column) => LaneBound(column, isMin: false);

        /// <summary>The raw Parquet-space minimum for <paramref name="column"/> (before lane
        /// normalization), or <see langword="null"/> if absent.</summary>
        public object? RawMin(string column) => ForColumn(column)?.MinValue;

        /// <summary>The raw Parquet-space maximum for <paramref name="column"/> (before lane
        /// normalization), or <see langword="null"/> if absent.</summary>
        public object? RawMax(string column) => ForColumn(column)?.MaxValue;

        /// <summary>The null count recorded for <paramref name="column"/>, or <see langword="null"/>.</summary>
        public long? NullCount(string column) =>
            _byColumn.TryGetValue(column, out ColumnEntry entry) ? entry.Statistics?.NullCount : null;

        private object? LaneBound(string column, bool isMin)
        {
            if (!_byColumn.TryGetValue(column, out ColumnEntry entry) || entry.Statistics is null)
            {
                return null;
            }

            object? raw = isMin ? entry.Statistics.MinValue : entry.Statistics.MaxValue;
            if (raw is null)
            {
                return null;
            }

            return Normalize(entry.DataType, raw, isMin);
        }

        // Convert a Parquet-space statistic to the engine lane value, returning null ("cannot prune")
        // whenever normalization would be lossy/ambiguous so a row is never wrongly skipped (M3/M6).
        private static object? Normalize(DataType type, object raw, bool isMin)
        {
            switch (type)
            {
                case BooleanType when raw is bool b:
                    return b;
                case ByteType when raw is int i:
                    return (sbyte)i; // INT8 stat is the signed logical tinyint value.
                case ShortType when raw is int i:
                    return (short)i;
                case IntegerType when raw is int i:
                    return i;
                case LongType when raw is long l:
                    return l;
                case FloatType when raw is float f:
                    return float.IsNaN(f) ? null : f; // NaN-poisoned bound => cannot prune.
                case DoubleType when raw is double d:
                    return double.IsNaN(d) ? null : d;
                case DateType when raw is int i:
                    return i; // Epoch-day already equals the engine lane.
                case TimestampType when raw is long millis:
                    return WidenTimestampMillis(millis, isMin);
                case DecimalType decimalType:
                    return NormalizeDecimal(decimalType, raw);
                default:
                    // string/binary and any unexpected physical type: not prunable in v1.
                    return null;
            }
        }

        // Parquet timestamp statistics are millisecond-truncated toward zero, so the engine-lane
        // (microsecond) bound must be widened by ±1 ms to stay a true lower/upper bound regardless of
        // sign; overflow of the widening or the ms→µs scale => null ("cannot prune").
        private static object? WidenTimestampMillis(long millis, bool isMin)
        {
            try
            {
                long widenedMillis = checked(isMin ? millis - 1 : millis + 1);
                return checked(widenedMillis * 1000L);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        private static object? NormalizeDecimal(DecimalType decimalType, object raw)
        {
            // A compact decimal (precision ≤ 18) stat is the Int64 unscaled value == the engine lane; a
            // wide decimal stat is a logical BigDecimal we cannot losslessly lane-normalize here, so we
            // return null (cannot prune) rather than risk an incorrect bound.
            return raw switch
            {
                long unscaled => (Int128)unscaled,
                int unscaled => (Int128)unscaled,
                _ => null,
            };
        }

        private readonly struct ColumnEntry
        {
            internal ColumnEntry(DataType dataType, DataColumnStatistics? statistics)
            {
                DataType = dataType;
                Statistics = statistics;
            }

            internal DataType DataType { get; }

            internal DataColumnStatistics? Statistics { get; }
        }
    }
}
