using DeltaSharp.Engine.Columnar;

namespace DeltaSharp.Storage.Reading;

/// <summary>
/// A decoded deletion vector applied at READ time — the sorted, distinct set of PHYSICAL, file-relative row
/// ordinals to exclude, plus the file's physical record count for a post-read consistency check.
///
/// <para>SHARED by BOTH read doors — the snapshot path (<see cref="DeltaReadSource"/>) and the change-feed
/// path (<c>ChangeFeedReader</c>) — so the DV masking / survivor-selection semantics can NEVER drift between
/// them (the same single-source-of-truth pattern as the shared <c>ColumnMappingProjection</c>, #529). The DV
/// <b>decode</b> stays shared in <c>DeletionVectorStore.LoadAsync</c>; this type owns only the "given decoded
/// positions + a physical batch/offset, produce the surviving rows" step both doors previously duplicated.</para>
/// </summary>
internal sealed class DeletionVectorMask
{
    private readonly long[] _deleted;

    /// <param name="sortedDistinctPositions">The PHYSICAL, file-relative row ordinals the DV excludes.
    /// <c>RoaringBitmapArray.Deserialize</c> guarantees these are ascending and distinct, so the array doubles
    /// as a sorted-set membership structure (<see cref="Array.BinarySearch(long[], long)"/>) with no second
    /// copy — the read path never holds both a <c>long[]</c> AND a hash set of the same set.</param>
    /// <param name="physicalRecords">The file's physical row count (from the Parquet footer, never a
    /// caller-supplied size) the DV positions were validated against.</param>
    public DeletionVectorMask(long[] sortedDistinctPositions, long physicalRecords)
    {
        _deleted = sortedDistinctPositions;
        PhysicalRecords = physicalRecords;
    }

    /// <summary>The file's physical row count the DV was validated against (its footer row-group total).</summary>
    public long PhysicalRecords { get; }

    /// <summary>True iff the given PHYSICAL, file-relative row position is masked (deleted) by this DV.</summary>
    public bool IsDeleted(long filePosition) => Array.BinarySearch(_deleted, filePosition) >= 0;

    /// <summary>
    /// Applies the DV to one full-schema batch by building a <see cref="SelectionVector"/> of the surviving
    /// physical rows — those whose file-relative ordinal <paramref name="fileRowOffset"/> + r is NOT masked.
    /// Returns the batch UNCHANGED when no row is masked (an identity selection is pure overhead), and
    /// <see langword="null"/> when EVERY row is masked (the caller drops the empty batch). DV positions are
    /// file-relative, so the running <paramref name="fileRowOffset"/> maps this batch's rows
    /// <c>[0, RowCount)</c> onto the file's ordinal space.
    /// </summary>
    public ColumnBatch? Apply(ColumnBatch batch, long fileRowOffset)
    {
        int rowCount = batch.RowCount;
        var survivors = new List<int>(rowCount);
        for (int r = 0; r < rowCount; r++)
        {
            if (!IsDeleted(fileRowOffset + r))
            {
                survivors.Add(r);
            }
        }

        if (survivors.Count == rowCount)
        {
            return batch;
        }

        return survivors.Count == 0 ? null : batch.WithSelection(new SelectionVector(survivors.ToArray()));
    }

    /// <summary>
    /// Fail-closed post-read check: the file's actual physical row count (summed across row groups) MUST equal
    /// the record count the DV was validated against. A mismatch means the file changed under the DV, so the
    /// positions can no longer be trusted to map to the right rows — the read fails closed.
    /// </summary>
    /// <exception cref="DeltaReadException">The physical rows read disagree with <see cref="PhysicalRecords"/>.</exception>
    public void EnsureConsumed(long physicalRowsRead, string path)
    {
        if (physicalRowsRead != PhysicalRecords)
        {
            throw new DeltaReadException(
                $"File '{path}' carries a deletion vector validated against {PhysicalRecords} physical "
                + $"record(s), but the Parquet file produced {physicalRowsRead} on read; the deletion vector "
                + "disagrees with the data file, so the read fails closed.");
        }
    }
}
