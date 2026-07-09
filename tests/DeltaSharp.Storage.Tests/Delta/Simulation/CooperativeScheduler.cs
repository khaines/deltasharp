using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace DeltaSharp.Storage.Tests.Delta.Simulation;

/// <summary>
/// A deterministic, single-threaded <b>cooperative scheduler</b> that enumerates commit interleavings from
/// a seed (design §3.0/§3.4). It drives several logical writers through the <see cref="DeltaCommitter"/>'s
/// await/interleaving points — <c>BeforePutProbe</c>, the backend put-if-absent CAS, the ambiguous re-read,
/// the winner scan, and the transient backoff — in a <b>seed-determined order</b>, with <b>no real thread
/// races and no wall-clock sleeps</b>. Every run is byte-reproducible from its <see cref="Seed"/>.
///
/// <para><b>How determinism is achieved.</b> <see cref="DeltaCommitter"/> awaits every asynchronous step
/// with <c>ConfigureAwait(false)</c>, so a <see cref="SynchronizationContext"/> pump cannot intercept its
/// continuations. Instead each interleaving point parks the running writer on a fresh
/// <see cref="TaskCompletionSource"/> <b>gate</b> (created WITHOUT
/// <c>RunContinuationsAsynchronously</c>). The single scheduler thread resumes a writer by calling
/// <see cref="TaskCompletionSource.SetResult()"/>; because the awaiter used <c>ConfigureAwait(false)</c> on
/// an incomplete gate, the continuation runs <b>inline</b> on the scheduler thread until the writer reaches
/// its next park (a new incomplete gate) or completes, at which point control unwinds back into
/// <see cref="SetResult()"/>. The whole simulation therefore executes on one thread with a single logical
/// writer running at any instant — the interleaving is exactly the seeded sequence of resume decisions,
/// never OS thread timing.</para>
/// </summary>
internal sealed class CooperativeScheduler
{
    private readonly ulong _seed;
    private readonly List<SchedulerDecision> _decisions = new();
    private Writer[] _writers = Array.Empty<Writer>();
    private ulong _prng;
    private int _step;
    private Writer? _running;

    /// <summary>Creates a scheduler whose interleaving is fully determined by <paramref name="seed"/>.</summary>
    public CooperativeScheduler(int seed)
    {
        _seed = unchecked((ulong)seed);

        // Avoid the xorshift fixed point at 0; mix with the golden-ratio constant so adjacent seeds diverge.
        _prng = (_seed ^ 0x9E3779B97F4A7C15UL) is 0UL ? 0x9E3779B97F4A7C15UL : _seed ^ 0x9E3779B97F4A7C15UL;
    }

    /// <summary>The base seed that reproduces this exact interleaving.</summary>
    public int Seed => unchecked((int)_seed);

    /// <summary>The ordered log of resume decisions (one per scheduling step) — part of the reproduction
    /// bundle so a failing interleaving can be replayed and inspected step by step.</summary>
    public ImmutableArray<SchedulerDecision> Decisions => _decisions.ToImmutableArray();

    /// <summary>A single-line, human-readable rendering of the interleaving: <c>w0,w2,w1,w0,…</c>.</summary>
    public string InterleavingSummary =>
        string.Join(",", _decisions.Select(d => "w" + d.WriterId.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Whether the logical writer <paramref name="writerId"/> has run to completion. Used by a
    /// writer body that must observe another writer's committed effect (a scripted <b>happens-before</b>),
    /// by spin-yielding until the dependency completes — deterministically, since the scheduler advances one
    /// writer at a time.</summary>
    public bool IsCompleted(int writerId) =>
        writerId >= 0 && writerId < _writers.Length && _writers[writerId].Completed;

    /// <summary>
    /// Awaited by the in-memory backend and by the commit probe at each interleaving point. Parks the
    /// <b>currently running</b> writer on a fresh gate and returns its (incomplete) task, so control
    /// unwinds to the scheduler, which then resumes some parked writer per the seed. When no writer is
    /// running (e.g. table seeding performed outside <see cref="RunAsync"/>), it is an inert no-op.
    /// </summary>
    public Task YieldAsync(string reason)
    {
        Writer? w = _running;
        if (w is null)
        {
            return Task.CompletedTask; // not under scheduler control — do not interleave.
        }

        // A gate WITHOUT RunContinuationsAsynchronously: SetResult resumes the ConfigureAwait(false)
        // continuation inline on the scheduler thread (the load-bearing single-thread invariant).
        var gate = new TaskCompletionSource();
        w.Gate = gate;
        w.Reason = reason;
        w.Parked = true;
        w.Steps++;
        return gate.Task;
    }

    /// <summary>
    /// Runs every <paramref name="writerBodies"/> to completion under the seeded interleaving and returns
    /// only when all have finished. Each body is a logical writer; expected commit outcomes (a classified
    /// conflict, an idempotent skip) must be caught inside the body — an exception that escapes a body is a
    /// genuine defect and is rethrown here.
    ///
    /// <para><b>Why a dedicated thread.</b> Inline resumption of a <c>ConfigureAwait(false)</c> continuation
    /// on <see cref="TaskCompletionSource.SetResult()"/> only happens when the runtime deems the current
    /// location valid for inlining (<c>AwaitTaskContinuation.IsValidLocationForInlining</c>): no ambient
    /// non-default <see cref="SynchronizationContext"/> and a default <see cref="TaskScheduler"/>. A test
    /// harness (xUnit) installs a <b>derived</b> synchronization context on its test thread, which suppresses
    /// inlining and would scatter the writers across the thread pool — defeating determinism. We therefore
    /// drive the whole interleaving on one dedicated thread with a null synchronization context, where every
    /// gate resume runs inline and exactly one logical writer is live at any instant.</para>
    /// </summary>
    public Task RunAsync(IReadOnlyList<Func<Task>> writerBodies)
    {
        ArgumentNullException.ThrowIfNull(writerBodies);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // A pristine driving thread: no derived SynchronizationContext, default TaskScheduler ⇒
            // SetResult resumes ConfigureAwait(false) gate continuations INLINE on this one thread.
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Drive(writerBodies);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "deltasharp-coop-scheduler",
        };

        thread.Start();
        return completion.Task;
    }

    private void Drive(IReadOnlyList<Func<Task>> writerBodies)
    {
        _writers = new Writer[writerBodies.Count];
        var launches = new Task[writerBodies.Count];
        for (int i = 0; i < writerBodies.Count; i++)
        {
            var writer = new Writer(i);
            _writers[i] = writer;
            launches[i] = Launch(writer, writerBodies[i]); // starts, then parks at its initial gate.
        }

        while (true)
        {
            var ready = _writers.Where(w => w.Parked && !w.Completed).ToList();
            if (ready.Count == 0)
            {
                if (_writers.All(w => w.Completed))
                {
                    break;
                }

                throw new InvalidOperationException(
                    "Cooperative scheduler stalled: no runnable writer but not all writers completed — a writer "
                    + "awaited something outside the scheduler's interleaving points (a real thread race or an "
                    + "un-instrumented await). State: "
                    + string.Join("; ", _writers.Select(w => $"w{w.Id}[parked={w.Parked},done={w.Completed},steps={w.Steps},reason={w.Reason}]"))
                    + $" decisions={_decisions.Count}");
            }

            Writer chosen = ready[NextIndex(ready.Count)];
            _decisions.Add(new SchedulerDecision(_step++, chosen.Id, chosen.Reason));

            chosen.Parked = false;
            TaskCompletionSource gate = chosen.Gate!;
            _running = chosen;
            gate.SetResult(); // resumes 'chosen' INLINE until it parks again or completes.
            _running = null;

            // Self-check (design §3.4.3 determinism guard): a resumed writer must, by the time SetResult
            // unwinds, be EITHER re-parked at a fresh interleaving point OR completed. If it is neither, its
            // continuation escaped inline resumption — an un-instrumented await reached the thread pool — so
            // the interleaving is no longer under the scheduler's control. Fail fast rather than let a future
            // committer change silently corrupt determinism.
            if (!chosen.Completed && !chosen.Parked)
            {
                throw new InvalidOperationException(
                    $"Cooperative scheduler lost control of writer w{chosen.Id}: after resume it is neither re-parked "
                    + "nor completed, so its continuation escaped inline resumption (an un-instrumented await hit the "
                    + $"thread pool). Last reason={chosen.Reason}, steps={chosen.Steps}, decisions={_decisions.Count}.");
            }
        }

        // Every launch has completed inline by now; surface an escaping body exception as a genuine defect.
        foreach (Task launch in launches)
        {
            if (launch.IsFaulted)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(
                    launch.Exception!.InnerExceptions.Count == 1
                        ? launch.Exception.InnerExceptions[0]
                        : launch.Exception);
            }
        }
    }

    private async Task Launch(Writer writer, Func<Task> body)
    {
        // Park before running anything so RunAsync deterministically drives the first step too.
        var initialGate = new TaskCompletionSource();
        writer.Gate = initialGate;
        writer.Parked = true;
        await initialGate.Task.ConfigureAwait(false);

        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            writer.Completed = true;
        }
    }

    private int NextIndex(int count)
    {
        // xorshift64 — a small, self-contained, runtime-independent PRNG so the interleaving is reproducible
        // regardless of the System.Random implementation (and without tripping the System.Random ban).
        ulong x = _prng;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _prng = x;
        return (int)(x % (ulong)count);
    }

    private sealed class Writer
    {
        public Writer(int id) => Id = id;

        public int Id { get; }

        public TaskCompletionSource? Gate { get; set; }

        public bool Parked { get; set; }

        public bool Completed { get; set; }

        public int Steps { get; set; }

        public string Reason { get; set; } = "init";
    }
}

/// <summary>A single resume decision: the scheduler <see cref="Step"/> advanced writer <see cref="WriterId"/>
/// which was parked at the interleaving point named by <see cref="Reason"/>.</summary>
internal readonly record struct SchedulerDecision(int Step, int WriterId, string Reason);
