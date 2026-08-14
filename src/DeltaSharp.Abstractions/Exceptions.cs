using System.Globalization;
using DeltaSharp.Diagnostics;

namespace DeltaSharp.Types;

/// <summary>
/// Thrown when a schema or type definition is invalid — for example a struct with duplicate
/// field names, a map with an unsupported key type, or a decimal whose precision/scale is
/// out of range (STORY-02.5.1 AC2). The message names the offending element precisely.
/// </summary>
public sealed class SchemaValidationException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public SchemaValidationException()
    {
    }

    /// <summary>Initializes a new instance with a precise validation <paramref name="message"/>.</summary>
    public SchemaValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public SchemaValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a logical type has no supported physical representation for a builder that
/// requires one (STORY-02.5.1 AC4) — for example <see cref="NullType"/>. Prefer
/// <c>PhysicalLayoutResolver.TryResolve</c> to branch without exceptions on the hot path.
/// </summary>
public sealed class UnsupportedTypeException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public UnsupportedTypeException()
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/>.</summary>
    public UnsupportedTypeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public UnsupportedTypeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an implicit type coercion is not supported between two types (STORY-02.5.2
/// AC4). The message identifies the source type, the target type, and the expression path
/// (for example <c>items.element.price</c>) so a nested mismatch points at the exact field.
/// </summary>
public sealed class TypeCoercionException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public TypeCoercionException()
    {
    }

    /// <summary>Initializes a new instance with a precise coercion <paramref name="message"/>.</summary>
    public TypeCoercionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public TypeCoercionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The source type that could not be coerced; non-null for path-aware throws.
    /// <para><b>Privacy channel (#707):</b> for a non-atomic type this is the recursive
    /// <see cref="DataType.SimpleString"/>, which appends every nested <b>foreign schema field name</b>
    /// verbatim — potential personal data / an un-neutralized foreign-name echo. It is deliberately kept off
    /// the rendered <see cref="Exception.Message"/>/<c>ToString</c> (which echo the bounded
    /// <see cref="DataType.TypeName"/> kind instead) and exposed here only for an <b>entitled</b> owner. A
    /// consumer that logs or destructures this property owns its own data-minimization; do not forward it to
    /// an untrusted sink.</para></summary>
    public string? SourceType { get; private init; }

    /// <summary>The target type the source could not be coerced to; non-null for path-aware throws.
    /// <para><b>Privacy channel (#707):</b> for a non-atomic type this is the recursive
    /// <see cref="DataType.SimpleString"/>, which appends every nested <b>foreign schema field name</b>
    /// verbatim — potential personal data / an un-neutralized foreign-name echo. It is deliberately kept off
    /// the rendered <see cref="Exception.Message"/>/<c>ToString</c> (which echo the bounded
    /// <see cref="DataType.TypeName"/> kind instead) and exposed here only for an <b>entitled</b> owner. A
    /// consumer that logs or destructures this property owns its own data-minimization; do not forward it to
    /// an untrusted sink.</para></summary>
    public string? TargetType { get; private init; }

    /// <summary>The dotted expression path to the offending element; non-null for path-aware throws.
    /// <para><b>Privacy channel (#707):</b> this raw path is built from <b>foreign schema field names</b>
    /// (dotted/<c>element</c>/<c>key</c>/<c>value</c> segments) — potential personal data. It is deliberately
    /// kept off the rendered <see cref="Exception.Message"/>/<c>ToString</c> (which carry the
    /// sanitized, length-bounded path via <c>DiagnosticText.Sanitize</c>) and exposed here only for an
    /// <b>entitled</b> owner. A consumer that logs or destructures this property owns its own
    /// data-minimization; do not forward it to an untrusted sink.</para></summary>
    public string? Path { get; private init; }

    /// <summary>
    /// Builds an exception naming the source type, target type, and expression path. The path
    /// uses dotted/<c>element</c>/<c>key</c>/<c>value</c> segments so nested mismatches are precise.
    /// </summary>
    /// <remarks>
    /// #707 — adjudicated <b>by predicate</b>: at the throw site (<c>TypeCoercion.EnsureCoercible</c> →
    /// <c>TryCoerce</c>) the recursion descends into element/key/value/field types, so <paramref name="source"/>
    /// and <paramref name="target"/> <b>can be a non-atomic</b> <see cref="StructType"/>/<see cref="ArrayType"/>/
    /// <see cref="MapType"/> — e.g. coercing <c>array&lt;struct&lt;…&gt;&gt;</c> to <c>array&lt;int&gt;</c>
    /// fails with a struct on one side. <see cref="StructType.SimpleString"/> recursively appends every nested
    /// field name verbatim, so echoing it raw is an unbounded, un-neutralized foreign-name echo (the #705/#686
    /// defect class). The <paramref name="path"/> is likewise built from foreign schema field names.
    /// <para>
    /// So the <b>message</b> echoes the bounded <em>kind</em> (<see cref="DataType.TypeName"/>) for a
    /// non-atomic type — structurally bounded, not merely capped — and the safe atomic
    /// <see cref="DataType.SimpleString"/> otherwise (keeping e.g. <c>decimal(10,2)</c> precision), and the
    /// path is sanitized and length-bounded. The exact raw types/path remain on the typed
    /// <see cref="SourceType"/>/<see cref="TargetType"/>/<see cref="Path"/> channel for a caller entitled to
    /// them.
    /// </para>
    /// </remarks>
    public static TypeCoercionException ForPath(DataType source, DataType target, string path) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"Cannot coerce '{DescribeTypeForMessage(source)}' to '{DescribeTypeForMessage(target)}' at " +
            $"'{DiagnosticText.Sanitize(path)}'."))
        {
            SourceType = source.SimpleString,
            TargetType = target.SimpleString,
            Path = path,
        };

    // #707: echo the bounded KIND for a non-atomic type (SimpleString would recurse over foreign field
    // names); the atomic SimpleString is safe and short, so it survives verbatim to keep decimal precision.
    private static string DescribeTypeForMessage(DataType type) =>
        type is ArrayType or MapType or StructType ? type.TypeName : type.SimpleString;
}

/// <summary>
/// Thrown when an arithmetic or cast result exceeds the target type's precision/scale (decimal)
/// or value range (integral/temporal) under <see cref="AnsiMode.Ansi"/> (STORY-02.5.2 AC2).
/// Under <see cref="AnsiMode.Legacy"/> the same condition yields SQL <c>NULL</c> instead.
/// </summary>
public sealed class ArithmeticOverflowException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public ArithmeticOverflowException()
    {
    }

    /// <summary>Initializes a new instance with a precise overflow <paramref name="message"/>.</summary>
    public ArithmeticOverflowException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a <paramref name="message"/> and underlying cause.</summary>
    public ArithmeticOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
