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
        // trailing [footer_length][magic] framing the probe relies on, driving it down every early-return and
        // catch arm. None may be misclassified as encrypted, and none may throw.
        byte[] file = await ValidPlaintextFileAsync();

        for (int len = 0; len <= file.Length; len++)
        {
            byte[] mutant = file[..len];
            using var stream = new MemoryStream(mutant, writable: false);

            string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

            Assert.Null(verdict); // fail-closed: never the encryption sentinel for a non-encrypted file
        }
    }

    [Fact]
    public async Task ClassifyUnreadableInput_HostileFooterLengthClass_NeverFalsePositiveNeverThrows()
    {
        // Structural mutation class 2: overwrite the 4-byte little-endian footer_length with boundary/hostile
        // values (negative, zero, off-by-one, whole-file, int overflow). The bound check must reject each
        // without reading out of range, throwing, or classifying a non-encrypted file as encrypted.
        byte[] file = await ValidPlaintextFileAsync();
        int[] hostileLengths =
        {
            int.MinValue, -1, 0, 1, 7, 8, file.Length - 8, file.Length, file.Length + 1, int.MaxValue,
        };

        foreach (int hostile in hostileLengths)
        {
            byte[] mutant = (byte[])file.Clone();
            // Trailing 8 bytes are [footer_length int32 LE][magic 4 bytes]; footer_length starts at ^8.
            BinaryPrimitives.WriteInt32LittleEndian(mutant.AsSpan(mutant.Length - 8, 4), hostile);
            using var stream = new MemoryStream(mutant, writable: false);

            string? verdict = ParquetEncryption.ClassifyUnreadableInput(stream);

            Assert.Null(verdict);
        }
    }

    [Fact]
    public async Task ClassifyUnreadableInput_ByteRotationClass_NeverFalsePositiveNeverThrows()
    {
        // Structural mutation class 3: rotate the footer region by a byte (a framing shift, not a bit-flip) at
        // several offsets. Shifting the Thrift field stream desynchronizes field ids/types; the walk must stay
        // bounded and fail closed rather than stumble onto a field-8 shape and false-positive.
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

            Assert.Null(verdict);
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
}
