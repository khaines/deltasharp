using System.Diagnostics.CodeAnalysis;
using DeltaSharp.Types;

namespace DeltaSharp;

/// <summary>
/// A per-<typeparamref name="T"/> cache of the reflection-derived <see cref="StructType"/> schema, so
/// a long typed transformation chain (<c>As&lt;T&gt;().Where(...).Filter(...).Select(...)</c>) derives
/// <typeparamref name="T"/>'s schema <b>once</b> rather than re-running <see cref="DatasetSchema.Derive{T}"/>
/// at every hop. A JIT-generated closed generic type per <typeparamref name="T"/> gives us a natural,
/// lock-free, one-per-type slot.
/// </summary>
/// <remarks>
/// The derivation is deferred to first access through a <see cref="Lazy{T}"/> (its factory is not run
/// in the static constructor), so a <typeparamref name="T"/> with an unmappable property still throws
/// the deterministic <see cref="UnsupportedTypedSchemaException"/> <b>directly</b> — never wrapped in a
/// <see cref="TypeInitializationException"/>. Only successful derivations are cached; a failing
/// derivation caches (and deterministically re-throws) the same exception, preserving AC4.
/// </remarks>
/// <typeparam name="T">The encoded record/POCO type whose schema is cached.</typeparam>
internal static class TypedSchemaCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
{
    private static readonly Lazy<StructType> Cached = new(DatasetSchema.Derive<T>);

    /// <summary>The derived schema for <typeparamref name="T"/>, computed once and reused.</summary>
    /// <exception cref="UnsupportedTypedSchemaException">A property of <typeparamref name="T"/> has no
    /// supported schema mapping (thrown directly, and cached for subsequent access).</exception>
    public static StructType Value => Cached.Value;
}
