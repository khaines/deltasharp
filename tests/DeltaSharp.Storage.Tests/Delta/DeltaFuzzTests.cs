using System.Text;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Fuzz coverage for the untrusted-input parsers (design §5.4 C-DECODE; "fail deterministically, name the
/// defect, publish no partial state, fail closed"). Random and mutated inputs to the JSON action reader,
/// the checkpoint Parquet reader, the <c>_last_checkpoint</c> hint, and the log-file classifier must only
/// ever succeed or throw the typed <see cref="DeltaProtocolException"/> — never an unexpected exception,
/// and never hang.
/// </summary>
public sealed class DeltaFuzzTests
{
    [Fact]
    public void JsonActionReader_OnlyFailsClosed_OnRandomBytes()
    {
        var random = new Random(1);
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 64)];
            random.NextBytes(bytes);
            AssertJsonParseIsClosed(bytes);
        }
    }

    [Fact]
    public void JsonActionReader_OnlyFailsClosed_OnMutatedValidCommits()
    {
        byte[] valid = Encoding.UTF8.GetBytes(string.Join('\n',
            DeltaTestHarness.Protocol(),
            DeltaTestHarness.Metadata(id: "t", partitionColumns: ["year"]),
            DeltaTestHarness.Add("a.parquet", stats: """{"numRecords":3,"minValues":{"id":1},"maxValues":{"id":9}}""",
                partitionValues: [("year", "2026")]),
            DeltaTestHarness.Remove("b.parquet"),
            DeltaTestHarness.Txn("app", 4)) + "\n");

        var random = new Random(2);
        for (int i = 0; i < 5000; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            int mutations = random.Next(1, 4);
            for (int m = 0; m < mutations; m++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(0, 256);
            }

            AssertJsonParseIsClosed(mutated);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnRandomBytes()
    {
        var random = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            byte[] bytes = new byte[random.Next(0, 256)];
            random.NextBytes(bytes);
            await AssertCheckpointReadIsClosedAsync(bytes);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnTruncatedValidCheckpoint()
    {
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""", partitionColumns: ["year"])
            .Add("a.parquet", size: 5, partitionValues: [("year", "2026")], tags: [("k", "v")])
            .Txn("app", 7)
            .ToParquetAsync();

        var random = new Random(4);
        for (int i = 0; i < 400; i++)
        {
            int length = random.Next(0, valid.Length);
            byte[] truncated = valid[..length];
            await AssertCheckpointReadIsClosedAsync(truncated);
        }
    }

    [Fact]
    public async Task CheckpointReader_OnlyFailsClosed_OnByteFlippedCheckpoint()
    {
        byte[] valid = await new CheckpointFixture()
            .Protocol(1, 2)
            .Metadata("t", """{"type":"struct","fields":[]}""")
            .Add("a.parquet", size: 1)
            .ToParquetAsync();

        var random = new Random(5);
        for (int i = 0; i < 400; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));
            await AssertCheckpointReadIsClosedAsync(mutated);
        }
    }

    [Fact]
    public void LastCheckpointHint_NeverThrows_OnRandomBytes()
    {
        var random = new Random(6);
        for (int i = 0; i < 5000; i++)
        {
            byte[] bytes = new byte[random.Next(0, 48)];
            random.NextBytes(bytes);
            _ = LastCheckpointHint.TryParse(bytes); // must never throw; null or a hint
        }
    }

    [Fact]
    public void DeltaLogFiles_Classify_NeverThrows_OnRandomNames()
    {
        var random = new Random(7);
        const string alphabet = "0123456789abcdef.-checkpoint_json_parquet";
        for (int i = 0; i < 20000; i++)
        {
            int length = random.Next(0, 48);
            var sb = new StringBuilder(length);
            for (int c = 0; c < length; c++)
            {
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            }

            _ = DeltaLogFiles.Classify(sb.ToString()); // total function; must never throw
        }
    }

    private static void AssertJsonParseIsClosed(byte[] bytes)
    {
        try
        {
            _ = DeltaLogActionReader.ParseCommit(bytes, version: 0);
        }
        catch (DeltaProtocolException)
        {
            // acceptable: fail closed
        }
    }

    private static async Task AssertCheckpointReadIsClosedAsync(byte[] bytes)
    {
        try
        {
            _ = await DeltaCheckpointReader.ReadAsync(new MemoryStream(bytes), default);
        }
        catch (DeltaProtocolException)
        {
            // acceptable: fail closed
        }
    }
}
