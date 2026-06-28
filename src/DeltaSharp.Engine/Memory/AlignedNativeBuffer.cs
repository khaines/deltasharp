using System.Runtime.InteropServices;

namespace DeltaSharp.Engine.Memory;

/// <summary>
/// An <see cref="OwnedBuffer"/> backed by 64-byte-aligned off-heap memory from
/// <see cref="NativeMemory.AlignedAlloc(nuint, nuint)"/> — the ADR-0013 default for columnar batch buffers
/// and binary row buffers. Off-heap bytes are invisible to the GC, reclaimed deterministically on dispose,
/// SIMD-aligned, and free of Large-Object-Heap / gen-2 pauses on large buffers.
/// </summary>
/// <remarks>
/// The physical allocation is rounded up to a 64-byte multiple so a vectorized kernel may safely over-read the
/// final partial vector (AVX-512/<c>Vector512</c>) past the logical <see cref="OwnedBuffer.Length"/> without
/// faulting (Arrow-parity); <see cref="OwnedBuffer.Length"/> stays the requested usable size. A finalizer is kept
/// as a <b>safety net only</b>: if a deterministic <see cref="OwnedBuffer.Dispose()"/> is
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
    private readonly int _capacity;

    private unsafe AlignedNativeBuffer(NativeMemoryAllocator owner, nint pointer, int length, int capacity, bool zero)
        : base(owner, length)
    {
        _pointer = pointer;
        _capacity = capacity;
        if (zero)
        {
            // Zero the full padded capacity so neither the usable bytes nor the SIMD over-read tail disclose stale
            // (possibly freed/foreign) native memory. AlignedAlloc does not zero; this is the secure default.
            NativeMemory.Clear((void*)pointer, (nuint)capacity);
        }
    }

    /// <inheritdoc/>
    public override bool IsNative => true;

    /// <summary>
    /// The 64-byte-aligned base address of the allocation — the exact pointer <see cref="OwnedBuffer.AsSpan"/>
    /// wraps. Exposed internally for alignment assertions and diagnostics; not part of the operator-facing surface.
    /// </summary>
    internal nint BaseAddress => _pointer;

    /// <summary>The physical allocation size — <see cref="OwnedBuffer.Length"/> rounded up to a 64-byte multiple (SIMD over-read margin).</summary>
    internal int Capacity => _capacity;

    /// <summary>
    /// Allocates <paramref name="byteCount"/> usable bytes of 64-byte-aligned native memory for <paramref name="owner"/>,
    /// padding the physical allocation to a 64-byte multiple. The allocation happens before the wrapper is constructed,
    /// so a failed allocation neither constructs an object nor moves any counter; <paramref name="zero"/> clears the
    /// whole capacity. <paramref name="allocate"/> is the native allocation primitive (injectable for tests).
    /// </summary>
    /// <exception cref="OutOfMemoryException">The aligned allocation failed.</exception>
    internal static unsafe AlignedNativeBuffer Allocate(NativeMemoryAllocator owner, int byteCount, bool zero, AlignedAllocator allocate)
    {
        int capacity = PaddedCapacity(byteCount);
        void* pointer = allocate((nuint)capacity, (nuint)Alignment);
        if (pointer is null)
        {
            throw new OutOfMemoryException(
                $"Failed to allocate {capacity} bytes of {Alignment}-byte-aligned native memory.");
        }

        return new AlignedNativeBuffer(owner, (nint)pointer, byteCount, capacity, zero);
    }

    /// <summary>Rounds a usable byte count up to a 64-byte multiple, clamping near <see cref="int.MaxValue"/>.</summary>
    private static int PaddedCapacity(int byteCount)
        => byteCount >= int.MaxValue - (Alignment - 1) ? byteCount : (byteCount + (Alignment - 1)) & ~(Alignment - 1);

    /// <inheritdoc/>
    private protected override unsafe void Release()
    {
        NativeMemory.AlignedFree((void*)_pointer);
        Owner.OnNativeFreed();
    }

    /// <inheritdoc/>
    private protected override unsafe Span<byte> AsSpanCore() => new((void*)_pointer, Length);

    /// <summary>Safety net: frees native memory and flags the missed dispose if the buffer was never disposed.</summary>
    ~AlignedNativeBuffer() => Dispose(disposing: false);
}
