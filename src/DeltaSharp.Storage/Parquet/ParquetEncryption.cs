using System.Buffers.Binary;

namespace DeltaSharp.Storage.Parquet;

/// <summary>
/// The single Parquet <b>Modular Encryption</b> classifier shared by every reader door
/// (<see cref="ParquetFileReader"/> for data files, <see cref="Delta.DeltaCheckpointReader"/> for
/// <c>_delta_log</c> checkpoints). DeltaSharp cannot read encrypted Parquet, so both doors must fail
/// <b>closed</b>; this type exists only so they fail closed with the same <b>actionable</b> diagnosis
/// (an unsupported-but-valid feature) instead of each door inventing its own — and so the detection
/// rules live in exactly one place (#681; the encrypted-footer arm is #649/#654, the plaintext-footer
/// arms are #655/#680).
///
/// <para><b>The format has two modes, and they surface differently.</b>
/// <list type="bullet">
/// <item><b>Encrypted footer</b> — the file is bracketed by the <c>PARE</c> magic instead of
/// <c>PAR1</c>. Parquet.Net 6.0.3 rejects it at open with a message byte-for-byte identical to the one
/// it emits for arbitrary garbage, so it is discriminated by the file's own magic
/// (<see cref="IsParquetEncryptedFooterMagic"/>), never by message matching.</item>
/// <item><b>Plaintext footer</b> — the file keeps the ordinary <c>PAR1</c> magic and a readable footer
/// carrying <c>encryption_algorithm</c> and/or per-column <c>ColumnCryptoMetaData</c>, while the column
/// chunks are encrypted. When the encrypted columns retain their plaintext <c>ColumnMetaData</c> the
/// open <b>succeeds</b> and it is detected from the parsed metadata
/// (<see cref="IsPlaintextFooterEncrypted"/>); when a real encryptor omits that metadata the open
/// <b>fails</b> inside the library and it is detected by probing the file's own plaintext footer.</item>
/// </list>
/// A door therefore needs both arms: the parsed-metadata check on its success path, and
/// <see cref="ClassifyUnreadableInput"/> on its failure path (before it disposes the reader, which
/// releases the input stream).</para>
///
/// <para><b>Presence-only, and fail-closed.</b> The diagnosis is the mere <i>presence</i> of a crypto
/// marker — no footer field name, value, key id, or path is ever read into a message (#653 info-leak
/// hygiene), so both doors can surface a fixed string. Nothing here asserts encryption on a guess: an
/// input that cannot be positively confirmed encrypted returns <see langword="false"/>/<see langword="null"/>
/// and the caller keeps its fail-closed corrupt-input default. The footer probe parses <b>untrusted</b>
/// bytes and is bounded (footer-size cap, recursion cap, work linear in the footer length), never throws,
/// and restores the stream position so the observation is transparent.</para>
/// </summary>
internal static class ParquetEncryption
{
    // The shared message for the ENCRYPTED-FOOTER arm (#649). Presence-only, like its plaintext-footer
    // sibling: it names the feature and the on-disk marker family, never any file content.
    internal const string EncryptedFooterEncryptionMessage =
        "Parquet Modular Encryption is not supported: the file uses an encrypted footer (PARE "
        + "magic). DeltaSharp cannot read encrypted Parquet files.";

    /// <summary>
    /// The FAILURE-path classifier both reader doors call when the Parquet library refused to open
    /// <paramref name="input"/>: returns the actionable unsupported-feature message when the input is
    /// positively identified as a Parquet Modular Encryption file (encrypted-footer <c>PARE</c> bracketing,
    /// or a plaintext footer carrying <c>encryption_algorithm</c>), or <see langword="null"/> when it is not
    /// — in which case the caller keeps its fail-closed corrupt-input classification. Must be called
    /// <b>before</b> the door disposes its reader, because disposal releases the input stream this reads.
    /// </summary>
    internal static string? ClassifyUnreadableInput(Stream input)
    {
        // Order matters only for message selection: a PARE-bracketed file has no readable plaintext footer,
        // and a plaintext-footer file is PAR1-bracketed, so the two arms are mutually exclusive in practice.
        if (IsParquetEncryptedFooterMagic(input))
        {
            return EncryptedFooterEncryptionMessage;
        }

        return IsPlaintextFooterEncryptedByFooterProbe(input) ? PlaintextFooterEncryptionMessage : null;
    }

    // Detects a plaintext-footer Parquet Modular Encryption file (#655) from the PARSED footer metadata.
    // Three markers, any of which is sufficient (Parquet format Encryption.md):
    //   * FileMetaData.EncryptionAlgorithm (Thrift field 8) — "set only in encrypted files with plaintext
    //     footer" per the format spec, carrying a NON-EMPTY union (see below);
    //   * any ColumnChunk.CryptoMetadata (ColumnCryptoMetaData) — a plaintext-footer file may encrypt only a
    //     SUBSET of columns, carrying per-column crypto metadata even when the file-level algorithm is unset;
    //     and
    //   * a non-null EncryptionAlgorithm of any shape when the footer has NO column chunk to inspect, where
    //     the per-column marker is vacuous and the narrowed union rule must fail closed (#698 gate).
    // Detection is presence-only: no field CONTENTS are read, so no attacker-controlled footer value can be
    // echoed (#653 hygiene). A healthy unencrypted file has no marker, so this never false-positives on one.
    // A CORRUPT footer, however, can still PARSE into a spurious marker (~1% of single-bit flips in the
    // checkpoint fuzz corpus) — which is why both doors materialize the high-level schema BEFORE consulting
    // this check, so a genuinely corrupt file is proven corrupt first. That ordering is REQUIRED, not
    // belt-and-braces: this classifier does NOT cover the observed corpus on its own, because every one of
    // those flips lands on the third arm above (empty union + zero inspectable column chunks), which by
    // design reads as bare presence. The schema probe is the sole control for them, and it is pinned by
    // DeltaCheckpointReaderTests.SchemaFirstOrdering_IsSoleControl_ForAtLeastOneCorruptFlip.
    internal static bool IsPlaintextFooterEncrypted(global::Parquet.Meta.FileMetaData? metadata)
    {
        if (metadata is null)
        {
            return false;
        }

        // Require a NON-EMPTY union, the parsed analogue of the rule the FAILURE-path footer walk already
        // applies: per parquet.thrift, EncryptionAlgorithm is a union of exactly AES_GCM_V1 and
        // AES_GCM_CTR_V1, and a valid encrypted file always sets exactly one member — so requiring one carries
        // no false-negative risk for a real encrypted file, while rejecting the EMPTY union that a corrupt
        // footer can parse into (every observed fuzz false-positive was exactly this shape). Applying the same
        // precision rule on both arms removes an EMPTY-union false positive here without relying on the
        // unrelated property of schema materializability. It does NOT make the schema probe redundant: those
        // same fuzz shapes also carry zero inspectable column chunks, so they fall through to the third arm
        // below and are read as bare presence anyway — see the ordering note at the top of this method.
        //
        // Forward-compat note: if a future format revision adds a third union member that this Parquet.Net
        // version cannot deserialize, both properties stay null here. The backstop is then the PER-COLUMN
        // CryptoMetadata arm below — NOT the raw-bytes failure-path probe, which only runs when CreateAsync
        // THROWS, and such a file opens cleanly (verified: patching the union's member field id to an unknown
        // value leaves Parquet.Net opening the file with a non-null algorithm and both members null). That is
        // why the per-column arm is deliberately left BARE-PRESENCE rather than being tightened to match this
        // one: the asymmetry between the two arms is the mechanism that keeps an unknown future algorithm
        // classified. The spec mandates crypto_metadata on every encrypted column, so a real file with any
        // columns always carries it — an assumption about FOREIGN writers, whose residual is named at the
        // third arm below. That clause is also VACUOUS for a file with no column chunks at all, so
        // the third arm below restores fail-closed behaviour in exactly that case.
        global::Parquet.Meta.EncryptionAlgorithm? algorithm = metadata.EncryptionAlgorithm;
        if (algorithm is not null && (algorithm.AESGCMV1 is not null || algorithm.AESGCMCTRV1 is not null))
        {
            return true;
        }

        // Per-column arm — BARE PRESENCE by design, not an oversight or an un-tightened leftover. Unlike the
        // union above, ColumnCryptoMetaData has no "empty" shape that corruption is observed to produce, and
        // keeping it presence-only is what makes it the forward-compat backstop described above: an unknown
        // future algorithm nulls both union members but still leaves every encrypted column marked here.
        bool anyColumnChunkInspected = false;
        IReadOnlyList<global::Parquet.Meta.RowGroup>? rowGroups = metadata.RowGroups;
        if (rowGroups is not null)
        {
            foreach (global::Parquet.Meta.RowGroup? rowGroup in rowGroups)
            {
                IReadOnlyList<global::Parquet.Meta.ColumnChunk>? columns = rowGroup?.Columns;
                if (columns is null)
                {
                    continue;
                }

                foreach (global::Parquet.Meta.ColumnChunk? column in columns)
                {
                    if (column is null)
                    {
                        continue; // Not inspectable: a null chunk can neither carry nor deny the marker.
                    }

                    anyColumnChunkInspected = true;
                    if (column.CryptoMetadata is not null)
                    {
                        return true;
                    }
                }
            }
        }

        // Third arm: the per-column backstop could not be EVALUATED — the footer has no column chunk to
        // inspect (no row groups, or row groups with no columns) — so its silence is vacuous rather than a
        // finding of "not encrypted". Where the backstop cannot speak, fall back to BARE PRESENCE on the
        // algorithm so the narrowing above fails CLOSED instead of becoming silently permissive: a non-null
        // algorithm whose known union members are both null (an unknown future algorithm id, which this
        // Parquet.Net version drops) would otherwise be read as an ordinary plaintext file. Deliberately
        // scoped to "no chunk was inspectable", NOT to "no chunk was marked": control only reaches here when
        // nothing was marked, so the latter reduces EXACTLY to `algorithm is not null` — plain bare presence,
        // undoing the empty-union precision fix. (It would not fire on ordinary unencrypted files, which carry
        // no algorithm at all; it fires on the corrupt footers that parse into a spurious one.) A corrupt
        // footer that parses into an empty union keeps its columns, so it is unaffected.
        //
        // NAMED RESIDUAL — this arm rests on a guarantee about FOREIGN writers, not on our own behaviour: the
        // format spec mandates crypto_metadata on every encrypted column, which is why "columns exist and none
        // is marked" is treated as real evidence of "not encrypted" rather than as absent evidence. A writer
        // that violates the mandate — encrypted columns carrying no crypto_metadata, under an algorithm id
        // this version cannot recognize — classifies as PLAINTEXT here. That is a deliberate trade, not an
        // oversight: closing it requires the bare-presence collapse above. The practical backstop is that such
        // a file's pages are ciphertext and do not decode as valid Parquet, so the read still fails closed as
        // CORRUPT rather than returning wrong rows — a downstream consequence, not a guarantee this method
        // makes. Diagnosis degrades from "unsupported feature" to "corrupt"; correctness does not.
        return !anyColumnChunkInspected && algorithm is not null;
    }

    // Shared message for both plaintext-footer arms (success-path metadata check + failure-path footer probe).
    // Presence-only diagnosis: it names the feature, never a footer field name/path/value (#653 info-leak
    // hygiene), and is accurate whether the file-level encryption_algorithm or a per-column crypto_metadata
    // triggered detection.
    internal const string PlaintextFooterEncryptionMessage =
        "Parquet Modular Encryption is not supported: the file uses plaintext-footer encryption "
        + "(the footer carries Parquet Modular Encryption metadata). DeltaSharp cannot read encrypted Parquet files.";

    // Upper bound on the plaintext footer we will parse on the CreateAsync-FAILURE path. A Parquet footer is
    // normally KB-scale; this cap stops a crafted oversized footer_length from forcing a large read/scan before
    // we fall back to the fail-closed CorruptData default.
    private const int MaxProbedFooterBytes = 16 * 1024 * 1024;

    // Recursion-depth ceiling for the untrusted Thrift-compact footer walk so a crafted deeply-nested footer
    // cannot exhaust the stack. Real Parquet FileMetaData nests only a handful of levels.
    private const int MaxThriftProbeDepth = 64;

    // Thrift Compact Protocol type codes — the subset the footer walk must recognize to skip a field value
    // (Apache Thrift compact spec). FileMetaData.encryption_algorithm is field 8, a struct (union).
    private const int ThriftBoolTrue = 1;
    private const int ThriftBoolFalse = 2;
    private const int ThriftI8 = 3;
    private const int ThriftI16 = 4;
    private const int ThriftI32 = 5;
    private const int ThriftI64 = 6;
    private const int ThriftDouble = 7;
    private const int ThriftBinary = 8;
    private const int ThriftList = 9;
    private const int ThriftSet = 10;
    private const int ThriftMap = 11;
    private const int ThriftStruct = 12;
    private const int EncryptionAlgorithmFieldId = 8;

    /// <summary>
    /// Reflection-free detection of a plaintext-footer Parquet Modular Encryption file by reading the file's
    /// own plaintext footer and probing the <c>FileMetaData.encryption_algorithm</c> field (Thrift field 8),
    /// which the format spec sets for <b>every</b> plaintext-footer encrypted file. Needed because a real
    /// encrypting writer omits the plaintext <c>ColumnMetaData</c> on encrypted columns, which makes
    /// Parquet.Net 6.0.3 throw during <c>CreateAsync</c>'s row-group-reader init <b>before</b> the success-path
    /// <c>reader.Metadata</c> check runs — so that shape is only reachable here, on the failure path. Reflection
    /// is deliberately avoided (Storage keeps the trim/AOT analyzers clean, ADR-0014); this walks the footer as
    /// a <see cref="ReadOnlySpan{T}"/>. As a parser of <b>untrusted</b> bytes it is strictly bounded (footer
    /// size cap + recursion-depth cap + total-work bounded by the footer length) and <b>fail-closed</b>: any
    /// truncated/oversized/malformed input returns <see langword="false"/> so the caller keeps the CorruptData
    /// default, and it never throws. The input position is restored so the observation is transparent.
    /// </summary>
    private static bool IsPlaintextFooterEncryptedByFooterProbe(Stream input)
    {
        if (input is null || !input.CanSeek)
        {
            return false;
        }

        try
        {
            long length = input.Length;
            // Minimum plausible file: a (>=1 byte) footer + the 4-byte footer length + 4-byte trailing magic.
            if (length < ParquetMagicLength + 8)
            {
                return false;
            }

            long savedPosition = input.Position;
            try
            {
                // Trailing 8 bytes: [footer_length int32 LE][magic 4 bytes]. (PARE was excluded by the caller;
                // a genuine mode-b file is PAR1-bracketed. The footer_length bound below — not the magic — is
                // the real guard: garbage will not parse to a field-8 struct and stays CorruptData.)
                Span<byte> tail = stackalloc byte[8];
                input.Position = length - 8;
                if (input.ReadAtLeast(tail, 8, throwOnEndOfStream: false) != 8)
                {
                    return false;
                }

                int footerLength = BinaryPrimitives.ReadInt32LittleEndian(tail);
                if (footerLength <= 0 || footerLength > length - 8 || footerLength > MaxProbedFooterBytes)
                {
                    return false;
                }

                byte[] footer = new byte[footerLength];
                input.Position = length - 8 - footerLength;
                if (input.ReadAtLeast(footer, footerLength, throwOnEndOfStream: false) != footerLength)
                {
                    return false;
                }

                return ThriftFooterHasEncryptionAlgorithm(footer);
            }
            finally
            {
                // Restore on every path so the probe is transparent to any later use of the stream.
                input.Position = savedPosition;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            // A probe I/O fault on an already-failing input must never REPLACE the fail-closed classification.
            return false;
        }
    }

    // Walks the ENTIRE top-level FileMetaData struct (Thrift Compact Protocol), skipping every field's value,
    // and reports whether it is a PLAUSIBLE ENCRYPTED FileMetaData: a well-formed footer that (a) parses
    // cleanly to its top-level STOP, (b) carries the FileMetaData required fields WITH their expected Thrift
    // types (1=version i32, 2=schema list, 3=num_rows i64, 4=row_groups list per parquet.thrift), and (c)
    // carries field 8 (encryption_algorithm) as a NON-EMPTY struct (a valid EncryptionAlgorithm union has
    // exactly one member, so its struct body does not start with an immediate STOP). Requiring all three — not
    // merely a field-8-struct header, nor a type-blind field-id presence — keeps a malformed/truncated footer,
    // or a syntactically-valid-but-not-a-FileMetaData footer that merely embeds a field-8 struct (e.g. 0x8C…,
    // an empty field-8 union, or wrong-typed required fields), on the fail-closed CorruptData default rather
    // than mislabeling it "encrypted" (red-team R2/R3/R4). A real encrypted Parquet file always satisfies all
    // three. Fail-closed: returns false on truncation, an unparseable value, or a footer that is not a
    // plausible encrypted FileMetaData. Bounded: every non-boolean field/element consumes >= 1 byte (total
    // work is O(footer length)) and recursion is depth-capped.
    private static bool ThriftFooterHasEncryptionAlgorithm(ReadOnlySpan<byte> footer)
    {
        // Bits 1..4 = the FileMetaData required fields (version/schema/num_rows/row_groups).
        const int requiredFieldsMask = (1 << 1) | (1 << 2) | (1 << 3) | (1 << 4);
        int seenRequiredFields = 0;
        bool encryptionAlgorithmPresent = false;
        int pos = 0;
        int lastFieldId = 0;
        while (true)
        {
            if (pos >= footer.Length)
            {
                return false; // Ran out before a clean STOP => malformed => not confidently encrypted.
            }

            byte header = footer[pos++];
            if (header == 0)
            {
                // Clean STOP: trust the encryption signal only on a plausible encrypted FileMetaData.
                return encryptionAlgorithmPresent
                    && (seenRequiredFields & requiredFieldsMask) == requiredFieldsMask;
            }

            int type = header & 0x0F;
            int delta = (header >> 4) & 0x0F;
            int fieldId;
            if (delta != 0)
            {
                fieldId = lastFieldId + delta;
            }
            else if (TryReadZigZag(footer, ref pos, out long explicitId) && explicitId > 0 && explicitId <= int.MaxValue)
            {
                fieldId = (int)explicitId;
            }
            else
            {
                return false;
            }

            lastFieldId = fieldId;

            if (fieldId is >= 1 and <= 4)
            {
                // Require each required field to also carry its FileMetaData type (version=i32, schema=list,
                // num_rows=i64, row_groups=list per parquet.thrift) — a type-blind presence check would accept
                // a footer that reuses those ids with arbitrary types (red-team R4).
                bool typeMatches = fieldId switch
                {
                    1 => type == ThriftI32,
                    2 => type == ThriftList,
                    3 => type == ThriftI64,
                    4 => type == ThriftList,
                    _ => false,
                };
                if (typeMatches)
                {
                    seenRequiredFields |= 1 << fieldId;
                }
            }
            else if (fieldId == EncryptionAlgorithmFieldId
                && type == ThriftStruct
                && pos < footer.Length
                && footer[pos] != 0)
            {
                // Field 8 is a struct whose body does not start with an immediate STOP => a NON-EMPTY
                // EncryptionAlgorithm union (a valid one has exactly one member). An empty field-8 struct is
                // rejected. Fall through to skip (and thereby fully validate) the struct body.
                encryptionAlgorithmPresent = true;
            }

            // A boolean field carries its value in the header (no data bytes); every other type has a value to
            // skip (encryption_algorithm is a struct, so its body IS skipped/validated here).
            if (type is ThriftBoolTrue or ThriftBoolFalse)
            {
                continue;
            }

            if (!SkipThriftValue(footer, ref pos, type, depth: 1))
            {
                return false;
            }
        }
    }

    // Skips one Thrift-compact value of the given type. Called for non-boolean struct-field values and for
    // collection/map elements (where a boolean element DOES occupy one byte). Fail-closed on truncation,
    // unknown type, or excessive depth.
    private static bool SkipThriftValue(ReadOnlySpan<byte> data, ref int pos, int type, int depth)
    {
        if (depth > MaxThriftProbeDepth)
        {
            return false;
        }

        switch (type)
        {
            case ThriftBoolTrue:
            case ThriftBoolFalse:
                return AdvanceThrift(data, ref pos, 1); // element context: one byte per boolean.
            case ThriftI8:
                return AdvanceThrift(data, ref pos, 1);
            case ThriftI16:
            case ThriftI32:
            case ThriftI64:
                return TrySkipVarint(data, ref pos);
            case ThriftDouble:
                return AdvanceThrift(data, ref pos, 8);
            case ThriftBinary:
                if (!TryReadVarint(data, ref pos, out long binLen) || binLen < 0 || binLen > int.MaxValue)
                {
                    return false;
                }

                return AdvanceThrift(data, ref pos, (int)binLen);
            case ThriftList:
            case ThriftSet:
                return SkipThriftCollection(data, ref pos, depth);
            case ThriftMap:
                return SkipThriftMap(data, ref pos, depth);
            case ThriftStruct:
                return SkipThriftStruct(data, ref pos, depth);
            default:
                return false; // unknown/invalid compact type => fail closed.
        }
    }

    // Skips a struct: field headers until STOP, skipping each field's value (a boolean field value is in its
    // header, so nothing extra is consumed for it).
    private static bool SkipThriftStruct(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > MaxThriftProbeDepth)
        {
            return false;
        }

        while (true)
        {
            if (pos >= data.Length)
            {
                return false;
            }

            byte header = data[pos++];
            if (header == 0)
            {
                return true; // STOP.
            }

            int type = header & 0x0F;
            int delta = (header >> 4) & 0x0F;
            if (delta == 0 && !TrySkipVarint(data, ref pos))
            {
                return false; // explicit zigzag field id.
            }

            if (type is ThriftBoolTrue or ThriftBoolFalse)
            {
                continue; // boolean field value carried in the header.
            }

            if (!SkipThriftValue(data, ref pos, type, depth + 1))
            {
                return false;
            }
        }
    }

    // Skips a list/set: a 1-byte size/element-type header (size extended to a varint when the nibble is 0xF),
    // then each element. Every element consumes >= 1 byte, so a crafted huge size runs out of footer and bails.
    private static bool SkipThriftCollection(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (pos >= data.Length)
        {
            return false;
        }

        byte sizeAndType = data[pos++];
        int elementType = sizeAndType & 0x0F;
        long size = (sizeAndType >> 4) & 0x0F;
        if (size == 0x0F && (!TryReadVarint(data, ref pos, out size) || size < 0 || size > int.MaxValue))
        {
            return false;
        }

        for (long i = 0; i < size; i++)
        {
            if (!SkipThriftValue(data, ref pos, elementType, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    // Skips a map: a varint size, then (for a non-empty map) a 1-byte key/value type header, then each pair.
    private static bool SkipThriftMap(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (!TryReadVarint(data, ref pos, out long size) || size < 0 || size > int.MaxValue)
        {
            return false;
        }

        if (size == 0)
        {
            return true;
        }

        if (pos >= data.Length)
        {
            return false;
        }

        byte kvTypes = data[pos++];
        int keyType = (kvTypes >> 4) & 0x0F;
        int valueType = kvTypes & 0x0F;
        for (long i = 0; i < size; i++)
        {
            if (!SkipThriftValue(data, ref pos, keyType, depth + 1)
                || !SkipThriftValue(data, ref pos, valueType, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    // Reads an unsigned LEB128 varint (max 10 bytes for 64 bits). Fail-closed on truncation/overrun.
    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out long value)
    {
        value = 0;
        int shift = 0;
        while (shift <= 63)
        {
            if (pos >= data.Length)
            {
                value = 0;
                return false;
            }

            byte b = data[pos++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false; // more than 10 continuation bytes => malformed.
    }

    private static bool TrySkipVarint(ReadOnlySpan<byte> data, ref int pos) => TryReadVarint(data, ref pos, out _);

    // Reads a zigzag-encoded varint (Thrift's signed int encoding), used only for an explicit field id.
    private static bool TryReadZigZag(ReadOnlySpan<byte> data, ref int pos, out long value)
    {
        if (!TryReadVarint(data, ref pos, out long raw))
        {
            value = 0;
            return false;
        }

        value = (long)((ulong)raw >> 1) ^ -(raw & 1L);
        return true;
    }

    private static bool AdvanceThrift(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (count < 0 || count > data.Length - pos)
        {
            return false;
        }

        pos += count;
        return true;
    }

    // The Parquet file magic is 4 bytes. A plaintext file is bracketed by 'PAR1'; a Parquet Modular
    // Encryption file written in ENCRYPTED-FOOTER mode is bracketed by 'PARE' (0x50 0x41 0x52 0x45) instead
    // (Parquet format Encryption.md). Parquet.Net 6.0.3 cannot read encrypted files and rejects the 'PARE'
    // head during CreateAsync (#649).
    private const int ParquetMagicLength = 4;

    private static ReadOnlySpan<byte> EncryptedFooterMagic => "PARE"u8;

    /// <summary>
    /// Peeks whether <paramref name="input"/> is bracketed by the Parquet <b>encrypted-footer</b> magic
    /// (<c>PARE</c>) at <b>both</b> ends — the on-disk marker of a (complete) Parquet Modular Encryption file
    /// the library rejects as "not a parquet file" (#649). This is the <b>robust</b> encryption discriminator:
    /// it reads the file's own leading and trailing magic from the seekable input (every reader entry point
    /// passes a seekable
    /// <see cref="MemoryStream"/>, and <see cref="ParquetReader.CreateAsync(Stream, ParquetOptions?, bool, CancellationToken)"/>
    /// leaves the caller's stream open when it throws) rather than substring-matching the library's error
    /// message, which is byte-for-byte identical for genuine non-Parquet garbage ("not a parquet file, head:
    /// …") and so cannot separate encryption from corruption. Only an input positively bracketed by <c>PARE</c>
    /// at both ends returns <see langword="true"/>: a non-seekable, too-short, merely-<c>PARE</c>-prefixed
    /// (corrupt/truncated), or unreadable input can NOT be confirmed a complete encrypted file, so it returns
    /// <see langword="false"/> and the caller keeps the fail-closed CorruptData default — encryption is
    /// asserted, never guessed. The input's position is restored so this observation is transparent to any
    /// later use.
    /// </summary>
    internal static bool IsParquetEncryptedFooterMagic(Stream input)
    {
        if (input is null || !input.CanSeek)
        {
            return false;
        }

        try
        {
            // A valid Parquet Modular Encryption (encrypted-footer mode) file is bracketed by the 'PARE' magic
            // at BOTH ends (Parquet Encryption.md), so it is at least two 4-byte magics long. Requiring the
            // TRAILING magic too — not just the head — keeps a merely-'PARE'-prefixed CORRUPT file (a 'PARE'
            // head with a non-'PARE' or absent/truncated tail) mapped to the fail-closed CorruptData default
            // instead of mislabeled "encrypted" (#649 precision, council R1). Only a fully-bracketed file is
            // confidently a (complete) encrypted-footer file; a truncated one is genuinely corrupt.
            if (input.Length < 2 * ParquetMagicLength)
            {
                return false;
            }

            long savedPosition = input.Position;
            try
            {
                Span<byte> magic = stackalloc byte[ParquetMagicLength];

                input.Position = 0;
                if (input.ReadAtLeast(magic, ParquetMagicLength, throwOnEndOfStream: false) != ParquetMagicLength
                    || !magic.SequenceEqual(EncryptedFooterMagic))
                {
                    return false;
                }

                input.Position = input.Length - ParquetMagicLength;
                return input.ReadAtLeast(magic, ParquetMagicLength, throwOnEndOfStream: false) == ParquetMagicLength
                    && magic.SequenceEqual(EncryptedFooterMagic);
            }
            finally
            {
                // Restore on every path (both magic reads and any fault) so the observation is transparent.
                input.Position = savedPosition;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            // A peek fault on an already-failing input must never REPLACE the deterministic classification;
            // "cannot confirm the magic" degrades to "not encrypted" so the CorruptData default still holds.
            return false;
        }
    }
}
