namespace DeltaSharp.Engine.RowFormat;

/// <summary>
/// Thrown when deserializing a binary row from untrusted bytes (spill/shuffle storage) detects a
/// malformed or truncated frame — a bad magic/version, a length or reference that points outside
/// the buffer, a schema-version mismatch, or a structurally invalid payload (STORY-02.4.2 AC4).
/// </summary>
/// <remarks>
/// It derives from <see cref="RowFormatException"/> so existing <c>catch (RowFormatException)</c>
/// sites still observe it, while new code can catch <see cref="RowValidationException"/> to handle
/// untrusted-input failures specifically. Validation is <b>bounded</b>: it is detected with explicit
/// in-bounds checks before any read, so a malformed buffer fails fast and never reads out of bounds.
/// </remarks>
public sealed class RowValidationException : RowFormatException
{
    /// <summary>Creates the exception with a message.</summary>
    public RowValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public RowValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
