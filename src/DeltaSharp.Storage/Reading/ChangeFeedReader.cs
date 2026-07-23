using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Delta.DeletionVectors;
using DeltaSharp.Storage.Parquet;
using DeltaSharp.Types;

namespace DeltaSharp.Storage.Reading;

/// <summary>
/// The engine behind the Change Data Feed <b>read door</b> (design §2.6) — the streaming counterpart of the
/// snapshot read path in <see cref="DeltaReadSource"/>. It has two phases, mirroring the snapshot pair:
///
/// <para><b>Resolve (<see cref="ResolveAsync"/>).</b> Resolves a <see cref="DeltaChangeFeedRange"/> to a pinned,
/// inclusive <c>[start, end]</c> version range ONCE — each endpoint independently a version xor a timestamp
/// (a timestamp resolves through the SAME monotonic <c>&lt;N&gt;.json</c>-mtime policy <c>timestampAsOf</c>
/// uses) — and VALIDATES it fail-closed: a start below 0, an end past the latest committed version, a start
/// after the end, a start aged past log retention, a version whose commit log is not retained, or CDF not
/// active for EVERY version in the range (the conservative enablement rule, §2.7). Resolving once pins the
/// range against a concurrent commit shifting it between analysis and execution (the same no-TOCTOU guarantee
/// as snapshot pinning). It returns the resolved range + the reconciled output schema (§2.4/§2.8).</para>
///
/// <para><b>Read (<see cref="ReadAsync"/>).</b> Replays <c>[start, end]</c> in <b>ascending commit order</b>,
/// yielding change rows as full-schema <see cref="ColumnBatch"/>es. <b>Precedence (INV C1/C2, §2.2):</b> a
/// version that committed any <c>cdc</c> action is read EXACTLY from its <c>cdc</c> files (each row carries its
/// own <c>_change_type</c>; the version's <c>add</c>/<c>remove</c> are NOT re-derived — no double count); a
/// version with no <c>cdc</c> is derived implicitly — <c>insert</c> from <c>add(dataChange=true)</c> and
/// <c>delete</c> from <c>remove(dataChange=true)</c>, DV-aware so only LIVE physical rows surface. Each yielded
/// batch is stamped with the three engine-synthesized metadata columns and carries exactly ONE
/// <c>_commit_version</c> (INV C8) — batches never span versions.</para>
/// </summary>
internal sealed class ChangeFeedReader
{
    // The CDF output metadata columns (§2.4), engine-synthesized and NEVER column-mapped: appended, in this
    // order, after the table's data columns. `_change_type` is per-row on the explicit path and constant per
    // version on the implicit path; `_commit_version`/`_commit_timestamp` are constant per version (stamped,
    // never materialized in a cdc body). `_commit_timestamp`'s lane is epoch MICROS (TimestampType), sourced
    // from the version's effective `<N>.json` mtime in millis × 1000 (§2.8).
    private static readonly StructField ChangeTypeField =
        new(ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType, nullable: false);

    private static readonly StructField CommitVersionField =
        new(ChangeDataWriter.CommitVersionColumn, DataTypes.LongType, nullable: false);

    private static readonly StructField CommitTimestampField =
        new(ChangeDataWriter.CommitTimestampColumn, DataTypes.TimestampType, nullable: false);

    // The projection used to read ONLY the `_change_type` column out of a cdc file body, by NAME. `_change_type`
    // is engine-synthesized and never column-mapped, so it is always stored under this literal name and read
    // by name (resolveByFieldId: false) — critical in id mode, where it has no field_id and a by-id read would
    // treat it as absent (§2.4).
    private static readonly StructType ChangeTypeOnlySchema =
        new(new[] { new StructField(ChangeDataWriter.ChangeTypeColumn, DataTypes.StringType, nullable: false) });

    // The `_change_type` domain is only four interned strings (§2.4). Their UTF-8 encodings are cached once
    // and shared, so the explicit path's per-row `_change_type` lane appends a pooled byte[] instead of
    // re-encoding `Encoding.UTF8.GetBytes(...)` for every row. `AppendBytes` copies the span into the
    // vector's buffer, so sharing the cached arrays is safe.
    private static readonly byte[] InsertChangeBytes = Encoding.UTF8.GetBytes(ChangeDataWriter.InsertChange);
    private static readonly byte[] DeleteChangeBytes = Encoding.UTF8.GetBytes(ChangeDataWriter.DeleteChange);

    private static readonly byte[] UpdatePreimageChangeBytes =
        Encoding.UTF8.GetBytes(ChangeDataWriter.UpdatePreimageChange);

    private static readonly byte[] UpdatePostimageChangeBytes =
        Encoding.UTF8.GetBytes(ChangeDataWriter.UpdatePostimageChange);

    private readonly LocalFileSystemBackend _backend;
    private readonly DeltaLog _log;
    private readonly ParquetFileReader _reader;

    public ChangeFeedReader(LocalFileSystemBackend backend, DeltaLog log, ParquetFileReader reader)
    {
        _backend = backend;
        _log = log;
        _reader = reader;
    }

    /// <summary>
    /// Resolves + validates a <see cref="DeltaChangeFeedRange"/> ONCE into a pinned inclusive
    /// <c>[start, end]</c> version range plus the reconciled output schema (§2.6). See the type remarks for
    /// the full validation contract.
    /// </summary>
    /// <exception cref="ArgumentException">A single endpoint specified both a version and a timestamp, or no
    /// start bound was supplied — mirroring <see cref="DeltaReadSource.LoadSnapshotAsync"/>'s xor rule.</exception>
    /// <exception cref="DeltaReadException">The range is invalid or unavailable fail-closed: not a Delta table,
    /// a negative start, an end past the latest version, a start after the end, a start/version aged past log
    /// retention, or CDF not active for every version in the range (§2.7).</exception>
    public async Task<DeltaChangeFeedInfo> ResolveAsync(DeltaChangeFeedRange range, CancellationToken cancellationToken)
    {
        // Per-endpoint version-xor-timestamp rule (mirrors LoadSnapshotAsync). A SINGLE endpoint may not carry
        // both; mixing ACROSS endpoints (startingVersion + endingTimestamp) is allowed (Spark parity). These
        // are caller-input contract violations → ArgumentException, distinct from the fail-closed range errors.
        if (range.StartingVersion is not null && range.StartingTimestamp is not null)
        {
            throw new ArgumentException(
                "A change feed's start endpoint may specify startingVersion XOR startingTimestamp, never both.",
                nameof(range));
        }

        if (range.EndingVersion is not null && range.EndingTimestamp is not null)
        {
            throw new ArgumentException(
                "A change feed's end endpoint may specify endingVersion XOR endingTimestamp, never both.",
                nameof(range));
        }

        if (range.StartingVersion is null && range.StartingTimestamp is null)
        {
            throw new ArgumentException(
                "A change feed requires a start bound: set startingVersion or startingTimestamp.", nameof(range));
        }

        ChangeFeedLog log;
        try
        {
            log = await _log.LoadChangeFeedLogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        // Endpoints resolve INDEPENDENTLY. A version endpoint is verbatim; a timestamp endpoint resolves off
        // the effective-commit timeline — start rounds UP (first commit at/after the ts), end rounds DOWN
        // (last commit at/before the ts, matching timestampAsOf). Omitting both end bounds defaults to latest.
        long startVersion = range.StartingVersion ?? ResolveStartTimestamp(log, range.StartingTimestamp!.Value);
        long endVersion = range.EndingVersion
            ?? (range.EndingTimestamp is { } endTs ? ResolveEndTimestamp(log, endTs) : log.LatestVersion);

        if (startVersion < 0)
        {
            throw new DeltaReadException(
                $"Change feed startingVersion {startVersion} is negative; the start version must be >= 0.");
        }

        if (endVersion > log.LatestVersion)
        {
            throw new DeltaReadException(
                $"Change feed endingVersion {endVersion} is beyond the latest committed version "
                + $"{log.LatestVersion}; the requested range extends past the end of the table's history.");
        }

        if (startVersion > endVersion)
        {
            throw new DeltaReadException(
                $"Change feed startingVersion {startVersion} is after endingVersion {endVersion}; the "
                + "requested range is empty (start must be <= end).");
        }

        // Availability (§2.6/CDF-EE-09). The start must be at/above the reconstructable floor (else its
        // snapshot — needed for the enablement check — is log-cleaned), and every version in the range must
        // have a retained commit log (else its actions cannot be replayed). Both fail closed as "outside the
        // CDF-readable window" rather than silently truncating the range.
        if (startVersion < log.EarliestReconstructableVersion)
        {
            throw new DeltaReadException(
                $"Change feed startingVersion {startVersion} has aged past log retention (the earliest "
                + $"reconstructable version is {log.EarliestReconstructableVersion}); the requested range is "
                + "outside the CDF-readable window.");
        }

        for (long v = startVersion; v <= endVersion; v++)
        {
            if (!log.EffectiveMillisByVersion.ContainsKey(v))
            {
                throw new DeltaReadException(
                    $"Change feed version {v} in [{startVersion}, {endVersion}] has no retained commit log "
                    + "(log cleanup removed it); the requested range is outside the CDF-readable window.");
            }
        }

        // Conservative enablement (§2.7): CDF must be active for EVERY version in the range, else fail closed.
        await ValidateCdfEnabledAsync(startVersion, endVersion, cancellationToken).ConfigureAwait(false);

        // Reconciled output schema (§2.8): the end version's table schema + the three metadata columns. A
        // cdc/data file physically narrower than this (a pre-evolution file) is null-filled on read; a
        // renamed column reads through under its end-version logical name (column mapping).
        StructType outputSchema;
        try
        {
            Snapshot end = await _log.LoadSnapshotAsync(endVersion, cancellationToken).ConfigureAwait(false);
            outputSchema = BuildOutputSchema(end.Schema);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        // Pin the effective-commit-millis map for [start, end] into the resolved info (item 4 / query-exec
        // L2): `_commit_timestamp` is stamped from THIS snapshot of the timeline at read time, never
        // re-derived, so a log-cleanup advancing the earliest-reconstructable floor between resolve and read
        // cannot shift a near-floor version's stamped timestamp. Every version in [start, end] was validated
        // present in EffectiveMillisByVersion above, so each lookup is total.
        ImmutableSortedDictionary<long, long>.Builder pinnedMillis =
            ImmutableSortedDictionary.CreateBuilder<long, long>();
        for (long v = startVersion; v <= endVersion; v++)
        {
            pinnedMillis[v] = log.EffectiveMillisByVersion[v];
        }

        // Stamp the non-forgeable resolution proof: it is the evidence that this info passed the full
        // resolve-time validation above (bounds, availability, and the §2.7 CDF-enablement gate) and carries
        // the pinned timeline. ReadAsync REQUIRES it, so ONLY a LoadChangeFeedAsync-produced info can be read;
        // a forged/`default` info (no proof) fails closed there instead of surfacing an unvalidated range.
        return new DeltaChangeFeedInfo(startVersion, endVersion, outputSchema)
        {
            Resolution = new ChangeFeedResolution(pinnedMillis.ToImmutable()),
        };
    }

    /// <summary>
    /// Replays the pinned <c>[<see cref="DeltaChangeFeedInfo.StartVersion"/>,
    /// <see cref="DeltaChangeFeedInfo.EndVersion"/>]</c> range into change batches, in ascending commit order.
    /// See the type remarks for the precedence (explicit cdc vs implicit derivation), DV-awareness, metadata
    /// stamping, and one-version-per-batch (INV C8) contract.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="info"/> was not obtained from
    /// <see cref="DeltaReadSource.LoadChangeFeedAsync"/> (a manually-constructed or <c>default</c> info): it
    /// carries no resolution proof, so its range never passed resolve-time validation (bounds, availability,
    /// and the §2.7 CDF-enablement gate). Rejected fail-closed BEFORE any I/O so a forged info can never
    /// surface change rows from an unvalidated range.</exception>
    /// <exception cref="DeltaReadException">A version's commit log or a required change/data file is no longer
    /// available (aged out / vacuumed between resolution and read), or a change-data file is inconsistent.</exception>
    /// <exception cref="DeltaReadSchemaEvolutionException">A cdc/data file is missing a REQUIRED (non-nullable)
    /// column the reconciled output schema demands — read-side null-fill cannot satisfy it — fails closed.</exception>
    public IAsyncEnumerable<ColumnBatch> ReadAsync(
        DeltaChangeFeedInfo info, CancellationToken cancellationToken)
    {
        // Fail closed EAGERLY (standard ArgumentException semantics — before the iterator body runs, so before
        // any I/O or yield): an info WITHOUT a resolution proof did not come from ResolveAsync /
        // LoadChangeFeedAsync, so its [start, end] range never passed resolve-time validation — crucially the
        // §2.7 conservative "CDF active for EVERY version in the range" gate. A consumer could otherwise forge
        // `new DeltaChangeFeedInfo(0, 2, schema)` (or pass `default`) and read change rows from a version where
        // CDF was never enabled, defeating the fail-closed contract. A ChangeFeedResolution can be minted ONLY
        // by ResolveAsync (internal, no public ctor), so its presence is the sole trust boundary here.
        if (info.Resolution is not { } resolution)
        {
            throw new ArgumentException(
                "DeltaChangeFeedInfo must be obtained from LoadChangeFeedAsync; a manually-constructed info "
                + "bypasses range and CDF-enablement validation.", nameof(info));
        }

        return ReadCoreAsync(info, resolution, cancellationToken);
    }

    private async IAsyncEnumerable<ColumnBatch> ReadCoreAsync(
        DeltaChangeFeedInfo info, ChangeFeedResolution resolution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        OutputContext ctx;
        try
        {
            ctx = await BuildOutputContextAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }
        catch (SchemaValidationException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        // `_commit_timestamp` is pinned at RESOLVE time (item 4 / query-exec L2): LoadChangeFeedAsync captured
        // the effective `<N>.json`-mtime map for [start, end] into the resolution proof, so an intervening
        // log-cleanup — which can advance the earliest-reconstructable floor between resolve and read — cannot
        // shift a near-floor version's stamped timestamp (versions/rows are already pinned; this pins the
        // timestamp lane too, §2.8). There is NO read-time re-derivation: every info reaching here carries a
        // resolution (ReadAsync rejected any that did not), so the stamp always comes from the pinned map.
        IReadOnlyDictionary<long, long> commitMillisByVersion = resolution.CommitMillisByVersion;

        // Per-version cdc schema validation (item 3 / §3.2 CDF-EE-08): the explicit path validates each cdc
        // file's decoded leaf schema against THAT version's own log-resident metadata — the trusted authority
        // — BEFORE any row is yielded, so a hostile/inconsistent cdc file whose columns disagree with its
        // version fails closed rather than surfacing attacker-chosen columns. We track the prevailing metadata
        // across the range: the baseline is the metadata as of `start`, then each version's own MetadataAction
        // (a metaData REPLACES the whole metadata, Delta semantics) supersedes it.
        MetadataAction currentMetadata;
        try
        {
            Snapshot startSnapshot = await _log.LoadSnapshotAsync(info.StartVersion, cancellationToken)
                .ConfigureAwait(false);
            currentMetadata = startSnapshot.Metadata;
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        for (long v = info.StartVersion; v <= info.EndVersion; v++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The version's effective `<N>.json` mtime (millis) — the SAME value timestampAsOf resolves
            // (§2.8), read from the pinned/derived map. A version missing here aged out between resolve and
            // read.
            long commitMillis = ResolveCommitMillis(commitMillisByVersion, v);

            IReadOnlyList<DeltaAction> actions;
            try
            {
                actions = await _log.ReadCommitActionsAsync(v, cancellationToken).ConfigureAwait(false);
            }
            catch (DeltaStorageException ex)
            {
                throw ex.Kind == StorageErrorKind.NotFound
                    ? new DeltaReadException(
                        $"Change feed version {v}'s commit log is no longer available (log cleanup removed it "
                        + "between range resolution and read); the requested range is outside the "
                        + "CDF-readable window.", ex)
                    : new DeltaReadException(ex.Message, ex);
            }
            catch (DeltaProtocolException ex)
            {
                throw new DeltaReadException(ex.Message, ex);
            }

            // Track the prevailing metadata (item 3): a metaData action in this commit REPLACES it (Delta
            // semantics). At v == start this re-applies start's own metaData (idempotent — already baked into
            // the baseline snapshot); for later versions it advances the schema the cdc validation trusts.
            foreach (DeltaAction action in actions)
            {
                if (action is MetadataAction updatedMetadata)
                {
                    currentMetadata = updatedMetadata;
                }
            }

            // Precedence (INV C1/C2, §2.2): ANY cdc action ⇒ explicit (read exactly the cdc rows, ignore
            // add/remove — no double count); otherwise implicit (derive from add/remove — no miss).
            bool hasCdc = false;
            foreach (DeltaAction action in actions)
            {
                if (action is AddCdcFileAction)
                {
                    hasCdc = true;
                    break;
                }
            }

            if (hasCdc)
            {
                // §3.2 CDF-EE-08: validate every cdc file's leaf schema against this version's metadata before
                // reading any row (the schema is built once per version, then reused for each cdc file).
                StructType versionPhysicalDataSchema = BuildVersionPhysicalDataSchema(currentMetadata, v);
                foreach (DeltaAction action in actions)
                {
                    if (action is AddCdcFileAction cdc)
                    {
                        await ValidateExplicitCdcSchemaAsync(
                            cdc.Path, versionPhysicalDataSchema, v, cancellationToken).ConfigureAwait(false);
                        IReadOnlyList<ColumnBatch> batches = await ReadExplicitFileAsync(
                            cdc, ctx, v, commitMillis, cancellationToken).ConfigureAwait(false);
                        foreach (ColumnBatch batch in batches)
                        {
                            yield return batch;
                        }
                    }
                }

                continue;
            }

            // Implicit path. Within a version we emit derived DELETEs (from removes) before derived INSERTs
            // (from adds) — a fixed, deterministic intra-version order (Delta guarantees no cross-file order
            // within a commit, so DeltaSharp pins one). Cross-version ascending order is guaranteed by the
            // outer loop; every batch carries this version's `_commit_version` (INV C8).
            foreach (DeltaAction action in actions)
            {
                if (action is RemoveFileAction remove && remove.DataChange)
                {
                    await foreach (ColumnBatch batch in ReadImplicitFileAsync(
                            remove.Path, remove.PartitionValues, remove.DeletionVector, declaredPhysicalRecords: null,
                            ChangeDataWriter.DeleteChange, ctx, v, commitMillis, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        yield return batch;
                    }
                }
            }

            foreach (DeltaAction action in actions)
            {
                if (action is AddFileAction add && add.DataChange)
                {
                    await foreach (ColumnBatch batch in ReadImplicitFileAsync(
                            add.Path, add.PartitionValues, add.DeletionVector, add.Stats?.NumRecords,
                            ChangeDataWriter.InsertChange, ctx, v, commitMillis, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        yield return batch;
                    }
                }
            }
        }
    }

    // start-timestamp resolution: the FIRST commit whose effective timestamp is at/after the request (round
    // UP, Spark parity). A request after the last commit has no in-range change → fail closed.
    private static long ResolveStartTimestamp(ChangeFeedLog log, DateTimeOffset timestamp)
    {
        if (log.CommitVersions.Count == 0)
        {
            throw new DeltaReadException(
                "The change feed has no retained commit logs to resolve a startingTimestamp against.");
        }

        long millis = timestamp.ToUnixTimeMilliseconds();
        for (int i = 0; i < log.CommitVersions.Count; i++)
        {
            if (log.EffectiveMillis[i] >= millis)
            {
                return log.CommitVersions[i];
            }
        }

        throw new DeltaReadException(
            $"Change feed startingTimestamp {timestamp:o} is after the latest committed change (effective "
            + $"{DateTimeOffset.FromUnixTimeMilliseconds(log.EffectiveMillis[^1]):o}); no changes fall in the "
            + "requested range.");
    }

    // end-timestamp resolution: the LAST commit whose effective timestamp is at/before the request (round
    // DOWN, matching timestampAsOf). A request before the first commit has no in-range change → fail closed.
    private static long ResolveEndTimestamp(ChangeFeedLog log, DateTimeOffset timestamp)
    {
        if (log.CommitVersions.Count == 0)
        {
            throw new DeltaReadException(
                "The change feed has no retained commit logs to resolve an endingTimestamp against.");
        }

        long millis = timestamp.ToUnixTimeMilliseconds();
        long? resolved = null;
        for (int i = 0; i < log.CommitVersions.Count; i++)
        {
            if (log.EffectiveMillis[i] <= millis)
            {
                resolved = log.CommitVersions[i];
            }
            else
            {
                // Effective timestamps are strictly increasing, so no later commit can qualify either.
                break;
            }
        }

        return resolved ?? throw new DeltaReadException(
            $"Change feed endingTimestamp {timestamp:o} is before the earliest retained change (effective "
            + $"{DateTimeOffset.FromUnixTimeMilliseconds(log.EffectiveMillis[0]):o}); no changes fall in the "
            + "requested range.");
    }

    // Conservative enablement (§2.7): walk [start, end] and require CDF active at EVERY version. Efficient:
    // reconstruct the snapshot AT start once (protocol + config after commit start), then replay each later
    // commit's protocol/metaData actions to track the post-commit state and check IsActive at each version.
    private async Task ValidateCdfEnabledAsync(long start, long end, CancellationToken cancellationToken)
    {
        ProtocolAction protocol;
        IReadOnlyDictionary<string, string> configuration;
        try
        {
            Snapshot startSnapshot = await _log.LoadSnapshotAsync(start, cancellationToken).ConfigureAwait(false);
            protocol = startSnapshot.Protocol;
            configuration = startSnapshot.Metadata.Configuration;
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        EnsureCdfActive(start, protocol, configuration);

        for (long v = start + 1; v <= end; v++)
        {
            IReadOnlyList<DeltaAction> actions;
            try
            {
                actions = await _log.ReadCommitActionsAsync(v, cancellationToken).ConfigureAwait(false);
            }
            catch (DeltaStorageException ex)
            {
                throw new DeltaReadException(
                    $"Change feed version {v}'s commit log could not be read while validating CDF enablement "
                    + $"({ex.Kind}); the requested range is outside the CDF-readable window.", ex);
            }
            catch (DeltaProtocolException ex)
            {
                throw new DeltaReadException(ex.Message, ex);
            }

            // A metaData action REPLACES the whole configuration (Delta semantics), and a protocol action
            // replaces the protocol; apply both so the post-commit state is exact.
            foreach (DeltaAction action in actions)
            {
                if (action is ProtocolAction updatedProtocol)
                {
                    protocol = updatedProtocol;
                }
                else if (action is MetadataAction updatedMetadata)
                {
                    configuration = updatedMetadata.Configuration;
                }
            }

            EnsureCdfActive(v, protocol, configuration);
        }
    }

    private static void EnsureCdfActive(
        long version, ProtocolAction protocol, IReadOnlyDictionary<string, string> configuration)
    {
        if (!ChangeDataFeedFeature.IsActive(protocol, configuration))
        {
            throw new DeltaReadException(
                $"Change Data Feed is not enabled at version {version} of the requested range. DeltaSharp reads "
                + "a change feed only when CDF is active for EVERY version in [start, end] (the conservative "
                + "enablement rule, design §2.7): enable delta.enableChangeDataFeed (with the changeDataFeed "
                + "writer feature) across the whole range, or narrow the range to a CDF-enabled span.");
        }
    }

    private static long ResolveCommitMillis(IReadOnlyDictionary<long, long> effectiveMillisByVersion, long version) =>
        effectiveMillisByVersion.TryGetValue(version, out long millis)
            ? millis
            : throw new DeltaReadException(
                $"Change feed version {version}'s commit log is no longer available (log cleanup removed it "
                + "between range resolution and read); the requested range is outside the CDF-readable window.");

    private async Task<OutputContext> BuildOutputContextAsync(
        DeltaChangeFeedInfo info, CancellationToken cancellationToken)
    {
        Snapshot end = await _log.LoadSnapshotAsync(info.EndVersion, cancellationToken).ConfigureAwait(false);
        StructType tableSchema = end.Schema;
        ColumnMappingMode mode = ColumnMapping.ResolveMode(end.Metadata.Configuration);
        bool resolveByFieldId = mode == ColumnMappingMode.Id;
        ImmutableArray<string> partitionColumns = end.Metadata.PartitionColumns;
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(tableSchema, mode);
        StructType physicalDataSchema =
            ColumnMappingProjection.BuildDataSchema(tableSchema, physicalNames, partitionColumns);
        int[] dataOrdinalByField = ColumnMappingProjection.MapDataOrdinals(physicalNames, physicalDataSchema);
        bool allowTypeWideningPromotion = TypeWideningFeature.Supports(end.Protocol);
        return new OutputContext(
            info.Schema, tableSchema, physicalDataSchema, physicalNames, dataOrdinalByField, resolveByFieldId,
            allowTypeWideningPromotion);
    }

    // §3.2 CDF-EE-08: builds the version's expected PHYSICAL data-leaf schema from its log-resident metadata
    // (the trusted authority): parse the metadata's schemaString, resolve the column-mapping physical names,
    // and drop the partition columns (which live only in the log, never the file body). A legitimate cdc
    // file's data columns must match THIS schema exactly (leaf name + leaf type) — cross-version reconciliation
    // to the output schema (§2.8) happens afterwards, against `ctx`, only for a file that passed this gate.
    private static StructType BuildVersionPhysicalDataSchema(MetadataAction metadata, long version)
    {
        DataType parsed;
        try
        {
            parsed = SchemaJson.FromJson(metadata.SchemaString);
        }
        catch (SchemaValidationException ex)
        {
            throw new DeltaReadException(
                $"Change feed version {version}'s metadata schemaString is unparseable; the commit log is "
                + "inconsistent, so the read fails closed.", ex);
        }

        if (parsed is not StructType schema)
        {
            throw new DeltaReadException(
                $"Change feed version {version}'s metadata schemaString is not a struct; the commit log is "
                + "inconsistent, so the read fails closed.");
        }

        ColumnMappingMode mode = ColumnMapping.ResolveMode(metadata.Configuration);
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(schema, mode);
        return ColumnMappingProjection.BuildDataSchema(schema, physicalNames, metadata.PartitionColumns);
    }

    // §3.2 CDF-EE-08: reads a cdc file's decoded leaf schema (footer only — no page decode) and validates it
    // against `versionPhysicalDataSchema` (the trusted per-version authority) BEFORE any row is read. Fails
    // closed on a mismatch (a missing/extra data column, or a leaf-type disagreement), distinct from the
    // NotFound/vacuumed classification (CDF-EE-09) and the corrupt-body classification (CDF-EE-07).
    private async Task ValidateExplicitCdcSchemaAsync(
        string path, StructType versionPhysicalDataSchema, long version, CancellationToken cancellationToken)
    {
        StructType fileSchema;
        try
        {
            Stream stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                fileSchema = await _reader.ReadDataSchemaAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DeltaStorageException ex)
        {
            throw ClassifyFileError(path, ex);
        }

        ValidateCdcLeafSchema(path, version, versionPhysicalDataSchema, fileSchema);
    }

    // The leaf comparison for CDF-EE-08. Physical names are 1:1 with field-ids in DeltaSharp's mapping (a
    // renamed column keeps its physical name/id), so matching by physical name + leaf DataType validates both
    // name and id modes. The synthesized `_change_type` column is excluded (it is engine-owned, not part of a
    // version's data schema, and its VALUE domain is validated separately in ReadChangeTypesAsync).
    private static void ValidateCdcLeafSchema(
        string path, long version, StructType expected, StructType fileSchema)
    {
        var fileByName = new Dictionary<string, DataType>(StringComparer.Ordinal);
        foreach (StructField field in fileSchema)
        {
            if (string.Equals(field.Name, ChangeDataWriter.ChangeTypeColumn, StringComparison.Ordinal))
            {
                continue;
            }

            if (!fileByName.TryAdd(field.Name, field.DataType))
            {
                throw NewCdcSchemaMismatch(path, version, $"it declares data column '{field.Name}' more than once");
            }
        }

        foreach (StructField expectedField in expected)
        {
            if (!fileByName.TryGetValue(expectedField.Name, out DataType? fileType))
            {
                throw NewCdcSchemaMismatch(
                    path, version, $"it is missing the version's data column '{expectedField.Name}'");
            }

            if (!expectedField.DataType.Equals(fileType))
            {
                throw NewCdcSchemaMismatch(
                    path, version,
                    $"data column '{expectedField.Name}' has leaf type {fileType.SimpleString} but the version's "
                    + $"metadata declares {expectedField.DataType.SimpleString}");
            }

            fileByName.Remove(expectedField.Name);
        }

        if (fileByName.Count > 0)
        {
            string extras = string.Join(", ", fileByName.Keys.OrderBy(name => name, StringComparer.Ordinal));
            throw NewCdcSchemaMismatch(
                path, version, $"it declares data column(s) [{extras}] absent from the version's metadata schema");
        }
    }

    private static DeltaReadException NewCdcSchemaMismatch(string path, long version, string detail) =>
        new($"Change-data file '{path}' is inconsistent with version {version}'s schema: {detail}. DeltaSharp "
            + "validates each cdc file's leaf schema against that version's log-resident metadata (the trusted "
            + "authority) before reading, and fails closed on a mismatch (design §3.2 CDF-EE-08).");

    // Explicit path (§2.2): the change rows for this version are EXACTLY the cdc file's rows, each carrying its
    // own `_change_type`. cdc files hold only the change rows (bounded by the change size, not the table), so
    // this materializes one cdc file's batches — enabling a clean TWO-READ over the same file: read the
    // `_change_type` column (by name — never column-mapped) fully into a flat array, then stream the data
    // columns (mode-aware physical resolution) and align each data batch to its slice of the change types by
    // cumulative row position (asserting the totals match). The two reads project the same file in the same
    // row order, so the alignment is exact.
    private async Task<IReadOnlyList<ColumnBatch>> ReadExplicitFileAsync(
        AddCdcFileAction cdc, OutputContext ctx, long version, long commitMillis, CancellationToken cancellationToken)
    {
        string[] changeTypes = await ReadChangeTypesAsync(cdc.Path, cancellationToken).ConfigureAwait(false);

        var outputs = new List<ColumnBatch>();
        long offset = 0;
        try
        {
            Stream stream = await _backend.OpenReadAsync(cdc.Path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                await foreach (ColumnBatch physical in _reader
                    .ReadAsync(
                        stream, ctx.PhysicalDataSchema, keepRowGroup: null, nullFillMissingColumns: true,
                        ctx.AllowTypeWideningPromotion, ctx.ResolveByFieldId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    int rows = physical.RowCount;
                    if (checked(offset + rows) > changeTypes.Length)
                    {
                        throw new DeltaReadException(
                            $"Change-data file '{cdc.Path}' produced more data rows than "
                            + $"'{ChangeDataWriter.ChangeTypeColumn}' values; the change-data file is inconsistent.");
                    }

                    ColumnBatch logical = ColumnMappingProjection.BuildFullBatch(
                        cdc.PartitionValues, ctx.OutputDataSchema, ctx.PhysicalNames, ctx.DataOrdinalByField,
                        physical);
                    outputs.Add(AppendMetadataColumns(
                        logical, ctx, PerRowChangeType(changeTypes, (int)offset, rows), version, commitMillis));
                    offset += rows;
                }
            }
        }
        catch (DeltaStorageException ex) when (IsNarrowSchemaEvolutionInput(ex))
        {
            throw new DeltaReadSchemaEvolutionException(cdc.Path, ex);
        }
        catch (DeltaStorageException ex)
        {
            throw ClassifyFileError(cdc.Path, ex);
        }

        if (offset != changeTypes.Length)
        {
            throw new DeltaReadException(
                $"Change-data file '{cdc.Path}' produced {offset} data row(s) but has {changeTypes.Length} "
                + $"'{ChangeDataWriter.ChangeTypeColumn}' value(s); the change-data file is inconsistent.");
        }

        return outputs;
    }

    // Reads the `_change_type` column (by name; never column-mapped) fully, validating each value non-null and
    // within the closed change-type domain (§5.2 — a foreign/tampered writer cannot smuggle an unknown type).
    private async Task<string[]> ReadChangeTypesAsync(string path, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        try
        {
            Stream stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                await foreach (ColumnBatch batch in _reader
                    .ReadAsync(
                        stream, ChangeTypeOnlySchema, keepRowGroup: null, nullFillMissingColumns: false,
                        allowTypeWideningPromotion: false, resolveByFieldId: false, cancellationToken)
                    .ConfigureAwait(false))
                {
                    ColumnVector column = batch.Column(0);
                    for (int r = 0; r < batch.RowCount; r++)
                    {
                        if (column.IsNull(r))
                        {
                            throw new DeltaReadException(
                                $"Change-data file '{path}' has a null '{ChangeDataWriter.ChangeTypeColumn}' "
                                + "value; a cdc file's change-type column must be non-null.");
                        }

                        string changeType = Encoding.UTF8.GetString(column.GetBytes(r));
                        if (!ChangeDataWriter.ChangeTypeDomain.Contains(changeType))
                        {
                            throw new DeltaReadException(
                                $"Change-data file '{path}' has an unrecognized "
                                + $"'{ChangeDataWriter.ChangeTypeColumn}' value '{changeType}'; the legal values "
                                + "are insert / delete / update_preimage / update_postimage.");
                        }

                        values.Add(changeType);
                    }
                }
            }
        }
        catch (DeltaStorageException ex)
        {
            // A cdc file's `_change_type` column is engine-written and REQUIRED; any storage fault (absent
            // column, corruption, or a vanished file) makes the change-data file unreadable → fail closed.
            throw ex.Kind == StorageErrorKind.NotFound
                ? ClassifyFileError(path, ex)
                : new DeltaReadException(
                    $"Change-data file '{path}' could not be read for its "
                    + $"'{ChangeDataWriter.ChangeTypeColumn}' column ({ex.Kind}); the change-data file is "
                    + "unreadable.", ex);
        }

        return values.ToArray();
    }

    // Implicit path (§2.2): stream a data/removed file row-group by row-group (bounded per-batch decode; a
    // large overwrite file is not materialized), relabel physical→logical + hydrate partition columns, stamp
    // the synthesized change type + metadata, and — DV-aware — surface only rows still LIVE (a row already
    // masked by the file's deletion vector never surfaces, so a prior-DV-masked row is not re-emitted as an
    // insert/delete). Errors are classified before/around the yielding loop (never across a `yield return`).
    private async IAsyncEnumerable<ColumnBatch> ReadImplicitFileAsync(
        string path,
        ImmutableSortedDictionary<string, string?> partitionValues,
        DeletionVectorDescriptor? deletionVectorDescriptor,
        long? declaredPhysicalRecords,
        string changeType,
        OutputContext ctx,
        long version,
        long commitMillis,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Load the deletion vector (if any) BEFORE reading data, bounding the decode by the file's REAL
        // physical row count (from the footer, never a caller-supplied size). This fails a poisoned/vanished
        // DV closed here rather than after emitting rows.
        DeletionVectorMask? mask = null;
        if (deletionVectorDescriptor is { } descriptor)
        {
            try
            {
                long physicalRecords;
                Stream metaStream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
                await using (metaStream.ConfigureAwait(false))
                {
                    physicalRecords = await _reader.GetRowCountAsync(metaStream, cancellationToken)
                        .ConfigureAwait(false);
                }

                // An add carries stats.numRecords (the PHYSICAL count); cross-check it against the file. A
                // remove has no stats (declaredPhysicalRecords is null), so the footer count is authoritative.
                if (declaredPhysicalRecords is { } declared && declared != physicalRecords)
                {
                    throw new DeltaReadException(
                        $"Change-feed file '{path}' declares stats.numRecords={declared} but its Parquet file "
                        + $"contains {physicalRecords} physical row(s); a deletion-vector-carrying file's "
                        + "numRecords must equal the physical row count, so the read fails closed.");
                }

                long[] positions = await DeletionVectorStore
                    .LoadAsync(_backend, descriptor, physicalRecords, cancellationToken).ConfigureAwait(false);
                mask = new DeletionVectorMask(positions, physicalRecords);
            }
            catch (DeltaStorageException ex)
            {
                throw ClassifyFileError(path, ex);
            }
        }

        Stream stream;
        try
        {
            stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaStorageException ex)
        {
            throw ClassifyFileError(path, ex);
        }

        long fileRowOffset = 0;
        await using (stream.ConfigureAwait(false))
        {
            IAsyncEnumerator<ColumnBatch> enumerator = _reader
                .ReadAsync(
                    stream, ctx.PhysicalDataSchema, keepRowGroup: null, nullFillMissingColumns: true,
                    ctx.AllowTypeWideningPromotion, ctx.ResolveByFieldId, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    ColumnBatch physical;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        physical = enumerator.Current;
                    }
                    catch (DeltaStorageException ex) when (IsNarrowSchemaEvolutionInput(ex))
                    {
                        throw new DeltaReadSchemaEvolutionException(path, ex);
                    }
                    catch (DeltaStorageException ex)
                    {
                        throw ClassifyFileError(path, ex);
                    }

                    ColumnBatch? output = BuildImplicitBatch(
                        physical, partitionValues, changeType, ctx, version, commitMillis, mask, fileRowOffset);
                    fileRowOffset = checked(fileRowOffset + physical.RowCount);
                    if (output is { } batch)
                    {
                        yield return batch;
                    }
                }
            }
        }

        // The file's real physical row count must match the count the DV was validated against — a mismatch
        // means the file changed under the DV, so the positions can no longer be trusted. Fail closed.
        mask?.EnsureConsumed(fileRowOffset, path);
    }

    // Assembles one implicit output batch: relabel + hydrate partition columns, stamp a CONSTANT change type
    // and the per-version metadata, then apply the DV as a selection of surviving physical rows (null when the
    // whole batch is masked → dropped by the caller).
    private ColumnBatch? BuildImplicitBatch(
        ColumnBatch physical,
        ImmutableSortedDictionary<string, string?> partitionValues,
        string changeType,
        OutputContext ctx,
        long version,
        long commitMillis,
        DeletionVectorMask? mask,
        long fileRowOffset)
    {
        ColumnBatch logical = ColumnMappingProjection.BuildFullBatch(
            partitionValues, ctx.OutputDataSchema, ctx.PhysicalNames, ctx.DataOrdinalByField, physical);
        ColumnBatch full = AppendMetadataColumns(
            logical, ctx, ConstantChangeType(changeType, physical.RowCount), version, commitMillis);

        // DV-aware (§2.2): surface only rows still LIVE. A batch with no masked row returns unchanged; a
        // fully-masked batch returns null (the caller drops it). Shared with the snapshot door so the two
        // can never drift (item 2 / #529).
        return mask is null ? full : mask.Apply(full, fileRowOffset);
    }

    // Appends the three metadata columns to a data+partition logical batch, yielding a full-schema output
    // batch. `_commit_version` and `_commit_timestamp` are constant per version (INV C8 — one commit version
    // per batch); `_change_type` is supplied per batch (constant on the implicit path, per-row on explicit).
    private static ColumnBatch AppendMetadataColumns(
        ColumnBatch dataBatch, OutputContext ctx, ColumnVector changeType, long version, long commitMillis)
    {
        int dataColumnCount = ctx.OutputDataSchema.Count;
        int rowCount = dataBatch.RowCount;
        var columns = new ColumnVector[dataColumnCount + 3];
        for (int i = 0; i < dataColumnCount; i++)
        {
            columns[i] = dataBatch.Column(i);
        }

        columns[dataColumnCount] = changeType;
        columns[dataColumnCount + 1] = ConstantLong(version, rowCount);
        // TimestampType lane is epoch MICROS; the effective mtime is millis (§2.8) → × 1000.
        columns[dataColumnCount + 2] = ConstantTimestampMicros(checked(commitMillis * 1000L), rowCount);
        return new ManagedColumnBatch(ctx.OutputSchema, columns, rowCount);
    }

    private static ColumnVector ConstantChangeType(string value, int rowCount) =>
        DeltaReadEncoding.BuildConstantColumn(DataTypes.StringType, value, rowCount);

    private static ColumnVector PerRowChangeType(string[] values, int start, int rowCount)
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.StringType, Math.Max(rowCount, 1));
        for (int r = 0; r < rowCount; r++)
        {
            vector.AppendBytes(ChangeTypeBytes(values[start + r]));
        }

        return vector;
    }

    // Maps a validated (domain-checked in ReadChangeTypesAsync) change-type string to its cached UTF-8 bytes,
    // avoiding a per-row re-encode. The `_` arm is unreachable for in-domain values but keeps the mapping
    // total (defence in depth) rather than throwing.
    private static byte[] ChangeTypeBytes(string changeType) => changeType switch
    {
        ChangeDataWriter.InsertChange => InsertChangeBytes,
        ChangeDataWriter.DeleteChange => DeleteChangeBytes,
        ChangeDataWriter.UpdatePreimageChange => UpdatePreimageChangeBytes,
        ChangeDataWriter.UpdatePostimageChange => UpdatePostimageChangeBytes,
        _ => Encoding.UTF8.GetBytes(changeType),
    };

    private static ColumnVector ConstantLong(long value, int rowCount)
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.LongType, Math.Max(rowCount, 1));
        for (int r = 0; r < rowCount; r++)
        {
            vector.AppendValue<long>(value);
        }

        return vector;
    }

    private static ColumnVector ConstantTimestampMicros(long micros, int rowCount)
    {
        MutableColumnVector vector = ColumnVectors.Create(DataTypes.TimestampType, Math.Max(rowCount, 1));
        for (int r = 0; r < rowCount; r++)
        {
            vector.AppendValue<long>(micros);
        }

        return vector;
    }

    private static StructType BuildOutputSchema(StructType tableSchema)
    {
        var fields = new List<StructField>(tableSchema.Count + 3);
        for (int i = 0; i < tableSchema.Count; i++)
        {
            fields.Add(tableSchema[i]);
        }

        fields.Add(ChangeTypeField);
        fields.Add(CommitVersionField);
        fields.Add(CommitTimestampField);
        return new StructType(fields);
    }

    private static bool IsNarrowSchemaEvolutionInput(DeltaStorageException ex) =>
        ex.Kind == StorageErrorKind.ColumnNotPresentInFile;

    private static DeltaReadException ClassifyFileError(string path, DeltaStorageException ex) =>
        ex.Kind == StorageErrorKind.NotFound
            ? new DeltaReadException(
                $"Change-feed file '{path}' is no longer available (vacuumed, or past the data-retention "
                + "window); the requested change-feed range is outside the CDF-readable window.", ex)
            : new DeltaReadException(ex.Message, ex);

    // The resolved read context, built once from the end snapshot: the reconciled output schema (data + 3
    // metadata), the end-version logical data schema (data + partition, for relabeling), the physical data
    // schema the Parquet reader projects, the per-field physical names + data ordinals (−1 = partition), and
    // the id-resolution / type-widening gates. Every version's file reads through this SAME context (schema-
    // on-read reconciliation, §2.8).
    private sealed record OutputContext(
        StructType OutputSchema,
        StructType OutputDataSchema,
        StructType PhysicalDataSchema,
        string[] PhysicalNames,
        int[] DataOrdinalByField,
        bool ResolveByFieldId,
        bool AllowTypeWideningPromotion);
}
