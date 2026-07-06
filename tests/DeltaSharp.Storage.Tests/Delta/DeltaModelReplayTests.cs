using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Model-based / stateful reliability test for Delta snapshot reconstruction (design §3.3.3; INV I7;
/// HP-03/HP-04; STORY-05.2.2 AC1/AC3). An independent abstract model <c>M</c> — <c>(path→add, tombstones,
/// txns, metadata, protocol)</c> — is driven by a seeded generator of random <b>legal</b> command
/// sequences (append / overwrite / txn). Every command is applied to <c>M</c> and written as a real JSON
/// commit; at a random version a checkpoint is derived from <c>M</c> and written. The production
/// <see cref="DeltaLog"/> then reconstructs each version and must equal the model's recorded state — both
/// on the JSON-only twin and on the checkpoint-seeded table (replay equivalence). The model folds commands
/// with trivial dictionary operations, wholly independent of the production replay, so any reader bug
/// (parse, replay, checkpoint decode, discovery) surfaces as a mismatch.
/// </summary>
public sealed class DeltaModelReplayTests : IDisposable
{
    private static readonly string[] Years = ["2024", "2025", "2026"];

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    private LocalFileSystemBackend NewBackend()
    {
        string root = Path.Combine(Path.GetTempPath(), "model-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return new LocalFileSystemBackend(root);
    }

    [Fact]
    public async Task RandomHistories_ReconstructToModelState_JsonAndCheckpoint()
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var random = new Random(seed);
            int commits = random.Next(3, 25);

            // Build a JSON-only twin and a checkpoint-bearing table from the SAME generated history.
            IStorageBackend jsonOnly = NewBackend();
            IStorageBackend withCheckpoint = NewBackend();

            var model = new DeltaModel();
            var stateByVersion = new List<string>();
            int checkpointAt = random.Next(0, commits); // which version gets a checkpoint

            for (long version = 0; version < commits; version++)
            {
                string[] lines = model.Step(version, random);
                await DeltaTestHarness.WriteCommitAsync(jsonOnly, version, lines);
                await DeltaTestHarness.WriteCommitAsync(withCheckpoint, version, lines);
                stateByVersion.Add(model.Describe(version));

                if (version == checkpointAt)
                {
                    await DeltaTestHarness.WriteCheckpointAsync(withCheckpoint, version, model.ToCheckpoint());
                    await DeltaTestHarness.WriteLastCheckpointAsync(withCheckpoint, version);
                }
            }

            long latest = commits - 1;

            // Latest snapshot: both twins equal the model.
            Snapshot fromJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync();
            Snapshot fromCheckpoint = await new DeltaLog(withCheckpoint).LoadSnapshotAsync();
            Assert.Equal(stateByVersion[(int)latest], DeltaTestHarness.Describe(fromJson));
            Assert.Equal(stateByVersion[(int)latest], DeltaTestHarness.Describe(fromCheckpoint));

            // The checkpoint-bearing table actually used the fast path when the checkpoint is at/below latest.
            Assert.Equal(checkpointAt, fromCheckpoint.Metrics.CheckpointVersion);

            // Time travel to a version at/after the checkpoint (so the checkpoint applies) equals the model.
            long travel = checkpointAt + random.Next(0, (int)(latest - checkpointAt) + 1);
            Snapshot travelJson = await new DeltaLog(jsonOnly).LoadSnapshotAsync(travel);
            Snapshot travelCheckpoint = await new DeltaLog(withCheckpoint).LoadSnapshotAsync(travel);
            Assert.Equal(stateByVersion[(int)travel], DeltaTestHarness.Describe(travelJson));
            Assert.Equal(stateByVersion[(int)travel], DeltaTestHarness.Describe(travelCheckpoint));
        }
    }

    /// <summary>The independent abstract model of Delta table state, folded by trivial dictionary ops.</summary>
    private sealed class DeltaModel
    {
        private readonly SortedDictionary<string, (long Size, string Year)> _active = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, long> _txns = new(StringComparer.Ordinal);
        private int _nextFile;

        public string[] Step(long version, Random random)
        {
            var lines = new List<string>();
            if (version == 0)
            {
                lines.Add(DeltaTestHarness.Protocol());
                lines.Add(DeltaTestHarness.Metadata(id: "model-table", partitionColumns: ["year"]));
            }

            // Overwrite: remove a random subset of active files.
            if (_active.Count > 0 && random.Next(0, 3) == 0)
            {
                foreach (string path in _active.Keys.Where(_ => random.Next(0, 2) == 0).ToArray())
                {
                    lines.Add(DeltaTestHarness.Remove(path));
                    _active.Remove(path);
                }
            }

            // Append: add 0..3 new files.
            int adds = random.Next(0, 4);
            for (int i = 0; i < adds; i++)
            {
                string path = string.Create(CultureInfo.InvariantCulture, $"f{_nextFile++}.parquet");
                string year = Years[random.Next(Years.Length)];
                long size = random.Next(1, 1000);
                lines.Add(AddLine(path, size, year));
                _active[path] = (size, year);
            }

            // Occasionally record a txn (idempotency marker), sometimes bumping an existing appId.
            if (random.Next(0, 3) == 0)
            {
                string appId = "app-" + random.Next(0, 3).ToString(CultureInfo.InvariantCulture);
                long txnVersion = _txns.TryGetValue(appId, out long existing) ? existing + 1 : 0;
                lines.Add(DeltaTestHarness.Txn(appId, txnVersion));
                _txns[appId] = txnVersion;
            }

            return lines.ToArray();
        }

        public CheckpointFixture ToCheckpoint()
        {
            var fixture = new CheckpointFixture()
                .Protocol(1, 2)
                .Metadata(id: "model-table", schemaString: EmptySchemaUnescaped, partitionColumns: ["year"]);
            foreach ((string path, (long size, string year)) in _active)
            {
                fixture.Add(path, size: size, partitionValues: [("year", year)]);
            }

            foreach ((string appId, long version) in _txns)
            {
                fixture.Txn(appId, version);
            }

            return fixture;
        }

        public string Describe(long version)
        {
            var sb = new StringBuilder();
            sb.Append("version=").Append(version).Append('\n');
            sb.Append("protocol=1/2 rf=[] wf=[]\n");
            sb.Append("metadata id=model-table name=∅ provider=parquet schema=").Append(EmptySchemaUnescaped)
                .Append(" part=[year] config={}\n");
            sb.Append("txns=");
            foreach ((string appId, long v) in _txns)
            {
                sb.Append(appId).Append(':').Append(v).Append(';');
            }

            sb.Append('\n');
            foreach ((string path, (long size, string year)) in _active)
            {
                sb.Append("add path=").Append(path).Append(" size=").Append(size)
                    .Append(" pv={year=").Append(year).Append("} tags={} stats=∅\n");
            }

            return sb.ToString();
        }

        private static string AddLine(string path, long size, string year) =>
            """{"add":{"path":"__P__","partitionValues":{"year":"__Y__"},"size":__S__,"modificationTime":1,"dataChange":true}}"""
                .Replace("__P__", path, StringComparison.Ordinal)
                .Replace("__Y__", year, StringComparison.Ordinal)
                .Replace("__S__", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        private const string EmptySchemaUnescaped = """{"type":"struct","fields":[]}""";
    }
}
