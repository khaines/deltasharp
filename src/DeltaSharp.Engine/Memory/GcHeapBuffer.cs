using System.Buffers;

namespace DeltaSharp.Engine.Memory;

/// <summary>
/// An <see cref="OwnedBuffer"/> backed by a pooled <see cref="byte"/> array rented from
/// <see cref="ArrayPool{T}.Shared"/> — the GC-heap path the <see cref="NativeMemoryAllocator"/> uses only for
/// <b>small, short-lived scratch</b> below its scratch threshold (ADR-0013). Pooling small scratch avoids both
/// native allocation overhead and per-use GC garbage, while large batch buffers stay off-heap.
/// </summary>
/// <remarks>
/// No finalizer is needed: the backing store is a managed array the GC reclaims on its own if a dispose is
/// missed. <see cref="OwnedBuffer.Dispose()"/> returns the array to the pool exactly once; the rented array may be
/// larger than <see cref="OwnedBuffer.Length"/>, so the usable span is the first <c>Length</c> bytes.
/// </remarks>
public sealed class GcHeapBuffer : OwnedBuffer
{
    private readonly ArrayPool<byte> _pool;
    private byte[] _array;

    internal GcHeapBuffer(NativeMemoryAllocator owner, int byteCount, bool zero, ArrayPool<byte> pool)
        : base(owner, byteCount)
    {
        _pool = pool;
        _array = pool.Rent(byteCount);
        if (zero)
        {
            // Rent does not clear; zero the usable window so scratch never exposes the previous renter's bytes.
            _array.AsSpan(0, byteCount).Clear();
        }
    }

    /// <inheritdoc/>
    public override bool IsNative => false;

    /// <inheritdoc/>
    private protected override void Release()
    {
        // clearArray: true so a pooled scratch array never carries one caller's bytes into the next renter
        // (defense-in-depth for row/PII data). Scratch is small (<= the allocator's threshold), so zeroing is cheap.
        _pool.Return(_array, clearArray: true);
        _array = [];
        Owner.OnScratchReturned();
    }

    /// <inheritdoc/>
    private protected override Span<byte> AsSpanCore() => _array.AsSpan(0, Length);
}
