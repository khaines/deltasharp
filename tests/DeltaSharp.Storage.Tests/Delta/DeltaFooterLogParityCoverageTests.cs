using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DeltaSharp.Storage.Writing;
using Xunit;

namespace DeltaSharp.Storage.Tests.Delta;

/// <summary>
/// Completeness guard for the footer/log parity suite's OPERATION set.
/// </summary>
/// <remarks>
/// This is a GUARD -- a test whose subject is the adequacy of other tests -- so it is subject to
/// the practice in <c>docs/engineering/design/testing-conventions.md</c>, "Falsify a guard before
/// reporting it": a guard that stops working goes GREEN rather than red, because its failure mode
/// is agreeing with whatever it audits. This one was mutated before it was reported, and the first
/// version FAILED that check (see <see cref="ReachableFromTests"/>). Do not modify it without
/// re-running that falsification.
/// </remarks>
/// <remarks>
/// <para>
/// The parity suite derives its CONTENT axes -- metadata value kinds come from
/// <c>Enum.GetValues&lt;MetadataValueKind&gt;()</c>, the type corpus from a shared object that is
/// itself checked against what the writer accepts. But its OPERATION set was hand-listed, and for
/// several rounds it was hand-listed at exactly one: a single append. A schema-carrying commit is
/// produced from several different call sites, and an append-only sequence reaches only some of
/// them, so a transform installed at the overwrite site was invisible to any number of appends --
/// demonstrated end to end, at the two real artifacts, with the whole suite green.
/// </para>
/// <para>
/// A derived CONTENT axis over a hand-listed OPERATION set is the same defect this PR exists to
/// remove, one level up: the thing that varies is thorough, and the thing that selects what varies
/// is a list nobody is obliged to maintain. So the required set is read off the product here. A
/// new write entry point fails this test until the parity suite drives it, rather than silently
/// enlarging the set of unguarded operations.
/// </para>
/// <para>
/// Both sides are derived. The REQUIRED side is every method on <see cref="DeltaWriteTarget"/> that
/// commits (returns <c>Task&lt;DeltaWriteResult&gt;</c>), public or internal. The DRIVEN side is
/// read out of the parity suite's compiled IL rather than from a list, because a list of "what we
/// drive" can be edited to say anything; IL cannot. The IL is walked opcode by opcode rather than
/// scanned for call-opcode bytes, since operand bytes can coincide with an opcode value and a false
/// positive here would report an operation as covered when it is not -- failing OPEN, which is the
/// one failure mode this guard must not have.
/// </para>
/// </remarks>
public sealed class DeltaFooterLogParityCoverageTests
{
    [Fact]
    public async Task ParityGuard_DrivesEveryWriteEntryPoint()
    {
        MethodInfo[] required = typeof(DeltaWriteTarget)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(Task<DeltaWriteResult>))
            .ToArray();

        // Non-vacuity: an empty required set makes every check below pass for the wrong reason.
        Assert.NotEmpty(required);

        IReadOnlySet<string> driven = await RunParitySuiteAsync();

        string[] undriven = required
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(n => !driven.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (undriven.Length > 0)
        {
            Assert.Fail(
                "DeltaWriteTarget commits schema-carrying metaData from entry points that NOTHING "
                + "in the footer/log parity suite executed, so a transform at their call sites "
                + "reproduces issue #679's divergence with the suite green."
                + $"{Environment.NewLine}  undriven: {string.Join(", ", undriven)}"
                + $"{Environment.NewLine}  executed: {string.Join(", ", driven.OrderBy(n => n, StringComparer.Ordinal))}"
                + $"{Environment.NewLine}  A skipped test, a deleted [InlineData] row and a "
                + "commented-out [Fact] all reach here, because this counts EXECUTION.");
        }
    }

    /// <summary>
    /// Runs every executable case of the parity suite and returns the entry points they invoked.
    /// </summary>
    /// <remarks>
    /// The suite is executed rather than analysed, and the case list is built the way xUnit builds
    /// it: <c>[Fact]</c> and <c>[Theory]</c> methods, one case per <c>[InlineData]</c> row, MINUS
    /// anything carrying a <c>Skip</c>. That is what makes a deleted data row and a skipped theory
    /// visible -- both remove cases from this enumeration, so the entry points they drove stop
    /// appearing in the recorded set.
    /// </remarks>
    private static async Task<IReadOnlySet<string>> RunParitySuiteAsync()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        MethodInfo[] tests = typeof(DeltaFooterLogSchemaParityTests).GetMethods(flags)
            .Where(m => m.GetCustomAttributes(inherit: false).Any(IsExecutableTestCase))
            .ToArray();

        // Non-vacuity: if the suite has no executable cases at all, the recorded set is empty and
        // the guard must report every operation undriven rather than silently pass.
        Assert.NotEmpty(tests);

        WriteEntryPointRecorder.Reset();

        foreach (MethodInfo test in tests)
        {
            foreach (object?[] args in CasesFor(test))
            {
                object instance = Activator.CreateInstance(typeof(DeltaFooterLogSchemaParityTests))!;
                try
                {
                    if (test.Invoke(instance, args) is Task task)
                    {
                        await task;
                    }
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                }
            }
        }

        return WriteEntryPointRecorder.Snapshot();
    }

    /// <summary>A test attribute that will actually run -- i.e. one without a <c>Skip</c>.</summary>
    private static bool IsExecutableTestCase(object attribute)
    {
        if (attribute.GetType().Name is not ("FactAttribute" or "TheoryAttribute"))
        {
            return false;
        }

        string? skip = attribute.GetType().GetProperty("Skip")?.GetValue(attribute) as string;
        return string.IsNullOrEmpty(skip);
    }

    private static IEnumerable<object?[]> CasesFor(MethodInfo test)
    {
        object[] rows = test.GetCustomAttributes(inherit: false)
            .Where(a => a.GetType().Name == "InlineDataAttribute")
            .ToArray();

        if (rows.Length == 0)
        {
            yield return Array.Empty<object?>();
            yield break;
        }

        foreach (object row in rows)
        {
            object?[]? data = row.GetType()
                .GetMethod("GetData")?
                .Invoke(row, new object?[] { test }) is IEnumerable<object?[]> sets
                ? sets.FirstOrDefault()
                : null;

            yield return data ?? (object?[])row.GetType().GetProperty("Data")!.GetValue(row)!;
        }
    }

    /// <summary>
    /// Every recorded label names a method the recording code really calls.
    /// </summary>
    /// <remarks>
    /// The recorded side is written by hand beside each invocation, so on its own it could claim
    /// anything. This checks each label against the IL of the parity suite: a label with no
    /// corresponding call is a coverage claim with nothing behind it. Static reachability is the
    /// wrong tool for "was it executed" and the right one for "is this label honest" -- the two
    /// guards together give both halves.
    /// </remarks>
    [Fact]
    public async Task RecordedEntryPoints_AreBackedByRealCalls()
    {
        IReadOnlySet<string> recorded = await RunParitySuiteAsync();
        HashSet<string> callable = ReachableFromTests(typeof(DeltaFooterLogSchemaParityTests))
            .Where(m => m.DeclaringType == typeof(DeltaWriteTarget))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(callable);

        string[] unbacked = recorded.Where(n => !callable.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        if (unbacked.Length > 0)
        {
            Assert.Fail(
                "The parity suite records driving entry points its own code never calls, so the "
                + "recorded coverage overstates what runs."
                + $"{Environment.NewLine}  recorded but never called: {string.Join(", ", unbacked)}");
        }
    }

    /// <summary>
    /// Every method reachable from the suite's actual <c>[Fact]</c>/<c>[Theory]</c> entry points,
    /// following calls transitively through the suite's own helpers.
    /// </summary>
    /// <remarks>
    /// REACHABILITY, not mention. The first version of this guard collected every call appearing
    /// anywhere in the test class, and that was measurably too weak: deleting the only
    /// <c>await OverwriteAsync(...)</c> from the test body left the guard GREEN, because the
    /// private helper that wraps the call still existed and still named the method. A guard that a
    /// dead helper satisfies reports coverage that no test performs -- failing OPEN, which is the
    /// one direction this guard must never fail. So the walk starts at the xUnit entry points and
    /// follows only calls it can actually reach from them.
    /// <para>
    /// Async bodies live in compiler-generated state machines, so a test method's own IL contains
    /// almost none of its calls. Those state machines are resolved by ATTRIBUTE IDENTITY -- each
    /// reached method is asked for its own <see cref="StateMachineAttribute"/> -- and never by
    /// name. An earlier version matched them by name substring
    /// (<c>nested.Name.Contains($"&lt;{m.Name}&gt;")</c>), which walked ANY nested type whose name
    /// embedded a reached method's name, reachable or not. A never-invoked non-capturing async
    /// local function compiles to <c>&lt;&lt;Outer&gt;g__Dead|N_M&gt;d__X</c>, matched that
    /// pattern, and had its calls reported as backing a recorded label -- measured GREEN with the
    /// only real call sitting in dead code. Worse, the same walk was capture-SENSITIVE: the
    /// capturing form of the identical local function lands in a <c>&lt;&gt;c__DisplayClass</c>,
    /// was not matched, and went red. Soundness that depends on whether a lambda happens to
    /// capture is not soundness, and compiler lowering is not a contract. Attribute identity
    /// resolves the state machine OF a specific reached method, so dead code is unreachable by
    /// construction and a called async local function is still followed transitively (its own
    /// method is a call target, and its state machine comes off its own attribute).
    /// </para>
    /// <para>
    /// Where this walk is now deliberately INCOMPLETE it is incomplete in the strict direction: a
    /// body reached only through a delegate (<c>ldftn</c> + <c>newobj</c>) is not followed, so an
    /// entry point called exclusively from a lambda would be reported as unbacked. That fails
    /// CLOSED -- a false alarm, not a false clearance -- which is the correct direction for a
    /// guard.
    /// </para>
    /// </remarks>
    private static HashSet<MethodBase> ReachableFromTests(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        MethodInfo[] entryPoints = type.GetMethods(flags)
            .Where(m => m.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .ToArray();

        // Non-vacuity: no entry points means an empty reachable set, which would make every
        // coverage check below pass for the wrong reason.
        Assert.NotEmpty(entryPoints);

        var found = new HashSet<MethodBase>();
        var seen = new HashSet<MethodBase>(entryPoints);
        var queue = new Queue<MethodBase>(entryPoints);

        while (queue.Count > 0)
        {
            MethodBase current = queue.Dequeue();

            // An async or iterator method's own body is a stub that builds a state machine; the
            // calls are in the machine. Ask THIS method which machine is its own, so nothing is
            // reached that this method did not produce.
            foreach (MethodBase moved in StateMachineMethodsOf(current))
            {
                if (seen.Add(moved))
                {
                    queue.Enqueue(moved);
                }
            }

            foreach (MethodBase called in CallTargets(current))
            {
                found.Add(called);

                // Follow the suite's own code -- its helpers and local functions -- but stop at
                // the product boundary.
                if (IsWithin(called.DeclaringType, type) && seen.Add(called))
                {
                    queue.Enqueue(called);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The methods of <paramref name="method"/>'s own compiler-generated state machine, if it has
    /// one, resolved from the attribute the compiler stamped on that specific method.
    /// </summary>
    private static IEnumerable<MethodBase> StateMachineMethodsOf(MethodBase method)
    {
        Type? machine = (method as MethodInfo)?
            .GetCustomAttribute<StateMachineAttribute>(inherit: false)?.StateMachineType;

        if (machine is null)
        {
            yield break;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MethodBase m in machine.GetMethods(flags))
        {
            yield return m;
        }
    }

    private static bool IsWithin(Type? candidate, Type outer)
    {
        for (Type? t = candidate; t is not null; t = t.DeclaringType)
        {
            if (t == outer)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<MethodBase> CallTargets(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        Type[] typeArgs = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : Type.EmptyTypes;
        Type[] methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : Type.EmptyTypes;

        int i = 0;
        while (i < il.Length)
        {
            OpCode op = ReadOpCode(il, ref i);

            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                int token = BitConverter.ToInt32(il, i);
                MethodBase? target = null;
                try
                {
                    target = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                }
                catch (ArgumentException)
                {
                    // A token this module cannot resolve is not a call we can attribute.
                }

                if (target is not null)
                {
                    yield return target;
                }
            }

            i += OperandSize(op, il, i);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int i)
    {
        byte b = il[i++];
        if (b != 0xFE)
        {
            return SingleByteOpCodes[b];
        }

        return TwoByteOpCodes[il[i++]];
    }

    /// <summary>Bytes of inline operand following an opcode, so the walk lands on real opcodes.</summary>
    private static int OperandSize(OpCode op, byte[] il, int i) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,

        // A jump table: 4-byte count, then that many 4-byte targets.
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, i) * 4),
        _ => throw new NotSupportedException(
            $"IL operand type {op.OperandType} is not handled, so the instruction walk would "
            + "desynchronise and this guard could report an undriven operation as covered."),
    };

    private static readonly OpCode[] SingleByteOpCodes = BuildTable(single: true);
    private static readonly OpCode[] TwoByteOpCodes = BuildTable(single: false);

    private static OpCode[] BuildTable(bool single)
    {
        var table = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
            {
                continue;
            }

            bool isSingle = op.Size == 1;
            if (isSingle == single)
            {
                table[op.Value & 0xFF] = op;
            }
        }

        return table;
    }
}
