using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Engine.Columnar;
using DeltaSharp.Engine.Execution;
using DeltaSharp.Engine.Types;
using Xunit;
using ExecutionContext = DeltaSharp.Engine.Execution.ExecutionContext;

namespace DeltaSharp.Engine.Tests.Execution;

/// <summary>
/// Exercises STORY-03.1.1 backend &amp; operator execution contracts: typed input/output operator
/// nodes, the per-operator metrics surface, the precise unsupported-operator error (no
/// row-at-a-time fallback), and the AOT-cleanliness of the interpreter path.
/// </summary>
public class ExecutionContractsTests
{
    private static StructType Schema => new(
    [
        new StructField("a", DataTypes.IntegerType, nullable: false),
        new StructField("b", DataTypes.LongType, nullable: true),
    ]);

    private static ScanOperator Scan() => new(Schema, "src://table");

    private static ColumnReference IntCol => new(0, DataTypes.IntegerType, nullable: false);

    private static ColumnReference BoolPredicate => new(0, DataTypes.BooleanType, nullable: false);

    // ----- AC1/AC2: each v1 operator carries typed input/output schemas + a metrics surface -----

    [Fact]
    public void Scan_IsLeaf_WithOutputSchemaAndMetrics()
    {
        ScanOperator scan = Scan();
        Assert.Equal(OperatorKind.Scan, scan.Kind);
        Assert.Empty(scan.Children);
        Assert.Equal(Schema, scan.OutputSchema);
        Assert.NotNull(scan.Metrics);
    }

    [Fact]
    public void Filter_PreservesSchema_AndExposesInputSchema()
    {
        var filter = new FilterOperator(Scan(), BoolPredicate);
        Assert.Equal(OperatorKind.Filter, filter.Kind);
        Assert.Equal(Schema, filter.OutputSchema);
        Assert.Equal(Schema, filter.InputSchema(0));
        Assert.Single(filter.Children);
    }

    [Fact]
    public void Aggregate_GroupingKeyOrdinalOutOfRange_Throws()
    {
        var outSchema = new StructType([new StructField("k", DataTypes.IntegerType, nullable: false), new StructField("c", DataTypes.LongType, nullable: true)]);
        var badKey = new ColumnReference(9, DataTypes.IntegerType, nullable: false); // input has 2 fields
        Assert.Throws<ArgumentException>(() =>
            new AggregateOperator(Scan(), outSchema, groupingKeys: [badKey], aggregates: [new ColumnReference(1, DataTypes.LongType, true)]));
    }

    [Fact]
    public void Join_KeyOrdinalOutOfRange_Throws()
    {
        var bad = new ColumnReference(9, DataTypes.IntegerType, nullable: false); // inputs have 2 fields
        Assert.Throws<ArgumentException>(() => new JoinOperator(Scan(), Scan(), Schema, JoinType.Inner, [bad], [IntCol]));
    }

    [Fact]
    public void Project_TypedOutput_OneExpressionPerField()
    {
        var outSchema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: false)]);
        var project = new ProjectOperator(Scan(), outSchema, [IntCol]);
        Assert.Equal(OperatorKind.Project, project.Kind);
        Assert.Equal(outSchema, project.OutputSchema);
        Assert.Single(project.Projections);
    }

    [Fact]
    public void Aggregate_OutputIsKeysThenAggregates()
    {
        var outSchema = new StructType(
        [
            new StructField("a", DataTypes.IntegerType, nullable: false),
            new StructField("sum_b", DataTypes.LongType, nullable: true),
        ]);
        var agg = new AggregateOperator(Scan(), outSchema, groupingKeys: [IntCol], aggregates: [new ColumnReference(1, DataTypes.LongType, true)]);
        Assert.Equal(OperatorKind.Aggregate, agg.Kind);
        Assert.Single(agg.GroupingKeys);
        Assert.Single(agg.Aggregates);
    }

    [Fact]
    public void Sort_PreservesSchema_AndCarriesOrders()
    {
        var sort = new SortOperator(Scan(), [new SortOrder(IntCol, SortDirection.Descending, NullOrdering.NullsLast)]);
        Assert.Equal(OperatorKind.Sort, sort.Kind);
        Assert.Equal(Schema, sort.OutputSchema);
        Assert.True(sort.Global);
        Assert.Equal(SortDirection.Descending, sort.SortOrders[0].Direction);
    }

    [Fact]
    public void Join_HasTwoInputs_AndPairedKeys()
    {
        var join = new JoinOperator(Scan(), Scan(), Schema, JoinType.LeftOuter, [IntCol], [IntCol]);
        Assert.Equal(OperatorKind.Join, join.Kind);
        Assert.Equal(2, join.Children.Count);
        Assert.Equal(JoinType.LeftOuter, join.JoinType);
        Assert.Equal(Schema, join.InputSchema(1));
    }

    [Fact]
    public void ExchangeLocal_PreservesSchema_AndPartitions()
    {
        var ex = new ExchangeLocalOperator(Scan(), partitionCount: 8, [IntCol]);
        Assert.Equal(OperatorKind.ExchangeLocal, ex.Kind);
        Assert.Equal(8, ex.PartitionCount);
        Assert.Equal(Schema, ex.OutputSchema);
    }

    // ----- AC2: typed-contract validation rejects malformed shapes at construction -----

    [Fact]
    public void Filter_NonBooleanPredicate_Throws()
        => Assert.Throws<ArgumentException>(() => new FilterOperator(Scan(), IntCol));

    [Fact]
    public void Project_CountMismatch_Throws()
    {
        var outSchema = new StructType([new StructField("x", DataTypes.IntegerType, nullable: false)]);
        Assert.Throws<ArgumentException>(() => new ProjectOperator(Scan(), outSchema, [IntCol, IntCol]));
    }

    [Fact]
    public void Project_TypeMismatch_Throws()
    {
        var outSchema = new StructType([new StructField("x", DataTypes.LongType, nullable: false)]);
        Assert.Throws<ArgumentException>(() => new ProjectOperator(Scan(), outSchema, [IntCol]));
    }

    [Fact]
    public void Join_KeyCountMismatch_Throws()
        => Assert.Throws<ArgumentException>(() => new JoinOperator(Scan(), Scan(), Schema, JoinType.Inner, [IntCol], []));

    [Fact]
    public void Sort_NoOrders_Throws()
        => Assert.Throws<ArgumentException>(() => new SortOperator(Scan(), []));

    [Fact]
    public void Exchange_NonPositivePartitions_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ExchangeLocalOperator(Scan(), 0));

    [Fact]
    public void InputSchema_OutOfRange_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Scan().InputSchema(0));

    // ----- AC3: unsupported operators raise a precise error, never a row-at-a-time fallback -----

    [Theory]
    [InlineData(OperatorKind.Scan)]
    [InlineData(OperatorKind.Filter)]
    [InlineData(OperatorKind.Project)]
    public void Interpreted_SupportsV1ExecutableKinds(OperatorKind kind)
        => Assert.True(InterpretedVectorizedBackend.Instance.Supports(kind));

    [Theory]
    [InlineData(OperatorKind.Aggregate)]
    [InlineData(OperatorKind.Sort)]
    [InlineData(OperatorKind.Join)]
    [InlineData(OperatorKind.ExchangeLocal)]
    public void Interpreted_DeclaresRemainingKindsUnsupported_ForV1(OperatorKind kind)
        => Assert.False(InterpretedVectorizedBackend.Instance.Supports(kind));

    [Fact]
    public void Open_UnsupportedKind_ThrowsPreciseError_NoFallback()
    {
        IExecutionBackend backend = InterpretedVectorizedBackend.Instance;
        var ctx = new ExecutionContext(BoundedExecutionMemory.Unbounded);
        var sort = new SortOperator(Scan(), [new SortOrder(IntCol, SortDirection.Ascending, NullOrdering.NullsFirst)]);
        var ex = Assert.Throws<UnsupportedOperatorException>(() => backend.Open(sort, ctx));
        Assert.Equal(OperatorKind.Sort, ex.Kind);
        Assert.Equal(backend.Name, ex.BackendName);
        Assert.Contains("No row-at-a-time fallback", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<NotSupportedException>(ex);
    }

    [Fact]
    public void Open_NullArguments_Throw()
    {
        IExecutionBackend backend = InterpretedVectorizedBackend.Instance;
        Assert.Throws<ArgumentNullException>(() => backend.Open(null!, new ExecutionContext(BoundedExecutionMemory.Unbounded)));
        Assert.Throws<ArgumentNullException>(() => backend.Open(Scan(), null!));
    }

    // ----- AC4: the interpreter contracts require no dynamic code (NativeAOT-clean) -----

    [Theory]
    [InlineData(typeof(InterpretedVectorizedBackend))]
    [InlineData(typeof(PhysicalOperator))]
    [InlineData(typeof(ScanOperator))]
    [InlineData(typeof(PhysicalExpression))]
    [InlineData(typeof(IBatchStream))]
    [InlineData(typeof(ExecutionContext))]
    [InlineData(typeof(OperatorMetrics))]
    public void Contracts_AreAotClean_NoDynamicCodeRequired(Type type)
        => Assert.Empty(type.GetCustomAttributes(typeof(RequiresDynamicCodeAttribute), inherit: false));

    // ----- Metrics surface accumulates and snapshots -----

    [Fact]
    public void Metrics_AccumulateAndSnapshot()
    {
        var m = new OperatorMetrics();
        m.AddInputRows(100);
        m.AddSelectedRows(40);
        m.AddOutput(40);
        m.AddBytesScanned(1024);
        m.ObservePeakMemory(512);
        m.ObservePeakMemory(256); // lower; stays at high-water mark
        OperatorMetricsSnapshot s = m.Snapshot();
        Assert.Equal(100, s.InputRows);
        Assert.Equal(40, s.SelectedRows);
        Assert.Equal(40, s.OutputRows);
        Assert.Equal(1, s.OutputBatches);
        Assert.Equal(1024, s.BytesScanned);
        Assert.Equal(512, s.PeakMemoryBytes);
    }

    // ----- Bounded memory context -----

    [Fact]
    public void Memory_Refuses_OverBudget_AllowsAfterRelease()
    {
        var mem = new BoundedExecutionMemory(100);
        Assert.True(mem.TryReserve(80));
        Assert.False(mem.TryReserve(40)); // would exceed 100
        Assert.Equal(20, mem.AvailableBytes);
        mem.Release(80);
        Assert.True(mem.TryReserve(40));
    }

    [Fact]
    public void Memory_HugeRequest_DoesNotOverflowBudget()
    {
        var mem = new BoundedExecutionMemory(100);
        Assert.True(mem.TryReserve(80));
        // Int64-overflow attempt must be rejected, not wrap past the budget.
        Assert.False(mem.TryReserve(long.MaxValue));
        Assert.Equal(20, mem.AvailableBytes); // reservation rolled back
    }

    [Fact]
    public void ExecutionContext_NullMemory_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ExecutionContext(null!));

    [Fact]
    public void BatchStream_EmitsSchemaConformingBatches()
    {
        using IBatchStream stream = new EmptyStream(StructType.Empty);
        Assert.Equal(StructType.Empty, stream.Schema);
        Assert.False(stream.TryGetNext(out ColumnBatch? batch));
        Assert.Null(batch);
    }

    private sealed class EmptyStream(StructType schema) : IBatchStream
    {
        public StructType Schema { get; } = schema;

        public bool TryGetNext([NotNullWhen(true)] out ColumnBatch? batch)
        {
            batch = null;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
