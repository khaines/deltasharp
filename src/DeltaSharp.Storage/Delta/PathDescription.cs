namespace DeltaSharp.Storage.Delta;

/// <summary>
/// The compiler-enforced result of <see cref="DiagnosticText.DescribePath"/>: a path token that has
/// PROVABLY passed through the partition-value-dropping renderer (#700). It exists so the "render via
/// <c>DescribePath</c>" half of the disclosure control (#696) is expressed in the type system rather than
/// left to caller discipline — a bare <see cref="string"/> gave no compiler signal that its content had
/// been sanitized, so a future call site (or a copy-paste of an existing one) could silently interpolate a
/// raw, Hive-encoded <c>add.path</c> — partition VALUES and all — straight into a diagnostic message or an
/// unconditionally-emitting <c>LoggerMessage</c> sink.
/// </summary>
/// <remarks>
/// <para>The construction of a <see cref="PathDescription"/> is confined so the "described" claim cannot be
/// forged. A <see cref="string"/> cannot be converted TO a <see cref="PathDescription"/> implicitly, so the
/// accidental/copy-paste bare-string disclosure vector — silently handing a raw <c>add.path</c> where a
/// described path is required — is a compile error; the primary constructor is banned by
/// <c>BannedSymbols.txt</c> (RS0030), so a direct <c>new PathDescription(raw)</c> is a build error
/// EVERYWHERE except <see cref="DiagnosticText.DescribePath"/>'s single sanctioned, pragma-suppressed call
/// site (#700/#696); and <see cref="Value"/> is get-only, so <c>existing with { Value = raw }</c> cannot
/// re-init a described path from an unsanitized string. A sink that requires a described path (e.g.
/// <c>DeltaVacuumLog.VacuumCandidateDecision</c>) therefore CANNOT be handed an undescribed one. The reverse
/// — a described path FLOWING INTO a <see cref="string"/> context, such as an interpolated exception message
/// — is always safe (the value is already sanitized), so an implicit conversion to <see cref="string"/> and
/// a value-returning <see cref="ToString"/> are provided; that keeps every existing message- and
/// log-rendering call site byte-identical.</para>
/// <para>A <c>readonly record struct</c> (not a class) so wrapping the renderer's result adds NO heap
/// allocation on the fault/diagnostic path, and it stays trim/AOT-clean.</para>
/// </remarks>
internal readonly record struct PathDescription
{
    internal PathDescription(string value) => Value = value;

    /// <summary>The sanitized path description. Get-only so <c>with { Value = … }</c> cannot re-init a
    /// described path from an unsanitized string.</summary>
    public string Value { get; }

    /// <summary>Returns the sanitized description verbatim, so interpolation renders byte-identically to
    /// the pre-#700 <c>string</c>-returning <see cref="DiagnosticText.DescribePath"/>.</summary>
    public override string ToString() => Value;

    /// <summary>Widens a described path into a <see cref="string"/>. Safe in one direction only: a
    /// <see cref="PathDescription"/> is already sanitized, so it may flow into any <see cref="string"/>
    /// context, but the absence of a <see cref="string"/>-to-<see cref="PathDescription"/> conversion is
    /// what makes "this path went through <see cref="DiagnosticText.DescribePath"/>" compiler-enforced.</summary>
    public static implicit operator string(PathDescription description) => description.Value;
}
