using System.Collections.Immutable;
using DeltaSharp.Storage.Delta;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Exhaustive per-branch tests for <see cref="DeltaConflictChecker"/> — the read-scope-driven classifier at
/// the heart of optimistic concurrency (design §2.11.2). Each test pins one matrix cell: a blind append
/// rebases past data changes but aborts on metadata/protocol; a whole-table/read-files loser aborts on
/// overlapping data changes; concurrent same-appId transactions abort.
/// </summary>
public sealed class DeltaConflictCheckerTests
{
    private static readonly ImmutableSortedDictionary<string, string?> NoPartition =
        ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableSortedDictionary<string, string> NoTags =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static AddFileAction Add(string path) =>
        new(path, NoPartition, 1L, 1L, DataChange: true, Stats: null, Tags: NoTags);

    private static RemoveFileAction Remove(string path) =>
        new(path, DeletionTimestamp: 1L, DataChange: true, ExtendedFileMetadata: false, NoPartition, Size: null);

    private static ProtocolAction Protocol() =>
        new(1, 2, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

    private static MetadataAction Metadata() =>
        new(
            "t",
            Name: null,
            Description: null,
            new TableFormat("parquet", NoTags),
            "{\"type\":\"struct\",\"fields\":[]}",
            ImmutableArray<string>.Empty,
            NoTags,
            CreatedTime: null);

    private static TxnAction Txn(string appId, long version) => new(appId, version, LastUpdated: null);

    private static void Check(DeltaAction[] loser, DeltaReadScope scope, DeltaAction[] winners) =>
        DeltaConflictChecker.Check(loser, scope, winners);

    // ---- Blind append (empty read set): row 1 of the matrix ----

    [Fact]
    public void BlindAppend_RebasesPastConcurrentAppend()
    {
        // ✅ no logical conflict: a blind append never conflicts with a concurrent append.
        Check(new DeltaAction[] { Add("new.parquet") }, DeltaReadScope.BlindAppend, new DeltaAction[] { Add("winner.parquet") });
    }

    [Fact]
    public void BlindAppend_RebasesPastConcurrentDelete()
    {
        Check(new DeltaAction[] { Add("new.parquet") }, DeltaReadScope.BlindAppend, new DeltaAction[] { Remove("old.parquet") });
    }

    [Fact]
    public void BlindAppend_AbortsOnConcurrentMetadataChange()
    {
        var ex = Assert.Throws<MetadataChangedException>(() =>
            Check(new DeltaAction[] { Add("new.parquet") }, DeltaReadScope.BlindAppend, new DeltaAction[] { Metadata(), Add("w.parquet") }));
        Assert.Equal(DeltaConflictKind.MetadataChanged, ex.Kind);
    }

    [Fact]
    public void BlindAppend_AbortsOnConcurrentProtocolChange()
    {
        var ex = Assert.Throws<ProtocolChangedException>(() =>
            Check(new DeltaAction[] { Add("new.parquet") }, DeltaReadScope.BlindAppend, new DeltaAction[] { Protocol() }));
        Assert.Equal(DeltaConflictKind.ProtocolChanged, ex.Kind);
    }

    [Fact]
    public void ProtocolChange_TakesPrecedenceOverMetadataChange()
    {
        // Both changed: protocol is the more significant winner-driven abort.
        Assert.Throws<ProtocolChangedException>(() =>
            Check(new DeltaAction[] { Add("n.parquet") }, DeltaReadScope.BlindAppend, new DeltaAction[] { Protocol(), Metadata() }));
    }

    // ---- Whole-table (overwrite / unpartitioned delete): row 2 ----

    [Fact]
    public void WholeTable_AbortsOnConcurrentAppend()
    {
        var ex = Assert.Throws<ConcurrentAppendException>(() =>
            Check(new DeltaAction[] { Add("o.parquet") }, DeltaReadScope.WholeTable, new DeltaAction[] { Add("winner.parquet") }));
        Assert.Equal(DeltaConflictKind.ConcurrentAppend, ex.Kind);
    }

    [Fact]
    public void WholeTable_AbortsOnConcurrentDelete()
    {
        Assert.Throws<ConcurrentDeleteReadException>(() =>
            Check(new DeltaAction[] { Add("o.parquet") }, DeltaReadScope.WholeTable, new DeltaAction[] { Remove("gone.parquet") }));
    }

    [Fact]
    public void WholeTable_RebasesWhenNoConcurrentDataChange()
    {
        // Only a concurrent txn for a different appId — no data overlap, no metadata/protocol change.
        Check(new DeltaAction[] { Add("o.parquet") }, DeltaReadScope.WholeTable, new DeltaAction[] { Txn("other", 1L) });
    }

    // ---- Read-files (targeted delete/merge): row 3 ----

    [Fact]
    public void ReadFiles_AbortsWhenWinnerRemovesReadFile()
    {
        var ex = Assert.Throws<ConcurrentDeleteReadException>(() =>
            Check(
                new DeltaAction[] { Remove("x.parquet") },
                DeltaReadScope.ReadFiles(new[] { "x.parquet" }),
                new DeltaAction[] { Remove("x.parquet") }));
        Assert.Equal(DeltaConflictKind.ConcurrentDeleteRead, ex.Kind);
    }

    [Fact]
    public void ReadFiles_AbortsWhenWinnerReAddsReadFile()
    {
        Assert.Throws<ConcurrentAppendException>(() =>
            Check(
                new DeltaAction[] { Remove("x.parquet") },
                DeltaReadScope.ReadFiles(new[] { "x.parquet" }),
                new DeltaAction[] { Add("x.parquet") }));
    }

    [Fact]
    public void ReadFiles_RebasesWhenWinnerTouchesDifferentFile()
    {
        // ✅ the winner removed/added a file outside our read set — safe to rebase.
        Check(
            new DeltaAction[] { Remove("x.parquet") },
            DeltaReadScope.ReadFiles(new[] { "x.parquet" }),
            new DeltaAction[] { Remove("y.parquet"), Add("z.parquet") });
    }

    // ---- Concurrent transaction ----

    [Fact]
    public void SameAppIdTxn_AbortsWithConcurrentTransaction()
    {
        var ex = Assert.Throws<ConcurrentTransactionException>(() =>
            Check(
                new DeltaAction[] { Txn("stream", 5L), Add("n.parquet") },
                DeltaReadScope.BlindAppend,
                new DeltaAction[] { Txn("stream", 4L), Add("w.parquet") }));
        Assert.Equal(DeltaConflictKind.ConcurrentTransaction, ex.Kind);
    }

    [Fact]
    public void DifferentAppIdTxn_DoesNotConflict()
    {
        Check(
            new DeltaAction[] { Txn("stream-a", 5L), Add("n.parquet") },
            DeltaReadScope.BlindAppend,
            new DeltaAction[] { Txn("stream-b", 4L), Add("w.parquet") });
    }

    // ---- Loser exclusivity (rows 5–6, stricter-than-Delta) ----

    [Fact]
    public void MetadataChangingLoser_AbortsOnAnyConcurrentCommit()
    {
        // Loser changes metadata; winner only appended data (changed neither metadata nor protocol).
        var ex = Assert.Throws<MetadataChangedException>(() =>
            Check(new DeltaAction[] { Metadata() }, DeltaReadScope.BlindAppend, new DeltaAction[] { Add("w.parquet") }));
        Assert.Equal(DeltaConflictKind.MetadataChanged, ex.Kind);
    }

    [Fact]
    public void ProtocolChangingLoser_AbortsOnAnyConcurrentCommit()
    {
        Assert.Throws<ProtocolChangedException>(() =>
            Check(new DeltaAction[] { Protocol() }, DeltaReadScope.BlindAppend, new DeltaAction[] { Add("w.parquet") }));
    }
}
