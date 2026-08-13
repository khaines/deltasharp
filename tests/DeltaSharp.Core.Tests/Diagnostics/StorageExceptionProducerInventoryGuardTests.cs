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
/// is either (a) wrapped in a hygiene helper whose call RESOLVES (via the semantic model) to a method on
/// <c>DeltaSharp.Diagnostics.DiagnosticText</c>, <c>DeltaSharp.Storage.Delta.DiagnosticText</c>, or
/// <c>LocalFileSystemBackend.Redact</c> — SANITIZED (a bare <c>Sanitize</c>-named local does NOT clear); or
/// (b) resolved to a bounded value type (integral/enum/bool/char/<c>DateTimeOffset</c>/<c>Guid</c>/…) —
/// BOUNDED. Every remaining token MUST appear in the checked-in inventory
/// <c>storage-exception-producer-inventory.tsv</c>, keyed on the producer <b>site</b>
/// (file + enclosing member + token), with an explicit classification and justification.
/// </para>
/// <para>
/// <b>Round-1 hardening.</b> (1) The hygiene clearance is resolved by SEMANTIC MODEL and gated on the
/// containing type, not a bare method-name match — a forged local <c>Sanitize</c> identity no longer
/// auto-clears; an unresolved symbol falls back to residual (fail-safe). (2) Rows are keyed on the site
/// <c>(file, member, token)</c>, so a NEW unsanitized producer reusing a generic name already inventoried
/// elsewhere in the file (<c>detail</c>/<c>context</c>/<c>reason</c>/…) cannot auto-clear. (3) Write mode
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

    // Hygiene clearance is gated on the CONTAINING TYPE (resolved by the semantic model), not a bare
    // method-name match. Any method on one of the two DiagnosticText types clears; LocalFileSystemBackend
    // clears only via its private Redact. A local/foreign `Sanitize` therefore does NOT auto-clear.
    private const string AbstractionsDiagnosticText = "DeltaSharp.Diagnostics.DiagnosticText";
    private const string StorageDiagnosticText = "DeltaSharp.Storage.Delta.DiagnosticText";
    private const string LocalBackend = "DeltaSharp.Storage.Backends.LocalFileSystemBackend";

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
        var inventory = LoadInventory();

        if (Environment.GetEnvironmentVariable("DELTASHARP_WRITE_PRODUCER_INVENTORY") == "1")
        {
            WriteInventory(residual, inventory);
            Assert.Fail(
                "inventory regenerated; classify new rows and re-run in verify mode. Write mode never passes "
                + "so it cannot be mistaken for a green audit.");
        }

        // 1. Every discovered residual token has an inventory row — a NEW unclassified producer is RED here.
        var missing = residual.Where(r => !inventory.ContainsKey(r))
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Member, StringComparer.Ordinal)
            .ThenBy(r => r.Token, StringComparer.Ordinal)
            .Select(r => $"{r.File}\t{r.Member}\t{r.Token}")
            .ToList();
        Assert.True(
            missing.Count == 0,
            "Unclassified storage exception-message producer token(s) found in source but absent from "
            + $"{InventoryFileName}. Either wrap the token in a hygiene helper or add a classified inventory row "
            + "(regenerate with DELTASHARP_WRITE_PRODUCER_INVENTORY=1):\n" + string.Join("\n", missing));

        // 2. Every inventory row still corresponds to a live producer — a stale row is RED (forces upkeep).
        var stale = inventory.Keys.Where(k => !residual.Contains(k))
            .OrderBy(k => k.File, StringComparer.Ordinal).ThenBy(k => k.Member, StringComparer.Ordinal)
            .ThenBy(k => k.Token, StringComparer.Ordinal)
            .Select(k => $"{k.File}\t{k.Member}\t{k.Token}")
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
                $"{InventoryFileName} row '{key.File}\t{key.Member}\t{key.Token}' has invalid class "
                + $"'{entry.Class}'. Allowed: " + string.Join(", ", AllowedClasses.OrderBy(c => c, StringComparer.Ordinal)));
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Justification),
                $"{InventoryFileName} row '{key.File}\t{key.Member}\t{key.Token}' has an empty justification.");
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

    private readonly record struct ProducerSite(string File, string Member, string Token);

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

                        residual.Add(new ProducerSite(rel, EnclosingMemberName(hole), hole.Expression.ToString()));
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

    // ROUND-1: resolve the invocation via the SEMANTIC MODEL and gate on the containing type. A bare
    // method-name match is forgeable — a local `string Sanitize(string s) => s;` would auto-clear a raw
    // token. Clearance requires the resolved method (or, when overload binding fails on an unresolved
    // argument, the resolved RECEIVER TYPE) to belong to one of the two DiagnosticText types, or to be
    // LocalFileSystemBackend.Redact. Neither path is a name match; an UNRESOLVED call on an unknown receiver
    // falls back to residual (fail-safe).
    private static bool IsHygieneWrapped(SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is not InvocationExpressionSyntax inv)
        {
            return false;
        }

        // Path 1: the invocation binds -> use the resolved method's containing type. Handles Redact and any
        // DiagnosticText call whose arguments all resolve.
        if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol method)
        {
            string containing = method.ContainingType?.ToDisplayString() ?? string.Empty;
            if (containing is AbstractionsDiagnosticText or StorageDiagnosticText)
            {
                return true;
            }

            return containing == LocalBackend && method.Name == "Redact";
        }

        // Path 2: overload binding failed because an argument is unresolved (e.g. a Parquet.Net-typed
        // `field.Name`/`leaf.Path`), but a type-qualified call `DiagnosticText.Method(...)` still resolves its
        // RECEIVER independent of the arguments. Clearing on the resolved receiver TYPE (never a bare name)
        // keeps a local/foreign `Sanitize` residual while a genuine DiagnosticText wrap clears.
        if (inv.Expression is MemberAccessExpressionSyntax ma)
        {
            string? receiverType = model.GetSymbolInfo(ma.Expression).Symbol switch
            {
                INamedTypeSymbol t => t.ToDisplayString(),
                _ => model.GetTypeInfo(ma.Expression).Type?.ToDisplayString(),
            };
            if (receiverType is AbstractionsDiagnosticText or StorageDiagnosticText)
            {
                return true;
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
                return true;
        }

        if (type is INamedTypeSymbol { Name: "Nullable" } nullable && nullable.TypeArguments.Length == 1)
        {
            return IsBoundedType(nullable.TypeArguments[0]);
        }

        return type.ToDisplayString() switch
        {
            "System.DateTimeOffset" or "System.DateTime" or "System.TimeSpan" or "System.Version"
                or "System.Int128" or "System.UInt128" or "System.Guid" or "System.Decimal" => true,
            _ => false,
        };
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

    // ----- inventory I/O -----

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
            Assert.True(parts.Length == 5, $"{InventoryFileName}: expected 5 tab-separated columns, got '{line}'.");
            var key = new ProducerSite(parts[0], parts[1], parts[2]);

            // Duplicate keys must FAIL rather than silently overwrite a strong class with a weaker one.
            Assert.False(
                map.ContainsKey(key),
                $"{InventoryFileName}: duplicate row for site '{key.File}\t{key.Member}\t{key.Token}'. A "
                + "duplicate would let a weaker class silently overwrite a stronger one.");
            map[key] = (parts[3], parts[4]);
        }

        return map;
    }

    private static void WriteInventory(
        HashSet<ProducerSite> residual,
        Dictionary<ProducerSite, (string Class, string Justification)> existing)
    {
        var lines = new List<string>
        {
            "# storage-exception-producer-inventory.tsv — GENERATED KEY SET, MANUAL classification.",
            "# Columns: file<TAB>member<TAB>token<TAB>class<TAB>justification.",
            "# Keyed on the producer SITE (file + enclosing member + token) so a reused generic name in an",
            "# already-inventoried file cannot auto-clear. Regenerate keys with DELTASHARP_WRITE_PRODUCER_INVENTORY=1;",
            "# then classify any UNCLASSIFIED rows. Owned by StorageExceptionProducerInventoryGuardTests (#749).",
        };
        foreach (var key in residual
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Member, StringComparer.Ordinal)
            .ThenBy(r => r.Token, StringComparer.Ordinal))
        {
            (string Class, string Justification) v = existing.TryGetValue(key, out var e) ? e : ("UNCLASSIFIED", "TODO");
            lines.Add($"{key.File}\t{key.Member}\t{key.Token}\t{v.Class}\t{v.Justification}");
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
