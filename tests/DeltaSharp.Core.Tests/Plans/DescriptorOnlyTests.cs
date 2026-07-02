using System.IO;
using System.Reflection;
using DeltaSharp.Plans;
using DeltaSharp.Plans.Expressions;
using DeltaSharp.Plans.Logical;
using Xunit;

namespace DeltaSharp.Core.Tests.Plans;

/// <summary>
/// AC3: scan and write nodes hold logical source/sink descriptors only — no open readers,
/// writers, streams, tasks, or backend handles, and constructing them performs no I/O.
/// </summary>
public sealed class DescriptorOnlyTests
{
    private static readonly Type[] AllIrTypes =
    {
        typeof(UnresolvedRelation), typeof(Project), typeof(Filter), typeof(Aggregate),
        typeof(Join), typeof(Sort), typeof(Limit), typeof(Distinct), typeof(Union),
        typeof(WriteToSource), typeof(SinkDescriptor),
        typeof(UnresolvedAttribute), typeof(UnresolvedFunction),
        typeof(Literal), typeof(Cast), typeof(AttributeReference), typeof(Alias),
        typeof(And), typeof(Or), typeof(Not), typeof(BinaryComparison),
        typeof(BinaryArithmetic), typeof(EqualNullSafe), typeof(IsNull), typeof(IsNotNull),
        typeof(SortOrder), typeof(UnresolvedStar),
    };

    [Fact]
    public void UnresolvedRelationExposesOnlyIdentifierAndOptions()
    {
        var relation = new UnresolvedRelation(new[] { "db", "t" });
        Assert.Equal(new[] { "db", "t" }, relation.Identifier);
        Assert.Empty(relation.Options);
    }

    [Fact]
    public void WriteNodeExposesOnlyLogicalSinkDescriptor()
    {
        var sink = new SinkDescriptor(
            "parquet",
            SaveMode.Append,
            path: "/out",
            partitionColumns: new[] { "year" });
        var write = new WriteToSource(PlanFixtures.Relation("t"), sink);

        Assert.Equal("parquet", write.Sink.Format);
        Assert.Equal(SaveMode.Append, write.Sink.Mode);
        Assert.Equal("/out", write.Sink.Path);
        Assert.Equal(new[] { "year" }, write.Sink.PartitionColumns);
    }

    [Fact]
    public void NoIrFieldOrPropertyIsTypedAsAStreamWriterOrEngineHandle()
    {
        foreach (Type type in AllIrTypes)
        {
            foreach (MemberInfo member in DescriptorMembers(type))
            {
                Type memberType = member switch
                {
                    PropertyInfo p => p.PropertyType,
                    FieldInfo f => f.FieldType,
                    _ => typeof(void),
                };

                Type? banned = FindBannedType(memberType);
                Assert.True(
                    banned is null,
                    $"{type.Name}.{member.Name} (declared type {memberType.Name}) exposes the "
                    + $"banned type {banned?.FullName} — reachable through arrays, generic type "
                    + "arguments, tuple elements, or nullable. Plan nodes must hold logical "
                    + "descriptors only (no stream/reader/writer/disposable/engine handle), even "
                    + "when the handle is nested inside a generic or tuple, and even in a private "
                    + "field.");
            }
        }
    }

    /// <summary>
    /// Recursively unwraps <paramref name="type"/> — arrays (element type), all generic type
    /// arguments (which naturally covers <c>List&lt;T&gt;</c>, <c>Lazy&lt;T&gt;</c>,
    /// <c>Func&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>, <c>Nullable&lt;T&gt;</c>, and
    /// <c>ValueTuple</c>/<c>Tuple</c> elements), and by-ref/pointer wrappers — and returns the
    /// first contained type that is a <see cref="Stream"/>, <see cref="TextReader"/>,
    /// <see cref="TextWriter"/>, <see cref="IDisposable"/>, or an engine
    /// (<c>DeltaSharp.Engine</c>) type, or <see langword="null"/> if none. Only the banned
    /// <b>leaf</b> types are flagged, so benign generics such as
    /// <c>IReadOnlyList&lt;Expression&gt;</c> or <c>ReadOnlyCollection&lt;string&gt;</c> pass
    /// (their arguments recurse to non-banned leaves). For DeltaSharp-defined types it additionally
    /// recurses into declared instance <b>fields and properties</b> (so an interface member that
    /// declares a banned property, or a computed property whose type wraps a banned type, is
    /// caught) AND scans the <b>transitive interface closure</b> (<see cref="Type.GetInterfaces"/>)
    /// so a banned property declared on a <b>base interface</b> at any inheritance depth (2-level,
    /// diamond) is caught even though an interface's <see cref="Type.BaseType"/> is
    /// <see langword="null"/>; BCL/third-party types are treated as leaves to avoid false positives.
    /// After this, the guard catches every STATICALLY-DECLARED banned member reachable from an IR
    /// type via its fields, properties, base classes, and the transitive interface closure, plus
    /// arrays/generics/tuples/nullable unwrapping and DeltaSharp-type recursion. The ONLY remaining
    /// residuals are inherent to static type scanning and are covered by the compile-time
    /// Core⊄Engine assembly boundary (the PRIMARY guarantee, asserted in
    /// <see cref="CoreAssemblyDoesNotReferenceTheEngineAssembly"/>): (i) a handle held by the
    /// IMPLEMENTATION of a marker interface that DECLARES no banned member (the runtime type is
    /// unknowable statically); (ii) a handle inside a third-party/BCL type's private internals
    /// (recursing BCL internals would false-positive); and (iii) a handle produced by a method
    /// return or static member (not held instance state).
    /// </summary>
    private static Type? FindBannedType(Type type) => FindBannedType(type, new HashSet<Type>());

    private static Type? FindBannedType(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return null;
        }

        if (IsBannedLeaf(type))
        {
            return type;
        }

        if (type.HasElementType)
        {
            Type? element = type.GetElementType();
            if (element is not null && FindBannedType(element, visited) is Type fromElement)
            {
                return fromElement;
            }
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                if (FindBannedType(argument, visited) is Type fromArgument)
                {
                    return fromArgument;
                }
            }
        }

        // F2/F3: recurse into the instance FIELDS and declared PROPERTIES of a *DeltaSharp-defined*
        // custom type (the IR's own structs/classes/interfaces) so held state cannot hide a handle:
        //   • a wrapper `struct ExoticWrapper { Stream Handle; }` (a field), and
        //   • an interface member that DECLARES a banned property
        //     (`interface IPartitionDescriptor { Stream Handle { get; } }` — interfaces have no
        //     fields, so a field-only scan missed them), and
        //   • a computed/auto property whose type is (or wraps) a banned type
        //     (`Stream StreamProp => …` — not backed by a scannable field).
        // Scoped to DeltaSharp namespaces on purpose: BCL/third-party types (string, List<T>, Task,
        // …) internally hold IDisposable/Stream fields and expose disposable-typed properties, so
        // recursing into them would explode into false positives. Their public generic arguments are
        // already unwrapped above, which is the only handle-carrying surface a benign BCL wrapper
        // legitimately exposes.
        //
        // Inherent residual (a static type scan CANNOT close these — they are covered by the
        // compile-time boundary asserted in CoreAssemblyDoesNotReferenceTheEngineAssembly, which is
        // the PRIMARY guarantee the plan IR holds no Engine handles):
        //   • a handle held by the *implementation* of a marker interface that does not itself
        //     declare a banned member — the concrete runtime type is unknown at scan time; and
        //   • a handle buried in a third-party/BCL wrapper's private internals — recursing BCL
        //     internals would explode into false positives (every string/List<T>/Task transitively
        //     references IDisposable/Stream state).
        // This scan enforces the remaining surface: BCL handles held as *declared* state (fields or
        // properties of banned or DeltaSharp-wrapping types). Only held instance state is in scope;
        // method return types and static members are deliberately not scanned.
        if (IsDeltaSharpDefinedType(type))
        {
            const BindingFlags memberFlags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(memberFlags))
                {
                    if (FindBannedType(field.FieldType, visited) is Type fromField)
                    {
                        return fromField;
                    }
                }

                foreach (PropertyInfo property in t.GetProperties(memberFlags))
                {
                    if (FindBannedType(property.PropertyType, visited) is Type fromProperty)
                    {
                        return fromProperty;
                    }
                }
            }

            // F4: INTERFACE-INHERITANCE closure. The base-CLASS-chain loop above walks
            // `t = t.BaseType`, but an interface's BaseType is null — so for an interface `type`
            // that loop visits only the interface's OWN declared members and never reaches its BASE
            // interfaces. A DeltaSharp `interface IDerived : IBase` whose IBase declares a banned
            // `Stream Handle { get; }`, held by an IR member typed IDerived, therefore bypassed a
            // members-only scan (the property is declared on IBase, statically visible through the
            // IDerived closure). `GetInterfaces()` returns the FULL TRANSITIVE set of implemented /
            // base interfaces, so scanning each one's declared properties closes interface-declared
            // banned members at ANY inheritance depth (2-level, diamond) in a single call — no
            // per-level walking. Each interface property TYPE is run through the same
            // leaf/unwrap/DeltaSharp-recurse logic, so benign interfaces stay clean: BCL interfaces
            // (`IEnumerable<Expression>`, `IReadOnlyList<string>`, `IEquatable<T>`) declare either no
            // properties or property types that unwrap to non-banned leaves, and are never falsely
            // flagged. Scanned interfaces are added to `visited` so diamond hierarchies (and any
            // self-/mutually-referential interface graph) are scanned once and terminate.
            foreach (Type iface in type.GetInterfaces())
            {
                if (!visited.Add(iface))
                {
                    continue;
                }

                foreach (PropertyInfo property in iface.GetProperties(memberFlags))
                {
                    if (FindBannedType(property.PropertyType, visited) is Type fromInterface)
                    {
                        return fromInterface;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="type"/> is defined in the DeltaSharp assemblies (namespace starts
    /// with <c>DeltaSharp</c>) — i.e. one of the IR's own types — and is therefore safe to recurse
    /// into field-by-field. BCL/third-party types are treated as leaves.
    /// </summary>
    private static bool IsDeltaSharpDefinedType(Type type) =>
        type.Namespace?.StartsWith("DeltaSharp", StringComparison.Ordinal) == true;

    private static bool IsBannedLeaf(Type type) =>
        typeof(Stream).IsAssignableFrom(type)
        || typeof(TextReader).IsAssignableFrom(type)
        || typeof(TextWriter).IsAssignableFrom(type)
        || typeof(IDisposable).IsAssignableFrom(type)
        || typeof(IAsyncDisposable).IsAssignableFrom(type)
        || type.Namespace?.StartsWith("DeltaSharp.Engine", StringComparison.Ordinal) == true;

    /// <summary>A test-only fixture whose fields hide stream handles inside generics/tuples to
    /// prove the recursive scan (unlike a shallow <c>IsAssignableFrom</c> check) catches them.</summary>
#pragma warning disable CS0649 // Fields exist only to be inspected reflectively; never assigned.
    private sealed class HiddenHandleFixture
    {
        public List<Stream>? ListOfStream;
        public Lazy<Stream>? LazyStream;
        public Func<Stream>? StreamFactory;
        public Stream[]? StreamArray;
        public (Stream Handle, int Id) TupleWithStream;
        public Dictionary<string, Stream>? MapToStream;
    }
#pragma warning restore CS0649

    [Fact]
    public void RecursiveScanCatchesHandlesNestedInGenericsArraysAndTuples()
    {
        // Non-vacuity: a shallow typeof(Stream).IsAssignableFrom(memberType) check would MISS every
        // one of these — the recursive scan must flag them all.
        Assert.NotNull(FindBannedType(typeof(List<Stream>)));
        Assert.NotNull(FindBannedType(typeof(Lazy<Stream>)));
        Assert.NotNull(FindBannedType(typeof(Func<Stream>)));
        Assert.NotNull(FindBannedType(typeof(Stream[])));
        Assert.NotNull(FindBannedType(typeof((Stream, int))));
        Assert.NotNull(FindBannedType(typeof(Dictionary<string, Stream>)));

        // And the member-level path (mirroring the production scan) catches a hidden field too.
        const BindingFlags fieldFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (FieldInfo field in typeof(HiddenHandleFixture).GetFields(fieldFlags))
        {
            Assert.True(
                FindBannedType(field.FieldType) is not null,
                $"The recursive scan failed to catch the hidden handle in "
                + $"{nameof(HiddenHandleFixture)}.{field.Name} ({field.FieldType.Name}).");
        }
    }

    [Fact]
    public void RecursiveScanDoesNotFlagBenignGenericsUsedByTheRealIr()
    {
        Assert.Null(FindBannedType(typeof(IReadOnlyList<Expression>)));
        Assert.Null(FindBannedType(typeof(IReadOnlyList<string>)));
        Assert.Null(FindBannedType(typeof(IReadOnlyDictionary<string, string>)));
        Assert.Null(FindBannedType(typeof(System.Collections.ObjectModel.ReadOnlyCollection<string>)));
        Assert.Null(FindBannedType(typeof((string, int))));
        Assert.Null(FindBannedType(typeof(int?)));
    }

    // ---- F1: IAsyncDisposable leaf gap ----------------------------------------------------------
    // Every Stream (e.g. FileStream) implements IAsyncDisposable, which does NOT derive from
    // IDisposable — so a field typed IAsyncDisposable (or List<IAsyncDisposable>, …) would bypass a
    // guard that only knows IDisposable. IAsyncDisposable is now a banned leaf.
#pragma warning disable CS0649 // Fields exist only to be inspected reflectively; never assigned.
    private sealed class AsyncDisposableFixture
    {
        public IAsyncDisposable? AsyncHandle;
        public List<IAsyncDisposable>? ListOfAsyncHandles;
    }
#pragma warning restore CS0649

    [Fact]
    public void RecursiveScanCatchesIAsyncDisposableHandles()
    {
        Assert.NotNull(FindBannedType(typeof(IAsyncDisposable)));
        Assert.NotNull(FindBannedType(typeof(List<IAsyncDisposable>)));

        const BindingFlags fieldFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (FieldInfo field in typeof(AsyncDisposableFixture).GetFields(fieldFlags))
        {
            Assert.True(
                FindBannedType(field.FieldType) is not null,
                $"The scan failed to catch the IAsyncDisposable handle in "
                + $"{nameof(AsyncDisposableFixture)}.{field.Name} ({field.FieldType.Name}).");
        }
    }

    // ---- F2: custom (DeltaSharp-defined) struct/class fields must be recursed -------------------
    // FindBannedType unwraps arrays/generics/tuples/nullable but did NOT descend into the fields of
    // a custom wrapper, so `struct ExoticWrapper { Stream Handle; }` slipped past. The scan now
    // recurses into DeltaSharp-defined types' fields (these fixtures live in a DeltaSharp.* test
    // namespace, mirroring an IR-owned wrapper). BCL/third-party types stay leaves.
#pragma warning disable CS0649 // Fields exist only to be inspected reflectively; never assigned.
    private struct ExoticWrapperFixture
    {
        public Stream Handle;
    }

    private sealed class HandleWrapperFixture
    {
        public ExoticWrapperFixture Inner; // nested two levels: wrapper -> struct -> Stream
    }

    private sealed class DirectHandleWrapperFixture
    {
        private readonly Stream? _handle;

        public DirectHandleWrapperFixture(Stream? handle) => _handle = handle;
    }

    private sealed class BenignWrapperFixture
    {
        public string? Name;
        public int? Count;
        public IReadOnlyList<Expression>? Expressions;
    }
#pragma warning restore CS0649

    [Fact]
    public void RecursiveScanCatchesHandlesHiddenInsideDeltaSharpDefinedCustomTypes()
    {
        // Bypass proof: unwrapping alone (arrays/generics/tuples) returns null for a custom wrapper;
        // the field recursion must find the Stream nested one and two levels deep.
        Assert.NotNull(FindBannedType(typeof(ExoticWrapperFixture)));
        Assert.NotNull(FindBannedType(typeof(HandleWrapperFixture)));
        Assert.NotNull(FindBannedType(typeof(DirectHandleWrapperFixture)));

        // And a member-level path: an IR node holding such a wrapper is caught.
        Assert.NotNull(FindBannedType(typeof(List<HandleWrapperFixture>)));
    }

    [Fact]
    public void RecursiveScanDoesNotFalselyFlagDeltaSharpTypesWithOnlyBenignFields()
    {
        // The field recursion must NOT over-reach: a DeltaSharp type whose fields are only BCL
        // scalars/collections and other benign IR types stays clean.
        Assert.Null(FindBannedType(typeof(BenignWrapperFixture)));
        Assert.Null(FindBannedType(typeof(Expression)));
        Assert.Null(FindBannedType(typeof(LogicalPlan)));
    }

    // ---- F3: interface-declared + computed properties must be recursed --------------------------
    // The recursion scanned FIELDS ONLY, so two surfaces slipped past:
    //   (a) a DeltaSharp interface DECLARING a banned property — interfaces have no fields, so
    //       GetFields() is empty; but GetProperties() exposes the declared member; and
    //   (b) a computed/auto property whose type is (or wraps) a banned type — not backed by a
    //       scannable field.
    // The scan now also applies FindBannedType to each declared instance property's PropertyType.
#pragma warning disable CS0649 // Fields exist only to be inspected reflectively; never assigned.
    private interface IStreamDescriptorFixture
    {
        Stream Handle { get; }
    }

    private sealed class InterfaceTypedMemberFixture
    {
        public IStreamDescriptorFixture? Descriptor;
    }

    private sealed class ComputedStreamPropertyFixture
    {
        public Stream StreamProp => throw new NotSupportedException("fixture: never invoked");
    }

    private sealed class AsyncDisposablePropertyFixture
    {
        public IAsyncDisposable AsyncHandle => throw new NotSupportedException("fixture: never invoked");
    }

    private sealed class BenignPropertyFixture
    {
        public IReadOnlyList<Expression>? Expressions { get; init; }

        public LogicalPlan? Child { get; init; }

        public string Name => "benign";
    }
#pragma warning restore CS0649

    [Fact]
    public void RecursiveScanCatchesBannedPropertyDeclaredByADeltaSharpInterface()
    {
        // (a) The interface itself declares a Stream property — caught directly and via a member.
        Assert.NotNull(FindBannedType(typeof(IStreamDescriptorFixture)));
        Assert.NotNull(FindBannedType(typeof(InterfaceTypedMemberFixture)));
    }

    [Fact]
    public void RecursiveScanCatchesComputedBannedProperties()
    {
        // (b) A computed Stream property and (c) an IAsyncDisposable property — no backing field.
        Assert.NotNull(FindBannedType(typeof(ComputedStreamPropertyFixture)));
        Assert.NotNull(FindBannedType(typeof(AsyncDisposablePropertyFixture)));
    }

    [Fact]
    public void RecursiveScanDoesNotFalselyFlagDeltaSharpTypesWithOnlyBenignProperties()
    {
        // The property recursion must NOT over-reach: benign IR-shaped properties (an expression
        // list, a child plan, a string) stay clean — mirroring the real IR's property surface.
        Assert.Null(FindBannedType(typeof(BenignPropertyFixture)));
    }

    // A self-referential property must not loop — the visited-set cycle guard covers properties too.
    private sealed class SelfReferentialPropertyFixture
    {
        public SelfReferentialPropertyFixture? Next { get; init; }

        public string Name => "cycle";
    }

    [Fact]
    public void PropertyRecursionTerminatesOnSelfReferentialTypes()
    {
        Assert.Null(FindBannedType(typeof(SelfReferentialPropertyFixture)));
    }

    // ---- F4: banned property declared on a BASE interface (interface-inheritance closure) -------
    // An interface's Type.BaseType is null, so the base-CLASS-chain loop visits only an interface's
    // OWN declared members and never its BASE interfaces. The red-team repro below — a DeltaSharp
    // `interface IDerived : IBase` whose IBase declares `Stream Handle { get; }`, held by an IR
    // member typed IDerived — is genuinely reachable held state (a declared property statically
    // visible through the IDerived closure) yet bypassed a members-only scan. The scan now walks
    // Type.GetInterfaces() (the FULL TRANSITIVE interface set) and applies FindBannedType to each
    // interface's declared property types, closing this at any depth (2-level, diamond) in one call.
    private interface IBaseStreamDescriptorFixture
    {
        Stream Handle { get; }
    }

    // The exact repro: derived interface adds nothing; the banned member lives on the base.
    private interface IDerivedDescriptorFixture : IBaseStreamDescriptorFixture
    {
    }

    // 2-level interface inheritance: IDeep : IMid : IBase{Stream}.
    private interface IMidDescriptorFixture : IBaseStreamDescriptorFixture
    {
    }

    private interface IDeepDescriptorFixture : IMidDescriptorFixture
    {
    }

    // Diamond: IDiamond : ILeft, IRight ; ILeft and IRight both : IBase{Stream}. GetInterfaces()
    // deduplicates IBase, and the visited-set guard ensures it is scanned once.
    private interface ILeftDescriptorFixture : IBaseStreamDescriptorFixture
    {
    }

    private interface IRightDescriptorFixture : IBaseStreamDescriptorFixture
    {
    }

    private interface IDiamondDescriptorFixture : ILeftDescriptorFixture, IRightDescriptorFixture
    {
    }

    // A mutually/self-referential interface graph must terminate (cycle guard covers interfaces).
    private interface ISelfReferentialDescriptorFixture
    {
        ISelfReferentialDescriptorFixture? Next { get; }
    }

    // Benign DeltaSharp interfaces (own + inherited) with no banned member must stay clean.
    private interface IBenignDescriptorFixture
    {
        IReadOnlyList<Expression> Expressions { get; }

        string Name { get; }
    }

    private interface IDerivedBenignDescriptorFixture : IBenignDescriptorFixture
    {
    }

#pragma warning disable CS0649 // Fields exist only to be inspected reflectively; never assigned.
    private sealed class DerivedInterfaceTypedMemberFixture
    {
        public IDerivedDescriptorFixture? Descriptor;
    }
#pragma warning restore CS0649

    [Fact]
    public void RecursiveScanCatchesBannedPropertyDeclaredByABaseInterface()
    {
        // Red-team repro: the banned Stream is declared on the BASE interface, not the derived one.
        // type.BaseType is null for interfaces, so ONLY the GetInterfaces() closure reaches it.
        Assert.NotNull(FindBannedType(typeof(IDerivedDescriptorFixture)));
        Assert.NotNull(FindBannedType(typeof(DerivedInterfaceTypedMemberFixture)));

        // 2-level interface inheritance (IDeep : IMid : IBase{Stream}).
        Assert.NotNull(FindBannedType(typeof(IDeepDescriptorFixture)));

        // Diamond (IDiamond : ILeft, IRight ; both : IBase{Stream}) — caught, no infinite loop.
        Assert.NotNull(FindBannedType(typeof(IDiamondDescriptorFixture)));
    }

    [Fact]
    public void InterfaceClosureScanTerminatesAndDoesNotFalselyFlagBenignInterfaces()
    {
        // Cycle guard covers self-/mutually-referential interface hierarchies (no stack overflow).
        Assert.Null(FindBannedType(typeof(ISelfReferentialDescriptorFixture)));

        // Benign DeltaSharp interfaces (own members and inherited) with no banned member stay clean.
        Assert.Null(FindBannedType(typeof(IBenignDescriptorFixture)));
        Assert.Null(FindBannedType(typeof(IDerivedBenignDescriptorFixture)));

        // Benign BCL interfaces must NOT be flagged — the exact false-positive risk from
        // GetInterfaces() returning framework interfaces.
        Assert.Null(FindBannedType(typeof(IEnumerable<Expression>)));
        Assert.Null(FindBannedType(typeof(IReadOnlyList<string>)));
        Assert.Null(FindBannedType(typeof(IReadOnlyList<Expression>)));

        // The real IR descriptor types (which implement IEquatable<T>) stay clean.
        foreach (Type type in AllIrTypes)
        {
            Assert.Null(FindBannedType(type));
        }
    }


    /// <summary>
    /// Every instance field (public and private, declared on the type or any base IR type) and
    /// every public instance property — so a hidden <c>private Stream _handle</c> cannot slip past
    /// a public-property-only scan.
    /// </summary>
    private static IEnumerable<MemberInfo> DescriptorMembers(Type type)
    {
        const BindingFlags fieldFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(fieldFlags))
            {
                yield return field;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return property;
        }
    }

    [Fact]
    public void NoIrPropertyIsTypedAsAStreamWriterOrEngineHandle()
    {
        foreach (Type type in AllIrTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type propertyType = property.PropertyType;
                Assert.False(
                    typeof(Stream).IsAssignableFrom(propertyType)
                    || typeof(TextReader).IsAssignableFrom(propertyType)
                    || typeof(TextWriter).IsAssignableFrom(propertyType)
                    || typeof(IDisposable).IsAssignableFrom(propertyType),
                    $"{type.Name}.{property.Name} is a stream/handle/disposable type "
                    + $"({propertyType.Name}); plan nodes must hold logical descriptors only.");

                Assert.False(
                    propertyType.Namespace?.StartsWith("DeltaSharp.Engine", StringComparison.Ordinal) == true,
                    $"{type.Name}.{property.Name} references the engine type {propertyType.FullName}; "
                    + "the logical plan IR must be Engine-free.");
            }
        }
    }

    [Fact]
    public void CoreAssemblyDoesNotReferenceTheEngineAssembly()
    {
        IEnumerable<string> referenced = typeof(UnresolvedRelation).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain("DeltaSharp.Engine", referenced);
    }

    [Fact]
    public void ConstructingScanAndWriteOverANonExistentPathDoesNoIo()
    {
        string bogus = Path.Combine(
            Path.GetTempPath(), "deltasharp-plan-ir-nonexistent-path-ac3-fixture");

        // No exception: construction reads nothing and opens nothing.
        var scan = new UnresolvedRelation(new[] { bogus });
        var write = new WriteToSource(
            scan, new SinkDescriptor("parquet", SaveMode.Overwrite, path: bogus));

        Assert.False(File.Exists(bogus));
        Assert.NotNull(write.Sink.Path);
    }
}
