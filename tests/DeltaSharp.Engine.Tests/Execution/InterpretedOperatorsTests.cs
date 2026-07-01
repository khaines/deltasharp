using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Exercises the first executable vectorized operators (STORY-03.2.1): the in-memory
/// <see cref="InterpretedScanStream"/>, the selection-vector <see cref="InterpretedFilterStream"/>,
/// and the zero-copy <see cref="InterpretedProjectStream"/>, plus their shared
/// <see cref="InterpretedOperators"/> dispatch. Tests assert columnar semantics (scan emits,
/// selection-vector filtering for all/none/null predicates, projection schema/values, zero-copy),
/// pull-based laziness, cancellation at batch boundaries, bounded-memory reservation, populated
/// metrics, and compiled↔interpreted parity (DoD checklists 04a/08/16/21).
/// </summary>
public class InterpretedOperatorsTests
{
    // ----- schema + fixtures -----

    private static readonly StructType ThreeCol = new(
    [
        new StructField("id", DataTypes.IntegerType, nullable: false),
        new StructField("name", DataTypes.StringType, nullable: true),
        new StructField("flag", DataTypes.BooleanType, nullable: true),
    ]);

    private static ColumnReference IdRef => new(0, DataTypes.IntegerType, nullable: false);

    private static ColumnReference NameRef => new(1, DataTypes.StringType, nullable: true);

    private static ColumnReference FlagRef => new(2, DataTypes.BooleanType, nullable: true);

    private static InterpretedVectorizedBackend Backend => InterpretedVectorizedBackend.Instance;

    private static ColumnVector IntCol(params int?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.IntegerType, Math.Max(values.Length, 1));
        foreach (int? x in values)
        {
            if (x.HasValue)
            {
                v.AppendValue(x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static ColumnVector StrCol(params string?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.StringType, Math.Max(values.Length, 1));
        foreach (string? x in values)
        {
            if (x is null)
            {
                v.AppendNull();
            }
            else
            {
                v.AppendBytes(Encoding.UTF8.GetBytes(x));
            }
        }

        return v;
    }

    private static ColumnVector BoolCol(params bool?[] values)
    {
        MutableColumnVector v = ColumnVectors.Create(DataTypes.BooleanType, Math.Max(values.Length, 1));
        foreach (bool? x in values)
        {
            if (x.HasValue)
            {
                v.AppendValue(x.Value);
            }
            else
            {
                v.AppendNull();
            }
        }

        return v;
    }

    private static ColumnBatch Batch(StructType schema, params ColumnVector[] columns)
        => new ManagedColumnBatch(schema, columns, columns.Length > 0 ? columns[0].Length : 0);

    private static InMemoryScanOperator Scan(params ColumnBatch[] batches) => new(ThreeCol, batches);

    private static ExecutionContext Ctx(IExecutionMemory? memory = null, CancellationToken cancellation = default)
        => new(memory ?? BoundedExecutionMemory.Unbounded, cancellation);

    private static List<ColumnBatch> Drain(IBatchStream stream)
    {
        var batches = new List<ColumnBatch>();
        while (stream.TryGetNext(out ColumnBatch? batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static List<int?> IntValues(ColumnBatch batch, int ordinal)
    {
        ColumnVector column = batch.SelectedColumn(ordinal);
        var values = new List<int?>(column.Length);
        for (int i = 0; i < column.Length; i++)
        {
            values.Add(column.IsNull(i) ? null : column.GetValue<int>(i));
        }

        return values;
    }

    private static List<string?> StrValues(ColumnBatch batch, int ordinal)
    {
        ColumnVector column = batch.SelectedColumn(ordinal);
        var values = new List<string?>(column.Length);
        for (int i = 0; i < column.Length; i++)
        {
            values.Add(column.IsNull(i) ? null : Encoding.UTF8.GetString(column.GetBytes(i)));
        }

        return values;
    }

    // A non-column-reference expression: stands in for a cast/computed expression that the v1
    // operators cannot yet evaluate (general expression evaluation is STORY-03.4.1).
    private sealed class ConstantTrueExpression(DataType type) : PhysicalExpression(type, nullable: false);

    // A child IBatchStream that deliberately never observes the cancellation token — used to prove the
    // filter/project operators own their cancellation checks rather than relying on the child. The
    // real InMemoryScanOperator child also checks the token, which masks a missing parent check in the
    // end-to-end (Backend.Open) tests; constructing the operator stream directly over this fake removes
    // that mask. An optional onPull hook lets a test cancel exactly when the parent pulls the child.
    private sealed class FakeChildStream(StructType schema, ColumnBatch[] batches, Action? onPull = null) : IBatchStream
    {
        private int _index;

        public StructType Schema { get; } = schema;

        public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
        {
            onPull?.Invoke();
            if (_index < batches.Length)
            {
                batch = batches[_index++];
                return true;
            }

            batch = null;
            return false;
        }

        public void Dispose()
        {
        }
    }

    // An ArrayPool<int> that tracks outstanding rentals so a test can prove the filter returns its
    // selection scratch buffer exactly once on every path (no leak, no double-return). Delegates to the
    // shared pool for real buffers; flags a Return of an array not currently rented as a double-return.
    private sealed class TrackingArrayPool : ArrayPool<int>
    {
        private readonly HashSet<int[]> _outstanding = new(ReferenceEqualityComparer.Instance);

        public int Rents { get; private set; }

        public int Returns { get; private set; }

        public int DoubleReturns { get; private set; }

        public int Outstanding => _outstanding.Count;

        public override int[] Rent(int minimumLength)
        {
            Rents++;
            int[] array = Shared.Rent(minimumLength);
            _outstanding.Add(array);
            return array;
        }

        public override void Return(int[] array, bool clearArray = false)
        {
            Returns++;
            if (!_outstanding.Remove(array))
            {
                DoubleReturns++; // returning a buffer that is not currently rented (double or foreign)
            }

            Shared.Return(array, clearArray);
        }
    }

    // ===== Scan (AC: emits source batches verbatim; empty source; empty batch; preserves selection) =====

    [Fact]
    public void Scan_EmitsSourceBatchesVerbatim_AndPopulatesMetrics()
    {
        ColumnBatch b1 = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, false));
        ColumnBatch b2 = Batch(ThreeCol, IntCol(3), StrCol("c"), BoolCol(true));
        InMemoryScanOperator scan = Scan(b1, b2);

        using IBatchStream stream = Backend.Open(scan, Ctx());
        Assert.Equal(ThreeCol, stream.Schema);

        List<ColumnBatch> batches = Drain(stream);
        Assert.Equal(2, batches.Count);
        Assert.Same(b1, batches[0]); // verbatim — same instance, no copy
        Assert.Same(b2, batches[1]);

        OperatorMetricsSnapshot m = scan.Metrics.Snapshot();
        Assert.Equal(3, m.InputRows);
        Assert.Equal(3, m.OutputRows);
        Assert.Equal(2, m.OutputBatches);
        Assert.True(m.BytesScanned > 0);
        Assert.True(m.ElapsedNanos >= 0);
    }

    [Fact]
    public void Scan_EmptySource_EmitsNothing()
    {
        InMemoryScanOperator scan = Scan();
        using IBatchStream stream = Backend.Open(scan, Ctx());

        Assert.False(stream.TryGetNext(out ColumnBatch? batch));
        Assert.Null(batch);
        Assert.Equal(0, scan.Metrics.Snapshot().OutputBatches);
    }

    [Fact]
    public void Scan_EmptyBatch_IsStillEmitted()
    {
        ColumnBatch empty = Batch(ThreeCol, IntCol(), StrCol(), BoolCol()); // 0 rows
        InMemoryScanOperator scan = Scan(empty);
        using IBatchStream stream = Backend.Open(scan, Ctx());

        Assert.True(stream.TryGetNext(out ColumnBatch? batch));
        Assert.Equal(0, batch!.LogicalRowCount);
        Assert.False(stream.TryGetNext(out _));

        OperatorMetricsSnapshot m = scan.Metrics.Snapshot();
        Assert.Equal(0, m.InputRows);
        Assert.Equal(1, m.OutputBatches);
    }

    [Fact]
    public void Scan_PreservesPreExistingSelection()
    {
        ColumnBatch full = Batch(ThreeCol, IntCol(10, 20, 30), StrCol("x", "y", "z"), BoolCol(true, true, true));
        ColumnBatch selected = full.WithSelection(new SelectionVector([2, 0]));
        InMemoryScanOperator scan = Scan(selected);

        using IBatchStream stream = Backend.Open(scan, Ctx());
        Assert.True(stream.TryGetNext(out ColumnBatch? batch));
        Assert.Same(selected, batch);
        Assert.Equal(2, batch!.LogicalRowCount);
        Assert.Equal(new int?[] { 30, 10 }, IntValues(batch, 0));
    }

    [Fact]
    public void Scan_IsLazy_NoWorkUntilFirstPull()
    {
        InMemoryScanOperator scan = Scan(Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true)));
        using IBatchStream stream = Backend.Open(scan, Ctx());

        // Opening the stream must move no rows; metrics stay zero until TryGetNext drives work.
        OperatorMetricsSnapshot m = scan.Metrics.Snapshot();
        Assert.Equal(0, m.InputRows);
        Assert.Equal(0, m.OutputBatches);
        Assert.Equal(0, m.BytesScanned);
    }

    // ===== Filter (AC: selects correct rows incl. all / none / nulls; selection-vector, no row copy) =====

    [Fact]
    public void Filter_SelectsTrueRows_ExcludesFalseAndNull()
    {
        ColumnBatch batch = Batch(
            ThreeCol,
            IntCol(1, 2, 3, 4, 5),
            StrCol("a", "b", "c", "d", "e"),
            BoolCol(true, false, null, null, true));
        var filter = new FilterOperator(Scan(batch), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.Equal(new int?[] { 1, 5 }, IntValues(result, 0));

        OperatorMetricsSnapshot m = filter.Metrics.Snapshot();
        Assert.Equal(5, m.InputRows);
        Assert.Equal(2, m.SelectedRows);
        Assert.Equal(2, m.OutputRows);
        Assert.Equal(1, m.OutputBatches);
    }

    [Fact]
    public void Filter_AllRowsPass_EmitsEveryRow()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, true, true));
        var filter = new FilterOperator(Scan(batch), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.Equal(new int?[] { 1, 2, 3 }, IntValues(result, 0));
        Assert.Equal(3, filter.Metrics.Snapshot().SelectedRows);
    }

    [Fact]
    public void Filter_NoRowsPass_EmitsNothing()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(false, false));
        var filter = new FilterOperator(Scan(batch), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        Assert.Empty(Drain(stream));

        OperatorMetricsSnapshot m = filter.Metrics.Snapshot();
        Assert.Equal(2, m.InputRows);
        Assert.Equal(0, m.SelectedRows);
        Assert.Equal(0, m.OutputRows);
        Assert.Equal(0, m.OutputBatches);
    }

    [Fact]
    public void Filter_AllNullPredicate_EmitsNothing()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(null, null));
        var filter = new FilterOperator(Scan(batch), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        Assert.Empty(Drain(stream));
        Assert.Equal(0, filter.Metrics.Snapshot().SelectedRows);
    }

    [Fact]
    public void Filter_DoesNotCopyColumns_SharesInputVectors()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, false, true));
        var filter = new FilterOperator(Scan(batch), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        Assert.True(stream.TryGetNext(out ColumnBatch? result));

        // Late materialization: the surviving rows are exposed via a selection vector; value columns
        // are shared by reference, never copied (DoD-08 zero-copy).
        Assert.NotNull(result!.Selection);
        Assert.Same(batch.Column(0), result.Column(0));
        Assert.Same(batch.Column(1), result.Column(1));
        Assert.Same(batch.Column(2), result.Column(2));
    }

    [Fact]
    public void Filter_ComposesOverExistingSelection()
    {
        ColumnBatch full = Batch(ThreeCol, IntCol(1, 2, 3, 4), StrCol("a", "b", "c", "d"), BoolCol(true, true, false, true));
        // Pre-select physical rows 3,2,1 → logical view values 4,3,2 with flags true,false,true.
        ColumnBatch preselected = full.WithSelection(new SelectionVector([3, 2, 1]));
        var filter = new FilterOperator(Scan(preselected), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        // Logical positions 0 and 2 pass (flags true, true) → physical rows 3 and 1 → values 4 and 2.
        Assert.Equal(new int?[] { 4, 2 }, IntValues(result, 0));
    }

    [Fact]
    public void Filter_NullPredicate_ViaSelectionAwareGather_ExcludesNullRows()
    {
        // Spark WHERE null semantics on the SELECTION-AWARE gather path (input.Selection != null): a
        // null predicate value in the selected view does not pass. The contiguous fast path covers
        // nulls elsewhere; this drives nulls specifically through the gather branch, which the red-team
        // and the query-execution seat both found untested for null-safety.
        ColumnBatch full = Batch(ThreeCol, IntCol(1, 2, 3, 4), StrCol("a", "b", "c", "d"), BoolCol(true, null, false, null));
        // Pre-select physical rows 0,1,3 → logical view flags true, null, null.
        ColumnBatch preselected = full.WithSelection(new SelectionVector([0, 1, 3]));
        var filter = new FilterOperator(Scan(preselected), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        // Only logical row 0 (physical 0, flag true) passes; both null flags are excluded.
        Assert.Equal(new int?[] { 1 }, IntValues(result, 0));
        Assert.Equal(1, filter.Metrics.Snapshot().SelectedRows);
    }

    [Fact]
    public void Filter_EmptyInputBatch_EmitsNothing()
    {
        ColumnBatch empty = Batch(ThreeCol, IntCol(), StrCol(), BoolCol());
        var filter = new FilterOperator(Scan(empty), FlagRef);

        using IBatchStream stream = Backend.Open(filter, Ctx());
        Assert.Empty(Drain(stream));
        Assert.Equal(0, filter.Metrics.Snapshot().InputRows);
    }

    [Fact]
    public void Filter_PredicateColumnOutOfRange_ThrowsAtOpen()
    {
        var filter = new FilterOperator(Scan(), new ColumnReference(9, DataTypes.BooleanType, nullable: true));
        Assert.Throws<ArgumentException>(() => Backend.Open(filter, Ctx()));
    }

    [Fact]
    public void Filter_PredicateColumnNotBoolean_ThrowsAtOpen()
    {
        // Predicate is declared boolean (so the FilterOperator ctor accepts it) but points at the
        // integer "id" column; the operator rejects the type mismatch when opened.
        var filter = new FilterOperator(Scan(), new ColumnReference(0, DataTypes.BooleanType, nullable: false));
        Assert.Throws<ArgumentException>(() => Backend.Open(filter, Ctx()));
    }

    [Fact]
    public void Filter_NonColumnReferencePredicate_IsUnsupported()
    {
        var filter = new FilterOperator(Scan(), new ConstantTrueExpression(DataTypes.BooleanType));
        var ex = Assert.Throws<UnsupportedOperatorException>(() => Backend.Open(filter, Ctx()));
        Assert.Equal(OperatorKind.Filter, ex.Kind);
        Assert.Equal(Backend.Name, ex.BackendName);
    }

    // ===== Project (AC: output batch selects/reorders columns per the project expressions) =====

    [Fact]
    public void Project_ReordersAndRenames_ZeroCopy()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, false));
        var outSchema = new StructType(
        [
            new StructField("label", DataTypes.StringType, nullable: true),
            new StructField("key", DataTypes.IntegerType, nullable: false),
        ]);
        var project = new ProjectOperator(Scan(batch), outSchema, [NameRef, IdRef]);

        using IBatchStream stream = Backend.Open(project, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.Equal(outSchema, result.Schema);
        Assert.Equal(new string?[] { "a", "b" }, StrValues(result, 0));
        Assert.Equal(new int?[] { 1, 2 }, IntValues(result, 1));

        // Zero-copy reorder: output column i *is* the referenced input column (shared by reference).
        Assert.Same(batch.Column(1), result.Column(0));
        Assert.Same(batch.Column(0), result.Column(1));

        OperatorMetricsSnapshot m = project.Metrics.Snapshot();
        Assert.Equal(2, m.InputRows);
        Assert.Equal(2, m.OutputRows);
        Assert.Equal(1, m.OutputBatches);
    }

    [Fact]
    public void Project_PreservesSelection()
    {
        ColumnBatch full = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, false, true));
        ColumnBatch selected = full.WithSelection(new SelectionVector([2, 0]));
        var outSchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(Scan(selected), outSchema, [IdRef]);

        using IBatchStream stream = Backend.Open(project, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.NotNull(result.Selection);
        Assert.Equal(2, result.LogicalRowCount);
        Assert.Equal(new int?[] { 3, 1 }, IntValues(result, 0));
    }

    [Fact]
    public void Project_DuplicateColumnReference_IsAllowed()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(7, 8), StrCol("a", "b"), BoolCol(true, true));
        var outSchema = new StructType(
        [
            new StructField("a", DataTypes.IntegerType, nullable: false),
            new StructField("b", DataTypes.IntegerType, nullable: false),
        ]);
        var project = new ProjectOperator(Scan(batch), outSchema, [IdRef, IdRef]);

        using IBatchStream stream = Backend.Open(project, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.Equal(new int?[] { 7, 8 }, IntValues(result, 0));
        Assert.Equal(new int?[] { 7, 8 }, IntValues(result, 1));
        Assert.Same(result.Column(0), result.Column(1)); // both alias the same input column
    }

    [Fact]
    public void Project_AllNullColumn_PreservesNulls()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol(null, null), BoolCol(true, false));
        var outSchema = new StructType([new StructField("label", DataTypes.StringType, nullable: true)]);
        var project = new ProjectOperator(Scan(batch), outSchema, [NameRef]);

        using IBatchStream stream = Backend.Open(project, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.Equal(new string?[] { null, null }, StrValues(result, 0));
    }

    [Fact]
    public void Project_ProjectionOrdinalOutOfRange_ThrowsAtOpen()
    {
        var outSchema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(Scan(), outSchema, [new ColumnReference(9, DataTypes.IntegerType, nullable: false)]);
        Assert.Throws<ArgumentException>(() => Backend.Open(project, Ctx()));
    }

    [Fact]
    public void Project_ProjectionTypeMismatch_ThrowsAtOpen()
    {
        // Projection declares integer (matching the output field) but references the string "name"
        // column; the operator rejects it when opened.
        var outSchema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(Scan(), outSchema, [new ColumnReference(1, DataTypes.IntegerType, nullable: false)]);
        Assert.Throws<ArgumentException>(() => Backend.Open(project, Ctx()));
    }

    [Fact]
    public void Project_NonColumnReference_IsUnsupported()
    {
        var outSchema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(Scan(), outSchema, [new ConstantTrueExpression(DataTypes.IntegerType)]);
        var ex = Assert.Throws<UnsupportedOperatorException>(() => Backend.Open(project, Ctx()));
        Assert.Equal(OperatorKind.Project, ex.Kind);
    }

    // ===== Pipeline + parity (DoD-21 distributed correctness) =====

    [Fact]
    public void Pipeline_ScanFilterProject_ProducesExpectedRows()
    {
        ColumnBatch batch = Batch(
            ThreeCol,
            IntCol(1, 2, 3, 4),
            StrCol("a", "b", "c", "d"),
            BoolCol(true, false, true, null));
        InMemoryScanOperator scan = Scan(batch);
        var filter = new FilterOperator(scan, FlagRef);
        var outSchema = new StructType(
        [
            new StructField("label", DataTypes.StringType, nullable: true),
            new StructField("key", DataTypes.IntegerType, nullable: false),
        ]);
        var project = new ProjectOperator(filter, outSchema, [NameRef, IdRef]);

        using IBatchStream stream = Backend.Open(project, Ctx());
        ColumnBatch result = Assert.Single(Drain(stream));

        Assert.Equal(new string?[] { "a", "c" }, StrValues(result, 0));
        Assert.Equal(new int?[] { 1, 3 }, IntValues(result, 1));

        // Metrics flow honestly through the pipeline.
        Assert.Equal(4, scan.Metrics.Snapshot().OutputRows);
        Assert.Equal(2, filter.Metrics.Snapshot().SelectedRows);
        Assert.Equal(2, project.Metrics.Snapshot().OutputRows);
    }

    [Fact]
    public void Pipeline_IsLazy_NoWorkUntilFirstPull()
    {
        InMemoryScanOperator scan = Scan(Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, true)));
        var filter = new FilterOperator(scan, FlagRef);
        var outSchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(filter, outSchema, [IdRef]);

        using IBatchStream stream = Backend.Open(project, Ctx());

        // Building the whole pipeline does no row work; every operator's metrics are zero until pull.
        Assert.Equal(0, scan.Metrics.Snapshot().OutputBatches);
        Assert.Equal(0, filter.Metrics.Snapshot().InputRows);
        Assert.Equal(0, project.Metrics.Snapshot().InputRows);
    }

    [Fact]
    public void Parity_CompiledMatchesInterpreted_ForPipeline()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return; // compiled tier is elided on a NativeAOT host — nothing to compare against
        }

        static (List<string?> Labels, List<int?> Keys) Run(IExecutionBackend backend)
        {
            ColumnBatch batch = Batch(
                ThreeCol,
                IntCol(1, 2, 3, 4, 5),
                StrCol("a", "b", "c", "d", "e"),
                BoolCol(true, false, true, true, null));
            var filter = new FilterOperator(Scan(batch), FlagRef);
            var outSchema = new StructType(
            [
                new StructField("label", DataTypes.StringType, nullable: true),
                new StructField("key", DataTypes.IntegerType, nullable: false),
            ]);
            var project = new ProjectOperator(filter, outSchema, [NameRef, IdRef]);
            using IBatchStream stream = backend.Open(project, new ExecutionContext(BoundedExecutionMemory.Unbounded));
            ColumnBatch result = Assert.Single(Drain(stream));
            return (StrValues(result, 0), IntValues(result, 1));
        }

        (List<string?> Labels, List<int?> Keys) interpreted =
            Run(ExecutionBackends.Select(new ExecutionBackendOptions { ForceInterpreted = true }));
        (List<string?> Labels, List<int?> Keys) compiled = Run(ExecutionBackends.Select());

        Assert.Equal(new string?[] { "a", "c", "d" }, interpreted.Labels);
        Assert.Equal(interpreted.Labels, compiled.Labels);
        Assert.Equal(interpreted.Keys, compiled.Keys);
    }

    // ===== Cancellation (honored at batch boundaries) =====

    [Theory]
    [InlineData("scan")]
    [InlineData("filter")]
    [InlineData("project")]
    public void Operators_CancelledBeforeFirstPull_Throw(string shape)
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, true));
        InMemoryScanOperator scan = Scan(batch);
        PhysicalOperator op = shape switch
        {
            "filter" => new FilterOperator(scan, FlagRef),
            "project" => new ProjectOperator(
                scan,
                new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]),
                [IdRef]),
            _ => scan,
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using IBatchStream stream = Backend.Open(op, Ctx(cancellation: cts.Token));

        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Filter_CancelledMidStream_ThrowsOnNextPull()
    {
        ColumnBatch b1 = Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true));
        ColumnBatch b2 = Batch(ThreeCol, IntCol(2), StrCol("b"), BoolCol(true));
        var filter = new FilterOperator(Scan(b1, b2), FlagRef);

        using var cts = new CancellationTokenSource();
        using IBatchStream stream = Backend.Open(filter, Ctx(cancellation: cts.Token));

        Assert.True(stream.TryGetNext(out _)); // first batch flows
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Filter_EntryCancellation_IsOwnedByFilter_NotChild()
    {
        // The filter must observe an already-cancelled token through its OWN entry check, independent of
        // the child. Driving it over a non-canceling EMPTY child isolates that check: if the filter's
        // entry ThrowIfCancellationRequested is removed, the empty child makes TryGetNext return false
        // instead of throwing — so this test fails on that mutation (it is not vacuous).
        var op = new FilterOperator(Scan(Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true))), FlagRef);
        var child = new FakeChildStream(ThreeCol, []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new InterpretedFilterStream(op, predicateOrdinal: 2, child, Ctx(cancellation: cts.Token));

        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Filter_PostChildPullCancellation_IsObservedBeforeProcessing()
    {
        // Cancellation that arrives DURING the child pull (token not yet cancelled at entry) must still
        // be observed by the filter's post-pull check before it does any per-batch work. The child
        // cancels the token as it yields a valid batch; if the filter's in-loop check is removed it
        // would process the batch and return true, so this test fails on that mutation.
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, true));
        using var cts = new CancellationTokenSource();
        var op = new FilterOperator(Scan(batch), FlagRef);
        var child = new FakeChildStream(ThreeCol, [batch], onPull: cts.Cancel);
        using var stream = new InterpretedFilterStream(op, predicateOrdinal: 2, child, Ctx(cancellation: cts.Token));

        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    // ===== Bounded memory (reserve / release / refusal) =====

    [Fact]
    public void Filter_ReservesSelectionMemory_AndReleasesOnDispose()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, true, true));
        var mem = new BoundedExecutionMemory(1024);
        var filter = new FilterOperator(Scan(batch), FlagRef);

        IBatchStream stream = Backend.Open(filter, new ExecutionContext(mem));
        Assert.True(stream.TryGetNext(out _));
        Assert.True(mem.ReservedBytes > 0); // selection vector reserved while the batch is in flight
        Assert.True(filter.Metrics.Snapshot().PeakMemoryBytes > 0);

        stream.Dispose();
        Assert.Equal(0, mem.ReservedBytes); // released on dispose
    }

    [Fact]
    public void Filter_ReleasesReservation_BetweenBatches()
    {
        ColumnBatch b1 = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, true, true));
        ColumnBatch b2 = Batch(ThreeCol, IntCol(4), StrCol("d"), BoolCol(true));
        var mem = new BoundedExecutionMemory(1024);
        var filter = new FilterOperator(Scan(b1, b2), FlagRef);

        using IBatchStream stream = Backend.Open(filter, new ExecutionContext(mem));

        Assert.True(stream.TryGetNext(out _));
        long afterFirst = mem.ReservedBytes; // 3 surviving rows
        Assert.True(stream.TryGetNext(out _));
        long afterSecond = mem.ReservedBytes; // 1 surviving row — previous reservation was released

        Assert.True(afterFirst > 0);
        Assert.True(afterSecond > 0);
        Assert.True(afterSecond < afterFirst); // not accumulating across batches

        Assert.False(stream.TryGetNext(out _));
        Assert.Equal(0, mem.ReservedBytes); // released after the stream drains
    }

    [Fact]
    public void Filter_OverBudget_ThrowsExecutionMemoryException()
    {
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, true, true));
        var mem = new BoundedExecutionMemory(4); // room for a single int, but 3 rows survive
        var filter = new FilterOperator(Scan(batch), FlagRef);

        using IBatchStream stream = Backend.Open(filter, new ExecutionContext(mem));
        var ex = Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));

        Assert.Equal(3 * sizeof(int), ex.RequestedBytes);
        Assert.Equal(4, ex.BudgetBytes);
    }

    [Fact]
    public void Project_ReleasesReservation_BetweenBatches()
    {
        ColumnBatch b1 = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, true));
        ColumnBatch b2 = Batch(ThreeCol, IntCol(3), StrCol("c"), BoolCol(true));
        var mem = new BoundedExecutionMemory(1024);
        var outSchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(Scan(b1, b2), outSchema, [IdRef]);

        using IBatchStream stream = Backend.Open(project, new ExecutionContext(mem));

        Assert.True(stream.TryGetNext(out _));
        long afterFirst = mem.ReservedBytes;
        Assert.True(stream.TryGetNext(out _));
        long afterSecond = mem.ReservedBytes;

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, afterSecond); // per-batch reservation, not accumulating

        Assert.False(stream.TryGetNext(out _));
        Assert.Equal(0, mem.ReservedBytes);
    }

    [Fact]
    public void Project_EntryCancellation_IsOwnedByProject_NotChild()
    {
        // Project's single entry check must observe an already-cancelled token without relying on the
        // child. Over a non-canceling child that has a batch to yield, removing the project's
        // ThrowIfCancellationRequested would process the batch and return true — so this fails on that
        // mutation (not vacuous; the InMemoryScan child masks it in the Backend.Open path).
        var outSchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        ColumnBatch batch = Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true));
        var op = new ProjectOperator(Scan(batch), outSchema, [IdRef]);
        var child = new FakeChildStream(ThreeCol, [batch]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new InterpretedProjectStream(op, [0], child, Ctx(cancellation: cts.Token));

        Assert.Throws<OperationCanceledException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Project_OverBudget_ThrowsExecutionMemoryException()
    {
        // The projection reserves one sizeof(long) reference token per output column before allocating
        // the column array. A budget below that must fail closed with a typed ExecutionMemoryException
        // and leak no reservation. (Mirrors Filter_OverBudget_ThrowsExecutionMemoryException; without a
        // reserve-before-allocate refusal this passes silently — see the Quality finding.)
        ColumnBatch batch = Batch(ThreeCol, IntCol(1, 2), StrCol("a", "b"), BoolCol(true, true));
        var outSchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var mem = new BoundedExecutionMemory(4); // one projection reference needs sizeof(long) == 8
        var project = new ProjectOperator(Scan(batch), outSchema, [IdRef]);

        using IBatchStream stream = Backend.Open(project, new ExecutionContext(mem));
        var ex = Assert.Throws<ExecutionMemoryException>(() => stream.TryGetNext(out _));

        Assert.Equal(sizeof(long), ex.RequestedBytes);
        Assert.Equal(4, ex.BudgetBytes);
        Assert.Equal(0, mem.ReservedBytes); // a refused reservation leaks nothing
    }

    [Fact]
    public void Filter_AfterDispose_ThrowsObjectDisposed_OwnedByFilter()
    {
        // TryGetNext after Dispose must fail fast through the filter's OWN guard. A non-disposing
        // FakeChildStream (which would otherwise yield a batch) isolates it: removing the filter's
        // ObjectDisposedException.ThrowIf would let it process the child batch and return true.
        var op = new FilterOperator(Scan(Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true))), FlagRef);
        ColumnBatch batch = Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true));
        var stream = new InterpretedFilterStream(op, predicateOrdinal: 2, new FakeChildStream(ThreeCol, [batch]), Ctx());
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Project_AfterDispose_ThrowsObjectDisposed_OwnedByProject()
    {
        // Same fail-fast contract for project, isolated from the child via a non-disposing fake.
        var outSchema = new StructType([new StructField("key", DataTypes.IntegerType, nullable: false)]);
        var op = new ProjectOperator(Scan(Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true))), outSchema, [IdRef]);
        ColumnBatch batch = Batch(ThreeCol, IntCol(1), StrCol("a"), BoolCol(true));
        var stream = new InterpretedProjectStream(op, [0], new FakeChildStream(ThreeCol, [batch]), Ctx());
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.TryGetNext(out _));
    }

    [Fact]
    public void Filter_ReturnsRentedSelectionBuffer_ExactlyOnce_OnEveryPath()
    {
        // The selection scratch buffer must be returned to the pool exactly once on every path —
        // surviving rows, a fully-filtered drop, and drain — never leaked, never double-returned. A
        // tracking pool proves it: a leak leaves Outstanding > 0; a double/foreign return is counted.
        // (The InMemoryScan child does not rent, so every rent/return here is the filter's.)
        ColumnBatch b1 = Batch(ThreeCol, IntCol(1, 2, 3), StrCol("a", "b", "c"), BoolCol(true, false, true)); // some pass
        ColumnBatch b2 = Batch(ThreeCol, IntCol(4, 5), StrCol("d", "e"), BoolCol(false, false));              // none pass (drop)
        var pool = new TrackingArrayPool();
        var op = new FilterOperator(Scan(b1, b2), FlagRef);
        using var stream = new InterpretedFilterStream(
            op, predicateOrdinal: 2, Backend.Open(Scan(b1, b2), Ctx()), Ctx(), pool);

        Drain(stream);

        Assert.True(pool.Rents > 0);            // the filter actually rented
        Assert.Equal(pool.Rents, pool.Returns); // returned as many as rented
        Assert.Equal(0, pool.Outstanding);      // nothing leaked
        Assert.Equal(0, pool.DoubleReturns);    // nothing returned twice
    }

    [Fact]
    public void Open_BareScanOperator_IsUnsupported_AndPointsAtInMemoryScan()
    {
        var scan = new ScanOperator(ThreeCol, "src://table");
        var ex = Assert.Throws<UnsupportedOperatorException>(() => Backend.Open(scan, Ctx()));
        Assert.Equal(OperatorKind.Scan, ex.Kind);
        Assert.Contains("InMemoryScanOperator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Backend.Open(null!, Ctx()));
        Assert.Throws<ArgumentNullException>(() => Backend.Open(Scan(), null!));
    }

    [Theory]
    [InlineData(typeof(InMemoryScanOperator))]
    [InlineData(typeof(ExecutionMemoryException))]
    [InlineData(typeof(InterpretedOperators))]
    [InlineData(typeof(InterpretedScanStream))]
    [InlineData(typeof(InterpretedFilterStream))]
    [InlineData(typeof(InterpretedProjectStream))]
    public void NewExecutionTypes_AreAotClean_NoDynamicCodeRequired(Type type)
        => Assert.Empty(type.GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute), inherit: false));
}
