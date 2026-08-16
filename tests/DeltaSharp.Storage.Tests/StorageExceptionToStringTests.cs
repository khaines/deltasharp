using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using DeltaSharp.Storage.Backends;
using DeltaSharp.Storage.Delta;
using DeltaSharp.Storage.Writing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DeltaSharp.Storage.Tests;

/// <summary>
/// #664 (RF-8b parity): storage decode/validation exceptions surface an <see cref="Exception.Message"/>
/// while retaining the raw underlying cause as the inner for server-side diagnostics. Message hygiene varies
/// by producer, so every storage message is treated as untrusted. Their <see cref="object.ToString"/>
/// overrides must render the surfaced message (+ Kind) and the exception's own stack trace but NEVER the
/// <see cref="Exception.InnerException"/> chain — so a sink that logs <c>ex.ToString()</c> (or an
/// <c>ILogger</c> provider that renders <c>ToString()</c> once and does not walk the chain itself) cannot
/// re-surface the raw inner that the surfaced message omitted. The inner stays reachable via
/// <see cref="Exception.InnerException"/>.
/// <para>
/// #694 review: every guard here must assert <b>behaviour</b>, not a structural proxy for it. Three
/// successive versions of this file failed open because they asserted a proxy — a constructor shape, a
/// self-classification, a declared-but-unverified override. The load-bearing guard is therefore
/// <see cref="EveryExceptionType_IsConstructed_Thrown_AndIfItCarriesAnInner_RendersNeitherItsMessageNorItsTrace"/>,
/// which builds and <b>throws</b> an instance of every exception type in the assembly and asserts the
/// rendered text against the actual inner object. The name-pinned lists below are kept because each is
/// cross-checked against an <i>independent</i> reflective derivation, so a consistent downgrade of one
/// still fails the other.
/// </para>
/// </summary>
public sealed class StorageExceptionToStringTests
{
    private const string RawInnerLeak = "RAW-INNER-LEAK\r\ncrafted-bytes-0xDEADBEEF\u2028more";

    /// <summary>The probe message given to every reflectively constructed instance, distinct from
    /// <see cref="RawInnerLeak"/> so a rendered outer message is never mistaken for a leaked inner one.</summary>
    private const string ProbeSurfaceMessage =
        "probe-raw-state\r\ncrafted\u2028"
        + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>The operator-facing page whose tables these guards keep honest.</summary>
    private const string LogRoutingDocRelativePath =
        "docs/engineering/design/storage-exception-log-routing.md";

    /// <summary>
    /// The covered set published by <c>docs/engineering/design/storage-exception-log-routing.md</c>: the types
    /// that retain a raw <see cref="Exception.InnerException"/> and therefore override <c>ToString()</c>.
    /// Shared by the drift guards below so they cannot disagree with each other, or with the doc, about which
    /// types the operator-facing contract names.
    /// </summary>
    private static readonly string[] CoveredTypeNames =
    [
        nameof(DeltaCommitUnknownStateException),
        nameof(DeltaProtocolException),
        nameof(DeltaReadException),
        nameof(DeltaReadSchemaEvolutionException),
        nameof(DeltaStorageException),
        nameof(OptimizeSchemaEvolutionException),
    ];

    /// <summary>
    /// Reference-state paths that are deliberately sanitized or derived rather than raw. This is empty today,
    /// but provides an explicit, behavior-checked classification lane for a future hardening change: a path
    /// cannot be listed here while it still retains the hostile probe.
    /// </summary>
    private static readonly string[] SanitizedOrDerivedStatePaths = [];

    private static readonly string[] ExpectedRawStateEntries =
    [
        "DeltaCommitUnknownStateException.InnerException",
        "DeltaConstraintDependentColumnException.ColumnName",
        "DeltaConstraintDependentColumnException.Constraints[].Expression",
        "DeltaConstraintDependentColumnException.Constraints[].Name",
        "DeltaConstraintViolationException.Constraint.Expression",
        "DeltaConstraintViolationException.Constraint.Name",
        "DeltaProtocolException.InnerException",
        "DeltaReadException.InnerException",
        "DeltaReadSchemaEvolutionException.FilePath",
        "DeltaReadSchemaEvolutionException.InnerException",
        "DeltaSchemaMismatchException.Path",
        "DeltaStorageException.InnerException",
        "DeltaStorageException.Path",
        "OptimizeSchemaEvolutionException.FilePath",
        "OptimizeSchemaEvolutionException.InnerException",
    ];

    private static readonly string[] DocumentedMessagePostureRows =
    [
        "DeltaStorageException.ColumnNotPresentInFile.columnName:sanitized",
        "LocalFileSystemBackend.OpenReadAsync.missingPath:redacted",
        "LocalFileSystemBackend.SurfaceFailure.detail:sanitized",
        "LocalFileSystemBackend.SurfaceFailure.path:redacted",
    ];

    private static readonly string[] SolutionProjectPaths =
    [
        "samples/DeltaSharp.Samples.GettingStarted/DeltaSharp.Samples.GettingStarted.csproj",
        "src/DeltaSharp.Abstractions/DeltaSharp.Abstractions.csproj",
        "src/DeltaSharp.Core/DeltaSharp.Core.csproj",
        "src/DeltaSharp.Engine/DeltaSharp.Engine.csproj",
        "src/DeltaSharp.Executor/DeltaSharp.Executor.csproj",
        "src/DeltaSharp.Storage/DeltaSharp.Storage.csproj",
        "tests/DeltaSharp.Abstractions.Tests/DeltaSharp.Abstractions.Tests.csproj",
        "tests/DeltaSharp.Core.Tests/DeltaSharp.Core.Tests.csproj",
        "tests/DeltaSharp.Engine.Tests/DeltaSharp.Engine.Tests.csproj",
        "tests/DeltaSharp.Executor.Tests/DeltaSharp.Executor.Tests.csproj",
        "tests/DeltaSharp.Storage.Tests/DeltaSharp.Storage.Tests.csproj",
    ];

    private static long _tempDirectoryOrdinal;

    /// <summary>
    /// #694 finding 1 (Architect). The property that matters for a REFLECTING sink is not "does this type
    /// chain an inner" but "does this type retain <b>reflection-reachable unsanitized state</b>" — which is
    /// <c>InnerException</c> ∪ the typed properties. The previous doc cleared
    /// <c>DeltaConstraintViolationException</c>, <c>DeltaConstraintDependentColumnException</c>, and
    /// <c>DeltaSchemaMismatchException</c> as "inner-free, so there is no retained raw text for a sink to
    /// reach", but all three keep the raw, deliberately-unsanitized token on a typed property
    /// (<c>.Constraint</c>, <c>.ColumnName</c>/<c>.Constraints</c>, <c>.Path</c>) that a public-property
    /// destructurer surfaces verbatim, CR/LF and U+2028 intact. This is the union set the doc's
    /// "reflection-reachable state" table publishes.
    /// </summary>
    private static readonly string[] RawStateBearingTypeNames =
    [
        nameof(DeltaCommitUnknownStateException),
        nameof(DeltaConstraintDependentColumnException),
        nameof(DeltaConstraintViolationException),
        nameof(DeltaProtocolException),
        nameof(DeltaReadException),
        nameof(DeltaReadSchemaEvolutionException),
        nameof(DeltaSchemaMismatchException),
        nameof(DeltaStorageException),
        nameof(OptimizeSchemaEvolutionException),
    ];

    /// <summary>
    /// The pinned inventory of every DECLARED instance property on every storage exception type — i.e. exactly
    /// what a reflecting sink can reach beyond <see cref="Exception"/>'s own surface, with the accessibility
    /// that decides whether a public-binding destructurer reaches it. Adding, removing, renaming, or
    /// re-scoping a property fails here, which is the same edit that must classify it in the doc's
    /// reflection-reachable-state table.
    /// </summary>
    private static readonly string[] DeclaredExceptionState =
    [
        "DeltaCommitContentionException.MaxAttempts:public:Int32",
        "DeltaCommitContentionException.Version:public:Int64",
        "DeltaCommitUnknownStateException.Version:public:Int64",
        "DeltaConcurrentModificationException.Kind:public:DeltaConflictKind",
        "DeltaConstraintDependentColumnException.ColumnName:public:String",
        "DeltaConstraintDependentColumnException.Constraints:public:IReadOnlyList`1",
        "DeltaConstraintViolationException.Constraint:public:DeltaTableConstraint",
        "DeltaProtocolException.Kind:public:DeltaProtocolErrorKind",
        "DeltaReadSchemaEvolutionException.FilePath:public:String",
        "DeltaSchemaMismatchException.Kind:public:DeltaSchemaMismatchKind",
        "DeltaSchemaMismatchException.Path:public:String",
        "DeltaStorageException.Kind:public:StorageErrorKind",
        "DeltaStorageException.Path:public:String",
        "OptimizeColumnMappingUnsupportedException.Mode:internal:ColumnMappingMode",
        "OptimizeSchemaEvolutionException.FilePath:internal:String",
        "VacuumRetentionSafetyException.RequestedRetention:internal:TimeSpan",
        "VacuumRetentionSafetyException.SafetyThreshold:internal:TimeSpan",
    ];

    /// <summary>
    /// Every source-generated storage log-site signature. This is an exact population rather than a lower
    /// bound: adding, removing, or widening one site forces a review of whether it can carry exception state.
    /// </summary>
    private static readonly string[] StorageLogSiteSignatures =
    [
        "DeltaCheckpointLog.CheckpointDecodeTimeout(ILogger logger, Int64 version)",
        "DeltaCheckpointLog.CheckpointDecoderSaturated(ILogger logger, Int64 version)",
        "DeltaCheckpointLog.CheckpointFallback(ILogger logger, Int64 version, String reason)",
        "DeltaCheckpointLog.CheckpointForgedMultiMetadataRejected(ILogger logger, Int64 version)",
        "DeltaCheckpointLog.CheckpointNegativeCacheSkip(ILogger logger, Int64 version)",
        "DeltaCheckpointLog.CheckpointSelectionSkipped(ILogger logger, Int64 version)",
        "DeltaCommitLog.CommitCanceled(ILogger logger, Int64 version, Int32 attempts)",
        "DeltaCommitLog.CommitCompleted(ILogger logger, Int64 version, Int32 attempts, Double durationMs)",
        "DeltaCommitLog.CommitConflict(ILogger logger, Int32 attempt, Int64 targetVersion, String conflictClass)",
        "DeltaCommitLog.CommitContentionExhausted(ILogger logger, Int64 version, Int32 maxAttempts)",
        "DeltaCommitLog.CommitFailed(ILogger logger, Int64 version, Int32 attempts, String exceptionType)",
        "DeltaCommitLog.CommitPartialTransaction(ILogger logger, Int32 committedCount, Int32 uncommittedCount)",
        "DeltaCommitLog.CommitRetry(ILogger logger, Int32 attempt, Int64 targetVersion, String reason, Int32 rebaseCount)",
        "DeltaCommitLog.CommitSkipped(ILogger logger, Int64 version, String reason)",
        "DeltaCommitLog.CommitStarted(ILogger logger, Int64 targetVersion, String backend)",
        "DeltaCommitLog.CommitTransientRetry(ILogger logger, Int32 retry)",
        "DeltaCommitLog.CommitUnknownState(ILogger logger, Int64 version)",
        "DeltaDecodeLog.DoorUnderProvisioned(ILogger logger, String door, Int64 maxFootprintBytes, Int64 residualBudgetBytes, Int64 processMemoryBytes)",
        "DeltaDeleteLog.DeleteAborted(ILogger logger, String exceptionType)",
        "DeltaDeleteLog.DeleteCanceled(ILogger logger)",
        "DeltaDeleteLog.DeleteCompleted(ILogger logger, Int64 readVersion, Int64 committedVersion, Int64 rowsDeleted, Int32 filesWithDeletionVector, Double durationMs)",
        "DeltaDeleteLog.DeleteFailed(ILogger logger, String exceptionType)",
        "DeltaDeleteLog.DeleteNoOp(ILogger logger, Int64 readVersion, Double durationMs)",
        "DeltaDeleteLog.DeleteStarted(ILogger logger, String backend)",
        "DeltaOptimizeLog.OptimizeAborted(ILogger logger, String exceptionType)",
        "DeltaOptimizeLog.OptimizeCanceled(ILogger logger)",
        "DeltaOptimizeLog.OptimizeCompleted(ILogger logger, Int64 readVersion, Int64 committedVersion, Int32 filesRemoved, Int32 filesAdded, Boolean dryRun, Double durationMs)",
        "DeltaOptimizeLog.OptimizeFailed(ILogger logger, String exceptionType)",
        "DeltaOptimizeLog.OptimizeNoOp(ILogger logger, Int64 readVersion, Double durationMs)",
        "DeltaOptimizeLog.OptimizeStarted(ILogger logger, String backend, Int64 targetBytes, Boolean dryRun)",
        "DeltaVacuumLog.VacuumCanceled(ILogger logger)",
        "DeltaVacuumLog.VacuumAbortedStaleListing(ILogger logger, Int64 listedVersion, Int64 resolvedVersion)",
        "DeltaVacuumLog.VacuumCandidateDecisionCore(ILogger logger, String candidateDescription, String decision, Boolean deleted)",
        "DeltaVacuumLog.VacuumCdcScanCompleted(ILogger logger, Int32 commitsScanned, Double durationMs, Boolean completed)",
        "DeltaVacuumLog.VacuumCdcScanSkipped(ILogger logger, Int32 inWindowCommits, Int64 provenSpan)",
        "DeltaVacuumLog.VacuumCompleted(ILogger logger, Int64 version, Int32 candidateCount, Int32 deletableCount, Int32 deletedCount, Boolean dryRun, Double durationMs)",
        "DeltaVacuumLog.VacuumFailed(ILogger logger, String exceptionType)",
        "DeltaVacuumLog.VacuumRejectedRetention(ILogger logger, Double requestedHours, Double thresholdHours)",
        "DeltaVacuumLog.VacuumStarted(ILogger logger, String backend, Double retentionHours, Boolean dryRun, Boolean unsafeOverride)",
        "DeltaVacuumLog.VacuumWeakSafetyThreshold(ILogger logger, Double thresholdHours, Double defaultHours)",
    ];

    [Fact]
    public void DeltaStorageException_ToString_OmitsRawInner_KeepsSanitizedMessageAndKind()
    {
        var raw = new InvalidOperationException(RawInnerLeak);
        DeltaStorageException ex = DeltaStorageException.CorruptData("Parquet footer is malformed.", raw);

        string rendered = ex.ToString();

        Assert.Contains("Parquet footer is malformed.", rendered, StringComparison.Ordinal); // surfaced message kept
        Assert.Contains(nameof(StorageErrorKind.CorruptData), rendered, StringComparison.Ordinal); // Kind kept
        Assert.Contains(nameof(DeltaStorageException), rendered, StringComparison.Ordinal); // type name kept
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal); // raw inner NOT surfaced
        Assert.DoesNotContain("crafted-bytes-0xDEADBEEF", rendered, StringComparison.Ordinal);
        // Assert on the INJECTED sequence, not on '\r' alone: DescribeWithoutInner joins the trace with
        // Environment.NewLine, which IS "\r\n" on Windows, so a bare DoesNotContain('\r') would fail there
        // for a reason that has nothing to do with the leak it is meant to catch (CI is ubuntu-latest today).
        Assert.DoesNotContain("\r\ncrafted", rendered, StringComparison.Ordinal);
        // The raw cause is RETAINED for server-side diagnostics — omitted from ToString(), not discarded.
        Assert.Same(raw, ex.InnerException);
        Assert.Equal(RawInnerLeak, ex.InnerException!.Message);
    }

    [Fact]
    public void DeltaProtocolException_ToString_OmitsRawInner_KeepsSanitizedMessageAndKind()
    {
        var raw = new JsonException(RawInnerLeak);
        DeltaProtocolException ex = DeltaProtocolException.Malformed(
            "A Delta add.stats value is not valid JSON.", raw);

        string rendered = ex.ToString();

        Assert.Contains("A Delta add.stats value is not valid JSON.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaProtocolErrorKind.MalformedAction), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("crafted-bytes-0xDEADBEEF", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\ncrafted", rendered, StringComparison.Ordinal);
        Assert.Same(raw, ex.InnerException);
    }

    [Fact]
    public void DeltaReadException_ToString_OmitsEntireChain_DownToRawParquetInner()
    {
        // The read facade re-wraps a storage exception whose OWN inner is the raw cause. ToString() must omit
        // the whole chain — neither the intermediate storage message nor the deepest raw inner may surface.
        var raw = new InvalidOperationException(RawInnerLeak);
        DeltaStorageException storage = DeltaStorageException.CorruptData("Parquet footer is malformed.", raw);
        var read = new DeltaReadException("The table could not be read.", storage);

        string rendered = read.ToString();

        Assert.Contains("The table could not be read.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaReadException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal); // deepest raw inner absent
        Assert.DoesNotContain("Parquet footer is malformed.", rendered, StringComparison.Ordinal); // intermediate inner absent
        Assert.DoesNotContain("\r\ncrafted", rendered, StringComparison.Ordinal);
        // Chain retained for diagnostics: read -> storage -> raw.
        Assert.Same(storage, read.InnerException);
        Assert.Same(raw, read.InnerException!.InnerException);
    }

    [Fact]
    public void DeltaReadException_ToString_WithNoInner_RendersMessage()
    {
        var read = new DeltaReadException("The path is not a Delta table.");

        string rendered = read.ToString();

        Assert.Contains("The path is not a Delta table.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaReadException), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DeltaReadSchemaEvolutionException_ToString_OmitsInner_KeepsPathOnProperty()
    {
        // The attacker-controllable data-file path is kept only on .FilePath (never in the message); the
        // originating storage exception is the inner and must not be auto-rendered.
        const string poisonedPath = "s3://evil/\r\ninjected/file.parquet";
        var inner = DeltaStorageException.ColumnNotPresentInFile("secret_col");
        var ex = new DeltaReadSchemaEvolutionException(poisonedPath, inner);

        string rendered = ex.ToString();

        Assert.Contains(nameof(DeltaReadSchemaEvolutionException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(poisonedPath, rendered, StringComparison.Ordinal); // path only on .FilePath
        Assert.DoesNotContain("secret_col", rendered, StringComparison.Ordinal); // inner not auto-rendered
        Assert.DoesNotContain("\r\ninjected", rendered, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(poisonedPath, ex.FilePath); // raw path retained on the typed property
    }

    [Fact]
    public void DeltaCommitUnknownStateException_ToString_OmitsRawInner_KeepsMessage()
    {
        var raw = new InvalidOperationException(RawInnerLeak);
        var ex = new DeltaCommitUnknownStateException(42, "The commit outcome could not be resolved.", raw);

        string rendered = ex.ToString();

        Assert.Contains("The commit outcome could not be resolved.", rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(DeltaCommitUnknownStateException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\ncrafted", rendered, StringComparison.Ordinal);
        Assert.Same(raw, ex.InnerException);
        Assert.Equal(42, ex.Version);
    }

    [Fact]
    public void OptimizeSchemaEvolutionException_ToString_OmitsInner_KeepsPathOnProperty()
    {
        // Red-team R1: OPTIMIZE's narrow-file failure chains the raw storage exception and had no override.
        const string poisonedPath = "s3://evil/\r\ninjected/opt.parquet";
        DeltaStorageException inner = DeltaStorageException.ColumnNotPresentInFile("secret_col_leak");
        var ex = new OptimizeSchemaEvolutionException(poisonedPath, inner);

        string rendered = ex.ToString();

        Assert.Contains(nameof(OptimizeSchemaEvolutionException), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(poisonedPath, rendered, StringComparison.Ordinal); // path only on .FilePath
        Assert.DoesNotContain("secret_col_leak", rendered, StringComparison.Ordinal); // inner not auto-rendered
        Assert.DoesNotContain("\r\ninjected", rendered, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(poisonedPath, ex.FilePath); // raw path retained on the typed property
    }

    [Fact]
    public void CoveredException_WrappedInOuterOrAggregate_ToString_TransitivelyOmitsRawInner()
    {
        // Production reality: these exceptions are usually caught-and-wrapped or framework-logged, not
        // ToString()'d directly. Exception.ToString() recurses into the inner via the inner's OWN (overridden)
        // ToString(), so a covered storage exception nested in a plain outer exception or an
        // AggregateException must STILL suppress its raw inner — the load-bearing transitive-virtual-dispatch
        // behavior the default ILogger/OTel rendering relies on (SRE R1). Nothing else locks this.
        var raw = new InvalidOperationException(RawInnerLeak);
        DeltaStorageException storage = DeltaStorageException.CorruptData("Parquet footer is malformed.", raw);

        // (a) wrapped in a plain outer exception (caught-and-rethrown) — the covered layer's surfaced message
        // surfaces, but its raw inner does not.
        var wrapped = new InvalidOperationException("outer read step failed", storage);
        string wrappedRendered = wrapped.ToString();
        Assert.Contains("Parquet footer is malformed.", wrappedRendered, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", wrappedRendered, StringComparison.Ordinal);
        Assert.DoesNotContain("crafted-bytes-0xDEADBEEF", wrappedRendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\ncrafted", wrappedRendered, StringComparison.Ordinal);

        // (b) AggregateException + (c) its Flatten() (the Task / parallel pattern, common before logging).
        var aggregate = new AggregateException(storage);
        Assert.DoesNotContain("RAW-INNER-LEAK", aggregate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\ncrafted", aggregate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-INNER-LEAK", aggregate.Flatten().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InnerChainingExceptionTypes_AreExactlyTheDocumentedCoveredSet_AndOverrideToString()
    {
        // #689 drift guard. docs/engineering/design/storage-exception-log-routing.md publishes the covered-type
        // list to an operator wiring log routing, so the list must not be able to go stale silently. The
        // enforceable invariant is decidable from this assembly alone: every DeltaSharp.Storage exception type
        // that CAN chain an inner (i.e. declares a constructor taking an Exception) must declare its own
        // ToString() override, and the set must be exactly the documented six. A seventh inner-chaining type
        // fails here until it is covered AND the doc's table is updated. (The complementary sink-side rule —
        // "never reflect over .InnerException" — is a consumer-side obligation in a compilation this repo does
        // not build, which is why it is documented rather than analyzed; see the doc's enforcement section.)
        Type[] innerChaining = typeof(DeltaStorageException).Assembly.GetTypes()
            .Where(type => typeof(Exception).IsAssignableFrom(type))
            .Where(type => type
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(ctor => ctor.GetParameters()
                    .Any(parameter => typeof(Exception).IsAssignableFrom(parameter.ParameterType))))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(CoveredTypeNames, innerChaining.Select(type => type.Name).ToArray());

        foreach (Type type in innerChaining)
        {
            MethodInfo? toString = type.GetMethod(
                nameof(ToString),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);

            // DeclaringType == the type itself proves the override is declared here, not inherited from
            // Exception (whose default ToString() renders the whole InnerException chain).
            Assert.Equal(type, toString?.DeclaringType);
        }
    }

    [Fact]
    public void EveryStorageExceptionType_IsClassifiedAsCoveredOrInnerFree()
    {
        // #689 review follow-up: the ctor-shape predicate above is a PROXY, and it has a known blind spot —
        // a type can chain a SYNTHETIC inner without ever declaring an Exception parameter
        // (`base(message, new IOException(detail))`, the LocalFileSystemBackend.SurfaceFailure / RF-8b shape).
        // "Can chain an inner" is a dataflow property, not a structural one, so no reflection predicate
        // decides it. This guard closes the dominant drift mode instead: a NEW exception type of ANY
        // constructor shape fails until it is deliberately classified. The full inventory of exception types
        // in the assembly must equal the covered set (which overrides ToString()) plus this explicit
        // inner-free list, whose members docs/engineering/design/storage-exception-log-routing.md names in
        // "Every type that retains reflection-reachable unsanitized state".
        //
        // #694: "inner-free" is a statement about the InnerException HALF only. Three of these types
        // (DeltaConstraintViolationException, DeltaConstraintDependentColumnException,
        // DeltaSchemaMismatchException) still retain raw attacker text on typed properties, so they are NOT
        // safe to destructure — that half is classified by
        // ReflectionReachableExceptionState_IsPinned_AndDerivesTheDocumentedRawStateSet. The synthetic-inner
        // residual is now largely covered behaviourally by
        // EveryExceptionType_IsConstructed_Thrown_AndIfItCarriesAnInner_RendersNeitherItsMessageNorItsTrace.
        string[] innerFree =
        [
            nameof(ConcurrentAppendException),
            nameof(ConcurrentDeleteReadException),
            nameof(ConcurrentTransactionException),
            nameof(DecodeCapacityExhaustedException),
            nameof(DeltaCommitContentionException),
            nameof(DeltaConcurrentModificationException),
            nameof(DeltaConstraintDependentColumnException),
            nameof(DeltaConstraintViolationException),
            nameof(DeltaSchemaMismatchException),
            nameof(MetadataChangedException),
            nameof(OptimizeColumnMappingUnsupportedException),
            nameof(PartialTransactionException),
            nameof(ProtocolChangedException),
            nameof(VacuumRetentionSafetyException),
        ];

        Type[] exceptionTypes = typeof(DeltaStorageException).Assembly.GetTypes()
            .Where(type => typeof(Exception).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        string[] covered = exceptionTypes
            .Where(type => type.GetMethod(
                nameof(ToString),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)?.DeclaringType == type)
            .Select(type => type.Name)
            .ToArray();

        // The covered bucket is derived DYNAMICALLY ("declares its own ToString()"), so without this line a
        // new type that declares an override but takes no Exception ctor parameter would self-classify and
        // escape both guards — safe (it carries the override) but silently absent from the doc's six-row
        // table. Pinning covered to the published names closes that: the type must be named here, which is
        // the same edit that reminds you to add the row.
        Assert.Equal(CoveredTypeNames, covered);

        // No type may be in both buckets, and together they must account for every exception type.
        Assert.Empty(covered.Intersect(innerFree, StringComparer.Ordinal));
        Assert.Equal(
            exceptionTypes.Select(type => type.Name).ToArray(),
            covered.Concat(innerFree).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryExceptionType_IsConstructed_Thrown_AndIfItCarriesAnInner_RendersNeitherItsMessageNorItsTrace()
    {
        // #694 findings 3 + 4 — the ONE guard that asserts behaviour instead of a proxy for it.
        //
        //   * The two guards above check that an override is DECLARED and that a name is LISTED. Neither
        //     checks that the override actually OMITS the inner, so a type whose override is
        //     `ToString() => base.ToString()` passed both while rendering the raw chain (mutant M3), and a
        //     type chaining a synthetic inner passed by being added to the inner-free list (mutant M6).
        //   * Every other test in this file CONSTRUCTS its subject, so StackTrace is always null and
        //     DiagnosticText.DescribeWithoutInner's `if (exception.StackTrace is { } stackTrace)` branch —
        //     the ONLY branch reached in production — was never executed by any test (mutant M5).
        //
        // This guard therefore reflectively builds an instance of EVERY exception type in the assembly (no
        // hand-list, so a new type is covered the moment it compiles), THROWS it so the stack-trace branch is
        // live, and asserts the rendered text against the actual inner object rather than against a literal.
        Type[] exceptionTypes = StorageExceptionTypes();
        Exception rawInner = ThrowFromInnerCauseFrame(new InvalidOperationException(RawInnerLeak));

        int constructed = 0;
        var innerBearingTypes = new List<string>();
        var unconstructible = new List<string>();

        foreach (Type type in exceptionTypes)
        {
            if (type.IsAbstract)
            {
                // An abstract base is never instantiated, so no sink ever renders one; its concrete
                // subclasses are separate entries in this same inventory and ARE built below.
                continue;
            }

            if (!TryConstruct(type, rawInner, out Exception? instance))
            {
                // Fail LOUDLY rather than skipping: a type this harness cannot build is a type it cannot
                // check, which is precisely the fail-open shape that let M3/M6 through.
                unconstructible.Add(type.Name);
                continue;
            }

            constructed++;
            Exception surfaced = ThrowFromCoveredSurfaceFrame(instance!);
            if (surfaced.InnerException is null)
            {
                continue;
            }

            innerBearingTypes.Add(type.Name);
            string rendered = surfaced.ToString();

            // (a) The behavioural invariant: no ancestor's message may appear in the render. Asserted against
            // inner.Message (the live object), NOT against a literal, so it holds for any future inner.
            for (Exception? inner = surfaced.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (surfaced.Message.Contains(inner.Message, StringComparison.Ordinal))
                {
                    // The property that matters is "the render discloses nothing the SANITIZED SURFACE did
                    // not", not "these two strings never coincide". No shipping type has this shape; the
                    // carve-out exists so the oracle cannot false-positive on a future one, and check (b)
                    // below still catches a leak here because a leaking override also appends the inner's
                    // stack trace, which carries a distinct frame name.
                    continue;
                }

                Assert.DoesNotContain(inner.Message, rendered, StringComparison.Ordinal);
            }

            // (b) The stack-trace branch RAN, and the trace it appended is this exception's OWN. The inner was
            // thrown from a differently-named frame, so an override that appends the inner's trace (or the
            // inner object) is caught here even if the inner's message happened to be empty.
            Assert.Contains(nameof(ThrowFromCoveredSurfaceFrame), rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(ThrowFromInnerCauseFrame), rendered, StringComparison.Ordinal);

            // (c) The sanitized surface is still rendered — the override suppresses the inner, not everything.
            Assert.StartsWith($"{type}: ", rendered, StringComparison.Ordinal);
        }

        Assert.Empty(unconstructible);
        Assert.Equal(exceptionTypes.Count(type => !type.IsAbstract), constructed);

        // Exact identities, not a count: a covered type losing its inner while a different type gains one
        // must not cancel out and leave the oracle green.
        Assert.Equal(CoveredTypeNames, innerBearingTypes);
    }

    [Fact]
    public void ReflectionReachableExceptionState_IsPinned_AndDerivesTheDocumentedRawStateSet()
    {
        // #694 finding 1 (Architect). The doc used to clear three types as "not covered and do not need to be:
        // they construct with base(message) only, so there is no retained raw text for a sink to reach." That
        // clearance was FALSE: DeltaConstraintViolationException.Constraint,
        // DeltaConstraintDependentColumnException.ColumnName/.Constraints, and DeltaSchemaMismatchException.Path
        // all retain the raw, deliberately-unsanitized token on a PUBLIC typed property. Measured through a
        // real Serilog `{@Ex}` destructurer: .Message carries neither CR nor U+2028, while the destructured
        // JSON carries both — and puts the raw value in the FIRST key of the object.
        //
        // So the classification axis is re-keyed from "chains an inner" onto "retains reflection-reachable
        // unsanitized state" = InnerException ∪ typed properties. Both halves are observed from constructed
        // objects: the actual InnerException, plus every recursively reachable property path that retains its
        // distinct synthesized probe value. The observed paths must exactly equal the independent structural
        // property walk, so a new reference-bearing DTO/collection cannot disappear through a hand-list edit.
        Type[] exceptionTypes = StorageExceptionTypes();

        string[] actualState = exceptionTypes
            .SelectMany(type => DeclaredInstanceProperties(type).Select(property => string.Concat(
                type.Name,
                ".",
                property.Name,
                property.GetMethod?.IsPublic == true ? ":public:" : ":internal:",
                property.PropertyType.Name)))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        // Pin 1: the reachable surface itself. A new/renamed/re-scoped property fails here.
        Assert.Equal(DeclaredExceptionState, actualState);

        // Public fields are also traversed by public-member serializers. Forbid them on exception types so a
        // field cannot bypass the property inventory and its operator-facing table.
        string[] publicFields = exceptionTypes
            .SelectMany(type => type.GetFields(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(field => $"{field.DeclaringType?.Name}.{field.Name}:{field.FieldType.Name}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(publicFields);

        string[] derivedRawStateEntries = ReflectionReachableRawStateEntries(exceptionTypes);
        Assert.Equal(ExpectedRawStateEntries, derivedRawStateEntries);
        string[] derivedRawState = derivedRawStateEntries
            .Select(entry => entry[..entry.IndexOf('.', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Pin 2: the independent derivation must reproduce the published set exactly.
        Assert.Equal(RawStateBearingTypeNames, derivedRawState);

        // Every type that retains a raw inner necessarily retains reflection-reachable raw state, so the
        // covered set must be a subset. (The converse is false: three raw-state types carry no inner at all —
        // the whole point of finding 1, so assert that the difference is non-empty rather than letting the
        // two sets quietly collapse back into one.) Assert the exact per-type difference; an existential
        // NotEmpty check would still pass if one of the three typed-property leaks disappeared from coverage.
        Assert.Empty(CoveredTypeNames.Except(RawStateBearingTypeNames, StringComparer.Ordinal));
        Assert.Equal(
            [
                nameof(DeltaConstraintDependentColumnException),
                nameof(DeltaConstraintViolationException),
                nameof(DeltaSchemaMismatchException),
            ],
            RawStateBearingTypeNames.Except(CoveredTypeNames, StringComparer.Ordinal));
    }

    [Fact]
    public void LogRoutingDoc_CoveredAndRawStateTables_MatchTheCompiledInventories()
    {
        // #694 finding 5 (Quality). The doc claimed "a seventh exception type fails the build until it is
        // classified AND this page is updated". Nothing read the doc, so the second half was false: adding a
        // type plus one line to CoveredTypeNames went green with the table still showing six rows. The table
        // became safety-load-bearing once finding 1 re-keyed the contract onto reflection-reachable state, so
        // the honest fix is to EXECUTE the claim rather than delete it. The doc marks each table with an
        // explicit machine-read boundary naming this test, so an editor can see the coupling.
        string doc = File.ReadAllText(Path.Combine(RepositoryRoot(), LogRoutingDocRelativePath));

        Assert.Equal(CoveredTypeNames, DocTableTypeNames(doc, "covered-types"));
        Assert.Equal(RawStateBearingTypeNames, DocTableTypeNames(doc, "reflection-reachable-state"));
        Assert.Equal(
            ReflectionReachableRawStateEntries(StorageExceptionTypes()),
            DocTableRawStateEntries(doc, "reflection-reachable-state"));
        Assert.Equal(
            DocumentedMessagePostureRows,
            DocTableMessagePostureRows(doc, "message-posture"));
    }

    [Fact]
    public async Task DocumentedMessagePostureExamples_AgreeWithRuntime()
    {
        const string rawPath = "country=DE/patient_id=4815\r\nFORGED-PATH\u2028part.parquet";
        const string rawDetail = "framework-detail\r\nFORGED-DETAIL\u2028tail";
        string root = Path.Combine(
            Path.GetTempPath(),
            FormattableString.Invariant(
                $"deltasharp-message-posture-{Environment.ProcessId}-{Interlocked.Increment(ref _tempDirectoryOrdinal)}"));

        try
        {
            using var backend = new LocalFileSystemBackend(root);
            DeltaStorageException surface = backend.SurfaceFailure(
                "Opening a read for",
                rawPath,
                new IOException(rawDetail));

            const string expectedSurfaceMessage =
                "Opening a read for '(directory)' (partitioned by: country, patient_id) failed: IOException: "
                + "framework-detail\uFFFD\uFFFDFORGED-DETAIL\uFFFDtail";
            Assert.Equal(expectedSurfaceMessage, surface.Message);
            Assert.DoesNotContain(rawPath, surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("country=DE", surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("patient_id=4815", surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("FORGED-PATH", surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\nFORGED-PATH", surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2028part.parquet", surface.Message, StringComparison.Ordinal);
            Assert.Contains("'(directory)'", surface.Message, StringComparison.Ordinal);
            Assert.Contains("(partitioned by: country, patient_id)", surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(rawDetail, surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\nFORGED-DETAIL", surface.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2028tail", surface.Message, StringComparison.Ordinal);
            Assert.Contains("framework-detail\uFFFD\uFFFDFORGED-DETAIL\uFFFDtail", surface.Message, StringComparison.Ordinal);

            DeltaStorageException missing = await Assert.ThrowsAsync<DeltaStorageException>(async () =>
            {
                await using Stream stream = await backend.OpenReadAsync(rawPath, CancellationToken.None);
            });

            Assert.Equal(
                "Object '(directory)' (partitioned by: country, patient_id) does not exist.",
                missing.Message);
            Assert.DoesNotContain(rawPath, missing.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("country=DE", missing.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("patient_id=4815", missing.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("FORGED-PATH", missing.Message, StringComparison.Ordinal);

            DeltaStorageException column = DeltaStorageException.ColumnNotPresentInFile(rawPath);
            Assert.DoesNotContain(rawPath, column.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", column.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", column.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2028", column.Message, StringComparison.Ordinal);
            Assert.Contains("\uFFFD", column.Message, StringComparison.Ordinal);

            string[] observedPostures =
            [
                $"DeltaStorageException.ColumnNotPresentInFile.columnName:{ObservedMessagePosture(column.Message, rawPath)}",
                $"LocalFileSystemBackend.OpenReadAsync.missingPath:{ObservedSurfacePathPosture(missing.Message, rawPath)}",
                $"LocalFileSystemBackend.SurfaceFailure.detail:{ObservedMessagePosture(surface.Message, rawDetail)}",
                $"LocalFileSystemBackend.SurfaceFailure.path:{ObservedSurfacePathPosture(surface.Message, rawPath)}",
            ];
            Assert.Equal(DocumentedMessagePostureRows, observedPostures);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ReferenceStateClassification_RequiresDisjointBuckets()
    {
        Assert.Equal(
            ["raw.path", "safe.path"],
            ClassifyReferencePaths(["raw.path"], ["safe.path"]));

        InvalidOperationException overlap = Assert.Throws<InvalidOperationException>(
            () => ClassifyReferencePaths(["raw.path"], ["raw.path"]));
        Assert.Contains("raw.path", overlap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageLogSites_NeverAcceptAnExceptionObject()
    {
        // #694 finding 5 (Quality), unbacked-but-true-today claim: the doc tells an operator to copy the
        // repo's own log sites because they "never pass the exception object, its message, or its inner".
        // True today, but nothing made it stay true — and it is load-bearing for the "an in-repo analyzer
        // would have zero call sites to flag" reasoning. Six lines of reflection make it rot loudly.
        //
        // #810: this reflection sweep is, by design, scoped to [LoggerMessage] source-generated sites. The
        // complementary gap — a DIRECT LoggerExtensions.Log*(…, Exception, …) call anywhere in a production
        // assembly, which hands the exception object to a sink that can render or destructure it — is now
        // closed at COMPILE TIME by the BannedSymbols.txt RS0030 bans on all 14 exception-taking
        // LoggerExtensions overloads. That RS0030 enforcement (not this Storage-scoped sweep) is what makes the
        // "zero call sites to flag" guarantee hold repo-wide across every src/ production assembly; a future
        // direct exception-logging call fails the build. The residual primitives ILogger.Log<TState>(…,
        // Exception, …) and LoggerMessage.Define cannot be symbol-banned (the source generator emits both), so
        // they stay closed by the zero-direct-call-site convention. See
        // BannedSymbols_BanEveryExceptionTakingLoggerExtensionsOverload, which proves the bans resolve.
        MethodInfo[] logSites = typeof(DeltaStorageException).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Microsoft.Extensions.Logging.LoggerMessageAttribute"))
            .ToArray();

        Assert.Equal(
            StorageLogSiteSignatures.OrderBy(signature => signature, StringComparer.Ordinal),
            logSites.Select(LogSiteSignature).OrderBy(signature => signature, StringComparer.Ordinal));

        string[] offenders = logSites
            .Where(method => method.GetParameters()
                .Any(parameter => typeof(Exception).IsAssignableFrom(parameter.ParameterType)))
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void BannedSymbols_BanEveryExceptionTakingLoggerExtensionsOverload()
    {
        // #810: the #694 sweep above covers only [LoggerMessage] source-gen sites; the complementary compile-
        // time control is the BannedSymbols.txt RS0030 ban on every direct LoggerExtensions.Log*(…, Exception,
        // …) overload (a direct call would hand the exception object to the sink and re-leak the scrubbed
        // inner). With ZERO live call sites the analyzer never fires, so build-green alone does NOT prove the
        // ban IDs resolve — a mistyped/removed doc-ID is a SILENT no-op (BannedSymbols.txt's own header warns of
        // this). Derive the required set by REFLECTION over the real API so this guard fails loudly if (a) a ban
        // ID is typo'd/dropped, or (b) a future Microsoft.Extensions.Logging version adds a new exception-taking
        // overload that would otherwise silently escape the ban. Mirrors the #455 telemetry
        // TelemetryExceptionScrubbingGuardTests precedent this family extends. (The core ILogger.Log<TState> and
        // LoggerMessage.Define residuals are NOT banned — the source generator emits both, so a symbol ban
        // trips RS0030 on the generated code; see the BannedSymbols.txt rationale.)
        MethodInfo[] exceptionOverloads = typeof(LoggerExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetParameters().Any(p => p.ParameterType == typeof(Exception)))
            .ToArray();

        // 6 levels (Error/Warning/Information/Debug/Critical/Trace) × {no-EventId, EventId} + Log(level, ex, …)
        // + Log(level, eventId, ex, …) = 14. A change here means the API surface moved — update the bans too.
        Assert.Equal(14, exceptionOverloads.Length);

        string bannedSymbols = File.ReadAllText(Path.Combine(RepositoryRoot(), "BannedSymbols.txt"));
        string[] missing = exceptionOverloads
            .Select(LoggerExtensionsDocumentationId)
            .Where(docId => !bannedSymbols.Contains($"{docId};[security]", StringComparison.Ordinal))
            .OrderBy(docId => docId, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Exception-taking LoggerExtensions overload(s) not [security]-banned in BannedSymbols.txt (#810):\n"
            + string.Join("\n", missing));
    }

    // The documentation-comment ID for a LoggerExtensions overload whose parameters are all non-generic,
    // non-byref types (ILogger/EventId/LogLevel/Exception/String and the trailing params object[]), for which
    // Type.FullName is already the doc-ID form (e.g. object[] → "System.Object[]"). Kept deliberately narrow to
    // the shapes this guard scans; a future overload with a different parameter kind would trip Assert.Equal(14).
    private static string LoggerExtensionsDocumentationId(MethodInfo method)
    {
        string parameters = string.Join(
            ",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName));
        return $"M:{method.DeclaringType!.FullName}.{method.Name}({parameters})";
    }

    [Fact]
    public void StorageAssembly_IsNonPackable_AndReviewedProviderFamiliesAreAbsent()
    {
        // #694 finding 5 (Quality): pin the two decidable package-posture claims without pretending package
        // names are a universal provider classifier. DeltaSharp.Storage is non-packable, .Abstractions is the
        // only Microsoft.Extensions.Logging package, and the reviewed provider families remain absent.
        string root = RepositoryRoot();

        XDocument storageProject = XDocument.Load(
            Path.Combine(root, "src", "DeltaSharp.Storage", "DeltaSharp.Storage.csproj"));
        Assert.Equal(
            ["false"],
            storageProject.Descendants()
                .Where(element => element.Name.LocalName == "IsPackable")
                .Select(element => element.Value.Trim())
                .ToArray());

        string[] packageIds = RepositoryPackageIds(root);
        Assert.Equal(
            ["Microsoft.Extensions.Logging.Abstractions"],
            packageIds.Where(id =>
                id == "Microsoft.Extensions.Logging"
                || id.StartsWith("Microsoft.Extensions.Logging.", StringComparison.Ordinal)).ToArray());

        // Reviewed provider/sink/exporter families. This list is intentionally not described as a universal
        // package-name classifier; the operator document dates and scopes the current-tree claim.
        string[] providerPrefixes =
        [
            "log4net",
            "Microsoft.ApplicationInsights",
            "Microsoft.Extensions.Logging.ApplicationInsights",
            "Microsoft.Extensions.Logging.AzureAppServices",
            "Microsoft.Extensions.Logging.Console",
            "Microsoft.Extensions.Logging.Debug",
            "Microsoft.Extensions.Logging.EventLog",
            "Microsoft.Extensions.Logging.EventSource",
            "Microsoft.Extensions.Logging.TraceSource",
            "NLog",
            "OpenTelemetry",
            "Sentry",
            "Serilog",
        ];

        string[] providers = packageIds
            .Where(id => providerPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(providers);
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------

    private static Type[] StorageExceptionTypes() => typeof(DeltaStorageException).Assembly.GetTypes()
        .Where(type => typeof(Exception).IsAssignableFrom(type))
        .OrderBy(type => type.Name, StringComparer.Ordinal)
        .ToArray();

    private static PropertyInfo[] DeclaredInstanceProperties(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Every instance property a reflecting sink reaches on <paramref name="type"/> beyond
    /// <see cref="Exception"/>'s own surface — i.e. declared here OR inherited from a storage-owned base. The
    /// pin records the DECLARATION site (minimal and stable); the raw-state derivation uses this inherited
    /// walk, so a raw reference-typed property added to an abstract base classifies every concrete subclass
    /// rather than hiding on the base.
    /// </summary>
    private static PropertyInfo[] ReachableInstanceProperties(Type type)
    {
        var properties = new List<PropertyInfo>();
        for (Type? current = type; current is not null && current != typeof(Exception); current = current.BaseType)
        {
            properties.AddRange(DeclaredInstanceProperties(current));
        }

        return [.. properties];
    }

    private static string LogSiteSignature(MethodInfo method)
    {
        string parameters = string.Join(
            ", ",
            method.GetParameters().Select(parameter =>
                $"{parameter.ParameterType.Name} {parameter.Name}"));
        return $"{method.DeclaringType?.Name}.{method.Name}({parameters})";
    }

    private static string[] ReflectionReachableRawStateEntries(Type[] exceptionTypes)
    {
        Exception rawInner = ThrowFromInnerCauseFrame(new InvalidOperationException(RawInnerLeak));
        var entries = new List<string>();
        var observedTypedPaths = new List<string>();
        var unconstructible = new List<string>();

        foreach (Type type in exceptionTypes.Where(type => !type.IsAbstract))
        {
            if (!TryConstruct(type, rawInner, out Exception? instance))
            {
                unconstructible.Add(type.Name);
                continue;
            }

            if (instance!.InnerException is not null)
            {
                entries.Add($"{type.Name}.InnerException");
            }

            foreach (PropertyInfo property in ReachableInstanceProperties(type))
            {
                CollectProbeBearingPaths(
                    property.GetValue(instance),
                    property.PropertyType,
                    $"{type.Name}.{property.Name}",
                    observedTypedPaths,
                    new HashSet<Type>());
            }
        }

        Assert.Empty(unconstructible);

        string[] structuralTypedPaths = exceptionTypes
            .SelectMany(type => ReachableInstanceProperties(type)
                .SelectMany(property => ReferenceLeafPaths(
                    property.PropertyType,
                    $".{property.Name}",
                    new HashSet<Type>()))
                .Select(path => $"{type.Name}{path}"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        string[] observed = observedTypedPaths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            structuralTypedPaths,
            ClassifyReferencePaths(observed, SanitizedOrDerivedStatePaths));
        entries.AddRange(observed);
        return entries
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> ReferenceLeafPaths(
        Type type, string path, HashSet<Type> ancestors)
    {
        if (type == typeof(string))
        {
            yield return path;
            yield break;
        }

        if (IsTerminalValueType(type))
        {
            yield break;
        }

        Type? elementType = EnumerableElementType(type);
        if (elementType is not null)
        {
            string[] elementPaths = ReferenceLeafPaths(
                elementType,
                path + "[]",
                new HashSet<Type>(ancestors)).ToArray();
            if (elementPaths.Length == 0)
            {
                yield return path;
            }
            else
            {
                foreach (string elementPath in elementPaths)
                {
                    yield return elementPath;
                }
            }

            yield break;
        }

        if (!ancestors.Add(type))
        {
            yield return path;
            yield break;
        }

        string[] nestedPaths = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .SelectMany(property => ReferenceLeafPaths(
                property.PropertyType,
                $"{path}.{property.Name}",
                new HashSet<Type>(ancestors)))
            .Concat(type
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(field => ReferenceLeafPaths(
                    field.FieldType,
                    $"{path}.{field.Name}",
                    new HashSet<Type>(ancestors))))
            .ToArray();

        if (nestedPaths.Length == 0)
        {
            yield return path;
            yield break;
        }

        foreach (string nestedPath in nestedPaths)
        {
            yield return nestedPath;
        }
    }

    private static void CollectProbeBearingPaths(
        object? value,
        Type type,
        string path,
        ICollection<string> paths,
        HashSet<Type> ancestors)
    {
        if (type == typeof(string))
        {
            if (value is string text && text.Contains(ProbeSurfaceMessage, StringComparison.Ordinal))
            {
                paths.Add(path);
            }

            return;
        }

        if (IsTerminalValueType(type) || value is null)
        {
            return;
        }

        Type? elementType = EnumerableElementType(type);
        if (elementType is not null && value is IEnumerable sequence)
        {
            foreach (object? item in sequence)
            {
                CollectProbeBearingPaths(
                    item,
                    elementType,
                    path + "[]",
                    paths,
                    new HashSet<Type>(ancestors));
            }

            return;
        }

        if (!ancestors.Add(type))
        {
            return;
        }

        foreach (PropertyInfo property in type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0))
        {
            CollectProbeBearingPaths(
                property.GetValue(value),
                property.PropertyType,
                $"{path}.{property.Name}",
                paths,
                new HashSet<Type>(ancestors));
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            CollectProbeBearingPaths(
                field.GetValue(value),
                field.FieldType,
                $"{path}.{field.Name}",
                paths,
                new HashSet<Type>(ancestors));
        }
    }

    private static string[] ClassifyReferencePaths(
        IEnumerable<string> rawPaths, IEnumerable<string> sanitizedOrDerivedPaths)
    {
        string[] raw = rawPaths.Distinct(StringComparer.Ordinal).ToArray();
        string[] safe = sanitizedOrDerivedPaths.Distinct(StringComparer.Ordinal).ToArray();
        string[] overlap = raw.Intersect(safe, StringComparer.Ordinal).ToArray();
        if (overlap.Length != 0)
        {
            throw new InvalidOperationException(
                $"Reference-state paths cannot be both raw and sanitized/derived: {string.Join(", ", overlap)}");
        }

        return raw.Concat(safe).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static string ObservedMessagePosture(string message, string rawToken)
    {
        if (message.Contains(rawToken, StringComparison.Ordinal))
        {
            return "raw";
        }

        if (!message.Contains("\r", StringComparison.Ordinal)
            && !message.Contains("\n", StringComparison.Ordinal)
            && !message.Contains("\u2028", StringComparison.Ordinal)
            && message.Contains("\uFFFD", StringComparison.Ordinal))
        {
            return "sanitized";
        }

        return "unclassified";
    }

    private static string ObservedSurfacePathPosture(string message, string rawPath)
    {
        if (message.Contains(rawPath, StringComparison.Ordinal))
        {
            return "raw";
        }

        return message.Contains("'(directory)'", StringComparison.Ordinal)
            && message.Contains("(partitioned by: country, patient_id)", StringComparison.Ordinal)
            && !message.Contains("country=DE", StringComparison.Ordinal)
            && !message.Contains("patient_id=4815", StringComparison.Ordinal)
            && !message.Contains("FORGED-PATH", StringComparison.Ordinal)
                ? "redacted"
                : "unclassified";
    }

    private static Type? EnumerableElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        Type? enumerable = type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate =>
                    candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static bool IsTerminalValueType(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(Guid)
        || type == typeof(TimeSpan);

    private static string[] RepositoryPackageIds(string root)
    {
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        string[] buildFiles = RepositoryBuildFiles(root);
        string[] manifests = buildFiles
            .Where(path => path.EndsWith(".csproj", StringComparison.Ordinal)
                || path.EndsWith(".props", StringComparison.Ordinal)
                || path.EndsWith(".targets", StringComparison.Ordinal))
            .ToArray();

        foreach (string manifest in manifests)
        {
            XDocument document = XDocument.Load(manifest);
            foreach (XElement item in document.Descendants().Where(element =>
                element.Name.LocalName is "PackageReference" or "PackageVersion" or "GlobalPackageReference"))
            {
                string? id = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
                if (id is { Length: > 0 })
                {
                    packageIds.Add(id);
                }
            }
        }

        foreach (string lockFile in buildFiles.Where(path =>
            Path.GetFileName(path) == "packages.lock.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockFile));
            if (!document.RootElement.TryGetProperty("dependencies", out JsonElement dependencies))
            {
                continue;
            }

            foreach (JsonProperty target in dependencies.EnumerateObject())
            {
                foreach (JsonProperty package in target.Value.EnumerateObject())
                {
                    packageIds.Add(package.Name);
                }
            }
        }

        return packageIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private static string[] RepositoryBuildFiles(string root)
    {
        const string solutionFolderProjectType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
        var projectPaths = new List<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(root, "DeltaSharp.sln"))
            .Where(line => line.StartsWith("Project(", StringComparison.Ordinal)))
        {
            string[] quotedFields = line.Split('"')
                .Where((_, index) => index % 2 == 1)
                .ToArray();
            Assert.Equal(4, quotedFields.Length);

            string projectType = quotedFields[0];
            string path = quotedFields[2].Replace('\\', '/');
            if (path.EndsWith(".csproj", StringComparison.Ordinal))
            {
                projectPaths.Add(path);
            }
            else
            {
                Assert.Equal(solutionFolderProjectType, projectType);
            }
        }

        string[] projects = projectPaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(SolutionProjectPaths, projects);

        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (string project in projects)
        {
            string projectPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            files.Add(projectPath);

            for (DirectoryInfo? directory = new(Path.GetDirectoryName(projectPath)!);
                directory is not null;
                directory = directory.Parent)
            {
                foreach (string pattern in new[] { "*.props", "*.targets", "packages.lock.json" })
                {
                    files.UnionWith(Directory.EnumerateFiles(
                        directory.FullName, pattern, SearchOption.TopDirectoryOnly));
                }

                if (string.Equals(directory.FullName, root, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        return files.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Throws and catches <paramref name="exception"/> so its own <see cref="Exception.StackTrace"/>
    /// is populated with a frame named after THIS method — the marker the render assertions key on.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T ThrowFromCoveredSurfaceFrame<T>(T exception)
        where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return (T)caught;
        }
    }

    /// <summary>The inner cause's throw site, deliberately named differently from
    /// <see cref="ThrowFromCoveredSurfaceFrame"/> so "the appended trace is the exception's OWN" is
    /// decidable from the rendered text.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T ThrowFromInnerCauseFrame<T>(T exception)
        where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return (T)caught;
        }
    }

    /// <summary>
    /// Builds an instance of <paramref name="type"/> from its own constructors, preferring one that takes an
    /// <see cref="Exception"/> so the inner-carrying shape is exercised where the type supports it. Argument
    /// values are synthesized structurally, so no hand-maintained per-type table can go stale.
    /// </summary>
    private static bool TryConstruct(Type type, Exception rawInner, out Exception? instance)
    {
        instance = null;
        ConstructorInfo[] candidates = type
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(ctor => ctor.GetParameters()
                .Any(parameter => typeof(Exception).IsAssignableFrom(parameter.ParameterType)))
            .ThenByDescending(ctor => ctor.GetParameters().Length)
            .ThenBy(
                ctor => string.Join(",", ctor.GetParameters().Select(parameter => parameter.ParameterType.Name)),
                StringComparer.Ordinal)
            .ToArray();

        foreach (ConstructorInfo ctor in candidates)
        {
            if (!TrySynthesizeArguments(ctor, rawInner, depth: 0, out object?[]? arguments))
            {
                continue;
            }

            instance = (Exception)ctor.Invoke(arguments!);
            if (type == typeof(DeltaStorageException))
            {
                typeof(DeltaStorageException)
                    .GetProperty(nameof(DeltaStorageException.Path))!
                    .SetValue(instance, $"{ProbeSurfaceMessage}-path");
            }

            return true;
        }

        return false;
    }

    private static bool TrySynthesizeArguments(
        ConstructorInfo ctor, Exception rawInner, int depth, out object?[]? arguments)
    {
        var synthesized = new List<object?>();
        foreach (ParameterInfo parameter in ctor.GetParameters())
        {
            if (!TrySynthesize(
                parameter.ParameterType, parameter.Name ?? "arg", rawInner, depth, out object? argument))
            {
                arguments = null;
                return false;
            }

            synthesized.Add(argument);
        }

        arguments = [.. synthesized];
        return true;
    }

    private static bool TrySynthesize(
        Type type, string role, Exception rawInner, int depth, out object? value)
    {
        value = null;
        if (typeof(Exception).IsAssignableFrom(type))
        {
            value = rawInner;
            return true;
        }

        if (type == typeof(string))
        {
            // DISTINCT per parameter, deliberately. If every string argument were the same literal, a type
            // that chains `new IOException(detail)` would be caught only because `detail` happened to equal
            // its own message — a degenerate kill that would also fire on a safe type. Distinct values make
            // the synthetic-inner catch (mutant M6) a real one and model the shipping shape, where the
            // inner is an OS exception whose text is unrelated to the surfaced message.
            value = $"{ProbeSurfaceMessage}-{role}";
            return true;
        }

        if (type.IsEnum)
        {
            value = Enum.GetValues(type).GetValue(0);
            return value is not null;
        }

        if (type == typeof(TimeSpan))
        {
            value = TimeSpan.Zero;
            return true;
        }

        if (type.IsPrimitive)
        {
            value = Activator.CreateInstance(type);
            return true;
        }

        // A collection parameter (e.g. IReadOnlyList<DeltaTableConstraint>): an empty array satisfies every
        // list-shaped contract the constructors use.
        if (type.IsArray)
        {
            Type elementType = type.GetElementType()!;
            Array items = Array.CreateInstance(elementType, 1);
            if (TrySynthesize(elementType, $"{role}Item", rawInner, depth + 1, out object? item))
            {
                items.SetValue(item, 0);
            }
            else
            {
                items = Array.CreateInstance(elementType, 0);
            }

            value = items;
            return true;
        }

        if (type.IsGenericType && type.IsInterface)
        {
            Type[] typeArguments = type.GetGenericArguments();
            if (typeArguments.Length == 1)
            {
                Type elementType = typeArguments[0];
                Array items = Array.CreateInstance(elementType, 1);
                if (TrySynthesize(elementType, $"{role}Item", rawInner, depth + 1, out object? item))
                {
                    items.SetValue(item, 0);
                }
                else
                {
                    items = Array.CreateInstance(elementType, 0);
                }

                if (type.IsInstanceOfType(items))
                {
                    value = items;
                    return true;
                }
            }
        }

        // A record/DTO parameter (e.g. DeltaTableConstraint): recurse once into its own constructor.
        if (depth >= 2 || type.IsAbstract)
        {
            return false;
        }

        foreach (ConstructorInfo ctor in type
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(candidate => candidate.GetParameters().Length))
        {
            if (TrySynthesizeArguments(ctor, rawInner, depth + 1, out object?[]? arguments))
            {
                value = ctor.Invoke(arguments!);
                return true;
            }
        }

        return false;
    }

    /// <summary>Walks up from the test binaries to the repository root. Fails rather than skips when it is not
    /// found: a doc guard that silently opts out on an unexpected layout is the fail-open shape this file is
    /// trying to stop having.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeltaSharp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// Reads the first column of the Markdown table fenced by <c>&lt;!-- BEGIN:{marker} … --&gt;</c> /
    /// <c>&lt;!-- END:{marker} --&gt;</c>, returning the backticked type name from each data row.
    /// </summary>
    private static string[] DocTableTypeNames(string doc, string marker)
    {
        int begin = doc.IndexOf($"<!-- BEGIN:{marker}", StringComparison.Ordinal);
        int end = doc.IndexOf($"<!-- END:{marker}", StringComparison.Ordinal);
        Assert.True(begin >= 0, $"the log-routing doc is missing the BEGIN:{marker} marker");
        Assert.True(end > begin, $"the log-routing doc is missing the END:{marker} marker");

        return doc[begin..end]
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('`')[1])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] DocTableRawStateEntries(string doc, string marker)
    {
        int begin = doc.IndexOf($"<!-- BEGIN:{marker}", StringComparison.Ordinal);
        int end = doc.IndexOf($"<!-- END:{marker}", StringComparison.Ordinal);
        Assert.True(begin >= 0, $"the log-routing doc is missing the BEGIN:{marker} marker");
        Assert.True(end > begin, $"the log-routing doc is missing the END:{marker} marker");

        var entries = new List<string>();
        foreach (string line in doc[begin..end]
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal)))
        {
            string[] cells = line.Split('|');
            Assert.True(cells.Length >= 3, $"malformed {marker} table row: {line}");
            string typeName = Assert.Single(CodeSpans(cells[1]));
            foreach (string path in CodeSpans(cells[2]))
            {
                entries.Add($"{typeName}{path}");
            }
        }

        return entries.OrderBy(entry => entry, StringComparer.Ordinal).ToArray();
    }

    private static string[] DocTableMessagePostureRows(string doc, string marker)
    {
        int begin = doc.IndexOf($"<!-- BEGIN:{marker}", StringComparison.Ordinal);
        int end = doc.IndexOf($"<!-- END:{marker}", StringComparison.Ordinal);
        Assert.True(begin >= 0, $"the log-routing doc is missing the BEGIN:{marker} marker");
        Assert.True(end > begin, $"the log-routing doc is missing the END:{marker} marker");

        var rows = new List<string>();
        foreach (string line in doc[begin..end]
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal)))
        {
            string[] cells = line.Split('|');
            Assert.True(cells.Length >= 3, $"malformed {marker} table row: {line}");
            rows.Add($"{Assert.Single(CodeSpans(cells[1]))}:{Assert.Single(CodeSpans(cells[2]))}");
        }

        return rows.OrderBy(row => row, StringComparer.Ordinal).ToArray();
    }

    private static string[] CodeSpans(string markdown) => markdown
        .Split('`')
        .Where((_, index) => index % 2 == 1)
        .ToArray();
}
