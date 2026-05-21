// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Per-thread scratch storage used by the fragmentation paths of
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/> and
/// <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The fragmentation path follows a "build-once, slice-many" strategy: the
/// full unfragmented frame is materialised once into the scratch buffer (so
/// every checksum and length is computed exactly once) and every emitted
/// fragment then copies a slice of that scratch into the caller's destination
/// buffer.  Both buffers are kept thread-static so the hot path never
/// allocates after the first call on a given thread.
/// </para>
/// <para>
/// Reentrancy: the build pipeline can re-enter itself via user-supplied
/// callbacks (most commonly <see cref="IFrameInterceptor.OnFrameComplete"/>
/// emitting a sibling frame from the same thread).  To prevent the inner
/// call from corrupting the outer call's cached headers,
/// <see cref="TryAcquire"/> reports an in-use flag; reentrant calls fall
/// back to a freshly-allocated non-pooled buffer pair.  Outer (top-level)
/// callers must pair <see cref="TryAcquire"/> with <see cref="Release"/>
/// when they finish their fragment loop so a subsequent top-level call on
/// the same thread reuses the pooled buffers.
/// </para>
/// <para>
/// Thread safety: every accessor operates exclusively on the calling thread's
/// own arrays.  Callers must not retain the returned references across thread
/// boundaries.
/// </para>
/// </remarks>
internal static class FrameSequenceScratch
{
    [ThreadStatic]
    private static byte[]? _Buffer;

    [ThreadStatic]
    private static int[]? _Offsets;

    /// <summary>Re-entrancy depth on the calling thread; 0 = pooled buffers free.</summary>
    [ThreadStatic]
    private static int _InUseDepth;

    /// <summary>
    /// Attempts to reserve the per-thread scratch buffer pair.
    /// </summary>
    /// <param name="minimumBufferSize">Minimum byte buffer size required.</param>
    /// <param name="buffer">Receives a buffer of at least <paramref name="minimumBufferSize"/> bytes.</param>
    /// <param name="offsets">Receives an offsets array of <see cref="FrameLimits.MaxSupportedDepth"/> entries.</param>
    /// <returns>
    /// <c>true</c> when the pooled thread-static arrays were rented (caller
    /// must invoke <see cref="Release"/> when done).  <c>false</c> when the
    /// call is reentrant; the returned arrays are freshly allocated and need
    /// no release.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryAcquire(int minimumBufferSize, out byte[] buffer, out int[] offsets)
    {
        if (_InUseDepth > 0)
        {
            buffer = new byte[Math.Max(minimumBufferSize, 64)];
            offsets = new int[FrameLimits.MaxSupportedDepth];
            return false;
        }
        _InUseDepth = 1;
        buffer = GetBuffer(minimumBufferSize);
        offsets = GetOffsets();
        return true;
    }

    /// <summary>Releases the pooled thread-static reservation acquired via <see cref="TryAcquire"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Release()
    {
        if (_InUseDepth > 0)
        {
            _InUseDepth--;
        }
    }

    /// <summary>
    /// Returns a per-thread <see cref="byte"/> buffer of at least
    /// <paramref name="minimumSize"/> bytes.  Grown geometrically so the
    /// amortised per-call cost is constant.
    /// </summary>
    /// <param name="minimumSize">Minimum required size in bytes.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte[] GetBuffer(int minimumSize)
    {
        byte[]? buffer = _Buffer;
        if (buffer is null || buffer.Length < minimumSize)
        {
            // Round up to the next power of two so subsequent calls within the
            // same order of magnitude do not reallocate.
            int rounded = 64;
            while (rounded < minimumSize)
            {
                rounded <<= 1;
            }
            buffer = new byte[rounded];
            _Buffer = buffer;
        }
        return buffer;
    }

    /// <summary>
    /// Returns the per-thread offsets array (length =
    /// <see cref="FrameLimits.MaxSupportedDepth"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int[] GetOffsets()
    {
        int[]? offsets = _Offsets;
        if (offsets is null)
        {
            offsets = new int[FrameLimits.MaxSupportedDepth];
            _Offsets = offsets;
        }
        return offsets;
    }
}
