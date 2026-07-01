using DeltaSharp.Types;

namespace DeltaSharp.Engine.Execution;

/// <summary>
/// An explicit type conversion (<c>CAST(child AS target)</c>) over a single operand (STORY-03.4.1).
/// The node only declares the target <see cref="PhysicalExpression.Type"/>; the supported conversion
/// matrix and ANSI/Legacy overflow behavior are enforced when the evaluator is built and applied,
/// respectively (building the node does no row work). Under <see cref="AnsiMode.Ansi"/> an
/// out-of-range or non-finite conversion throws; under <see cref="AnsiMode.Legacy"/> it yields SQL
/// <c>NULL</c> — DeltaSharp never silently wraps (EPIC-02 contract).
/// </summary>
public sealed class CastExpression : PhysicalExpression
{
    private readonly PhysicalExpression[] _children;

    /// <summary>Creates <c>CAST(<paramref name="child"/> AS <paramref name="targetType"/>)</c>.</summary>
    /// <param name="child">The operand to convert.</param>
    /// <param name="targetType">The destination type.</param>
    /// <param name="mode">The ANSI strictness lens for lossy conversions (default <see cref="AnsiMode.Ansi"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> or <paramref name="targetType"/> is null.</exception>
    public CastExpression(PhysicalExpression child, DataType targetType, AnsiMode mode = AnsiMode.Ansi)
        : base(targetType, Nullability(child, targetType, mode))
    {
        _children = [child];
        Mode = mode;
    }

    /// <summary>The ANSI strictness lens applied to lossy conversions.</summary>
    public AnsiMode Mode { get; }

    /// <summary>The operand being converted.</summary>
    public PhysicalExpression Child => _children[0];

    /// <summary>The destination type (identical to <see cref="PhysicalExpression.Type"/>).</summary>
    public DataType TargetType => Type;

    /// <inheritdoc />
    public override IReadOnlyList<PhysicalExpression> Children => _children;

    private static bool Nullability(PhysicalExpression child, DataType targetType, AnsiMode mode)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(targetType);

        // Operand nulls always carry through. Under Legacy a lossy conversion can manufacture NULL, so
        // a non-identity cast is conservatively nullable. ANSI throws instead of nulling, so only the
        // operand's own nullability matters there.
        bool identity = child.Type.Equals(targetType);
        return child.Nullable || (mode == AnsiMode.Legacy && !identity);
    }
}
