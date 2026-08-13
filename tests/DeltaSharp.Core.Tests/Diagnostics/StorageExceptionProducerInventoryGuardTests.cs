using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DeltaSharp.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// #749 audit guard. The storage layer treats every <c>.Message</c> as untrusted tenant data, so every
/// exception-message PRODUCER — every interpolation hole that reaches a <c>*Exception</c> constructor or a
/// <c>*Exception.Factory(...)</c> call in <c>src/DeltaSharp.Storage</c> — must have each interpolated token
/// CLASSIFIED, and every attacker-influenceable (raw) token routed through the shared hygiene helper.
/// <para>
/// This is a <b>source-backed</b> guard, not a hand-maintained count or family-name denylist. It parses the
/// real storage sources with the C# compiler (<see cref="CSharpSyntaxTree"/>), builds a semantic model over
/// an <b>explicitly-anchored</b> reference set (the runtime's own BCL + <c>DeltaSharp.Abstractions</c>), and
/// for every exception-message interpolation hole resolves the token's TYPE. A token is auto-cleared when it
/// is either (a) wrapped in a hygiene helper whose call RESOLVES (via the semantic model) to an
/// ALLOWLISTED sanitizing/bounding method (<c>Sanitize</c>/<c>SanitizeAndJoin</c>/
/// <c>SanitizeAndJoinCounted</c>/<c>SanitizeTo</c>/<c>SanitizeToBudget</c>/<c>DescribePath</c>/
/// <c>DescribeSchema</c>/<c>DescribeType</c>) on <c>DeltaSharp.Diagnostics.DiagnosticText</c> or
/// <c>DeltaSharp.Storage.Delta.DiagnosticText</c>, or <c>LocalFileSystemBackend.Redact</c> — SANITIZED (a
/// bare <c>Sanitize</c>-named local does NOT clear, and <c>DiagnosticText.DescribeWithoutInner</c>, which
/// echoes <c>exception.Message</c> RAW, is DELIBERATELY NOT allowlisted); or
/// (b) resolved to a bounded value type (integral/enum/bool/char/<c>DateTimeOffset</c>/<c>Guid</c>/…) —
/// BOUNDED. Every remaining token MUST appear in the checked-in inventory
/// <c>storage-exception-producer-inventory.tsv</c>, keyed on the producer <b>site</b>
/// (file + enclosing TYPE chain + enclosing member + token), with an explicit classification and justification.
/// </para>
/// <para>
/// <b>Round-1/Round-2 hardening.</b> (1) The hygiene clearance is resolved by SEMANTIC MODEL and gated on an
/// explicit <c>(type, method)</c> ALLOWLIST, not a bare method-name match and not a whole-type clearance —
/// a forged local <c>Sanitize</c> identity does not auto-clear, and a <c>DescribeWithoutInner</c> wrapper (raw
/// <c>.Message</c> echo) no longer auto-clears as sanitized (Round-2); an unresolved symbol falls back to
/// residual (fail-safe). (2) Rows are keyed on the site <c>(file, type, member, token)</c> — the enclosing
/// TYPE chain (<c>INamedTypeSymbol.ToDisplayString()</c>) is now in the key, so a nested-type/overload/
/// <c>partial</c>-class method-name collision cannot alias two classifications, and a NEW unsanitized producer
/// reusing a generic name (<c>detail</c>/<c>context</c>/<c>reason</c>/…) cannot auto-clear. (3) Write mode
/// <see cref="Assert.Fail(string)"/>s after regeneration, so it can never be a green audit. (4) The Roslyn
/// reference set is anchored EXPLICITLY and the required anchor types are asserted resolved before scanning,
/// so a missing reference is a NAMED failure, not a silent reclassification. (5) Duplicate <c>.tsv</c> keys
/// fail rather than silently overwrite a strong class with a weaker one.
/// </para>
/// <para>
/// <b>Known blind spots — these remain REVIEWER obligations, not guard-enforced.</b> A message composed into
/// a local before it reaches the producer; <c>+</c>/<c>string.Format</c>/<c>StringBuilder</c> composition
/// (only interpolated-string holes are walked); a throw routed through a helper NOT named <c>*Exception</c>;
/// a whole-message pass-through (<c>new DeltaReadException(ex.Message, ex)</c>); a producer outside
/// <c>src/DeltaSharp.Storage</c>. The guard is an audit prompt for the interpolation-hole shape it walks; it
/// is not a proof of hygiene for every shape.
/// </para>
/// <para>
/// Regenerate the residual key set (after intentionally adding/removing a producer) by running this test with
/// the environment variable <c>DELTASHARP_WRITE_PRODUCER_INVENTORY=1</c>; it rewrites the site key columns
/// (preserving existing classifications) and then FAILS, so you classify any newly-added rows and re-run in
/// verify mode.
/// </para>
/// </summary>
public sealed class StorageExceptionProducerInventoryGuardTests
{
    private const string InventoryFileName = "storage-exception-producer-inventory.tsv";

    // Hygiene clearance is gated on the CONTAINING TYPE (resolved by the semantic model) AND on an EXPLICIT
    // (type, method) allowlist, not a bare method-name match and not a whole-type clearance. A whole-type
    // clearance is unsound because DiagnosticText.DescribeWithoutInner emits `exception.Message` RAW — an
    // interpolated raw-message pass-through wrapped in DescribeWithoutInner would auto-clear as sanitized.
    // Only the methods that actually SANITIZE/BOUND their argument clear. A local/foreign `Sanitize` does
    // NOT auto-clear (name match alone is forgeable and is rejected).
    private const string AbstractionsDiagnosticText = "DeltaSharp.Diagnostics.DiagnosticText";
    private const string StorageDiagnosticText = "DeltaSharp.Storage.Delta.DiagnosticText";
    private const string LocalBackend = "DeltaSharp.Storage.Backends.LocalFileSystemBackend";

    // The sanitizing/bounding methods on either DiagnosticText type that genuinely neutralize their argument.
    // DescribeWithoutInner is DELIBERATELY EXCLUDED (it echoes exception.Message raw). Any DescribeType/echo
    // that surfaces a raw token is likewise excluded. ROUND-4: `SanitizeEchoedToken` was REMOVED — it is a
    // private member of ColumnMapping, not of either DiagnosticText type, so it could never satisfy the
    // type-gated clearance; the entry was dead AND pre-authorized a future same-named DiagnosticText method.
    // Its three real call sites stay classified `sanitized-upstream` in the inventory. Every name here is
    // asserted to resolve to a real member of an allowlisted DiagnosticText type by
    // AssertClearingMethodsResolveToRealMembers, so a dead/over-permissive entry fails the guard.
    private static readonly HashSet<string> DiagnosticTextClearingMethods = new(StringComparer.Ordinal)
    {
        "Sanitize", "SanitizeAndJoin", "SanitizeAndJoinCounted", "SanitizeTo", "SanitizeToBudget",
        "DescribePath", "DescribeSchema", "DescribeType",
    };

    private static readonly HashSet<string> AllowedClasses = new(StringComparer.Ordinal)
    {
        // A compile-time constant string, or a bounded enum→label mapping, passed by the reader itself.
        "fixed",
        // A bounded Delta/CLR type-name render (DataType.TypeName / scalar SimpleString / Type.Name).
        "type-name",
        // A numeric/bounded value the model could not resolve (declaring assembly not referenced by the guard).
        "bounded",
        // The token is routed through a hygiene helper BEFORE it reaches the producer (sanitized-at-entry
        // local, a Redact()ed framework detail, a DescribePath()ed display path, a SanitizeEchoedToken()).
        "sanitized-upstream",
        // A DeltaSharp-generated internal name (staging temp basename) that is never tenant data.
        "internal-name",
        // A raw token intentionally surfaced with an explicit sink obligation documented in the log-routing doc.
        "raw-obligation",
    };

    [Fact]
    public void EveryStorageExceptionMessageProducerToken_IsClassified_WithNoInventoryDrift()
    {
        HashSet<ProducerSite> residual = ScanResidualProducerTokens();

        if (Environment.GetEnvironmentVariable("DELTASHARP_WRITE_PRODUCER_INVENTORY") == "1")
        {
            WriteInventory(residual, LoadInventoryForRegen());
            Assert.Fail(
                "inventory regenerated; classify new rows and re-run in verify mode. Write mode never passes "
                + "so it cannot be mistaken for a green audit.");
        }

        var inventory = LoadInventory();
        // 1. Every discovered residual token has an inventory row — a NEW unclassified producer is RED here.
        var missing = residual.Where(r => !inventory.ContainsKey(r))
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.Member, StringComparer.Ordinal).ThenBy(r => r.Token, StringComparer.Ordinal)
            .Select(r => $"{r.File}\t{r.Type}\t{r.Member}\t{r.Token}")
            .ToList();
        Assert.True(
            missing.Count == 0,
            "Unclassified storage exception-message producer token(s) found in source but absent from "
            + $"{InventoryFileName}. Either wrap the token in a hygiene helper or add a classified inventory row "
            + "(regenerate with DELTASHARP_WRITE_PRODUCER_INVENTORY=1):\n" + string.Join("\n", missing));

        // 2. Every inventory row still corresponds to a live producer — a stale row is RED (forces upkeep).
        var stale = inventory.Keys.Where(k => !residual.Contains(k))
            .OrderBy(k => k.File, StringComparer.Ordinal).ThenBy(k => k.Type, StringComparer.Ordinal)
            .ThenBy(k => k.Member, StringComparer.Ordinal).ThenBy(k => k.Token, StringComparer.Ordinal)
            .Select(k => $"{k.File}\t{k.Type}\t{k.Member}\t{k.Token}")
            .ToList();
        Assert.True(
            stale.Count == 0,
            $"Stale {InventoryFileName} row(s) that no longer match any storage producer site "
            + "(regenerate with DELTASHARP_WRITE_PRODUCER_INVENTORY=1):\n" + string.Join("\n", stale));

        // 3. Every row carries a valid classification and a non-empty justification.
        foreach (var (key, entry) in inventory)
        {
            Assert.True(
                AllowedClasses.Contains(entry.Class),
                $"{InventoryFileName} row '{key.File}\t{key.Type}\t{key.Member}\t{key.Token}' has invalid class "
                + $"'{entry.Class}'. Allowed: " + string.Join(", ", AllowedClasses.OrderBy(c => c, StringComparer.Ordinal)));
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Justification),
                $"{InventoryFileName} row '{key.File}\t{key.Type}\t{key.Member}\t{key.Token}' has an empty justification.");
        }
    }

    [Fact]
    public void ExplicitlyFlaggedProducers_AreCoveredByTheInventory_AndSanitizedAtTheDeclaringSite()
    {
        // #749 names these two families explicitly. Round-1 makes this SOURCE-BACKED rather than a tsv-only
        // assertion: the hygiene call must be PRESENT at the declaring site, so a refactor that drops the
        // sanitization is caught here as well as by the behavioural pins
        // (StorageMessageHygieneTests.NestedReader_ValidateShape_SanitizesColumnLabel and the
        // LocalFileSystemBackend staged-write hygiene tests).
        var inventory = LoadInventory();

        Assert.Contains(
            inventory,
            kv => kv.Key.File == "Parquet/NestedParquetColumnReader.cs" && kv.Key.Token == "columnName"
                && kv.Value.Class == "sanitized-upstream");
        AssertSourceContains(
            "Parquet/NestedParquetColumnReader.cs",
            "columnName = DiagnosticText.Sanitize(columnName);",
            "NestedParquetColumnReader must Sanitize `columnName` at entry (ValidateShape/ReadAsync).");

        Assert.Contains(
            inventory,
            kv => kv.Key.File == "Backends/LocalFileSystemBackend.cs" && kv.Key.Token == "_displayPath"
                && kv.Value.Class == "sanitized-upstream");
        AssertSourceContains(
            "Backends/LocalFileSystemBackend.cs",
            "_displayPath = DiagnosticText.DescribePath(displayPath);",
            "LocalFileSystemBackend.StagedWriteStream must set `_displayPath` via DescribePath at construction.");
    }

    [Fact]
    public void GuardOfTheGuard_DescribeWithoutInnerWrapper_IsNotCleared_ButSanitizingWrappersAre()
    {
        // ROUND-2 guard-of-the-guard. Hygiene clearance is a (type, method) ALLOWLIST, not whole-type. Prove
        // it directly against IsHygieneWrapped: a DiagnosticText.DescribeWithoutInner(...) wrapper — which
        // emits exception.Message RAW — must NOT clear, while Sanitize/DescribeSchema/DescribePath on the same
        // type MUST. A stub `DiagnosticText` in the DeltaSharp.Storage.Delta namespace makes the receiver type
        // resolve to the same display string the real one carries, so the allowlist is exercised exactly.
        const string source = @"
namespace DeltaSharp.Storage.Delta
{
    internal static class DiagnosticText
    {
        internal static string DescribeWithoutInner(System.Exception e) => e.Message;
        internal static string DescribeSchema(object s) => string.Empty;
        internal static string DescribePath(string s) => s;
        internal static string Sanitize(string s) => s;
    }
}

internal sealed class Probe
{
    public string RawEcho(System.Exception ex) => $""{DeltaSharp.Storage.Delta.DiagnosticText.DescribeWithoutInner(ex)}"";
    public string SchemaWrap(object s) => $""{DeltaSharp.Storage.Delta.DiagnosticText.DescribeSchema(s)}"";
    public string PathWrap(string s) => $""{DeltaSharp.Storage.Delta.DiagnosticText.DescribePath(s)}"";
    public string SanitizeWrap(string s) => $""{DeltaSharp.Storage.Delta.DiagnosticText.Sanitize(s)}"";
}";

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, ParseLegs[0]);
        var comp = CSharpCompilation.Create(
            "GuardOfGuardProbe", new[] { tree }, BuildReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        SemanticModel model = comp.GetSemanticModel(tree);

        var holes = tree.GetRoot().DescendantNodes()
            .OfType<InterpolatedStringExpressionSyntax>()
            .SelectMany(i => i.Contents.OfType<InterpolationSyntax>())
            .ToDictionary(
                h => ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)h.Expression).Expression).Name.Identifier.ValueText,
                h => h.Expression);

        Assert.False(
            IsHygieneWrapped(model, holes["DescribeWithoutInner"]),
            "DescribeWithoutInner echoes exception.Message RAW — it MUST NOT auto-clear a producer token. "
            + "A whole-type clearance (the Round-1 bug) would clear it green.");
        Assert.True(IsHygieneWrapped(model, holes["DescribeSchema"]), "DescribeSchema must clear.");
        Assert.True(IsHygieneWrapped(model, holes["DescribePath"]), "DescribePath must clear.");
        Assert.True(IsHygieneWrapped(model, holes["Sanitize"]), "Sanitize must clear.");
    }

    [Fact]
    public void EveryStorageSourceFile_ParsesCleanly_SoTheWalkCannotSilentlyDegrade()
    {
        var offenders = new List<string>();
        foreach (CSharpParseOptions leg in ParseLegs)
        {
            foreach (string file in EnumerateStorageSources())
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), leg, path: file);
                if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    offenders.Add(Path.GetRelativePath(StorageSourceRoot(), file));
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Storage source file(s) failed to PARSE on some parse leg (a parser/language drift would silently "
            + "degrade the producer walk):\n" + string.Join("\n", offenders.Distinct()));
    }

    // ----- scan -----

    private readonly record struct ProducerSite(string File, string Type, string Member, string Token);

    private static readonly CSharpParseOptions[] ParseLegs =
    [
        new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
            "NET", "NETCOREAPP", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0", "NET10_0_OR_GREATER", "RELEASE"),
        new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
            "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER", "DEBUG", "TRACE"),
    ];

    private static HashSet<ProducerSite> ScanResidualProducerTokens()
    {
        var refs = BuildReferences();
        var residual = new HashSet<ProducerSite>();

        foreach (CSharpParseOptions leg in ParseLegs)
        {
            var trees = new List<SyntaxTree>();
            var fileByTree = new Dictionary<SyntaxTree, string>();
            foreach (string file in EnumerateStorageSources())
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), leg, path: file);
                trees.Add(tree);
                fileByTree[tree] = Path.GetRelativePath(StorageSourceRoot(), file).Replace('\\', '/');
            }

            // Implicit global usings (ImplicitUsings=enable) so common BCL types resolve.
            trees.Add(CSharpSyntaxTree.ParseText(
                "global using System;\nglobal using System.Collections.Generic;\nglobal using System.IO;\n"
                + "global using System.Linq;\nglobal using System.Net.Http;\nglobal using System.Threading;\n"
                + "global using System.Threading.Tasks;\n", leg));

            var comp = CSharpCompilation.Create(
                "StorageProducerScan", trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            // A missing anchor reference must be a NAMED failure, never a silent reclassification of every
            // DataType/DiagnosticText token into "unresolved -> residual".
            AssertAnchorTypesResolved(comp);
            AssertClearingMethodsResolveToRealMembers(comp);

            foreach (SyntaxTree tree in trees)
            {
                if (!fileByTree.TryGetValue(tree, out string? rel))
                {
                    continue;
                }

                SemanticModel model = comp.GetSemanticModel(tree);
                foreach (InterpolatedStringExpressionSyntax interp in
                    tree.GetRoot().DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
                {
                    if (!IsExceptionMessage(interp))
                    {
                        continue;
                    }

                    foreach (InterpolationSyntax hole in interp.Contents.OfType<InterpolationSyntax>())
                    {
                        if (IsHygieneWrapped(model, hole.Expression))
                        {
                            continue; // SANITIZED
                        }

                        ITypeSymbol? type = model.GetTypeInfo(hole.Expression).Type
                            ?? model.GetTypeInfo(hole.Expression).ConvertedType;
                        if (type is not null && IsBoundedType(type))
                        {
                            continue; // BOUNDED
                        }

                        residual.Add(new ProducerSite(
                            rel, EnclosingTypeName(model, hole), EnclosingMemberName(hole), hole.Expression.ToString()));
                    }
                }
            }
        }

        return residual;
    }

    private static bool IsExceptionMessage(SyntaxNode interp)
    {
        for (SyntaxNode? n = interp.Parent; n is not null; n = n.Parent)
        {
            if (n is ObjectCreationExpressionSyntax oc && SimpleTypeName(oc.Type).EndsWith("Exception", StringComparison.Ordinal))
            {
                return true;
            }

            if (n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }
                && ma.Expression.ToString().EndsWith("Exception", StringComparison.Ordinal))
            {
                return true;
            }

            if (n is MethodDeclarationSyntax or LocalFunctionStatementSyntax or ClassDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    // ROUND-2: resolve the invocation via the SEMANTIC MODEL and gate on an EXPLICIT (type, method)
    // allowlist. A whole-type clearance is unsound: DiagnosticText.DescribeWithoutInner emits
    // `exception.Message` raw, so wrapping a raw-message pass-through in it would auto-clear. Clearance
    // requires the resolved method (or, when overload binding fails on an unresolved argument, the resolved
    // RECEIVER TYPE plus the syntactic method name) to be one of the sanitizing/bounding methods on a
    // DiagnosticText type, or LocalFileSystemBackend.Redact. An UNRESOLVED call on an unknown receiver falls
    // back to residual (fail-safe).
    private static bool IsHygieneWrapped(SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is not InvocationExpressionSyntax inv)
        {
            return false;
        }

        // Path 1: the invocation binds -> use the resolved method's containing type AND name. Handles Redact
        // and any DiagnosticText call whose arguments all resolve.
        if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol method)
        {
            string containing = method.ContainingType?.ToDisplayString() ?? string.Empty;
            if (containing is AbstractionsDiagnosticText or StorageDiagnosticText)
            {
                return DiagnosticTextClearingMethods.Contains(method.Name);
            }

            return containing == LocalBackend && method.Name == "Redact";
        }

        // Path 2: overload binding failed because an argument is unresolved (e.g. a Parquet.Net-typed
        // `field.Name`/`leaf.Path`), but a type-qualified call `DiagnosticText.Method(...)` still resolves its
        // RECEIVER independent of the arguments. Clear on the resolved receiver TYPE plus the SYNTACTIC method
        // name against the allowlist (never a bare name, never a whole-type clearance) — so a genuine
        // sanitizing DiagnosticText wrap clears while a DescribeWithoutInner-wrapped raw pass-through, or a
        // local/foreign `Sanitize`, stays residual.
        if (inv.Expression is MemberAccessExpressionSyntax ma)
        {
            string? receiverType = model.GetSymbolInfo(ma.Expression).Symbol switch
            {
                INamedTypeSymbol t => t.ToDisplayString(),
                _ => model.GetTypeInfo(ma.Expression).Type?.ToDisplayString(),
            };
            if (receiverType is AbstractionsDiagnosticText or StorageDiagnosticText)
            {
                return DiagnosticTextClearingMethods.Contains(ma.Name.Identifier.ValueText);
            }
        }

        return false;
    }

    private static bool IsBoundedType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return true;
        }

        if (type is INamedTypeSymbol { Name: "Nullable" } nullable && nullable.TypeArguments.Length == 1)
        {
            return IsBoundedType(nullable.TypeArguments[0]);
        }

        // NOTE: `decimal` is matched via SpecialType.System_Decimal above — ToDisplayString() renders the C#
        // keyword `decimal`, never `System.Decimal`, so a `"System.Decimal"` string match here would be DEAD.
        // The remaining entries have no C# keyword, so ToDisplayString() DOES render them fully qualified.
        return type.ToDisplayString() switch
        {
            "System.DateTimeOffset" or "System.DateTime" or "System.TimeSpan" or "System.Version"
                or "System.Int128" or "System.UInt128" or "System.Guid" => true,
            _ => false,
        };
    }

    // ROUND-2: the enclosing TYPE chain of a producer hole, resolved by the semantic model so a nested type
    // (LocalFileSystemBackend.StagedWriteStream) or a `partial`-class split renders its full containing chain
    // (INamedTypeSymbol.ToDisplayString()). Prefixing the key with the type disambiguates a method-name
    // collision across a nested type or an overload that would otherwise alias two distinct classifications
    // onto one (file, member, token) key. Falls back to the syntactic type identifier if the symbol does not
    // resolve (fail-safe: still a stable, non-empty discriminator).
    private static string EnclosingTypeName(SemanticModel model, SyntaxNode node)
    {
        for (SyntaxNode? n = node; n is not null; n = n.Parent)
        {
            if (n is BaseTypeDeclarationSyntax typeDecl)
            {
                if (model.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol sym)
                {
                    return sym.ToDisplayString();
                }

                return typeDecl.Identifier.ValueText;
            }
        }

        return "<file>";
    }

    // The enclosing member (method/local-function/ctor/property/accessor/indexer/field) of a producer hole,
    // so the inventory key is the producer SITE, not just (file, token). Innermost wins (a local function
    // inside a method keys to the local function). Field/property initializers fall back to the declared
    // variable name so a producer in a field initializer still has a stable site.
    private static string EnclosingMemberName(SyntaxNode node)
    {
        for (SyntaxNode? n = node; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case LocalFunctionStatementSyntax lf:
                    return lf.Identifier.ValueText;
                case MethodDeclarationSyntax m:
                    return m.Identifier.ValueText;
                case ConstructorDeclarationSyntax c:
                    return c.Identifier.ValueText + "..ctor";
                case AccessorDeclarationSyntax a when Ancestor<PropertyDeclarationSyntax>(a) is { } pp:
                    return pp.Identifier.ValueText + "." + a.Keyword.ValueText;
                case PropertyDeclarationSyntax p:
                    return p.Identifier.ValueText;
                case IndexerDeclarationSyntax:
                    return "this[]";
                case VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax:
                    return v.Identifier.ValueText;
            }
        }

        return "<file>";
    }

    private static T? Ancestor<T>(SyntaxNode node)
        where T : SyntaxNode
    {
        for (SyntaxNode? n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is T t)
            {
                return t;
            }
        }

        return null;
    }

    private static string SimpleTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        _ => type.ToString(),
    };

    // ----- references -----

    // Build the reference set EXPLICITLY from anchor types (so a missing one is a NAMED failure), unioned
    // with the runtime's trusted-platform assemblies for the broad BCL surface. DeltaSharp.Storage is
    // intentionally excluded (it is compiled from source; referencing its dll would duplicate every type),
    // and Parquet.Net is intentionally unreferenced (its tokens resolve as unresolved -> residual -> inventory,
    // which is deterministic and fail-safe).
    private static readonly (string Name, Func<string> Location)[] AnchorAssemblies =
    [
        ("System.Private.CoreLib (object)", () => typeof(object).Assembly.Location),
        ("System.Text.RegularExpressions (Regex)", () => typeof(Regex).Assembly.Location),
        ("DeltaSharp.Abstractions (DataType)", () => typeof(DataType).Assembly.Location),
        ("DeltaSharp.Abstractions (DiagnosticText)", () => typeof(SharedDiagnosticText).Assembly.Location),
    ];

    private static List<MetadataReference> BuildReferences()
    {
        var byPath = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);

        // 1. Explicit anchors first — assert each resolves to a real file so a broken build is NAMED.
        foreach ((string name, Func<string> location) in AnchorAssemblies)
        {
            string path = location();
            Assert.False(
                string.IsNullOrEmpty(path) || !File.Exists(path),
                $"Required Roslyn anchor reference '{name}' resolved to a missing location '{path}'. Without it "
                + "the scan would silently reclassify every dependent token instead of resolving it.");
            byPath[path] = MetadataReference.CreateFromFile(path);
        }

        // 2. Broad BCL from the trusted-platform assemblies (excluding the from-source Storage dll).
        string tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (string path in tpa.Split(Path.PathSeparator))
        {
            if (path.Length == 0 || !File.Exists(path) || byPath.ContainsKey(path))
            {
                continue;
            }

            if (path.EndsWith("DeltaSharp.Storage.dll", StringComparison.Ordinal))
            {
                continue;
            }

            byPath[path] = MetadataReference.CreateFromFile(path);
        }

        return [.. byPath.Values];
    }

    private static void AssertAnchorTypesResolved(CSharpCompilation comp)
    {
        foreach (string metadataName in new[] { "DeltaSharp.Types.DataType", "DeltaSharp.Diagnostics.DiagnosticText" })
        {
            Assert.True(
                comp.GetTypeByMetadataName(metadataName) is not null,
                $"Anchor type '{metadataName}' did not resolve in the producer-scan compilation. A missing "
                + "reference would silently reclassify hygiene/bounded tokens as residual — failing here names it.");
        }
    }

    // ROUND-4 guard-of-the-allowlist. Every name in DiagnosticTextClearingMethods must resolve to a REAL
    // member of one of the two allowlisted DiagnosticText types. A name that resolves nowhere is dead (it can
    // never clear anything) AND pre-authorizes a future same-named method to auto-clear without review — the
    // exact defect the removed `SanitizeEchoedToken` entry had (it is a private ColumnMapping member).
    // The two types need two lookups: the Storage one is compiled FROM SOURCE in this compilation (source
    // symbols, all accessibilities), while the Abstractions one arrives as a metadata REFERENCE whose
    // non-public members Roslyn drops (MetadataImportOptions.Public) — so it is enumerated by reflection,
    // which this friend assembly can do. Both are real-member checks against the real, shipped types.
    private static void AssertClearingMethodsResolveToRealMembers(CSharpCompilation comp)
    {
        INamedTypeSymbol? storage = comp.GetTypeByMetadataName(StorageDiagnosticText);
        Assert.True(
            storage is not null,
            $"'{StorageDiagnosticText}' did not resolve in the producer-scan compilation; the allowlist "
            + "resolution check would be vacuous.");

        var declared = storage!.GetMembers().OfType<IMethodSymbol>()
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var method in typeof(SharedDiagnosticText).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
        {
            declared.Add(method.Name);
        }

        var dead = DiagnosticTextClearingMethods.Where(m => !declared.Contains(m))
            .OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.True(
            dead.Count == 0,
            "DiagnosticTextClearingMethods entr(ies) that resolve to NO member of "
            + $"{AbstractionsDiagnosticText} or {StorageDiagnosticText}: {string.Join(", ", dead)}. Such an "
            + "entry is dead (the clearance is type-gated, so it can never fire) and pre-authorizes a future "
            + "same-named method to auto-clear unreviewed — remove it, or allowlist the type that declares it.");
    }

    // ----- inventory I/O -----

    // Lenient loader for REGEN only: tolerates the previous 5-column layout (file, member, token, class,
    // justification) AND the current 6-column one (file, type, member, token, class, justification), keyed on
    // the type-INDEPENDENT tuple (file, member, token) so an existing classification is preserved across the
    // type-in-key migration. Verify mode uses the strict LoadInventory below.
    private static Dictionary<(string File, string Member, string Token), (string Class, string Justification)>
        LoadInventoryForRegen()
    {
        string path = InventoryPath();
        var map = new Dictionary<(string, string, string), (string, string)>();
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] p = line.Split('\t');
            if (p.Length == 6)
            {
                map[(p[0], p[2], p[3])] = (p[4], p[5]);
            }
            else if (p.Length == 5)
            {
                map[(p[0], p[1], p[2])] = (p[3], p[4]);
            }
        }

        return map;
    }

    private static Dictionary<ProducerSite, (string Class, string Justification)> LoadInventory()
    {
        string path = InventoryPath();
        var map = new Dictionary<ProducerSite, (string, string)>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            Assert.True(parts.Length == 6, $"{InventoryFileName}: expected 6 tab-separated columns, got '{line}'.");
            var key = new ProducerSite(parts[0], parts[1], parts[2], parts[3]);

            // Duplicate keys must FAIL rather than silently overwrite a strong class with a weaker one.
            Assert.False(
                map.ContainsKey(key),
                $"{InventoryFileName}: duplicate row for site '{key.File}\t{key.Type}\t{key.Member}\t{key.Token}'. A "
                + "duplicate would let a weaker class silently overwrite a stronger one.");
            map[key] = (parts[4], parts[5]);
        }

        return map;
    }

    private static void WriteInventory(
        HashSet<ProducerSite> residual,
        Dictionary<(string File, string Member, string Token), (string Class, string Justification)> existing)
    {
        var lines = new List<string>
        {
            "# storage-exception-producer-inventory.tsv — GENERATED KEY SET, MANUAL classification.",
            "# Columns: file<TAB>type<TAB>member<TAB>token<TAB>class<TAB>justification.",
            "# Keyed on the producer SITE (file + enclosing TYPE chain + enclosing member + token) so a reused",
            "# generic name across a nested type / overload / partial-class split cannot auto-clear. Regenerate",
            "# keys with DELTASHARP_WRITE_PRODUCER_INVENTORY=1; then classify any UNCLASSIFIED rows. Owned by",
            "# StorageExceptionProducerInventoryGuardTests (#749).",
        };
        foreach (var key in residual
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal)
            .ThenBy(r => r.Member, StringComparer.Ordinal).ThenBy(r => r.Token, StringComparer.Ordinal))
        {
            (string Class, string Justification) v =
                existing.TryGetValue((key.File, key.Member, key.Token), out var e) ? e : ("UNCLASSIFIED", "TODO");
            lines.Add($"{key.File}\t{key.Type}\t{key.Member}\t{key.Token}\t{v.Class}\t{v.Justification}");
        }

        File.WriteAllLines(InventoryPath(), lines);
    }

    private static string InventoryPath()
    {
        // Checked in next to this test's source. Resolve from the repo root so it works from any bin/ layout.
        return Path.Combine(RepoRoot(), "tests", "DeltaSharp.Core.Tests", "Diagnostics", InventoryFileName);
    }

    // ----- filesystem -----

    private static void AssertSourceContains(string relFile, string needle, string because)
    {
        string full = Path.Combine(StorageSourceRoot(), relFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"{because} (source file '{relFile}' not found).");
        Assert.True(File.ReadAllText(full).Contains(needle, StringComparison.Ordinal), because);
    }

    private static IEnumerable<string> EnumerateStorageSources() =>
        Directory.EnumerateFiles(StorageSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

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
