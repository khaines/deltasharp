using System.Buffers.Binary;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;
using Xunit;
using StructField = DeltaSharp.Types.StructField;

namespace DeltaSharp.Storage.Tests.Parquet;

/// <summary>
/// #773 (Quality residual): <b>non-bit-flip</b> fuzz coverage for the shared Parquet Modular Encryption
/// classifier (<see cref="ParquetEncryption.ClassifyUnreadableInput"/>). The existing corpus and the CDF
/// cdc-file fuzz exercise <i>bit-flip</i> mutations; these exercise the complementary <b>structural</b>
/// mutation classes — prefix truncation at every offset and hostile footer-length values — that reshape the
/// footer's Thrift framing rather than perturbing individual bytes. The load-bearing property is that the
/// classifier stays <b>fail-closed</b> on a NON-encrypted file under every such mutation: it never returns a
/// false-positive encryption sentinel, never throws (the probe is exception-total on hostile input), and
/// always terminates (bounded, O(footer length)). A positive control proves the suite is not vacuous.
/// </summary>
public sealed class ParquetEncryptionClassifierFuzzTests
{
    private static readonly StructField KeepField = new("keep", DataTypes.LongType, nullable: false);

    private static ColumnBatch BuildLongBatch(StructType schema, long[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.LongType, values.Length);
        foreach (long value in values)
        {
            v.AppendValue(value);
        }

        return new ManagedColumnBatch(schema, new ColumnVector[] { v }, values.Length);
    }

    private static async Task<byte[]> ValidPlaintextFileAsync()
    {
        var schema = new StructType(new[] { KeepField });
        ColumnBatch batch = BuildLongBatch(schema, new long[] { 1, 2, 3, 4, 5 });
        return await ParquetTestHelpers.WriteToBytesAsync(schema, new[] { batch });
    }

    [Fact]
    public async Task ClassifyUnreadableInput_PrefixTruncationClass_NeverFalsePositiveNeverThrows()
    {
        // Structural mutation class 1: every prefix length of a NON-encrypted file. A truncation reshapes the
        // trailing [footer_length][magic] framing the probe relies on, driving it down its early-return arms.
        // None may be misclassified as encrypted, and none may throw. (The probe's IO-fault catch arm is a
        // separate axis pinned by ClassifyUnreadableInput_ThrowingStream_* below.)
        byte[] file = await ValidPlaintextFileAsync();

        for (int len = 0; len <= file.Length; len++)
        {
            byte[] mutant = file[..len];
            using var stream = new MemoryStream(mutant, writable: false);

            string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

            // fail-closed: never the encryption sentinel for a non-encrypted file
            Assert.True(verdict is null, $"truncation len={len} misclassified as encrypted: {verdict}");
        }
    }

    [Fact]
    public async Task ClassifyUnreadableInput_HostileFooterLengthClass_NeverFalsePositiveNeverThrows()
    {
        // Structural mutation class 2: overwrite the 4-byte little-endian footer_length with boundary/hostile
        // values (negative, zero, off-by-one, whole-file, int overflow). The bound check must reject each
        // without reading out of range, throwing, or classifying a non-encrypted file as encrypted.
        //
        // #717 survivor (3): the upper bound `footerLength > length - 8`. The OPEN WINDOW (length-8, length]
        // gives it bite — a weaker bound (e.g. `> length`) that admits any of these values then seeks to
        // `length - 8 - footerLength`, which is NEGATIVE, and throws ArgumentOutOfRangeException (NOT in the
        // probe's IO-fault catch filter) instead of failing closed. The correct bound rejects the whole window
        // and returns null with no throw, so asserting null-and-no-throw across (length-8, length) kills every
        // fail-open relaxation of the bound.
        byte[] file = await ValidPlaintextFileAsync();
        int[] hostileLengths =
        {
            int.MinValue, -1, 0, 1, 7, 8, file.Length - 8,
            file.Length - 7, file.Length - 5, file.Length - 3, file.Length - 2, file.Length - 1,
            file.Length, file.Length + 1, int.MaxValue,
        };

        foreach (int hostile in hostileLengths)
        {
            byte[] mutant = (byte[])file.Clone();
            // Trailing 8 bytes are [footer_length int32 LE][magic 4 bytes]; footer_length starts at ^8.
            BinaryPrimitives.WriteInt32LittleEndian(mutant.AsSpan(mutant.Length - 8, 4), hostile);
            using var stream = new MemoryStream(mutant, writable: false);

            string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

            Assert.True(verdict is null, $"hostile footer_length={hostile} misclassified as encrypted: {verdict}");
        }
    }

    [Fact]
    public async Task ClassifyUnreadableInput_ByteRotationClass_NeverFalsePositiveNeverThrows()
    {
        // Structural mutation class 3: rotate the WHOLE file left by N bytes (a framing shift, not a bit-flip),
        // which shifts the trailing [footer_length][magic] framing. Desynchronizing the Thrift field stream
        // must keep the walk bounded and fail closed rather than stumble onto a field-8 shape and false-positive.
        byte[] file = await ValidPlaintextFileAsync();

        foreach (int rotate in new[] { 1, 2, 3, 5, 8, 13 })
        {
            if (rotate >= file.Length)
            {
                continue;
            }

            byte[] mutant = new byte[file.Length];
            Array.Copy(file, rotate, mutant, 0, file.Length - rotate);
            Array.Copy(file, 0, mutant, file.Length - rotate, rotate);
            using var stream = new MemoryStream(mutant, writable: false);

            string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

            Assert.True(verdict is null, $"left-rotate by {rotate} misclassified as encrypted: {verdict}");
        }
    }

    [Fact]
    public async Task ClassifyUnreadableInput_PlaintextFooterEncrypted_IsPositiveControl()
    {
        // Not-vacuous control: a genuine plaintext-footer encrypted file (the shape the structural mutants
        // above must NOT be confused with) IS classified as the encryption sentinel. If this regressed to
        // null, the fail-closed assertions above would be meaningless.
        byte[] file = await ValidPlaintextFileAsync();
        byte[] encrypted = await ParquetTestHelpers.PlaintextFooterEncryptedFileAsync(file);
        using var stream = new MemoryStream(encrypted, writable: false);

        string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

        Assert.NotNull(verdict);
        Assert.Contains("ncrypt", verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyUnreadableInput_ThrowingReadStream_IsExceptionTotalReturnsNull()
    {
        // Pins the probe's IO-fault axis (both doors' `catch (IOException/ObjectDisposedException/
        // NotSupportedException)` arms): a seekable stream whose reads throw mid-probe must be classified
        // fail-closed as NOT-encrypted (null) — the classifier is exception-total on a hostile stream and
        // never lets an IO fault escape to be mistaken for (or mask) an encryption verdict. RED-on-revert:
        // removing either catch arm turns this into a thrown IOException.
        using var stream = new ThrowOnReadStream(length: 64);

        string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

        Assert.Null(verdict);
    }

    [Fact]
    public async Task ClassifyUnreadableInput_NonSeekableStream_FailsClosedReturnsNull()
    {
        // Pins the `!input.CanSeek` early-return arm on both doors: a readable but non-seekable stream cannot
        // be probed (the footer read seeks from the tail), so it must fail closed as NOT-encrypted. The stub is
        // a SPY whose seek surface (Length/Position/Seek) is fully functional but records any access; the guard
        // must short-circuit BEFORE touching it. Asserting the surface was never touched makes this a genuine
        // RED-on-revert pin: deleting the `!CanSeek` guard lets the probe read `input.Length` and trips the spy
        // (whereas a throwing stub would be masked by the IO-fault catch arm and pass for the wrong reason).
        using var stream = new SeekSpyNonSeekableStream(await ValidPlaintextFileAsync());

        string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

        Assert.Null(verdict);
        Assert.False(stream.SeekSurfaceTouched, "the !CanSeek guard must short-circuit before touching Length/Position/Seek");
    }

    [Fact]
    public void ClassifyUnreadableInput_ModelFooter_EmptyEncryptionUnion_FailsClosed()
    {
        // #717 survivor (1): the NON-EMPTY-union requirement `footer[pos] != 0` at the field-8 arm.
        // A model FileMetaData footer that is otherwise a perfectly plausible encrypted file — all four
        // required fields present WITH their correct Thrift types — but whose field-8 (encryption_algorithm)
        // struct body is an IMMEDIATE STOP (an EMPTY union, which no valid EncryptionAlgorithm ever is) must
        // stay fail-closed (null). Deleting `footer[pos] != 0` flips this to an encryption false-positive, so
        // this is a RED-on-revert pin on that survivor. (The whole-file structural fuzz above never constructs
        // a footer this close to valid, which is why the survivor lived.)
        byte[] footer = BuildFileMetaDataFooter(
            version: ThriftFieldType.I32,
            schema: ThriftFieldType.List,
            numRows: ThriftFieldType.I64,
            rowGroups: ThriftFieldType.List,
            encryptionUnion: EncryptionUnionShape.EmptyStop);
        AssertModelFooterClassifiedNull(footer, "an empty field-8 union must not read as encrypted");
    }

    [Fact]
    public void ClassifyUnreadableInput_ModelFooter_WrongTypedRequiredField_FailsClosed()
    {
        // #717 survivor (2): the TYPE-MATCH gate `if (typeMatches)` on the required fields. A model footer that
        // carries all four required field IDS *and* a non-empty field-8 union — but declares field 1 (version)
        // as an i64 instead of the spec's i32 — is NOT a FileMetaData and must stay fail-closed (null). Deleting
        // the `if (typeMatches)` gate (counting required fields by id alone) flips this to an encryption
        // false-positive, so this pins that survivor RED-on-revert.
        byte[] footer = BuildFileMetaDataFooter(
            version: ThriftFieldType.I64, // WRONG: spec requires i32.
            schema: ThriftFieldType.List,
            numRows: ThriftFieldType.I64,
            rowGroups: ThriftFieldType.List,
            encryptionUnion: EncryptionUnionShape.NonEmpty);
        AssertModelFooterClassifiedNull(footer, "a wrong-typed required field must not read as encrypted");
    }

    [Fact]
    public void ClassifyUnreadableInput_ModelFooter_PlausibleEncrypted_IsPositiveControl()
    {
        // Not-vacuous control for the two model-footer pins above: the SAME builder, given all four required
        // fields with correct Thrift types AND a non-empty field-8 union, IS classified as the encryption
        // sentinel. Without this, the null assertions above could pass because the model machinery can never
        // produce a positive at all.
        byte[] footer = BuildFileMetaDataFooter(
            version: ThriftFieldType.I32,
            schema: ThriftFieldType.List,
            numRows: ThriftFieldType.I64,
            rowGroups: ThriftFieldType.List,
            encryptionUnion: EncryptionUnionShape.NonEmpty);
        using var stream = new MemoryStream(FileFromFooter(footer), writable: false);

        string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

        Assert.NotNull(verdict);
        Assert.Contains("ncrypt", verdict, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertModelFooterClassifiedNull(byte[] footer, string because)
    {
        using var stream = new MemoryStream(FileFromFooter(footer), writable: false);
        string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);
        Assert.True(verdict is null, $"{because}: {verdict}");
    }

    // ---- Model-derived Thrift-compact FileMetaData footer builder (for the field-shape survivors) ----
    // These bytes are the FOOTER STRUCT only; FileFromFooter appends the [footer_length int32 LE][PAR1] trailer
    // the probe reads from the tail. The builder derives each footer from a small parameter space (required
    // field types x union shape) instead of hand-listing corpora, so a shape that would classify as encrypted
    // is one parameter flip away from one that must not.

    private enum ThriftFieldType
    {
        I32,
        I64,
        List,
    }

    private enum EncryptionUnionShape
    {
        // Field-8 struct body = immediate STOP => EMPTY union (no valid EncryptionAlgorithm is empty).
        EmptyStop,

        // Field-8 struct body = one member (field 1, an AES_GCM_V1 struct terminated by STOP) => non-empty.
        NonEmpty,
    }

    private const int ThriftStop = 0x00;
    private const int ThriftI32Code = 5;
    private const int ThriftI64Code = 6;
    private const int ThriftListCode = 9;
    private const int ThriftStructCode = 12;

    private static int TypeCode(ThriftFieldType t) => t switch
    {
        ThriftFieldType.I32 => ThriftI32Code,
        ThriftFieldType.I64 => ThriftI64Code,
        ThriftFieldType.List => ThriftListCode,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    private static void EmitScalarOrList(List<byte> footer, int fieldDelta, ThriftFieldType t)
    {
        // Compact short-form field header: (delta << 4) | type.
        footer.Add((byte)((fieldDelta << 4) | TypeCode(t)));
        if (t == ThriftFieldType.List)
        {
            // Empty list header: (size=0 << 4) | element-type (struct). Zero elements => no element bytes.
            footer.Add((byte)ThriftStructCode);
        }
        else
        {
            // i32/i64 value is a single-byte varint (0). The probe skips the value; its content is irrelevant.
            footer.Add(0x00);
        }
    }

    private static byte[] BuildFileMetaDataFooter(
        ThriftFieldType version,
        ThriftFieldType schema,
        ThriftFieldType numRows,
        ThriftFieldType rowGroups,
        EncryptionUnionShape encryptionUnion)
    {
        var footer = new List<byte>();

        // Fields 1..4 (deltas 1,1,1,1 from a starting id of 0).
        EmitScalarOrList(footer, fieldDelta: 1, version);   // field 1: version
        EmitScalarOrList(footer, fieldDelta: 1, schema);    // field 2: schema
        EmitScalarOrList(footer, fieldDelta: 1, numRows);   // field 3: num_rows
        EmitScalarOrList(footer, fieldDelta: 1, rowGroups); // field 4: row_groups

        // Field 8: encryption_algorithm struct (delta 4 from field 4).
        footer.Add((byte)((4 << 4) | ThriftStructCode));
        if (encryptionUnion == EncryptionUnionShape.NonEmpty)
        {
            footer.Add((byte)((1 << 4) | ThriftStructCode)); // union member: field 1 (AES_GCM_V1), a struct
            footer.Add((byte)ThriftStop);                    // AES_GCM_V1 body STOP
        }

        footer.Add((byte)ThriftStop); // field-8 union struct STOP (immediate STOP => empty when NonEmpty absent)
        footer.Add((byte)ThriftStop); // top-level FileMetaData STOP

        return footer.ToArray();
    }

    private static byte[] FileFromFooter(byte[] footer)
    {
        // [footer struct][footer_length int32 LE][PAR1] — the tail framing IsPlaintextFooterEncryptedByFooterProbe
        // reads. No leading magic is needed: the plaintext-footer probe does not check head magic, and the
        // footer's first byte is a Thrift header (never 'PARE'), so the encrypted-footer-magic arm passes over it.
        byte[] file = new byte[footer.Length + 8];
        footer.CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(footer.Length, 4), footer.Length);
        file[footer.Length + 4] = (byte)'P';
        file[footer.Length + 5] = (byte)'A';
        file[footer.Length + 6] = (byte)'R';
        file[footer.Length + 7] = (byte)'1';
        return file;
    }

    /// <summary>A seekable stream whose reads always throw <see cref="IOException"/>, to drive the probe's
    /// IO-fault catch arm.</summary>
    private sealed class ThrowOnReadStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated read fault during encryption probe");

        public override int Read(Span<byte> buffer) =>
            throw new IOException("simulated read fault during encryption probe");

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => length + offset,
            };
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A readable but non-seekable stream that <b>spies</b> on its seek surface: Length/Position/Seek
    /// are fully functional (delegating to an inner buffer) but flip <see cref="SeekSurfaceTouched"/> on any
    /// access. Lets a test prove the <c>!CanSeek</c> guard short-circuits BEFORE any seek attempt — a real
    /// RED-on-revert pin, unlike a throwing stub whose fault the probe's IO catch arm would mask.</summary>
    private sealed class SeekSpyNonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public bool SeekSurfaceTouched { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                SeekSurfaceTouched = true;
                return _inner.Length;
            }
        }

        public override long Position
        {
            get
            {
                SeekSurfaceTouched = true;
                return _inner.Position;
            }

            set
            {
                SeekSurfaceTouched = true;
                _inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
        {
            SeekSurfaceTouched = true;
            return _inner.Seek(offset, origin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
