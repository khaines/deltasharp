using System.Globalization;

namespace DeltaSharp.Engine.Memory;

/// <summary>
/// Thrown by <see cref="TaskMemoryManager.Reserve(MemoryPoolKind, long, ISpillable?)"/> when a reservation cannot be
/// satisfied <b>even after spilling</b> the requesting task's spillable reservations (ADR-0013). It is the deterministic
/// budget-exceeded signal — distinct from <see cref="OutOfMemoryException"/>: the manager bounds execution by failing the
/// reservation, never by exhausting process memory. The message and properties carry the task id, the requested bytes,
/// and the full pool state at the moment of failure so the error is reproducible and actionable.
/// </summary>
public sealed class MemoryBudgetExceededException : InvalidOperationException
{
    /// <summary>Creates the exception with the task id, requested bytes, failing pool, and both pools' state.</summary>
    public MemoryBudgetExceededException(
        long taskId,
        MemoryPoolKind pool,
        long requestedBytes,
        long poolUsedBytes,
        long poolSizeBytes,
        long executionUsedBytes,
        long executionPoolSizeBytes,
        long storageUsedBytes,
        long storagePoolSizeBytes,
        long maxMemoryBytes)
        : base(FormatMessage(
            taskId, pool, requestedBytes, poolUsedBytes, poolSizeBytes,
            executionUsedBytes, executionPoolSizeBytes, storageUsedBytes, storagePoolSizeBytes, maxMemoryBytes))
    {
        TaskId = taskId;
        Pool = pool;
        RequestedBytes = requestedBytes;
        PoolUsedBytes = poolUsedBytes;
        PoolSizeBytes = poolSizeBytes;
        ExecutionUsedBytes = executionUsedBytes;
        ExecutionPoolSizeBytes = executionPoolSizeBytes;
        StorageUsedBytes = storageUsedBytes;
        StoragePoolSizeBytes = storagePoolSizeBytes;
        MaxMemoryBytes = maxMemoryBytes;
    }

    /// <summary>The id of the task whose reservation failed.</summary>
    public long TaskId { get; }

    /// <summary>The pool the reservation targeted.</summary>
    public MemoryPoolKind Pool { get; }

    /// <summary>The bytes the task tried to reserve.</summary>
    public long RequestedBytes { get; }

    /// <summary>Bytes in use in the failing pool at the moment of failure.</summary>
    public long PoolUsedBytes { get; }

    /// <summary>The failing pool's soft size in bytes at the moment of failure.</summary>
    public long PoolSizeBytes { get; }

    /// <summary>Bytes in use in the execution pool at the moment of failure.</summary>
    public long ExecutionUsedBytes { get; }

    /// <summary>The execution pool's soft size in bytes at the moment of failure.</summary>
    public long ExecutionPoolSizeBytes { get; }

    /// <summary>Bytes in use in the storage pool at the moment of failure.</summary>
    public long StorageUsedBytes { get; }

    /// <summary>The storage pool's soft size in bytes at the moment of failure.</summary>
    public long StoragePoolSizeBytes { get; }

    /// <summary>The total unified budget in bytes.</summary>
    public long MaxMemoryBytes { get; }

    private static string FormatMessage(
        long taskId,
        MemoryPoolKind pool,
        long requestedBytes,
        long poolUsedBytes,
        long poolSizeBytes,
        long executionUsedBytes,
        long executionPoolSizeBytes,
        long storageUsedBytes,
        long storagePoolSizeBytes,
        long maxMemoryBytes)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Task {taskId} could not reserve {requestedBytes} bytes in the {pool} pool after spilling: " +
            $"{pool} pool used {poolUsedBytes}/{poolSizeBytes} bytes " +
            $"(execution {executionUsedBytes}/{executionPoolSizeBytes}, storage {storageUsedBytes}/{storagePoolSizeBytes}; " +
            $"max {maxMemoryBytes} bytes).");
}
