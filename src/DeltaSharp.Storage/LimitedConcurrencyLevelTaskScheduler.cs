using System.Collections.Generic;

namespace DeltaSharp.Storage;

/// <summary>
/// A <see cref="TaskScheduler"/> that runs at most <see cref="MaximumConcurrencyLevel"/> tasks concurrently on
/// the shared <see cref="ThreadPool"/> (the canonical .NET ParallelExtensionsExtras sample). It is the
/// <b>dedicated, hard-capped</b> execution surface for untrusted, cancellation-ignoring Parquet decodes
/// (design §5.4 C-DECODE): a non-terminating decode can never occupy more than <see cref="MaximumConcurrencyLevel"/>
/// pool threads at once, so a crafted input can neither starve the shared pool (the C7 "13.3× slower, never
/// recovers" failure) nor scale its damage with the number of malicious reads.
/// </summary>
/// <remarks>
/// <b>NativeAOT-safe:</b> uses only <see cref="ThreadPool.UnsafeQueueUserWorkItem(WaitCallback, object)"/>,
/// a lock, and a linked list — no reflection or dynamic codegen.
/// </remarks>
internal sealed class LimitedConcurrencyLevelTaskScheduler : TaskScheduler
{
    // Indicates whether the current thread is processing work items.
    [ThreadStatic]
    private static bool _currentThreadIsProcessingItems;

    // The list of tasks to be executed.
    private readonly LinkedList<Task> _tasks = new(); // protected by lock (_tasks)

    // The maximum concurrency level allowed by this scheduler.
    private readonly int _maxDegreeOfParallelism;

    // Indicates whether the scheduler is currently processing work items.
    private int _delegatesQueuedOrRunning; // protected by lock (_tasks)

    public LimitedConcurrencyLevelTaskScheduler(int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    public sealed override int MaximumConcurrencyLevel => _maxDegreeOfParallelism;

    // Queues a task to the scheduler.
    protected sealed override void QueueTask(Task task)
    {
        // Add the task to the list of tasks to be processed. If there aren't enough delegates currently queued
        // or running to process tasks, schedule another.
        lock (_tasks)
        {
            _tasks.AddLast(task);
            if (_delegatesQueuedOrRunning < _maxDegreeOfParallelism)
            {
                ++_delegatesQueuedOrRunning;
                NotifyThreadPoolOfPendingWork();
            }
        }
    }

    // Inform the ThreadPool that there's work to be executed for this scheduler.
    private void NotifyThreadPoolOfPendingWork() =>
        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                var scheduler = (LimitedConcurrencyLevelTaskScheduler)state!;

                // Note that the current thread is now processing work items. This is necessary to enable
                // inlining of tasks into this thread.
                _currentThreadIsProcessingItems = true;
                try
                {
                    // Process all available items in the queue.
                    while (true)
                    {
                        Task item;
                        lock (scheduler._tasks)
                        {
                            // When there are no more items to be processed, note that we're done processing, and
                            // get out.
                            if (scheduler._tasks.Count == 0)
                            {
                                --scheduler._delegatesQueuedOrRunning;
                                break;
                            }

                            // Get the next item from the queue.
                            item = scheduler._tasks.First!.Value;
                            scheduler._tasks.RemoveFirst();
                        }

                        // Execute the task we pulled out of the queue.
                        scheduler.TryExecuteTask(item);
                    }
                }

                // We're done processing items on the current thread.
                finally
                {
                    _currentThreadIsProcessingItems = false;
                }
            },
            this);

    // Attempts to execute the specified task on the current thread.
    protected sealed override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // If this thread isn't already processing a task, we don't support inlining.
        if (!_currentThreadIsProcessingItems)
        {
            return false;
        }

        // If the task was previously queued, remove it from the queue.
        if (taskWasPreviouslyQueued)
        {
            // Try to run the task.
            if (TryDequeue(task))
            {
                return TryExecuteTask(task);
            }

            return false;
        }

        return TryExecuteTask(task);
    }

    // Attempt to remove a previously scheduled task from the scheduler.
    protected sealed override bool TryDequeue(Task task)
    {
        lock (_tasks)
        {
            return _tasks.Remove(task);
        }
    }

    // Gets an enumerable of the tasks currently scheduled on this scheduler.
    protected sealed override IEnumerable<Task> GetScheduledTasks()
    {
        bool lockTaken = false;
        try
        {
            System.Threading.Monitor.TryEnter(_tasks, ref lockTaken);
            if (lockTaken)
            {
                return _tasks.ToArray();
            }

            throw new NotSupportedException();
        }
        finally
        {
            if (lockTaken)
            {
                System.Threading.Monitor.Exit(_tasks);
            }
        }
    }
}
