using System.Runtime.InteropServices;

namespace DeltaSharp.Engine.Memory;

/// <summary>
/// An <see cref="OwnedBuffer"/> backed by 64-byte-aligned off-heap memory from
/// <see cref="NativeMemory.AlignedAlloc(nuint, nuint)"/> — the ADR-0013 default for columnar batch buffers
/// and binary row buffers. Off-heap bytes are invisible to the GC, reclaimed deterministically on dispose,
/// SIMD-aligned, and free of Large-Object-Heap / gen-2 pauses on large buffers.
/// </summary>
/// <remarks>
/// A finalizer is kept as a <b>safety net only</b>: if a deterministic <see cref="OwnedBuffer.Dispose()"/> is
/// missed the native memory is still freed and the allocator's
/// <see cref="NativeMemoryAllocator.FinalizedWithoutDispose"/> counter is bumped so leaks surface in tests and
/// diagnostics. Correct callers always dispose (directly or via a <see cref="BufferGroup"/>), which suppresses
/// finalization.
/// </remarks>
public sealed class AlignedNativeBuffer : OwnedBuffer
{
    /// <summary>The base-address alignment, in bytes, guaranteed for every native buffer (SIMD-friendly, ADR-0013).</summary>
    public const int Alignment = 64;

    private readonly nint _pointer;

    private AlignedNativeBuffer(NativeMemoryAllocator owner, nint pointer, int length)
        : base(owner, length) => _pointer = pointer;

    /// <inheritdoc/>
    public override bool IsNative => true;

    /// <summary>
    /// The 64-byte-aligned base address of the allocation — the exact pointer <see cref="OwnedBuffer.AsSpan"/>
    /// wraps. Exposed internally for alignment assertions and diagnostics; not part of the operator-facing surface.
    /// </summary>
    internal nint BaseAddress => _pointer;

    /// <summary>
    /// Allocates <paramref name="byteCount"/> bytes of 64-byte-aligned native memory for <paramref name="owner"/>.
    /// The allocation happens before the wrapper is constructed, so a failed allocation neither constructs an
    /// object nor moves any counter.
    /// </summary>
    /// <exception cref="OutOfMemoryException">The aligned allocation failed.</exception>
    internal static unsafe AlignedNativeBuffer Allocate(NativeMemoryAllocator owner, int byteCount)
    {
        void* pointer = NativeMemory.AlignedAlloc((nuint)byteCount, (nuint)Alignment);
        if (pointer is null)
        {
            throw new OutOfMemoryException(
                $"Failed to allocate {byteCount} bytes of {Alignment}-byte-aligned native memory.");
        }

        return new AlignedNativeBuffer(owner, (nint)pointer, byteCount);
    }

    /// <inheritdoc/>
    protected override unsafe void Release() => NativeMemory.AlignedFree((void*)_pointer);

    /// <inheritdoc/>
    protected override unsafe Span<byte> AsSpanCore() => new((void*)_pointer, Length);

    /// <summary>Safety net: frees native memory and flags the missed dispose if the buffer was never disposed.</summary>
    ~AlignedNativeBuffer() => Dispose(disposing: false);
}
