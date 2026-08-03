using System.Globalization;
using System.Reflection;
using System.Text;
using DeltaSharp.Storage.Delta;
using Xunit;
using SharedDiagnosticText = DeltaSharp.Diagnostics.DiagnosticText;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// #768: pin cross-assembly agreement between ColumnMapping's path-segment guard and the shared
/// DeltaSharp.Abstractions diagnostic sanitizer.
/// </summary>
public sealed class ColumnMappingSharedSanitizerAgreementTests
{
    private static readonly MethodInfo PathSegmentGuard = typeof(ColumnMapping).GetMethod(
        "FindUnsafePathSegmentReason",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("FindUnsafePathSegmentReason not found.");

    [Fact]
    public void PathSegmentGuard_And_SharedSanitizer_Agree_OnEveryScalarValue_InContextualSegments()
    {
        var mismatches = new List<string>();
        int checkedScalars = 0;

        // All Unicode scalar values except the four explicit path delimiters checked separately by the guard.
        // 0x110000 total code points - 0x800 surrogates - 4 delimiters = 1,112,060.
        var codeUnits = new char[2];
        for (int value = 0; value <= 0x10FFFF; value++)
        {
            if (value is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            if (value is '/' or '\\' or '=' or ':')
            {
                continue;
            }

            Rune rune = new(value);
            int codeUnitCount = rune.EncodeToUtf16(codeUnits);
            string segment = "a" + new string(codeUnits, 0, codeUnitCount) + "b";

            string? reason = (string?)PathSegmentGuard.Invoke(null, [segment]);
            bool guardRejects = reason is not null;
            bool sanitizerMutates = !string.Equals(
                segment,
                SharedDiagnosticText.Sanitize(segment, maxLength: -1),
                StringComparison.Ordinal);

            if (guardRejects != sanitizerMutates && mismatches.Count < 20)
            {
                mismatches.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"U+{value:X6} category={Rune.GetUnicodeCategory(rune)} guardRejects={guardRejects} "
                        + $"sanitizerMutates={sanitizerMutates} reason={reason ?? "(null)"}"));
            }

            checkedScalars++;
        }

        Assert.Equal(1_112_060, checkedScalars);
        Assert.True(
            mismatches.Count == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Found {mismatches.Count} guard/sanitizer mismatches. First mismatches:{Environment.NewLine}")
            + string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void PathSegmentGuard_Rejects_ExplicitDelimiterRunes()
    {
        foreach (char delimiter in new[] { '/', '\\', '=', ':' })
        {
            string segment = string.Create(
                CultureInfo.InvariantCulture,
                $"a{delimiter}b");
            string? reason = (string?)PathSegmentGuard.Invoke(null, [segment]);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Theory]
    [InlineData("x")]
    [InlineData("xA")]
    [InlineData("Ax")]
    [InlineData("\u0001")]
    [InlineData("\u0001A")]
    [InlineData("A\u0001")]
    [InlineData("\u200F")]
    [InlineData("\u200FA")]
    [InlineData("A\u200F")]
    public void PathSegmentGuard_And_SharedSanitizer_Agree_OnBoundarySegmentShapes(string segment)
    {
        string? reason = (string?)PathSegmentGuard.Invoke(null, [segment]);
        bool guardRejects = reason is not null;
        bool sanitizerMutates = !string.Equals(
            segment,
            SharedDiagnosticText.Sanitize(segment, maxLength: -1),
            StringComparison.Ordinal);

        Assert.Equal(sanitizerMutates, guardRejects);
    }
}
