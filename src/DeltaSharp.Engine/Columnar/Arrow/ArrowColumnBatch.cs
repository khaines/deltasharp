using Apache.Arrow;
using DeltaSharp.Engine.Types;

namespace DeltaSharp.Engine.Columnar.Arrow;

/// <summary>
/// A <see cref="ColumnBatch"/> imported from an Apache Arrow <see cref="RecordBatch"/> by
/// <see cref="ArrowBatchConverter.FromArrow(RecordBatch, ArrowImportOwnership)"/> (STORY-02.2.2,
/// #136). It composes a managed batch over the wrapped/materialized columns and adds the disposal
/// half of the "Arrow at the edges" contract: it carries the source <see cref="RecordBatch"/> and an
/// <see cref="ArrowImportOwnership"/> so the Arrow buffers are released exactly once, and only in the
/// mode that owns them (AC4). The operator-facing read surface is the inherited
/// <see cref="ColumnBatch"/>; no member exposes an <c>Apache.Arrow</c> type, so the boundary stays at
/// this type and the converter.
/// </summary>
/// <remarks>
/// <para>
/// Disposal is exactly-once and thread-safe: the first <see cref="Dispose"/> wins (via an interlocked
/// flag); a <see cref="ArrowImportOwnership.Transfer"/> import then disposes the source once, while a
/// <see cref="ArrowImportOwnership.Borrowed"/> import releases nothing (the caller owns the source).
/// After disposal the column accessors throw <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// <see cref="Slice"/> and <see cref="WithSelection"/> return managed views that share these columns
/// (and, for a borrowed/transferred zero-copy column, the underlying Arrow buffers). Those views do
/// not own disposal: keep this batch — and, when <see cref="ArrowImportOwnership.Borrowed"/>, the
/// source — alive while any view is in use.
/// </para>
/// </remarks>
public sealed class ArrowColumnBatch : ColumnBatch, IDisposable
{
    private readonly ManagedColumnBatch _inner;
    private readonly RecordBatch _source;
    private int _disposed;

    /// <summary>Composes the imported columns with the source and its ownership mode.</summary>
    internal ArrowColumnBatch(ManagedColumnBatch inner, RecordBatch source, ArrowImportOwnership ownership)
    {
        _inner = inner;
        _source = source;
        Ownership = ownership;
    }

    /// <summary>How the source Arrow buffers are owned, and therefore who releases them.</summary>
    public ArrowImportOwnership Ownership { get; }

    /// <summary>Whether this batch has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc/>
    public override StructType Schema => _inner.Schema;

    /// <inheritdoc/>
    public override int RowCount => _inner.RowCount;

    /// <inheritdoc/>
    public override int ColumnCount => _inner.ColumnCount;

    /// <inheritdoc/>
    public override SelectionVector? Selection => _inner.Selection;

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public override ColumnVector Column(int ordinal)
    {
        ThrowIfDisposed();
        return _inner.Column(ordinal);
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public override ColumnBatch Slice(int offset, int length)
    {
        ThrowIfDisposed();
        return _inner.Slice(offset, length);
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public override ColumnBatch WithSelection(SelectionVector selection)
    {
        ThrowIfDisposed();
        return _inner.WithSelection(selection);
    }

    /// <summary>
    /// Releases the source Arrow buffers per <see cref="Ownership"/>: a single disposal for a
    /// <see cref="ArrowImportOwnership.Transfer"/> import, a no-op release for a
    /// <see cref="ArrowImportOwnership.Borrowed"/> import. Safe to call repeatedly — only the first
    /// call performs work — and from multiple threads.
    /// </summary>
    public void Dispose()
    {
        // Exactly-once: only the thread that flips 0 -> 1 performs the documented release, so the
        // source is disposed once regardless of how many times (or how concurrently) callers dispose.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Ownership == ArrowImportOwnership.Transfer)
        {
            _source.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
