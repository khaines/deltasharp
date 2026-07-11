using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Plans.Logical;
using DeltaSharp.Types;

namespace DeltaSharp.Executor;

/// <summary>
/// An <see cref="ILocalSinkFactory"/> that resolves a <see cref="SinkDescriptor"/> against an ordered list
/// of delegate factories, returning the first sink one creates. This is the composition point of the M1
/// write door's data-out seam: the in-memory <see cref="InMemorySinkRegistry"/> for the <c>memory</c>
/// format and the Storage↔Executor Delta sink for the <c>delta</c> format (#487) are registered side by
/// side, and a future read provider (#499) can register alongside them without restructuring. Order is the
/// registration order; a factory that does not recognize the format returns <see langword="false"/> and the
/// next is tried, so no format is claimed by more than one factory.
/// </summary>
internal sealed class CompositeSinkFactory : ILocalSinkFactory
{
    private readonly IReadOnlyList<ILocalSinkFactory> _factories;

    public CompositeSinkFactory(params ILocalSinkFactory[] factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = (ILocalSinkFactory[])factories.Clone();
    }

    /// <inheritdoc/>
    public bool TryCreate(SinkDescriptor descriptor, StructType schema, [NotNullWhen(true)] out ILocalSink? sink)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(schema);

        foreach (ILocalSinkFactory factory in _factories)
        {
            if (factory.TryCreate(descriptor, schema, out sink))
            {
                return true;
            }
        }

        sink = null;
        return false;
    }
}
