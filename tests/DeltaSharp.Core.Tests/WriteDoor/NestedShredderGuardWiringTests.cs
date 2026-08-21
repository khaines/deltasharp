using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DeltaSharp.Core.Tests.WriteDoor;

/// <summary>
/// #841 §2.3c source invariant. The nested write path's two CROSS-LEAF guards —
/// <c>NestedColumnShredder.ValidateStructNullParity</c> and <c>NestedColumnShredder.ValidateMapParallelLevels</c>
/// — protect a defect class that no per-leaf check can see: level streams that are each individually
/// well-formed but jointly describe a different table than the source (a struct whose children disagree about
/// whether the struct is null; a map whose key and value streams re-partition entries across rows). Such a file
/// is published silently and Spark reads WRONG ROWS.
/// <para>
/// Their BODIES are pinned by fault-injected negatives in <c>NestedCrossLeafGuardTests</c>. That is blind to the
/// BINDING: wrapping each invocation in <c>if (rowCount &lt; 0)</c> leaves the guards present, fully tested, and
/// never invoked — and the whole suite stays green. This guard closes that hole the same way
/// <c>storage-exception-producer-inventory.tsv</c> closes the message-hygiene one: by asserting a property of
/// the SOURCE rather than of a runtime path, so no additional mutable corruption seam has to ship in Release.
/// </para>
/// <para>
/// The invariant asserted for each guard is: exactly ONE invocation exists in all of
/// <c>src/DeltaSharp.Storage</c>; it sits in the expected shredder method; NO skip-capable construct
/// (<c>if</c>/<c>switch</c>/ternary/short-circuit/<c>while</c>/<c>catch</c>) stands between it and that method's
/// body; it shares a block with a leaf write; and it precedes EVERY leaf write in the method. Together those
/// mean a leaf cannot be written without the guard having run over it.
/// </para>
/// </summary>
public sealed class NestedShredderGuardWiringTests
{
    private const string ShredderFile = "Parquet/NestedColumnShredder.cs";
    private const string WriterFile = "Parquet/ParquetFileWriter.cs";

    // The call that actually emits a leaf's levels + values into the row group. Every cross-leaf guard must
    // dominate every one of these within its lane.
    private const string LeafWrite = "WriteLeafAsync";

    [Theory]
    [InlineData("ValidateStructNullParity", "WriteStructAsync")]
    [InlineData("ValidateMapParallelLevels", "WriteMapAsync")]
    public void CrossLeafGuard_IsInvokedUnconditionally_ExactlyOnce_BeforeEveryLeafWrite(
        string guard, string lane)
    {
        List<InvocationExpressionSyntax> calls = InvocationsOf(guard, StorageSourceFiles()).ToList();
        Assert.True(
            calls.Count == 1,
            $"'{guard}' must be invoked exactly once in src/DeltaSharp.Storage; found {calls.Count}. A guard "
            + "with no call site is a no-op, and a second call site means the lanes disagree about who checks.");

        InvocationExpressionSyntax call = calls[0];
        MethodDeclarationSyntax? method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        Assert.NotNull(method);
        Assert.Equal(lane, method!.Identifier.ValueText);

        // (a) Unconditional: nothing between the call and the lane's body may skip it.
        foreach (SyntaxNode ancestor in call.Ancestors())
        {
            if (ancestor == method)
            {
                break;
            }

            Assert.False(
                IsSkipCapable(ancestor),
                $"'{guard}' is nested inside a {ancestor.Kind()} within '{lane}', so it can be skipped. §2.3c "
                + "cross-leaf validation is unconditional — every leaf write must be dominated by it.");
        }

        // (b) Co-located with a leaf write, so it cannot be hoisted into a branch the writes do not share.
        BlockSyntax block = call.Ancestors().OfType<BlockSyntax>().First();
        Assert.Contains(
            InvocationsOf(LeafWrite, new[] { block }),
            _ => true);

        // (c) Dominating: no leaf write in the lane precedes it.
        foreach (InvocationExpressionSyntax write in InvocationsOf(LeafWrite, new[] { (SyntaxNode)method }))
        {
            Assert.True(
                write.SpanStart > call.SpanStart,
                $"'{lane}' invokes {LeafWrite} before '{guard}'; a leaf would be written before the cross-leaf "
                + "check could reject the file.");
        }
    }

    [Fact]
    public void LevelBufferCostPerSlot_MatchesTheIntStreamsEachLaneActuallyRents()
    {
        // Q3. §2.9.2's row-group planner sizes every split from LevelBufferBytesPerSlot. Asserting that table
        // against a hand-written copy of itself is self-referential: a lane that starts renting a sixth level
        // stream keeps a green suite while every row group silently rents more than the budget it was planned
        // against. This binds the DECLARED cost to the rents the lanes actually issue, both read from source,
        // so drift is a test failure rather than an invisible over-rent.
        //
        // #845 item 1 also binds the CALL SITE: each lane must pass LevelBufferBytesPerSlot(<its OWN type>) into
        // its slot-count helper. The table matching the rents is not enough — a lane could still pass a looser
        // (wrong-type) bytesPerSlot into its helper and get a more permissive backstop than its own transient
        // warrants. Only the planned path is exposed today, but an out-of-tree hand-built-segments caller is,
        // so the coupling is pinned at the source.
        SyntaxNode shredder = Assert.Single(
            StorageSourceFiles(),
            t => Path.GetFileName(t.FilePath) == "NestedColumnShredder.cs").GetRoot();

        var lanes = new (string Type, string Lane, string SlotHelper, string TypeVar)[]
        {
            ("MapType", "WriteMapAsync", "CountMapSlots", "mapType"),
            ("ArrayType", "WriteListAsync", "CountListSlots", "arrayType"),
            ("StructType", "WriteStructAsync", "TotalSegmentRows", "structType"),
        };

        MethodDeclarationSyntax cost = Assert.Single(
            shredder.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.ValueText == "LevelBufferBytesPerSlot");
        SwitchExpressionSyntax table = Assert.Single(
            cost.DescendantNodes().OfType<SwitchExpressionSyntax>());

        foreach ((string type, string lane, string slotHelper, string typeVar) in lanes)
        {
            MethodDeclarationSyntax method = Assert.Single(
                shredder.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                m => m.Identifier.ValueText == lane);

            int rented = method.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Count(i => InvokedName(i.Expression) == "Rent"
                    && i.Expression.ToString().Contains("ArrayPool<int>", StringComparison.Ordinal));
            Assert.True(rented > 0, $"'{lane}' rents no int level buffer, so the cost table cannot be bound.");

            // The arm binds its own type into a declaration pattern (`MapType map => …`) because it folds that
            // type's leaf value width; match on the declared type, then extract the level-stream count from the
            // `n * sizeof(int)` SUB-EXPRESSION.
            SwitchExpressionArmSyntax arm = Assert.Single(
                table.Arms, a => a.Pattern is DeclarationPatternSyntax d && d.Type.ToString() == type);
            Assert.Equal(rented, DeclaredIntStreams(arm.Expression, type));

            // #845 item 1: the lane feeds LevelBufferBytesPerSlot(<its OWN type var>) into its slot-count helper.
            InvocationExpressionSyntax slotCall = Assert.Single(
                method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => InvokedName(i.Expression) == slotHelper);
            Assert.Contains(
                slotCall.ArgumentList.Arguments,
                a => a.Expression is InvocationExpressionSyntax inv
                    && InvokedName(inv.Expression) == "LevelBufferBytesPerSlot"
                    && inv.ArgumentList.Arguments.Count == 1
                    && inv.ArgumentList.Arguments[0].Expression.ToString() == typeVar);
        }
    }

    // `n * sizeof(int)` declares n concurrent level streams; a bare `sizeof(int)` declares one. The arm folds a
    // leaf value-width term (#845 item 2), so the level-stream count is the `n * sizeof(int)` (or bare
    // `sizeof(int)`) SUB-EXPRESSION, not the whole arm.
    private static int DeclaredIntStreams(ExpressionSyntax expression, string type)
    {
        foreach (SyntaxNode node in expression.DescendantNodesAndSelf())
        {
            if (node is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.MultiplyExpression)
                && b.Left is LiteralExpressionSyntax literal
                && b.Right is SizeOfExpressionSyntax)
            {
                return (int)literal.Token.Value!;
            }
        }

        if (expression.DescendantNodesAndSelf().OfType<SizeOfExpressionSyntax>().Any())
        {
            return 1;
        }

        throw new InvalidOperationException(
            $"LevelBufferBytesPerSlot's '{type}' arm is not expressed as a count of int level streams: "
            + expression.ToString());
    }

    [Fact]
    public void ScalarStringAndBinaryWrite_UseTheZeroAllocGenericLane_AndPlumbCancellation()
    {
        // #845 item 5 (design §2.3b N5). The scalar string/binary lanes were migrated OFF the per-value
        // Encoding.UTF8.GetString + non-generic string WriteAsync (which allocated one string per value and
        // dropped the CancellationToken) ONTO the zero-alloc generic ReadOnlyMemory<T> lane the other scalar
        // arms use. Pin BOTH halves at the source so neither can regress: no lane may reintroduce the per-value
        // GetString allocation, and each must thread the token into the write. A string and its
        // ReadOnlyMemory<char> encode identically in Parquet.Net 6.1.0, so byte output is unchanged (covered
        // behaviorally by the round-trip suite).
        SyntaxNode writer = Assert.Single(
            StorageSourceFiles(),
            t => Path.GetFileName(t.FilePath) == "ParquetFileWriter.cs").GetRoot();

        var lanes = new (string Method, string Element)[]
        {
            ("WriteStringAsync", "ReadOnlyMemory<char>"),
            ("WriteBinaryAsync", "ReadOnlyMemory<byte>"),
        };

        foreach ((string methodName, string element) in lanes)
        {
            MethodDeclarationSyntax method = Assert.Single(
                writer.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                m => m.Identifier.ValueText == methodName);

            // No per-value string allocation may creep back in.
            Assert.DoesNotContain(
                method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => InvokedName(i.Expression) == "GetString");

            InvocationExpressionSyntax write = Assert.Single(
                method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                i => InvokedName(i.Expression) == "WriteAsync");

            // The write goes through the generic zero-alloc lane: WriteAsync<ReadOnlyMemory<char|byte>>(…).
            GenericNameSyntax generic = Assert.IsType<GenericNameSyntax>(
                ((MemberAccessExpressionSyntax)write.Expression).Name);
            Assert.Equal(
                element, Assert.Single(generic.TypeArgumentList.Arguments).ToString());

            // The CancellationToken is plumbed into the write.
            Assert.Contains(
                write.ArgumentList.Arguments,
                a => a.Expression.ToString() == "cancellationToken");
        }
    }

    [Fact]
    public void RowGroupSegmentFaultSeam_IsATestOnlyOverride_NoProductionTypeCanPerturbSegments()
    {
        // N1. The §2.4b reconciliation is driven from a real WriteAsync by perturbing a row group's segments.
        // That seam must NOT be a settable field/property on a shipping writer — a mutable corruption switch in
        // the Release assembly is reachable by any code holding the instance. It is a `protected virtual` hook
        // instead, so perturbing anything requires authoring a SUBCLASS, and no production type does.
        List<SyntaxTree> trees = StorageSourceFiles().ToList();

        Assert.DoesNotContain(
            trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>()),
            p => p.Identifier.ValueText == "RowGroupSegmentFault");

        MethodDeclarationSyntax hook = Assert.Single(
            trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()),
            m => m.Identifier.ValueText == "OnRowGroupSegmentsCollected");
        Assert.Contains(hook.Modifiers, m => m.IsKind(SyntaxKind.ProtectedKeyword));
        Assert.Contains(hook.Modifiers, m => m.IsKind(SyntaxKind.VirtualKeyword));
        Assert.DoesNotContain(hook.Modifiers, m => m.IsKind(SyntaxKind.OverrideKeyword));

        // No production type derives from the writer, so the base (identity) hook is the only one that ships.
        foreach (SyntaxTree tree in trees)
        {
            foreach (TypeDeclarationSyntax type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type.BaseList is null)
                {
                    continue;
                }

                Assert.DoesNotContain(
                    type.BaseList.Types,
                    b => b.Type.ToString() is "ParquetFileWriter" or "Parquet.ParquetFileWriter");
            }
        }
    }

    [Fact]
    public void ParquetFileWriterSource_IsPresent_SoTheSeamAssertionsCannotVacuouslyPass()
    {
        // Non-vacuity: the scan resolves the real writer source, so a renamed/moved file fails here rather than
        // silently turning the assertions above into empty sets.
        Assert.True(File.Exists(Path.Combine(StorageSourceRoot(), WriterFile.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(StorageSourceRoot(), ShredderFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static bool IsSkipCapable(SyntaxNode node) => node switch
    {
        IfStatementSyntax or SwitchStatementSyntax or SwitchSectionSyntax or SwitchExpressionSyntax
            or ConditionalExpressionSyntax or ConditionalAccessExpressionSyntax or WhileStatementSyntax
            or DoStatementSyntax or CatchClauseSyntax => true,
        BinaryExpressionSyntax b => b.IsKind(SyntaxKind.LogicalAndExpression)
            || b.IsKind(SyntaxKind.LogicalOrExpression)
            || b.IsKind(SyntaxKind.CoalesceExpression),
        _ => false,
    };

    private static IEnumerable<InvocationExpressionSyntax> InvocationsOf(
        string name, IEnumerable<SyntaxTree> trees) =>
        InvocationsOf(name, trees.Select(t => t.GetRoot()));

    private static IEnumerable<InvocationExpressionSyntax> InvocationsOf(
        string name, IEnumerable<SyntaxNode> roots) =>
        roots.SelectMany(r => r.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => InvokedName(i.Expression) == name);

    private static string? InvokedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private static IEnumerable<SyntaxTree> StorageSourceFiles() =>
        Directory.EnumerateFiles(StorageSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
            .ToList();

    private static string StorageSourceRoot() => Path.Combine(RepoRoot(), "src", "DeltaSharp.Storage");

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "DeltaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate DeltaSharp.sln above '{AppContext.BaseDirectory}'.");
    }
}
