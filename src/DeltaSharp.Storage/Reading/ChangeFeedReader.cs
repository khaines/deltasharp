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

    private readonly IStorageBackend _backend;
    private readonly string _sourceId;
    private readonly DeltaLog _log;
    private readonly ParquetFileReader _reader;

    public ChangeFeedReader(LocalFileSystemBackend backend, DeltaLog log, ParquetFileReader reader)
        : this(backend, backend.TableRootId, log, reader)
    {
    }

    /// <summary>
    /// DI/test seam: binds the reader to any <see cref="IStorageBackend"/> plus an explicit source identity
    /// (the resolution-proof <c>SourceId</c>). The public ctor forwards <see cref="LocalFileSystemBackend"/>'s
    /// <c>TableRootId</c>; only <see cref="IStorageBackend"/> members are used, so a decorating backend can
    /// serve the cdc file's bytes — exercised by the count-mismatch/TOCTOU consistency test (#644 red-team).
    /// </summary>
    internal ChangeFeedReader(IStorageBackend backend, string sourceId, DeltaLog log, ParquetFileReader reader)
    {
        _backend = backend;
        _sourceId = sourceId;
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
        // the pinned timeline. It is bound to THIS source (`_sourceId`, the backend's TableRootId) so it
        // cannot replay on a different table. ReadAsync REQUIRES it, so ONLY a LoadChangeFeedAsync-produced
        // info for THIS source can be read; a forged/`default`/cross-source info fails closed instead of
        // surfacing an unvalidated range.
        return new DeltaChangeFeedInfo(startVersion, endVersion, outputSchema)
        {
            Resolution = new ChangeFeedResolution(_sourceId, pinnedMillis.ToImmutable()),
        };
    }

    /// <summary>
    /// Replays the pinned <c>[<see cref="DeltaChangeFeedInfo.StartVersion"/>,
    /// <see cref="DeltaChangeFeedInfo.EndVersion"/>]</c> range into change batches, in ascending commit order.
    /// See the type remarks for the precedence (explicit cdc vs implicit derivation), DV-awareness, metadata
    /// stamping, and one-version-per-batch (INV C8) contract.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="info"/> was not obtained from
    /// <see cref="DeltaReadSource.LoadChangeFeedAsync"/> on THIS source — either a manually-constructed or
    /// <c>default</c> info (no resolution proof, so its range never passed resolve-time validation: bounds,
    /// availability, and the §2.7 CDF-enablement gate), or an info resolved by a DIFFERENT source/table
    /// (its validation and pinned timestamps do not apply here). Rejected fail-closed BEFORE any I/O so a
    /// forged or cross-source info can never surface change rows from an unvalidated range.</exception>
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

        // Defense-in-depth (Security F-1): the proof is bound to the SOURCE that minted it, not just to a
        // range. An info resolved on source A carries A's validation + A's pinned timestamps; replaying it on
        // a DIFFERENT source B would bypass B's own §2.7 enablement gate and stamp B's rows with A's
        // timestamps (a cross-table footgun). Reject when the bound source identity differs from this one.
        if (!string.Equals(resolution.SourceId, _sourceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DeltaChangeFeedInfo must be read by the same DeltaReadSource that resolved it (via "
                + "LoadChangeFeedAsync); this info was produced by a different source, so its range / "
                + "CDF-enablement validation and pinned timestamps do not apply to this table.", nameof(info));
        }

        return ReadCoreAsync(info, resolution, cancellationToken);
    }

    private async IAsyncEnumerable<ColumnBatch> ReadCoreAsync(
        DeltaChangeFeedInfo info, ChangeFeedResolution resolution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        OutputContext ctx;
        DeltaLog.ChangeFeedEndView endView;
        try
        {
            (ctx, endView) = await BuildOutputContextAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }
        catch (SchemaValidationException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }

        // Full-history column-mapping IDENTITY immutability (#671, maintainer-broadened from mode-only): the
        // per-version check below covers a transition WITHIN [start, end], but a file a `remove` in the range
        // references may have been AUTHORED before `start` under a different column-mapping identity (mode,
        // field id, or physical name) — the implicit-DELETE path reads it through the END identity, a mismap
        // the per-version check never sees. Validate that every RETAINED version before `start` shares the end
        // identity, failing closed on any difference.
        //
        // Per-version cdc schema validation (item 3 / §3.2 CDF-EE-08) needs the metadata prevailing at
        // `start`, which is the START snapshot's — so the SAME call returns it. The two were separate log
        // passes (a second `_delta_log` LIST plus a second GET of every pre-range commit); they are fused
        // here (#691), driven off the listing the END snapshot above was reconstructed from. The gate's
        // coverage, order, and fail-closed messages are unchanged (see
        // `DeltaLog.LoadChangeFeedStartSnapshotAsync`), and it still runs to completion BEFORE the replay loop
        // below reads any change/data file or yields any batch. FAIL ORDER did change: the start-snapshot
        // reconstruction now runs before the identity gate (it feeds it), so a log that is both
        // protocol-illegal at `start` and identity-forged earlier surfaces the protocol error rather than the
        // identity error. Both are fail-closed and path-free; only which one wins changed. The explicit path validates each cdc file's
        // decoded leaf schema against THAT version's own log-resident metadata — the trusted authority —
        // BEFORE any row is yielded, so a hostile/inconsistent cdc file whose columns disagree with its
        // version fails closed rather than surfacing attacker-chosen columns. We track the prevailing metadata
        // across the range: the baseline is the metadata as of `start`, then each version's own MetadataAction
        // (a metaData REPLACES the whole metadata, Delta semantics) supersedes it.
        MetadataAction currentMetadata;
        try
        {
            Snapshot startSnapshot = await _log.LoadChangeFeedStartSnapshotAsync(
                endView, info.StartVersion, cancellationToken).ConfigureAwait(false);
            currentMetadata = startSnapshot.Metadata;
        }
        catch (DeltaProtocolException ex)
        {
            throw new DeltaReadException(ex.Message, ex);
        }
        catch (DeltaStorageException ex)
        {
            // Covers BOTH halves of the fused call (#691): the start-snapshot reconstruction and the
            // pre-range validation scan. Named jointly so on-call is not told "pre-range validation" for a
            // fault raised while reconstructing the start version itself.
            throw new DeltaReadException(
                $"A change-feed start-snapshot load or pre-range validation read failed (storage fault: "
                + $"{ex.Kind}); the requested change-feed range failed closed.", ex);
        }

        // `_commit_timestamp` is pinned at RESOLVE time (item 4 / query-exec L2): LoadChangeFeedAsync captured
        // the effective `<N>.json`-mtime map for [start, end] into the resolution proof, so an intervening
        // log-cleanup — which can advance the earliest-reconstructable floor between resolve and read — cannot
        // shift a near-floor version's stamped timestamp (versions/rows are already pinned; this pins the
        // timestamp lane too, §2.8). There is NO read-time re-derivation: every info reaching here carries a
        // resolution (ReadAsync rejected any that did not), so the stamp always comes from the pinned map.
        IReadOnlyDictionary<long, long> commitMillisByVersion = resolution.CommitMillisByVersion;

        // #671 in-range half of the full-history column-mapping IDENTITY-immutability gate. The pre-range half
        // (DeltaLog.LoadChangeFeedStartSnapshotAsync, above) validated [earliest, start-1]; this validates
        // every version IN [start, end] against the end identity as the replay advances the prevailing
        // metadata. Their union is the full retained window a CDF read can touch (#671). Parse the end and the
        // start-baseline identities once; the per-version loop refreshes `currentIdentity` only when a metaData
        // action replaces the prevailing metadata.
        ColumnMappingIdentity endIdentity;
        ColumnMappingIdentity currentIdentity;
        try
        {
            endIdentity = ColumnMappingIdentity.FromMetadata(endView.EndMetadata);
            currentIdentity = ColumnMappingIdentity.FromMetadata(currentMetadata);
        }
        catch (Exception ex) when (ex is SchemaValidationException or DeltaProtocolException)
        {
            // Fixed, path-free message (both an unparseable/non-struct schema AND an unrecognized mode reach
            // here — the latter as DeltaProtocolException from ColumnMapping.ResolveMode); the raw detail is
            // retained on InnerException for diagnostics but never surfaced (#653 / #664).
            throw new DeltaReadException(
                "A change-feed range's metadata declared an unreadable column-mapping identity (unrecognized "
                + "mode or malformed/inconsistent schema); the read fails closed.", ex);
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
                    : new DeltaReadException(
                $"A change-feed file could not be read (storage fault: {ex.Kind}); the requested "
                + "change-feed range failed closed.", ex);
            }
            catch (DeltaProtocolException ex)
            {
                throw new DeltaReadException(ex.Message, ex);
            }

            // Track the prevailing metadata (item 3): a metaData action in this commit REPLACES it (Delta
            // semantics). At v == start this re-applies start's own metaData (idempotent — already baked into
            // the baseline snapshot); for later versions it advances the schema the cdc validation trusts. We
            // refresh the column-mapping identity in lock-step so the #671 per-version check below is a cheap
            // value compare (the schemaString is re-parsed only when the metadata actually changes).
            foreach (DeltaAction action in actions)
            {
                if (action is MetadataAction updatedMetadata)
                {
                    currentMetadata = updatedMetadata;
                    try
                    {
                        currentIdentity = ColumnMappingIdentity.FromMetadata(updatedMetadata);
                    }
                    catch (Exception ex) when (ex is SchemaValidationException or DeltaProtocolException)
                    {
                        // Same fixed, path-free fail-closed as the pre-loop parse: an in-range version whose
                        // metaData carries an unparseable/non-struct schema OR an unrecognized column-mapping
                        // mode (DeltaProtocolException from ResolveMode) fails the read closed uniformly.
                        throw new DeltaReadException(
                            "A change-feed range's metadata declared an unreadable column-mapping identity "
                            + "(unrecognized mode or malformed/inconsistent schema); the read fails closed.", ex);
                    }

                    // Defense-in-depth behind the parse-level single-metaData guard (DeltaLogActionReader rejects
                    // >1 metaData per commit): validate EVERY applied metaData's identity, not only the last-wins
                    // prevailing one, so a forged-then-reverted identity within a single commit could not slip
                    // past even if that guard were ever relaxed. Redundant while the guard holds (a commit
                    // carries at most one metaData ⇒ the applied metaData IS the prevailing one the post-loop
                    // check re-validates), so this only ever fires uniquely under a mutated/forged parser. Its
                    // message names the "within the commit" origin so a mutation that neuters it is attributable
                    // (removing it makes this scenario fall through to the post-loop backstop with a distinct
                    // message).
                    if (!endIdentity.IsImmutableFrom(currentIdentity))
                    {
                        throw new DeltaReadException(
                            $"The change-feed range crosses a column-mapping identity change at version {v} (a metaData "
                            + "applied within the commit declares an identity that differs from the end of the range); the "
                            + "range is read through a single (end-snapshot) column-mapping identity (mode, field ids, and "
                            + "physical names), so such a transition is not supported and the read fails closed rather "
                            + "than risk emitting mismapped change data.");
                    }
                }
            }

            // Fail closed on a column-mapping IDENTITY transition WITHIN the range (#671, maintainer-broadened
            // from #670's mode-only check). Every version's files (explicit cdc AND implicit add/remove) are
            // read through the END snapshot's column-mapping identity (`ctx` — its mode + physical-name /
            // field-id resolution). A version whose prevailing metadata declares a different mode, a reassigned
            // field id, or a changed physical name would be MISMAPPED (e.g. an id-mode file with swapped
            // field_ids read through the end field-ids, or a name-mode file read through a reassigned physical
            // name — WRONG change rows). Delta column-mapping identity is IMMUTABLE (mode is
            // creation-only/sticky; field ids and physical names are assigned once), so a differing per-version
            // identity is a corrupt/forged `_delta_log`; fail closed rather than emit mismapped change data.
            // Legitimate schema evolution (ADDING a column) is allowed — only columns present in BOTH the
            // version and the end are compared. The #662 EE-08 gate independently validates each EXPLICIT cdc
            // file's leaf schema; this gate covers the implicit add/remove path too. Path-free (#653): only the
            // bounded version is named.
            //
            // SCOPE: this catches an identity change at any version IN `[start, end]`; the COMPLEMENTARY
            // pre-range check (`DeltaLog.LoadChangeFeedStartSnapshotAsync`, run before this loop) covers a
            // file referenced by a `remove` in the range but AUTHORED before `start` under a different identity
            // (implicit-DELETE of a historical file across an identity boundary). Their union is the full
            // retained window (#671). Residual (inherent): an identity change entirely within AGED-OUT history
            // below the earliest reconstructable version is physically unreadable; a still-referenced
            // below-floor file is then read under the uniform retained identity — equivalent to ordinary
            // data-content forgery, no capability beyond the issue's `_delta_log`-write threat model.
            if (!endIdentity.IsImmutableFrom(currentIdentity))
            {
                throw new DeltaReadException(
                    $"The change-feed range crosses a column-mapping identity change at version {v}; the range "
                    + "is read through a single (end-snapshot) column-mapping identity (mode, field ids, and "
                    + "physical names), so such a transition is not supported and the read fails closed rather "
                    + "than risk emitting mismapped change data.");
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
                        // `ctx.ResolveByFieldId` (the END snapshot's mode) drives BOTH the gate and the read,
                        // so they never disagree on mode within a version. A range that crossed a column-mapping
                        // IDENTITY transition — which would mismap a historical version read through the end's
                        // mode / field-ids / physical names — was already failed closed above (the per-version
                        // identity-consistency check, #671), so by here every version in the range provably
                        // shares the end snapshot's column-mapping identity.
                        await ValidateExplicitCdcSchemaAsync(
                            cdc.Path, versionPhysicalDataSchema, v, ctx.ResolveByFieldId, cancellationToken)
                            .ConfigureAwait(false);
                        await foreach (ColumnBatch batch in ReadExplicitFileAsync(
                                cdc, ctx, v, commitMillis, cancellationToken).ConfigureAwait(false))
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

    private async Task<(OutputContext Ctx, DeltaLog.ChangeFeedEndView EndView)> BuildOutputContextAsync(
        DeltaChangeFeedInfo info, CancellationToken cancellationToken)
    {
        // Take the END snapshot AND the single `_delta_log` listing it was reconstructed from, bound together
        // in a `ChangeFeedEndView` only DeltaLog can mint (#691): the pre-range column-mapping identity gate
        // and the start-snapshot load are then driven off that SAME listing rather than re-listing the log
        // twice more, and the pairing is structural rather than a caller convention.
        //
        // Beyond the saved LISTs this is COVERAGE-SAFER: log cleanup only DELETES, so this (earlier) listing's
        // commit set is a superset of any later one's, and the gate can therefore only validate MORE than a
        // freshly listed view would. It is AVAILABILITY-COSTLIER: the listing is now consumed over one long
        // window instead of three short ones, so a commit that concurrent cleanup removes between the listing
        // and the gate's read of it fails the read closed instead of being silently absent from a re-listing.
        // That trade is deliberate — a gate that exists to fail closed on anything it cannot verify should
        // prefer a hard error to a quietly narrowed window.
        (Snapshot end, DeltaLog.ChangeFeedEndView endView) =
            await _log.LoadChangeFeedEndViewAsync(info.EndVersion, cancellationToken).ConfigureAwait(false);
        StructType tableSchema = end.Schema;
        ColumnMappingMode mode = ColumnMapping.ResolveMode(end.Metadata.Configuration);
        bool resolveByFieldId = mode == ColumnMappingMode.Id;
        ImmutableArray<string> partitionColumns = end.Metadata.PartitionColumns;
        string[] physicalNames = ColumnMappingProjection.ResolvePhysicalNames(tableSchema, mode);
        StructType physicalDataSchema =
            ColumnMappingProjection.BuildDataSchema(tableSchema, physicalNames, partitionColumns);
        int[] dataOrdinalByField = ColumnMappingProjection.MapDataOrdinals(physicalNames, physicalDataSchema);
        bool allowTypeWideningPromotion = TypeWideningFeature.Supports(end.Protocol);
        // Return the END VIEW alongside `ctx` so the caller can drive the #671 full-history column-mapping
        // IDENTITY-immutability check — and the start-snapshot load it is fused with (#691) — off the SAME end
        // snapshot's metadata (mode + schema field ids/physical names + partition columns) that `ctx` was
        // derived from, and off the SAME log view. The view carries both, so they cannot be mismatched.
        var ctx = new OutputContext(
            info.Schema, tableSchema, physicalDataSchema, physicalNames, dataOrdinalByField, resolveByFieldId,
            mode, allowTypeWideningPromotion);
        return (ctx, endView);
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
        string path, StructType versionPhysicalDataSchema, long version, bool resolveByFieldId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ParquetFileReader.ParquetLeafColumn> fileColumns;
        try
        {
            Stream stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                // Field-id-aware in id mode (#662): the leaf columns carry their footer field_id so a foreign
                // id-mode cdc file — whose physical names may diverge from the metaData physicalNames — is
                // validated by field_id (the same authority the read resolves by), not by physical name.
                fileColumns = await _reader.ReadDataLeafColumnsAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (DeltaStorageException ex)
        {
            throw ClassifyFileError(ex);
        }

        ValidateCdcLeafSchema(version, versionPhysicalDataSchema, fileColumns, resolveByFieldId);
    }

    // The leaf comparison for CDF-EE-08. In column-mapping <b>id</b> mode the cdc file's data columns are
    // validated by <c>field_id</c> — the Delta id-mode authority (#662): a FOREIGN id-mode cdc file's physical
    // Parquet column names may diverge from the metaData <c>physicalName</c>s, so the footer <c>field_id</c>
    // (not the physical name) is what resolves them, symmetric with the read (which reads that same file by
    // field-id). In name/none mode the columns are validated by physical name + leaf DataType (there are no
    // field_ids to trust). The synthesized `_change_type` column is excluded from the DATA-column comparison
    // (it is engine-owned, carries no field_id, and its VALUE domain is validated separately, per batch, in
    // ValidateChangeTypeColumn) — but its PRESENCE is required: the single-pass read (#658) projects
    // `_change_type` by name alongside the data columns, so a cdc file lacking it is a corrupt/foreign file that
    // is failed closed HERE with a precise "missing `_change_type`" error, rather than reaching the read and
    // surfacing the misleading data-column DeltaReadSchemaEvolutionException ("missing a required column the
    // table schema demands") for an engine column.
    private static void ValidateCdcLeafSchema(
        long version, StructType expected,
        IReadOnlyList<ParquetFileReader.ParquetLeafColumn> fileColumns, bool resolveByFieldId)
    {
        // `_change_type` PRESENCE (the #658 check): the engine-synthesized column is REQUIRED in every cdc body
        // (§2.2) and is EXCLUDED from the data-column comparison below. Its name is a FIXED engine literal —
        // safe to name under #653. Computed once; both mode branches share it.
        bool sawChangeType = false;
        foreach (ParquetFileReader.ParquetLeafColumn column in fileColumns)
        {
            if (string.Equals(column.Name, ChangeDataWriter.ChangeTypeColumn, StringComparison.Ordinal))
            {
                sawChangeType = true;
                break;
            }
        }

        if (resolveByFieldId)
        {
            // id mode: key the file's data columns by footer field_id (the id-mode authority). `_change_type`
            // carries no field_id (skipped); any OTHER data column missing a field_id is anomalous in id mode —
            // counted (bounded) as surplus, never named (#653).
            var fileByFieldId = new Dictionary<int, DataType>();
            int unmappedDataColumns = 0;
            foreach (ParquetFileReader.ParquetLeafColumn column in fileColumns)
            {
                if (string.Equals(column.Name, ChangeDataWriter.ChangeTypeColumn, StringComparison.Ordinal))
                {
                    if (column.FieldId is not null)
                    {
                        // A well-formed cdc file NEVER column-maps `_change_type` (it is engine-written by
                        // literal name and read by name, in both EE-08 presence and the projection). A foreign
                        // file that stamped a field_id on it is malformed — reject it PRECISELY at the gate so
                        // EE-08 stays strictly symmetric with the read (which would otherwise fail it closed
                        // less precisely as a schema-evolution error). Path-free (#653): only the fixed engine
                        // literal is named.
                        throw NewCdcSchemaMismatch(
                            version,
                            $"the engine-synthesized '{ChangeDataWriter.ChangeTypeColumn}' column must not "
                            + "carry a column-mapping field_id");
                    }

                    continue;
                }

                if (column.FieldId is not int fieldId)
                {
                    unmappedDataColumns++;
                    continue;
                }

                if (!fileByFieldId.TryAdd(fieldId, column.Type))
                {
                    // Defense-in-depth, UNREACHABLE via the read path: BuildFieldIdMap (called inside
                    // ReadDataLeafColumnsAsync, before this validator) already rejects a duplicate field_id
                    // fail-closed. Retained (in case that upstream invariant ever changes) but path-free: it
                    // names no file-derived token, only that a duplicate exists (#653).
                    throw NewCdcSchemaMismatch(version, "it declares a data column more than once");
                }
            }

            // `_change_type` PRESENCE is checked BEFORE the data-column comparison — SAME position as the
            // name/none branch below (so a file missing both `_change_type` and a data column reports the
            // engine-column absence first, consistently across modes).
            if (!sawChangeType)
            {
                throw NewCdcSchemaMismatch(
                    version,
                    $"it is missing the engine-synthesized '{ChangeDataWriter.ChangeTypeColumn}' column");
            }

            foreach (StructField expectedField in expected)
            {
                if (!ColumnMapping.TryGetId(expectedField, out long id))
                {
                    // UNREACHABLE for well-formed id-mode metadata: BuildDataSchema preserves each field's
                    // delta.columnMapping.id, so an id-mode version's expected columns always carry one. Fail
                    // closed (never silently fall back to name matching) with a path-free reason (#653).
                    throw NewCdcSchemaMismatch(
                        version, "a version metadata column lacks a column-mapping id");
                }

                // Mirror the read's field-id guard (ParquetFileReader.ResolveFileFields): a column-mapping id
                // outside the Parquet footer's int field_id range can never match a footer field_id, so treat
                // it as MISSING (fail closed) rather than narrowing-cast it into a spurious match. Keeps EE-08's
                // acceptance set identical to the read's.
                if (id is < 0 or > int.MaxValue || !fileByFieldId.TryGetValue((int)id, out DataType? fileType))
                {
                    // The column-mapping id is a version-metadata integer (the trusted authority) — safe to
                    // name; the file's physical name is NOT (#653).
                    throw NewCdcSchemaMismatch(
                        version, $"it is missing the version's data column with column-mapping id {id}");
                }

                if (!expectedField.DataType.Equals(fileType))
                {
                    throw NewCdcSchemaMismatch(
                        version,
                        $"the data column with column-mapping id {id} has leaf type {fileType.SimpleString} but "
                        + $"the version's metadata declares {expectedField.DataType.SimpleString}");
                }

                fileByFieldId.Remove((int)id);
            }

            if (fileByFieldId.Count > 0 || unmappedDataColumns > 0)
            {
                // Message hygiene (#653): surplus columns (by field_id) and field-id-less data columns are both
                // inconsistent with the version's id-mode metadata; report only the bounded COUNT, never a
                // file-derived name/field_id.
                throw NewCdcSchemaMismatch(
                    version,
                    $"it declares {fileByFieldId.Count + unmappedDataColumns} data column(s) absent from the "
                    + "version's metadata schema");
            }

            return;
        }

        // name/none mode: validate by physical name + leaf DataType — the pre-#662 behavior, now consuming the
        // leaf list instead of a StructType.
        var fileByName = new Dictionary<string, DataType>(StringComparer.Ordinal);
        foreach (ParquetFileReader.ParquetLeafColumn column in fileColumns)
        {
            if (string.Equals(column.Name, ChangeDataWriter.ChangeTypeColumn, StringComparison.Ordinal))
            {
                continue;
            }

            if (!fileByName.TryAdd(column.Name, column.Type))
            {
                // Defense-in-depth, UNREACHABLE via the read path: ReadDataLeafColumnsAsync (called before this
                // validator) builds a StructType, whose ctor rejects duplicate field names
                // (SchemaValidationException, STORY-02.5.1 AC2) and is re-mapped to CorruptData — so a
                // duplicate-column cdc file fails closed upstream before EE-08 runs. Retained fail-closed
                // (in case that upstream invariant ever changes) but not durably testable, so — like the
                // other EE-08 messages (#653) — it names no file-derived column, only that a duplicate exists.
                throw NewCdcSchemaMismatch(version, "it declares a data column more than once");
            }
        }

        if (!sawChangeType)
        {
            // The engine-synthesized `_change_type` column is REQUIRED in every cdc file body (§2.2). A file
            // lacking it is corrupt/foreign — fail closed with a precise, path-free message (the column name is
            // a FIXED engine literal, not a file-derived/attacker token, so naming it is safe under #653).
            throw NewCdcSchemaMismatch(
                version,
                $"it is missing the engine-synthesized '{ChangeDataWriter.ChangeTypeColumn}' column");
        }

        foreach (StructField expectedField in expected)
        {
            if (!fileByName.TryGetValue(expectedField.Name, out DataType? fileType))
            {
                throw NewCdcSchemaMismatch(
                    version, $"it is missing the version's data column '{expectedField.Name}'");
            }

            if (!expectedField.DataType.Equals(fileType))
            {
                throw NewCdcSchemaMismatch(
                    version,
                    $"data column '{expectedField.Name}' has leaf type {fileType.SimpleString} but the version's "
                    + $"metadata declares {expectedField.DataType.SimpleString}");
            }

            fileByName.Remove(expectedField.Name);
        }

        if (fileByName.Count > 0)
        {
            // Message hygiene (#653): the surplus column names come from the cdc FILE's schema — for a foreign
            // cdc file they are attacker-authored — so the message reports only the bounded COUNT, never the
            // names. The trusted authority is the version's log-resident metadata (below), not this file.
            throw NewCdcSchemaMismatch(
                version,
                $"it declares {fileByName.Count} data column(s) absent from the version's metadata schema");
        }
    }

    // Message hygiene (#653 / change-data-feed.md:344): the cdc file `path` is attacker-controllable on a
    // hostile log (CDF §5.1, mirrors #516), so it is NOT rendered into the surfaced message; the version
    // (a bounded int) and the caller's `detail` (scrubbed of file-derived tokens) are the only interpolations.
    private static DeltaReadException NewCdcSchemaMismatch(long version, string detail) =>
        new($"A change-data file is inconsistent with version {version}'s schema: {detail}. DeltaSharp "
            + "validates each cdc file's leaf schema against that version's log-resident metadata (the trusted "
            + "authority) before reading, and fails closed on a mismatch (design §3.2 CDF-EE-08).");

    // Explicit path (§2.2): the change rows for this version are EXACTLY the cdc file's rows, each carrying its
    // own per-row `_change_type`. Streams the cdc file row-group by row-group — SYMMETRIC with
    // ReadImplicitFileAsync (#644): a large cdc file is NOT buffered into a per-file List&lt;ColumnBatch&gt;;
    // each row group's batch is yielded as it decodes. Every yielded batch is one row group of one cdc file of
    // one version, so it carries exactly ONE `_commit_version` (INV C8).
    //
    // SINGLE PASS (#658): the data columns are projected ALONGSIDE the engine-synthesized `_change_type` in ONE
    // ReadAsync. The data columns resolve mode-aware (physical name in none/name mode, field-id in id mode);
    // `_change_type` carries no field-id and resolves by NAME through the reader's per-field id/name fallback
    // (#658). Because `_change_type` and the data are columns of the SAME decoded batch, they are INTRINSICALLY
    // aligned row-for-row — this replaces the previous TWO-read form (a full up-front `_change_type` pass + an
    // eager footer-only row-count probe + per-batch/post-loop count-consistency checks). There is no longer a
    // second `OpenReadAsync` whose file could differ mid-read, so the whole TOCTOU-window class (probe↔data
    // open, and the same-row-count replacement the count checks could never see) is CLOSED: the read is
    // fail-closed-before-first-batch structurally, not best-effort. `_change_type` is validated (non-null +
    // within the §5.2 closed domain) per batch before that batch is yielded.
    //
    // Errors are classified before/around the yielding loop (NEVER across a `yield return`, mirroring
    // ReadImplicitFileAsync): the read/classify step (MoveNextAsync + Current) sits in a try that maps a
    // DeltaStorageException → DeltaReadSchemaEvolutionException (narrow-schema-evolution input) or
    // ClassifyFileError; the `_change_type` validation + batch assembly + `yield return` happen OUTSIDE the try.
    private async IAsyncEnumerable<ColumnBatch> ReadExplicitFileAsync(
        AddCdcFileAction cdc, OutputContext ctx, long version, long commitMillis,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Combined single-pass projection (#658): the physical data columns FOLLOWED BY the engine-synthesized
        // `_change_type` column. The data columns resolve mode-aware (physical name / field-id); `_change_type`
        // carries no field-id, so it resolves by NAME through the reader's per-field fallback. `_change_type`
        // sits at the fixed trailing ordinal below.
        StructType physicalWithChangeType = AppendChangeTypeColumn(ctx.PhysicalDataSchema);
        int changeTypeOrdinal = ctx.PhysicalDataSchema.Count;

        Stream stream;
        try
        {
            stream = await _backend.OpenReadAsync(cdc.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaStorageException ex)
        {
            throw ClassifyFileError(ex);
        }

        // STREAMED single pass: decode [data…, `_change_type`] row-group by row-group. `_change_type` is a
        // column of each decoded batch, so it is intrinsically row-aligned to the data — no cumulative-offset
        // bookkeeping or count-consistency check is needed (the two-read form's alignment concern is gone).
        await using (stream.ConfigureAwait(false))
        {
            IAsyncEnumerator<ColumnBatch> enumerator = _reader
                .ReadAsync(
                    stream, physicalWithChangeType, keepRowGroup: null, nullFillMissingColumns: true,
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
                        throw new DeltaReadSchemaEvolutionException(cdc.Path, ex);
                    }
                    catch (DeltaStorageException ex)
                    {
                        throw ClassifyFileError(ex);
                    }

                    // The `_change_type` lane is the trailing column of THIS batch (row-aligned to the data).
                    // Validate its domain (non-null + §5.2 closed set) before relabeling the data to logical.
                    ColumnVector changeType = physical.Column(changeTypeOrdinal);
                    ValidateChangeTypeColumn(changeType, physical.RowCount);

                    // Relabel physical→logical + hydrate partition columns (BuildFullBatch reads only the data
                    // ordinals in ctx.DataOrdinalByField, ignoring the trailing `_change_type` column), then
                    // stamp the per-row `_change_type` + per-version metadata (one `_commit_version`, INV C8).
                    ColumnBatch logical = ColumnMappingProjection.BuildFullBatch(
                        cdc.PartitionValues, ctx.OutputDataSchema, ctx.PhysicalNames, ctx.DataOrdinalByField,
                        physical);
                    ColumnBatch output = AppendMetadataColumns(logical, ctx, changeType, version, commitMillis);
                    yield return output;
                }
            }
        }
    }

    // Validates a cdc file's `_change_type` column (read by name; never column-mapped): each value non-null and
    // within the closed change-type domain (§5.2 — a foreign/tampered writer cannot smuggle an unknown type).
    // Runs per batch in the single-pass explicit read (#658), before the batch is yielded.
    //
    // Message-hygiene group note (#653/cdf:344): Storage cannot redact (SecretRedaction is Core-internal and
    // UNREACHABLE from DeltaSharp.Storage), so the attacker-controllable cdc/change-feed file path is DROPPED,
    // not surfaced, from every fail-closed fault message across the CDF read door (here and in
    // ReadExplicitFileAsync, ReadImplicitFileAsync, and ClassifyFileError). A hostile log controls the cdc
    // `path` (§5.1, mirrors #516), so only the bounded, non-path context (the `_change_type` domain, counts,
    // storage-error kind) is named; the raw cell VALUE is likewise never echoed (obs-conventions: row/cell
    // values never reach an exception); `path` stays purely an I/O argument (OpenReadAsync / ClassifyFileError)
    // and, where one exists, the inner exception.
    private static void ValidateChangeTypeColumn(ColumnVector changeType, int rowCount)
    {
        for (int r = 0; r < rowCount; r++)
        {
            if (changeType.IsNull(r))
            {
                throw new DeltaReadException(
                    $"A change-data file has a null '{ChangeDataWriter.ChangeTypeColumn}' "
                    + "value; a cdc file's change-type column must be non-null.");
            }

            string changeTypeValue = Encoding.UTF8.GetString(changeType.GetBytes(r));
            if (!ChangeDataWriter.ChangeTypeDomain.Contains(changeTypeValue))
            {
                throw new DeltaReadException(
                    $"A change-data file has an unrecognized "
                    + $"'{ChangeDataWriter.ChangeTypeColumn}' value; the legal values "
                    + "are insert / delete / update_preimage / update_postimage.");
            }
        }
    }

    // Appends the engine-synthesized `_change_type` field (StringType, non-nullable, NO column-mapping id — so
    // the reader resolves it by name) to a physical data schema, forming the single-pass explicit-read
    // projection (#658). Mirrors the writer's `ChangeDataWriter.AppendChangeTypeColumn`: the read projection
    // must stay in lockstep with the cdc body layout the writer emits (data columns first, then `_change_type`).
    private static StructType AppendChangeTypeColumn(StructType physicalDataSchema)
    {
        var fields = new List<StructField>(physicalDataSchema.Count + 1);
        for (int i = 0; i < physicalDataSchema.Count; i++)
        {
            fields.Add(physicalDataSchema[i]);
        }

        fields.Add(ChangeTypeField);
        return new StructType(fields);
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
                    // Message hygiene (#653/cdf:344): the attacker-controllable path is dropped, not surfaced
                    // (see the group note above ValidateChangeTypeColumn); only the bounded counts are named.
                    throw new DeltaReadException(
                        $"A change-feed file declares stats.numRecords={declared} but its Parquet file "
                        + $"contains {physicalRecords} physical row(s); a deletion-vector-carrying file's "
                        + "numRecords must equal the physical row count, so the read fails closed.");
                }

                long[] positions = await DeletionVectorStore
                    .LoadAsync(_backend, descriptor, physicalRecords, cancellationToken).ConfigureAwait(false);
                mask = new DeletionVectorMask(positions, physicalRecords);
            }
            catch (DeltaStorageException ex)
            {
                throw ClassifyFileError(ex);
            }
        }

        Stream stream;
        try
        {
            stream = await _backend.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (DeltaStorageException ex)
        {
            throw ClassifyFileError(ex);
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
                        throw ClassifyFileError(ex);
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
        mask?.EnsureConsumed(fileRowOffset);
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

    // Message hygiene (#653/cdf:344): Storage cannot redact (SecretRedaction is Core-internal), so the
    // attacker-controllable cdc/change-feed file path is DROPPED, not surfaced. The NotFound branch renders a
    // fixed message; the non-NotFound branch must NOT forward `ex.Message` — a PathNotConfined fault (and
    // others) names the rejected path in its message, so passing it through would re-surface the attacker cdc
    // path. Instead it names only the bounded storage-error KIND (a closed enum); the inner exception still
    // carries the full fault for server-side diagnostics (its ToString()/log exposure is tracked in #664).
    private static DeltaReadException ClassifyFileError(DeltaStorageException ex) =>
        ex.Kind == StorageErrorKind.NotFound
            ? new DeltaReadException(
                "A change-feed file is no longer available (vacuumed, or past the data-retention "
                + "window); the requested change-feed range is outside the CDF-readable window.", ex)
            : new DeltaReadException(
                $"A change-feed file could not be read (storage fault: {ex.Kind}); the requested "
                + "change-feed range failed closed.", ex);

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
        ColumnMappingMode Mode,
        bool AllowTypeWideningPromotion);
}
