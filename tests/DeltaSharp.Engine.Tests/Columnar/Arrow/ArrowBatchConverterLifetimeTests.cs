using Apache.Arrow;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Columnar.Arrow;
using Xunit;

namespace DeltaSharp.Engine.Tests.Columnar.Arrow;

/// <summary>
/// Lifetime of views vended across the Arrow boundary (council columnar F4 / Balanced F5 + Security F1).
/// A nested column exported under <see cref="ArrowImportOwnership.Transfer"/> retains the buffers it
/// shares, so the exported batch outlives the imported batch's dispose. The documented use-after-free
/// contract's <b>safe</b> half is pinned too: a <see cref="ArrowImportOwnership.Borrowed"/> vended column
/// reads correctly while the source is alive, and the batch-level accessors throw
/// <see cref="System.ObjectDisposedException"/> after dispose (hygiene, not memory safety). The unsafe
/// half — reading a vended view after its owner is disposed — is quarantined: documented here but never
/// executed, since it is undefined behavior.
/// </summary>
public class ArrowBatchConverterLifetimeTests
{
    [Fact]
    public void ToArrow_NestedExportUnderTransfer_SurvivesImportedBatchDispose()
    {
        StructArray structArray = ArrowConverterTestSupport.BuildStructArray(new[] { 10, 20, 30 });
        RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("s", structArray, true));

        ArrowColumnBatch imported = ArrowBatchConverter.FromArrow(source, ArrowImportOwnership.Transfer);
        RecordBatch exported = ArrowBatchConverter.ToArrow(imported);

        // Disposing the imported batch disposes the source (Transfer). The exported nested column must
        // still read its values because ToArrow retained an independent reference (SliceShared) over the
        // shared buffers, so the source's release does not invalidate the export.
        imported.Dispose();

        StructArray exportedStruct = Assert.IsType<StructArray>(exported.Column(0));
        Assert.Equal(3, exportedStruct.Length);
        Int32Array child = Assert.IsType<Int32Array>(exportedStruct.Fields[0]);
        Assert.Equal(10, child.GetValue(0)!.Value);
        Assert.Equal(20, child.GetValue(1)!.Value);
        Assert.Equal(30, child.GetValue(2)!.Value);

        exported.Dispose();
    }

    [Fact]
    public void Borrowed_VendedColumn_ReadsWhileSourceAlive_AndBatchAccessorsThrowAfterDispose()
    {
        Int32Array column = new Int32Array.Builder().Append(10).AppendNull().Append(30).Build();
        using RecordBatch source = ArrowConverterTestSupport.RecordBatchOf(("c", column, true));

        // Borrowed: the caller owns `source` and keeps it alive for the whole scope.
        ArrowColumnBatch batch = ArrowBatchConverter.FromArrow(source);

        // SAFE half of the lifetime contract: a column vended while the borrowed source is alive reads
        // its values correctly (zero-copy straight through the live Arrow buffers).
        ColumnVector vended = batch.Column(0);
        Assert.Equal(3, vended.Length);
        Assert.Equal(10, vended.GetValue<int>(0));
        Assert.True(vended.IsNull(1));
        Assert.Equal(30, vended.GetValue<int>(2));

        batch.Dispose(); // Borrowed: releases nothing; `source` and its buffers stay alive.

        // Batch-level accessors throw ObjectDisposedException as disposal hygiene (NOT memory safety).
        Assert.Throws<ObjectDisposedException>(() => batch.Column(0));
        Assert.Throws<ObjectDisposedException>(() => batch.Slice(0, 1));
        Assert.Throws<ObjectDisposedException>(() => batch.WithSelection(new SelectionVector(new[] { 0 })));

        // QUARANTINED USE-AFTER-FREE (documented, deliberately NOT executed): reading a view vended from
        // a batch after the owning lifetime ends is undefined behavior. Under Transfer, disposing the
        // batch frees the source; under Borrowed, disposing `source` frees it. Either way a previously
        // vended `vended.GetValue<int>(0)` / `ReadOnlySpan<T>` would read freed Arrow memory (a native
        // NullReferenceException or silently recycled bytes). The batch-level ObjectDisposedException
        // above is hygiene on the accessor, not protection for an already-vended column; callers must
        // materialize values they need to outlive the batch (Transfer) or the source (Borrowed).
    }
}
