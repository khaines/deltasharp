using System.Collections.Immutable;
using System.Threading;
using DeltaSharp.Storage;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// End-to-end optimistic-concurrency tests for <see cref="DeltaCommitter"/> over a real
/// <see cref="LocalFileSystemBackend"/> (design §2.11, STORY-05.3.1 AC1–AC3): exactly one writer wins each
/// version, unsafe commits are rejected before publishing, and append-compatible writers rebase without
/// duplicating data. Ambiguous-ack recovery (AC4) is covered in <see cref="DeltaCommitAmbiguityTests"/>.
/// </summary>
public sealed class DeltaCommitterTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBackend _backend;

    public DeltaCommitterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deltacommit-tests-" + Guid.NewGuid().ToString("N"));
        _backend = new LocalFileSystemBackend(_root);
    }

    public void Dispose()
    {
        _backend.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path) =>
        new(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: NoTags);

    private static RemoveFileAction Remove(string path) =>
        new(path, DeletionTimestamp: 1L, DataChange: true, ExtendedFileMetadata: false, NoPartition, Size: null);

    private async Task SeedTableAsync(int minWriter = 2)
    {
        await DeltaTestHarness.WriteCommitAsync(
            _backend, 0, DeltaTestHarness.Protocol(minReader: 1, minWriter: minWriter), DeltaTestHarness.Metadata());
    }

    private Task<Snapshot> LoadAsync(long? version = null) => new DeltaLog(_backend).LoadSnapshotAsync(version);

    private async Task CommitRawAsync(long version, params string[] lines) =>
        await DeltaTestHarness.WriteCommitAsync(_backend, version, lines);

    [Fact]
    public async Task CommitsBlindAppend_AdvancingVersion_ReadYourWrites()
    {
        // AC (happy path): a blind append advances 0 → 1 on the first attempt and is immediately visible.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();

        DeltaCommitResult result = await new DeltaCommitter(_backend)
            .CommitAsync(snapshot, new DeltaAction[] { Add("part-0.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(1L, result.Version);
        Assert.Equal(1, result.Attempts);

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(1L, reloaded.Version);
        Assert.Equal("part-0.parquet", Assert.Single(reloaded.ActiveFiles).Path);
    }

    [Fact]
    public async Task AcceptsCommit_AuthoringIdModeMetadata_Since572()
    {
        // #572: id-mode WRITE is enabled, so the DeltaCommitter's former two-sided id-write gate (which
        // refused ANY commit that read OR authored an id-mode table) is removed. A FRESH-CREATE commit that
        // authors an id-mode table — against the synthetic empty/genesis snapshot (version -1, no prior
        // metadata), its metaData setting delta.columnMapping.mode=id and its protocol declaring the
        // columnMapping feature — now commits successfully (version 0) and reloads as an id-mode table. This
        // is the sanctioned mode-installation path the committer permits (a mode TRANSITION on an EXISTING
        // table is refused — see RejectsCommit_ModeTransitionOnExistingTable_Since572).
        Snapshot genesis = GenesisSnapshot();   // version -1, none mode, empty (the fresh-create base)
        Assert.Equal(ColumnMappingMode.None, ColumnMapping.ResolveMode(genesis.Metadata.Configuration));

        var idMetadata = new MetadataAction(
            Id: "t",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: "{\"type\":\"struct\",\"fields\":[]}",
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: ColumnMapping.IdModeConfiguration(1),
            CreatedTime: null);

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            genesis,
            new DeltaAction[] { ColumnMapping.IdModeProtocol(), idMetadata, Add("part-0.parquet") },
            DeltaReadScope.BlindAppend);
        Assert.Equal(0L, result.Version);

        // The published table reloads as an id-mode table (protocol support + mode=id both present).
        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.Id, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    [Fact]
    public async Task RejectsCommit_ModeTransitionOnExistingTable_Since572()
    {
        // #572 defense-in-depth (arch/security/relspec, 3 seats): the removed central id-write gate is
        // replaced by a NARROW committer-level assertion — a committed metaData may NOT change an EXISTING
        // table's column-mapping mode. Enabling id mode on a live none-mode table (a mode TRANSITION) is
        // refused fail-closed at the commit primitive, BEHIND the write door's TableExistsAsync guard, so the
        // "no mode transition on an existing table" invariant is enforced at the storage primitive too — not
        // only at the door.
        await SeedTableAsync();                 // v0 none-mode table
        Snapshot snapshot = await LoadAsync();  // read snapshot: none mode, version 0
        Assert.Equal(ColumnMappingMode.None, ColumnMapping.ResolveMode(snapshot.Metadata.Configuration));

        var idMetadata = new MetadataAction(
            Id: "t",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: "{\"type\":\"struct\",\"fields\":[]}",
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: ColumnMapping.IdModeConfiguration(1),
            CreatedTime: null);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { ColumnMapping.IdModeProtocol(), idMetadata, Add("part-0.parquet") },
                DeltaReadScope.WholeTable));
        Assert.Contains("column-mapping mode", ex.Message, StringComparison.Ordinal);

        // Fail-closed left the table unchanged: still version 0, still none mode (no id transition published).
        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.None, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    // Column-mapped schema strings that are STRUCTURALLY INVALID under column mapping — the shapes a foreign
    // engine or a corrupt writer could emit but the public write doors never construct. Each is the exact
    // untrusted input ColumnMapping.ValidateColumnMappingSchema rejects at load; N3 makes the committer reject
    // them at COMMIT too.
    private const string DuplicateIdSchemaJson =                            // two columns share id 1
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    private const string IdAboveMaxColumnIdSchemaJson =                     // id 5 > maxColumnId 2
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":5,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    private const string ValidIdSchemaJson =                               // ids 1,2 — a consistent id mapping
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    // (deltaspec N3/R4) more structurally-INVALID mapped schemas the hardened shared validator now rejects at
    // both load AND commit. #1: a non-positive id (Delta ids start at 1). #2: a nested (non-leaf) top-level
    // mapped column (the reader maps only leaf columns).
    private const string ZeroIdSchemaJson =                                // 'score' has id 0 (< 1)
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":0,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    private const string NegativeIdSchemaJson =                            // 'score' has id -3 (< 1)
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":-3,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    private const string NestedMappedColumnSchemaJson =                    // top-level 'info' is a struct (non-leaf)
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
        + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
        + "{\"name\":\"info\",\"type\":{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"inner\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]},"
        + "\"nullable\":true,\"metadata\":"
        + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"col-B\"}}]}";

    // A DEGENERATE zero-field id-mode schema (#572 deltaspec R5). Legal with maxColumnId=0 (a table before any
    // column is minted); paired with maxColumnId=-1 it is the crafted shape that slips past the per-field loop
    // (which never runs) and would mint id maxColumnId+1 = 0 on the next mergeSchema append — the 4th finding.
    private const string EmptyIdModeSchemaJson = "{\"type\":\"struct\",\"fields\":[]}";

    // A plain (unmapped) none-mode schema for the all-mode partition-existence test (finding #3).
    private const string NoneModeSchemaJson =
        "{\"type\":\"struct\",\"fields\":["
        + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}},"
        + "{\"name\":\"value\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]}";

    private static readonly ImmutableSortedDictionary<string, string> NoneModeConfiguration =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static MetadataAction IdModeMetadata(string schemaJson, long maxColumnId) => new(
        Id: "t",
        Name: null,
        Description: null,
        Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
        SchemaString: schemaJson,
        PartitionColumns: ImmutableArray<string>.Empty,
        Configuration: ColumnMapping.IdModeConfiguration(maxColumnId),
        CreatedTime: null);

    // A metaData builder with an explicit configuration + partitionColumns — for the all-mode (none/name/id)
    // partition-existence tests (deltaspec N3/R4 finding #3).
    private static MetadataAction MetadataWith(
        string schemaJson,
        ImmutableSortedDictionary<string, string> configuration,
        params string[] partitionColumns) => new(
        Id: "t",
        Name: null,
        Description: null,
        Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
        SchemaString: schemaJson,
        PartitionColumns: partitionColumns.ToImmutableArray(),
        Configuration: configuration,
        CreatedTime: null);

    [Fact]
    public async Task RejectsCommit_MalformedIdModeSchema_DuplicateId_FailsClosed_AtCommit()
    {
        // #572 / deltaspec N3 (completes the B3 committer hardening): the committer self-validates a
        // mapped-mode metaData's SCHEMA CONSISTENCY before publishing — the SAME
        // ColumnMapping.ValidateColumnMappingSchema the snapshot-load choke point runs. A fresh-create id-mode
        // metaData whose two columns share delta.columnMapping.id=1 is structurally invalid (a duplicate id
        // would let two logical columns resolve to one file column — a silent misread), so it must fail closed
        // at COMMIT — not commit successfully (as it did before N3) and only surface on the NEXT load. Uses the
        // sanctioned fresh-create path (genesis), proving even a malformed FIRST write is refused.
        Snapshot genesis = GenesisSnapshot();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaCommitter(_backend).CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    ColumnMapping.IdModeProtocol(), IdModeMetadata(DuplicateIdSchemaJson, 2), Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("assigned to more than one column", ex.Message, StringComparison.Ordinal);

        // Fail-closed BEFORE any bytes: no commit was ever published (the table still does not exist).
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_MalformedIdModeSchema_IdAboveMaxColumnId_FailsClosed_AtCommit()
    {
        // N3, a second malformed class: a field whose delta.columnMapping.id (5) exceeds the tracked
        // maxColumnId (2) violates the monotonic-id writer invariant and is refused at COMMIT.
        Snapshot genesis = GenesisSnapshot();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaCommitter(_backend).CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    ColumnMapping.IdModeProtocol(),
                    IdModeMetadata(IdAboveMaxColumnIdSchemaJson, 2),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("exceeds the tracked", ex.Message, StringComparison.Ordinal);

        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_MalformedIdModeSchema_OnExistingSameModeTable_FailsClosed_AtCommit()
    {
        // N3 against an EXISTING, VALID id-mode table (the red-team's exact repro: COMMIT_VERSION=1 without the
        // fix). A SAME-mode (id → id) evolution commit does NOT trip the B3 mode-transition guard, so N3 is the
        // ONLY thing standing between a malformed dup-id metaData and a published v1 that would poison the next
        // load. First install a consistent id-mode v0; then a same-mode dup-id v1 must fail closed at COMMIT,
        // leaving the table at v0 (still loadable — proving no malformed metaData was published).
        Snapshot genesis = GenesisSnapshot();
        DeltaCommitResult v0 = await new DeltaCommitter(_backend).CommitAsync(
            genesis,
            new DeltaAction[]
            {
                ColumnMapping.IdModeProtocol(), IdModeMetadata(ValidIdSchemaJson, 2), Add("part-0.parquet"),
            },
            DeltaReadScope.BlindAppend);
        Assert.Equal(0L, v0.Version);

        Snapshot atV0 = await LoadAsync();      // valid id-mode read snapshot, version 0
        Assert.Equal(ColumnMappingMode.Id, ColumnMapping.ResolveMode(atV0.Metadata.Configuration));

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaCommitter(_backend).CommitAsync(
                atV0,
                new DeltaAction[] { IdModeMetadata(DuplicateIdSchemaJson, 2), Add("part-1.parquet") },
                DeltaReadScope.BlindAppend));
        Assert.Contains("assigned to more than one column", ex.Message, StringComparison.Ordinal);

        // The malformed v1 was NOT published: the table is still at v0 and still LOADS (load re-runs the same
        // validation — a poisoned v1 would have made this throw).
        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.Id, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    [Fact]
    public async Task RejectsCommit_IdModeSchema_NonPositiveId_FailsClosed_AtCommit()
    {
        // deltaspec N3/R4 finding #1: Delta column-mapping ids start at 1, so a mapped field with id <= 0 is
        // structurally invalid — it commits + loads today but fails a LATER append at the Parquet field_id
        // stamp guard (ParquetTypeMapping.CreateField, range [1, int.MaxValue]). The hardened shared validator
        // (ColumnMapping.ValidateColumnMappingSchema) now rejects it fail-closed at COMMIT (and load), for BOTH
        // id=0 AND a negative id. Fresh-create path (genesis): even a malformed FIRST write is refused. (The
        // UPPER bound id > int.MaxValue stays a read-layer concern — see
        // ColumnMappingTests.IdMode_RequestedIdAboveInt32Max_IsRejectedFailClosed.)
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        foreach (string schema in new[] { ZeroIdSchemaJson, NegativeIdSchemaJson })
        {
            DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
                () => committer.CommitAsync(
                    genesis,
                    new DeltaAction[]
                    {
                        ColumnMapping.IdModeProtocol(), IdModeMetadata(schema, 2), Add("part-0.parquet"),
                    },
                    DeltaReadScope.BlindAppend));
            Assert.Contains("outside the valid column-mapping id range", ex.Message, StringComparison.Ordinal);
        }

        // Fail-closed before any bytes: neither malformed metaData reached the log (the table still does not exist).
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_IdModeSchema_NestedMappedColumn_FailsClosed_AtCommit()
    {
        // deltaspec N3/R4 finding #2: the reader/projection maps only top-level LEAF columns; a nested
        // (struct/array/map) top-level mapped column commits + loads today but throws "nested column mapping
        // is unsupported" at projection. The write doors reject it via EnsureLeaf, but a RAW committed metaData
        // bypasses that door, so the hardened shared validator now enforces the leaf contract at COMMIT (and
        // load). Here top-level 'info' is a struct carrying column-mapping metadata → refused fail-closed.
        Snapshot genesis = GenesisSnapshot();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaCommitter(_backend).CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    ColumnMapping.IdModeProtocol(),
                    IdModeMetadata(NestedMappedColumnSchemaJson, 2),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("Only top-level (leaf) columns", ex.Message, StringComparison.Ordinal);

        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_Metadata_PartitionColumnNotInSchema_FailsClosed_AtCommit()
    {
        // deltaspec N3/R4 finding #3 (ALL modes — none/name/id): every logical metaData.partitionColumns entry
        // MUST name a column present in the schema. A partitionColumns entry absent from the schema commits +
        // loads today and only surfaces at append/overwrite planning ("Partition column '…' is not present").
        // The committer now validates this before publish for EVERY committed metaData regardless of mode, so a
        // bad-partition table fails closed at COMMIT. Covers a NONE-mode AND an ID-mode metaData (all-mode).
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        // none mode: schema {id, value}, partitionColumns=["ghost"] — ghost is not a column.
        DeltaProtocolException noneModeEx = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    MetadataWith(NoneModeSchemaJson, NoneModeConfiguration, "ghost"), Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("is not present in the table schema", noneModeEx.Message, StringComparison.Ordinal);

        // id mode: a structurally-VALID id mapping {id, score}, but partitionColumns=["ghost"] — the all-mode
        // partition check fires before the mapping-consistency check.
        DeltaProtocolException idModeEx = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    ColumnMapping.IdModeProtocol(),
                    MetadataWith(ValidIdSchemaJson, ColumnMapping.IdModeConfiguration(2), "ghost"),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("is not present in the table schema", idModeEx.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes: neither bad-partition metaData was published.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_Metadata_DuplicatePartitionColumns_FailsClosed_AtCommit()
    {
        // deltaspec R8 (ALL modes — none/name/id): every logical metaData.partitionColumns entry must be
        // DISTINCT. A duplicate (e.g. [value, value]) passes the existence check yet doubles the
        // partition-directory path (value=…/value=…/) and produces a table a strict reader (Spark Delta
        // COLUMN_ALREADY_EXISTS) rejects — a DeltaSharp write that other engines cannot read. The committer now
        // rejects a duplicate before publish for EVERY committed metaData regardless of mode (the check sits
        // beside the all-mode partition-existence invariant). Covers a NONE-mode AND an ID-mode metaData.
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        // none mode: schema {id, value}, partitionColumns=[value, value] — value is a real column, listed twice.
        DeltaProtocolException noneModeEx = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    MetadataWith(NoneModeSchemaJson, NoneModeConfiguration, "value", "value"), Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("listed more than once", noneModeEx.Message, StringComparison.Ordinal);

        // id mode: a structurally-VALID id mapping {id, score}, partitionColumns=[score, score] duplicated. The
        // all-mode partition check fires regardless of mapping mode.
        DeltaProtocolException idModeEx = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    ColumnMapping.IdModeProtocol(),
                    MetadataWith(ValidIdSchemaJson, ColumnMapping.IdModeConfiguration(2), "score", "score"),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("listed more than once", idModeEx.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes: neither malformed metaData was published. (No over-reject of a DISTINCT
        // partition-column list is covered by the broad partitioned-commit suite, which all use distinct columns.)
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_Metadata_NonAtomicPartitionColumn_FailsClosed_AtCommit()
    {
        // deltaspec R9 (ALL modes): a partition column must be an ATOMIC type — a partition value renders to a
        // single directory-segment string. A struct/array/map (or binary) partition column passes the existence
        // check yet commits + loads and only fails later at the partition-value encode ("Type '…' is not
        // supported as a Delta partition column"). The committer now rejects a non-encodable partition-column
        // type before publish.
        const string structPartitionSchema =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}},"
            + "{\"name\":\"s\",\"type\":{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"x\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]},"
            + "\"nullable\":true,\"metadata\":{}}]}";
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    MetadataWith(structPartitionSchema, NoneModeConfiguration, "s"), Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("not a supported Delta partition-column type", ex.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_Metadata_CaseInsensitiveDuplicateColumns_FailsClosed_AtCommit()
    {
        // deltaspec R9 (ALL modes): DeltaSharp stores column names case-sensitively, but a strict reader that
        // resolves names case-insensitively (Spark's default) rejects a schema with e.g. `region` AND `REGION`
        // (COLUMN_ALREADY_EXISTS). Such a schema commits + loads in DeltaSharp today, authoring a table other
        // engines cannot read. The committer now rejects a case-insensitive column-name collision before publish
        // (all modes), complementing the schema-evolution path's identical guard.
        const string caseVariantSchema =
            "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"region\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}},"
            + "{\"name\":\"REGION\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}},"
            + "{\"name\":\"value\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]}";
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[] { MetadataWith(caseVariantSchema, NoneModeConfiguration), Add("part-0.parquet") },
                DeltaReadScope.BlindAppend));
        Assert.Contains("collides case-insensitively", ex.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_Protocol_FeaturesUnderLegacyVersion_FailsClosed_AtCommit()
    {
        // deltaspec R11 finding 1: a `readerFeatures`/`writerFeatures` list is valid ONLY at the table-features
        // version (reader 3 / writer 7). A protocol naming features under a legacy min-version is malformed — a
        // strict reader (Spark: "Mismatched min{Reader,Writer}Version and {reader,writer}Features") rejects it.
        // The committer validates every committed ProtocolAction, so it now fails closed at COMMIT.
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        // writerFeatures under minWriterVersion=2 (not 7).
        DeltaProtocolException writerEx = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray.Create("appendOnly")),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("writerFeatures", writerEx.Message, StringComparison.Ordinal);

        // readerFeatures under minReaderVersion=1 (not 3).
        DeltaProtocolException readerEx = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    new ProtocolAction(1, 2, ImmutableArray.Create("deletionVectors"), ImmutableArray<string>.Empty),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("readerFeatures", readerEx.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_Metadata_NonBooleanAppendOnlyValue_FailsClosed_AtCommit()
    {
        // deltaspec R11 finding 3: a committed `delta.appendOnly` value must be a valid boolean. A non-boolean
        // value committed today, then surfaced only at a later overwrite (parsed via AppendOnlyFeature.IsEnabled,
        // which fails closed). The committer now validates the committed value at COMMIT so it fails closed here.
        ImmutableSortedDictionary<string, string> badConfig =
            NoneModeConfiguration.SetItem(AppendOnlyFeature.PropertyKey, "not-a-boolean");
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => committer.CommitAsync(
                genesis,
                new DeltaAction[] { MetadataWith(NoneModeSchemaJson, badConfig), Add("part-0.parquet") },
                DeltaReadScope.BlindAppend));
        Assert.Contains("non-boolean value", ex.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCommit_IdModeSchema_NegativeMaxColumnId_FailsClosed_AtCommit()
    {
        // deltaspec R5 (the 4th validator finding): maxColumnId is a monotonic COUNT of assigned column ids —
        // 0 for a zero-field table, only ever increasing — so it is NEVER negative. A NON-empty mapped schema
        // already rejects maxColumnId < min(id)=1 via the per-field `id > maxColumnId` check, but a DEGENERATE
        // zero-field schema skips that loop entirely, so a crafted empty id-mode metaData with maxColumnId=-1
        // would otherwise commit + load and then mint id = maxColumnId+1 = 0 on the next mergeSchema append,
        // failing the [1, int.MaxValue] stamp guard. ReadMaxColumnId now rejects maxColumnId < 0 at the shared
        // choke point, so the empty-schema case fails closed at COMMIT (and load). An EMPTY schema is REQUIRED
        // here to unambiguously target this check: a non-empty schema would trip the per-field range check.
        Snapshot genesis = GenesisSnapshot();

        DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
            () => new DeltaCommitter(_backend).CommitAsync(
                genesis,
                new DeltaAction[]
                {
                    ColumnMapping.IdModeProtocol(),
                    IdModeMetadata(EmptyIdModeSchemaJson, -1),
                    Add("part-0.parquet"),
                },
                DeltaReadScope.BlindAppend));
        Assert.Contains("is negative", ex.Message, StringComparison.Ordinal);

        // Fail-closed before any bytes: the malformed metaData never reached the log (table still absent).
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsCommit_EmptyIdModeSchema_MaxColumnIdZero_IsLegal()
    {
        // No-over-rejection boundary for the R5 `maxColumnId >= 0` rule: a zero-field id-mode table with
        // maxColumnId=0 (the value AssignFreshMapping returns for an empty schema, before any column is minted)
        // is LEGAL and MUST commit + load. The reject rule is strictly `< 0`, so this passes untouched — the
        // guard fires only on genuinely malformed (negative) counts.
        Snapshot genesis = GenesisSnapshot();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            genesis,
            new DeltaAction[] { ColumnMapping.IdModeProtocol(), IdModeMetadata(EmptyIdModeSchemaJson, 0) },
            DeltaReadScope.BlindAppend);
        Assert.Equal(0L, result.Version);

        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.Id, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    // Builds a structurally-VALID 2-column id-mode schema (ids 1,2; maxColumnId=2) whose SECOND column's
    // delta.columnMapping.physicalName is exactly <paramref name="physicalName"/> — JSON-escaped so the schema
    // parses cleanly (control chars/backslashes are valid once escaped) and the physical name reads back
    // verbatim, so it is the shared VALIDATOR (not the JSON parser) that judges the name. The first column
    // uses a safe 'col-A', so only the crafted name can trip EnsureSafePhysicalName.
    private static string IdSchemaWithPhysicalName(string physicalName)
    {
        string escaped = System.Text.Json.JsonEncodedText.Encode(physicalName).ToString();
        return "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"id\",\"type\":\"long\",\"nullable\":false,\"metadata\":"
            + "{\"delta.columnMapping.id\":1,\"delta.columnMapping.physicalName\":\"col-A\"}},"
            + "{\"name\":\"score\",\"type\":\"long\",\"nullable\":true,\"metadata\":"
            + "{\"delta.columnMapping.id\":2,\"delta.columnMapping.physicalName\":\"" + escaped + "\"}}]}";
    }

    [Fact]
    public async Task RejectsCommit_IdModeSchema_UnsafePhysicalName_FailsClosed_AtCommit()
    {
        // deltaspec R6: a column-mapped physicalName doubles as a Parquet column name AND a Hive
        // partition-directory segment ("physicalName=value/"), so it MUST be a safe single path segment. A
        // crafted mapped metaData whose physicalName is a traversal/separator/'='/':'/control/whitespace-only
        // name commits + loads today and only fails a LATER partition-planning/staging op (the confined-root
        // guard rejects "../escape=v/part-….parquet"). The shared validator (EnsureSafePhysicalName) now
        // rejects every such name fail-closed at COMMIT (and load), for name AND id mode. Parquet.Net itself
        // round-trips any column name verbatim (it imposes no constraint) — the binding constraint is the path.
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        string[] unsafeNames =
        {
            "../escape",  // traversal (the reported finding) — '/' path separator + '..' component
            "a/b",        // forward-slash path separator (splits the directory tree)
            "a\\b",       // back-slash path separator (Windows/ambiguous)
            "..",         // whole segment is the parent-dir traversal component
            ".",          // whole segment is the current-dir degenerate component
            "col=x",      // '=' is the Hive key=value partition delimiter (corrupts partition-dir parsing)
            "col:x",      // ':' roots a Windows drive / NTFS alternate-data-stream path
            "ctl\u0001x", // a control character (filesystem-hostile, log/path-injection vector)
            "   ",        // whitespace-only (degenerate, filesystem-hostile segment)
        };

        foreach (string physical in unsafeNames)
        {
            DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
                () => committer.CommitAsync(
                    genesis,
                    new DeltaAction[]
                    {
                        ColumnMapping.IdModeProtocol(),
                        IdModeMetadata(IdSchemaWithPhysicalName(physical), 2),
                        Add("part-0.parquet"),
                    },
                    DeltaReadScope.BlindAppend));
            Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal);
        }

        // Fail-closed before any bytes: no crafted metaData reached the log (the table still does not exist).
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsCommit_IdModeSchema_SafeColUuidPhysicalName_Commits()
    {
        // No-over-rejection boundary for the R6 safe-path-segment rule: the engine-shaped 'col-<uuid>' physical
        // name every real table mints (AssignFreshMapping / EvolveNameModeMapping) is a safe segment and MUST
        // commit + load. The guard fires only on genuinely unsafe names, never on production mappings.
        Snapshot genesis = GenesisSnapshot();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            genesis,
            new DeltaAction[]
            {
                ColumnMapping.IdModeProtocol(),
                IdModeMetadata(IdSchemaWithPhysicalName("col-99999999-8888-7777-6666-555555555555"), 2),
                Add("part-0.parquet"),
            },
            DeltaReadScope.BlindAppend);
        Assert.Equal(0L, result.Version);

        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.Id, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    [Fact]
    public async Task RejectsCommit_IdModeSchema_OverLongPhysicalName_FailsClosed_AtCommit()
    {
        // deltaspec R7 finding #1: a physicalName is a partition-directory path segment ("physicalName=value/"),
        // so besides being char-safe (R6) it must fit a single filesystem path component (~255 bytes). A
        // crafted safe-but-over-long (~300-byte) physicalName commits + loads today, then a PARTITIONED append
        // fails resolving the path. The shared validator now bounds it at 128 UTF-8 bytes (half the ~255-byte
        // component budget, leaving room for the "=value" suffix) fail-closed at COMMIT (and load), name + id
        // mode. Measured in UTF-8 BYTES, so a 65-char two-byte-per-char name (130 bytes) is also rejected.
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        string[] overLongNames =
        {
            new string('x', 129),            // 129 ASCII bytes — one past the 128-byte bound
            new string('x', 300),            // the ~300-byte finding repro
            new string('\u00e9', 65),        // 65 × 2-byte UTF-8 chars = 130 bytes (char count 65, BYTE count wins)
        };

        foreach (string physical in overLongNames)
        {
            DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
                () => committer.CommitAsync(
                    genesis,
                    new DeltaAction[]
                    {
                        ColumnMapping.IdModeProtocol(),
                        IdModeMetadata(IdSchemaWithPhysicalName(physical), 2),
                        Add("part-0.parquet"),
                    },
                    DeltaReadScope.BlindAppend));
            Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal);
            Assert.Contains("exceed 128 UTF-8 bytes", ex.Message, StringComparison.Ordinal);
        }

        // Fail-closed before any bytes: no over-long-name metaData reached the log.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsCommit_IdModeSchema_MaxLengthPhysicalName_Commits()
    {
        // No-over-rejection boundary for R7 finding #1: the length bound is INCLUSIVE — a physicalName of
        // exactly 128 UTF-8 bytes is accepted (only 129+ is rejected), so a legitimately-unusual-but-safe long
        // name still commits + loads. (A real minted name is 'col-<uuid>' = 40 bytes, far under the bound.)
        Snapshot genesis = GenesisSnapshot();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            genesis,
            new DeltaAction[]
            {
                ColumnMapping.IdModeProtocol(),
                IdModeMetadata(IdSchemaWithPhysicalName(new string('x', 128)), 2),
                Add("part-0.parquet"),
            },
            DeltaReadScope.BlindAppend);
        Assert.Equal(0L, result.Version);

        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.Id, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    [Fact]
    public async Task RejectsCommit_NoneModeSchema_UnsafePartitionName_FailsClosed_AtCommit()
    {
        // deltaspec R7 finding #2: in NONE mode there is no physical mapping, so a partition column's LOGICAL
        // name IS the partition-directory path segment ("logicalName=value/"). R6's physicalName check only
        // covers mapped modes; a crafted none-mode metaData whose partitionColumns names an unsafe/over-long
        // LOGICAL column ("../escape", "a/b", "col=x", a ~300-byte name) commits + loads today and only fails a
        // later partitioned write at the confined-root/path guard. The committer now applies the SAME
        // safe-segment + length contract to none-mode partition names (EnsureNoneModePartitionNamesSafe),
        // fail-closed at COMMIT. The column IS in the schema (the all-mode existence check passes first), so it
        // is the NEW name-safety check that fires.
        Snapshot genesis = GenesisSnapshot();
        var committer = new DeltaCommitter(_backend);

        string[] unsafeNames =
        {
            "../escape",           // the reported traversal finding ('/' + '..')
            "a/b",                 // path separator (splits the directory tree)
            "col=x",               // '=' corrupts the Hive key=value partition-dir parsing
            new string('p', 300),  // over the 128-byte path-component length bound
        };

        foreach (string partition in unsafeNames)
        {
            DeltaProtocolException ex = await Assert.ThrowsAsync<DeltaProtocolException>(
                () => committer.CommitAsync(
                    genesis,
                    new DeltaAction[]
                    {
                        MetadataWith(NoneModeSchemaWithField(partition), NoneModeConfiguration, partition),
                        Add("part-0.parquet"),
                    },
                    DeltaReadScope.BlindAppend));
            Assert.Contains("not a safe path segment", ex.Message, StringComparison.Ordinal);
        }

        // Fail-closed before any bytes: no unsafe-partition-name metaData reached the log.
        Assert.Null(await new DeltaLog(_backend).GetLatestCommitVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsCommit_NoneModeSchema_SafePartitionName_Commits()
    {
        // No-over-rejection boundary for R7 finding #2: a normal none-mode LOGICAL partition name ('region')
        // is a safe path segment and MUST commit + load. The guard fires only on genuinely unsafe/over-long
        // names, never on a real partitioned table. (The 128-byte max-length boundary is proven for BOTH
        // callers by AcceptsCommit_IdModeSchema_MaxLengthPhysicalName_Commits via the shared length check.)
        Snapshot genesis = GenesisSnapshot();

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            genesis,
            new DeltaAction[]
            {
                new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
                MetadataWith(NoneModeSchemaWithField("region"), NoneModeConfiguration, "region"),
                Add("part-0.parquet"),
            },
            DeltaReadScope.BlindAppend);
        Assert.Equal(0L, result.Version);

        Snapshot after = await LoadAsync();
        Assert.Equal(0L, after.Version);
        Assert.Equal(ColumnMappingMode.None, ColumnMapping.ResolveMode(after.Metadata.Configuration));
    }

    // Builds a plain (unmapped) none-mode schema whose FIRST column is named exactly <paramref name="field"/>
    // (JSON-escaped so the schema parses cleanly and the name reads back verbatim), plus a second 'value'
    // column. Listing this field in partitionColumns exercises the none-mode LOGICAL-partition-name
    // path-safety check (EnsureNoneModePartitionNamesSafe) — in none mode the logical name IS the
    // partition-directory path segment (#572 R7).
    private static string NoneModeSchemaWithField(string field)
    {
        string escaped = System.Text.Json.JsonEncodedText.Encode(field).ToString();
        return "{\"type\":\"struct\",\"fields\":["
            + "{\"name\":\"" + escaped + "\",\"type\":\"long\",\"nullable\":false,\"metadata\":{}},"
            + "{\"name\":\"value\",\"type\":\"long\",\"nullable\":true,\"metadata\":{}}]}";
    }

    // A synthetic empty/genesis snapshot (version -1, no prior metadata) — the exact base the write door's
    // fresh-create path commits version 0 against (mirrors DeltaTableWriter.EmptySnapshot). Committing an
    // id-mode metaData against THIS exercises the sanctioned fresh-create mode installation the committer
    // permits, distinct from a (refused) mode transition on an existing table.
    private static Snapshot GenesisSnapshot() => new(
        version: -1L,
        new ProtocolAction(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty),
        new MetadataAction(
            Id: "00000000-0000-0000-0000-000000000000",
            Name: null,
            Description: null,
            Format: new TableFormat("parquet", ImmutableSortedDictionary<string, string>.Empty),
            SchemaString: "{\"type\":\"struct\",\"fields\":[]}",
            PartitionColumns: ImmutableArray<string>.Empty,
            Configuration: ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
            CreatedTime: null),
        ImmutableArray<AddFileAction>.Empty,
        ImmutableArray<RemoveFileAction>.Empty,
        ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal),
        SnapshotLoadMetrics.Empty);

    [Fact]
    public async Task ConcurrentBlindAppends_ExactlyOneWinsEachVersion_WithoutDuplication()
    {
        // AC1 + AC3: two blind appends race for the same next version; one wins v1, the loser observes a
        // retryable conflict, rebases onto v2, and commits — both files land exactly once, none lost.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();

        using var barrier = new Barrier(2);
        Func<int, long, CancellationToken, Task> gate = (attempt, _, ct) =>
        {
            if (attempt == 0 && !barrier.SignalAndWait(TimeSpan.FromSeconds(30), ct))
            {
                // Bounded: if a peer writer fails before the barrier, surface a fast timeout instead of
                // hanging the test until the host harness kills it.
                throw new TimeoutException("Race barrier: a peer writer did not reach the put-if-absent in time.");
            }

            return Task.CompletedTask;
        };

        var committerA = new DeltaCommitter(_backend) { BeforePutProbe = gate };
        var committerB = new DeltaCommitter(_backend) { BeforePutProbe = gate };

        Task<DeltaCommitResult> taskA = Task.Run(() =>
            committerA.CommitAsync(snapshot, new DeltaAction[] { Add("a.parquet") }, DeltaReadScope.BlindAppend));
        Task<DeltaCommitResult> taskB = Task.Run(() =>
            committerB.CommitAsync(snapshot, new DeltaAction[] { Add("b.parquet") }, DeltaReadScope.BlindAppend));

        DeltaCommitResult[] results = await Task.WhenAll(taskA, taskB);

        // Exactly one commit at each of v1 and v2.
        Assert.Equal(new[] { 1L, 2L }, results.Select(r => r.Version).OrderBy(v => v).ToArray());
        // The winner committed on attempt 1; the loser rebased and committed on attempt 2.
        Assert.Equal(new[] { 1, 2 }, results.Select(r => r.Attempts).OrderBy(a => a).ToArray());

        // Both files are present in the final snapshot — no duplication, no loss.
        Snapshot reloaded = await LoadAsync();
        Assert.Equal(2L, reloaded.Version);
        Assert.Equal(new[] { "a.parquet", "b.parquet" }, reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task RebasesPastMultipleConcurrentWinners_InOneWinnersPass()
    {
        // Two safe winners (v1, v2) landed since the read snapshot; a blind append reads BOTH in one winners
        // pass and rebases to v3 in a SINGLE rebase — so it wins on its 2nd attempt. (Reading only the first
        // winner per pass would need an extra attempt, so asserting Attempts=2 pins the whole (R,M] range read.)
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync(); // v0
        await CommitRawAsync(1, DeltaTestHarness.Add("a.parquet"));
        await CommitRawAsync(2, DeltaTestHarness.Add("b.parquet"));

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot, new DeltaAction[] { Add("c.parquet") }, DeltaReadScope.BlindAppend);

        Assert.Equal(3L, result.Version);
        Assert.Equal(2, result.Attempts); // one lost put + one winning put after a single (R,M]=2 rebase

        Snapshot reloaded = await LoadAsync();
        Assert.Equal(
            new[] { "a.parquet", "b.parquet", "c.parquet" },
            reloaded.ActiveFiles.Select(a => a.Path).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task DetectsConflictInLaterWinner_AcrossMultiVersionRange()
    {
        // Winners v1 (safe append) + v2 (metadata change): the loser must classify the WHOLE (R,M] range and
        // abort on the v2 metadata change — proving multi-winner classification, not just the first winner.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync(); // v0
        await CommitRawAsync(1, DeltaTestHarness.Add("a.parquet"));
        await CommitRawAsync(2, DeltaTestHarness.Metadata(id: "changed"));

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task BlindAppend_AbortsOnConcurrentMetadataChange()
    {
        // AC2: an intervening metadata change is rejected before publishing (blind append vs metaData).
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Metadata(id: "changed")); // winner v1 changes metadata

        await Assert.ThrowsAsync<MetadataChangedException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));

        Assert.Equal(1L, (await LoadAsync()).Version); // no v2 was published
    }

    [Fact]
    public async Task BlindAppend_AbortsOnConcurrentProtocolChange()
    {
        // AC2: an intervening protocol change is rejected before publishing.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Protocol(minReader: 1, minWriter: 2)); // winner v1 rewrites protocol

        await Assert.ThrowsAsync<ProtocolChangedException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("late.parquet") }, DeltaReadScope.BlindAppend));
    }

    [Fact]
    public async Task WholeTableOverwrite_AbortsOnConcurrentAppend()
    {
        // AC2 (overwrite/partition): a whole-table overwrite conflicts with a concurrent append.
        await SeedTableAsync();
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("winner.parquet")); // winner v1 appends

        await Assert.ThrowsAsync<ConcurrentAppendException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("overwrite.parquet") }, DeltaReadScope.WholeTable));
    }

    [Fact]
    public async Task ReadFilesDelete_AbortsWhenConcurrentCommitRemovesReadFile()
    {
        // AC2 (delete): a targeted delete conflicts when a concurrent commit removed a file it read.
        await SeedTableAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("target.parquet")); // v1: the file our delete will read
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(2, DeltaTestHarness.Remove("target.parquet")); // winner v2 removes it first

        await Assert.ThrowsAsync<ConcurrentDeleteReadException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot,
                new DeltaAction[] { Remove("target.parquet") },
                DeltaReadScope.ReadFiles(new[] { "target.parquet" })));
    }

    [Fact]
    public async Task ReadFilesDelete_RebasesWhenConcurrentCommitTouchesDifferentFile()
    {
        // AC3-adjacent: a targeted delete whose read set is disjoint from the winner rebases and commits.
        await SeedTableAsync();
        await CommitRawAsync(1, DeltaTestHarness.Add("mine.parquet"), DeltaTestHarness.Add("other.parquet"));
        Snapshot snapshot = await LoadAsync();
        await CommitRawAsync(2, DeltaTestHarness.Remove("other.parquet")); // winner removes a file we did NOT read

        DeltaCommitResult result = await new DeltaCommitter(_backend).CommitAsync(
            snapshot,
            new DeltaAction[] { Remove("mine.parquet") },
            DeltaReadScope.ReadFiles(new[] { "mine.parquet" }));

        Assert.Equal(3L, result.Version); // rebased onto v2 → committed v3
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task UnsupportedWriterProtocol_FailsClosed()
    {
        // AC2 / §2.14 P3: a table whose writer version this build does not support fails closed before write.
        await SeedTableAsync(minWriter: 5);
        Snapshot snapshot = await LoadAsync();

        var ex = await Assert.ThrowsAsync<DeltaProtocolException>(() =>
            new DeltaCommitter(_backend).CommitAsync(
                snapshot, new DeltaAction[] { Add("x.parquet") }, DeltaReadScope.BlindAppend));
        Assert.Equal(DeltaProtocolErrorKind.UnsupportedProtocol, ex.Kind);
        Assert.Equal(0L, (await LoadAsync()).Version); // nothing published
    }
}
