using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DeltaSharp.Core.Tests.Diagnostics;

/// <summary>
/// #749 audit guard. The storage layer treats every <c>.Message</c> as untrusted tenant data, so every
/// exception-message PRODUCER — every interpolation hole that reaches a <c>*Exception</c> constructor or a
/// <c>*Exception.Factory(...)</c> call in <c>src/DeltaSharp.Storage</c> — must have each interpolated token
/// CLASSIFIED, and every attacker-influenceable (raw) token routed through the shared hygiene helper.
/// <para>
/// This is a <b>source-backed</b> guard, not a hand-maintained count or family-name denylist. It parses the
/// real storage sources with the C# compiler (<see cref="CSharpSyntaxTree"/>), builds a semantic model over
/// the runtime's own BCL + <c>DeltaSharp.Abstractions</c> (via the trusted-platform-assembly set), and for
/// every exception-message interpolation hole resolves the token's TYPE. A token is auto-cleared when it is
/// either (a) wrapped in a hygiene helper (<c>Sanitize</c>/<c>DescribePath</c>/<c>DescribeType</c>/
/// <c>DescribeSchema</c>/<c>SanitizeAndJoin</c>/<c>SanitizeEchoedToken</c>/<c>Redact</c>) — SANITIZED; or
/// (b) resolved to a bounded value type (integral/enum/bool/char/<c>DateTimeOffset</c>/<c>Guid</c>/…) — BOUNDED,
/// which cannot carry an attacker string. Every remaining token (a <c>string</c>, or a type the model could not
/// resolve because its declaring assembly — e.g. Parquet.Net — is intentionally not referenced) MUST appear in
/// the checked-in inventory <c>storage-exception-producer-inventory.tsv</c> with an explicit classification and
/// justification. A NEW producer that echoes an unwrapped, non-bounded token therefore surfaces in the
/// residual set, is absent from the inventory, and turns this guard RED — it cannot silently escape
/// classification. Removing/renaming a producer leaves a stale inventory row, which is also RED, so the
/// inventory cannot rot.
/// </para>
/// <para>
/// Regenerate the residual key set (after intentionally adding/removing a producer) by running this test with
/// the environment variable <c>DELTASHARP_WRITE_PRODUCER_INVENTORY=1</c>; it rewrites the <c>file</c>/<c>token</c>
/// columns (preserving your existing classifications) and passes, so you then fill in the classification and
/// justification for any newly-added rows and re-run in verify mode.
/// </para>
/// </summary>
public sealed class StorageExceptionProducerInventoryGuardTests
{
    private const string InventoryFileName = "storage-exception-producer-inventory.tsv";

    private static readonly HashSet<string> HygieneHelpers = new(StringComparer.Ordinal)
    {
        "Sanitize", "SanitizeEchoedToken", "DescribePath", "DescribeType", "DescribeSchema",
        "SanitizeAndJoin", "Redact", "DescribeValue", "DescribeList", "DescribeConfigToken",
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
        // local, a Redact()ed framework detail, a DescribePath()ed display path).
        "sanitized-upstream",
        // A DeltaSharp-generated internal name (staging temp basename) that is never tenant data.
        "internal-name",
        // A raw token intentionally surfaced with an explicit sink obligation documented in the log-routing doc.
        "raw-obligation",
    };

    [Fact]
    public void EveryStorageExceptionMessageProducerToken_IsClassified_WithNoInventoryDrift()
    {
        HashSet<(string File, string Token)> residual = ScanResidualProducerTokens();
        var inventory = LoadInventory();

        if (Environment.GetEnvironmentVariable("DELTASHARP_WRITE_PRODUCER_INVENTORY") == "1")
        {
            WriteInventory(residual, inventory);
            return;
        }

        // 1. Every discovered residual token has an inventory row — a NEW unclassified producer is RED here.
        var missing = residual.Where(r => !inventory.ContainsKey(r))
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Token, StringComparer.Ordinal)
            .Select(r => $"{r.File}\t{r.Token}")
            .ToList();
        Assert.True(
            missing.Count == 0,
            "Unclassified storage exception-message producer token(s) found in source but absent from "
            + $"{InventoryFileName}. Either wrap the token in a hygiene helper or add a classified inventory row "
            + "(regenerate with DELTASHARP_WRITE_PRODUCER_INVENTORY=1):\n" + string.Join("\n", missing));

        // 2. Every inventory row still corresponds to a live producer — a stale row is RED (forces upkeep).
        var stale = inventory.Keys.Where(k => !residual.Contains(k))
            .OrderBy(k => k.File, StringComparer.Ordinal).ThenBy(k => k.Token, StringComparer.Ordinal)
            .Select(k => $"{k.File}\t{k.Token}")
            .ToList();
        Assert.True(
            stale.Count == 0,
            $"Stale {InventoryFileName} row(s) that no longer match any storage producer token "
            + "(regenerate with DELTASHARP_WRITE_PRODUCER_INVENTORY=1):\n" + string.Join("\n", stale));

        // 3. Every row carries a valid classification and a non-empty justification.
        foreach (var (key, entry) in inventory)
        {
            Assert.True(
                AllowedClasses.Contains(entry.Class),
                $"{InventoryFileName} row '{key.File}\t{key.Token}' has invalid class '{entry.Class}'. "
                + "Allowed: " + string.Join(", ", AllowedClasses.OrderBy(c => c, StringComparer.Ordinal)));
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Justification),
                $"{InventoryFileName} row '{key.File}\t{key.Token}' has an empty justification.");
        }
    }

    [Fact]
    public void ExplicitlyFlaggedProducers_AreCoveredByTheInventory()
    {
        // #749 names these two families explicitly; pin that they remain in the classified inventory so a
        // refactor that drops their sanitization cannot pass silently.
        var inventory = LoadInventory();

        Assert.True(
            inventory.TryGetValue(("Parquet/NestedParquetColumnReader.cs", "columnName"), out var col)
            && col.Class == "sanitized-upstream",
            "NestedParquetColumnReader.ValidateShape/ReadAsync `columnName` must be inventoried as "
            + "sanitized-upstream (Sanitize at entry).");

        Assert.True(
            inventory.TryGetValue(("Backends/LocalFileSystemBackend.cs", "_displayPath"), out var disp)
            && disp.Class == "sanitized-upstream",
            "LocalFileSystemBackend.StagedWriteStream `_displayPath` must be inventoried as sanitized-upstream "
            + "(DescribePath at construction).");
    }

    [Fact]
    public void EveryStorageSourceFile_ParsesCleanly_SoTheWalkCannotSilentlyDegrade()
    {
        var offenders = new List<string>();
        foreach (string file in EnumerateStorageSources())
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseLegs[0], path: file);
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                offenders.Add(Path.GetRelativePath(StorageSourceRoot(), file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Storage source file(s) failed to PARSE (a parser/language drift would silently degrade the "
            + "producer walk):\n" + string.Join("\n", offenders));
    }

    // ----- scan -----

    private static readonly CSharpParseOptions[] ParseLegs =
    [
        new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
            "NET", "NETCOREAPP", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0", "NET10_0_OR_GREATER", "RELEASE"),
        new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
            "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER", "DEBUG", "TRACE"),
    ];

    private static HashSet<(string File, string Token)> ScanResidualProducerTokens()
    {
        var refs = TrustedPlatformReferences();
        var residual = new HashSet<(string, string)>();

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
                        if (IsHygieneWrapped(hole.Expression))
                        {
                            continue; // SANITIZED
                        }

                        ITypeSymbol? type = model.GetTypeInfo(hole.Expression).Type
                            ?? model.GetTypeInfo(hole.Expression).ConvertedType;
                        if (type is not null && IsBoundedType(type))
                        {
                            continue; // BOUNDED
                        }

                        residual.Add((rel, hole.Expression.ToString()));
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

    private static bool IsHygieneWrapped(ExpressionSyntax expr) =>
        expr is InvocationExpressionSyntax inv
        && InvokedName(inv) is string name
        && HygieneHelpers.Contains(name);

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

    private static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        _ => null,
    };

    private static string SimpleTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static List<MetadataReference> TrustedPlatformReferences()
    {
        // The BCL the test process runs on, plus DeltaSharp.Abstractions/Core (Core.Tests loads them). Parquet.Net
        // and DeltaSharp.Storage are intentionally NOT here: Storage is compiled from source (referencing its own
        // compiled dll would duplicate every type), and leaving Parquet.Net unreferenced makes ClrType/level
        // tokens resolve as unresolved -> residual -> inventory, which is deterministic and fail-safe.
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator);
        var refs = new List<MetadataReference>();
        foreach (string path in tpa)
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                continue;
            }

            if (path.EndsWith("DeltaSharp.Storage.dll", StringComparison.Ordinal))
            {
                continue;
            }

            refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }

    // ----- inventory I/O -----

    private static Dictionary<(string File, string Token), (string Class, string Justification)> LoadInventory()
    {
        string path = InventoryPath();
        var map = new Dictionary<(string, string), (string, string)>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            Assert.True(parts.Length == 4, $"{InventoryFileName}: expected 4 tab-separated columns, got '{line}'.");
            map[(parts[0], parts[1])] = (parts[2], parts[3]);
        }

        return map;
    }

    private static void WriteInventory(
        HashSet<(string File, string Token)> residual,
        Dictionary<(string File, string Token), (string Class, string Justification)> existing)
    {
        var lines = new List<string>
        {
            "# storage-exception-producer-inventory.tsv — GENERATED KEY SET, MANUAL classification.",
            "# Columns: file<TAB>token<TAB>class<TAB>justification.",
            "# Regenerate keys with DELTASHARP_WRITE_PRODUCER_INVENTORY=1; then classify any UNCLASSIFIED rows.",
            "# Owned by StorageExceptionProducerInventoryGuardTests (#749).",
        };
        foreach (var key in residual.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Token, StringComparer.Ordinal))
        {
            (string Class, string Justification) v = existing.TryGetValue(key, out var e) ? e : ("UNCLASSIFIED", "TODO");
            lines.Add($"{key.File}\t{key.Token}\t{v.Class}\t{v.Justification}");
        }

        File.WriteAllLines(InventoryPath(), lines);
    }

    private static string InventoryPath()
    {
        // Checked in next to this test's source. Resolve from the repo root so it works from any bin/ layout.
        return Path.Combine(RepoRoot(), "tests", "DeltaSharp.Core.Tests", "Diagnostics", InventoryFileName);
    }

    // ----- filesystem -----

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
