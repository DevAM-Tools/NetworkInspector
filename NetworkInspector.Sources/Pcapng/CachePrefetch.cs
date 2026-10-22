// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Asks the CPU to load a byte range into the cache before it is parsed, so the cache lines of one block (header,
/// packet bytes, trailing length) arrive together instead of one after another.
/// </summary>
/// <remarks>
/// x86/x64: <c>PREFETCHT0</c> per 64-byte line. It is a hint: it never faults and never changes a result. Other
/// architectures (ARM64; .NET exposes no prefetch instruction there): one plain load per line. The loads do not depend
/// on each other, so they still overlap. The caller guarantees that the range is readable (inside the file, inside
/// the view gate or in-memory data). Stateless apart from a per-thread sink that keeps the loads; thread-safe.
/// </remarks>
internal static class CachePrefetch
{
    #region Constants

    /// <summary>Cache line size assumed for the stride (x64 and ARM64 cores in scope).</summary>
    internal const int LineSize = 64;

    /// <summary>
    /// Most bytes one block prefetch requests: 4 KiB (64 lines). Constant (user decision); a larger block reads its first
    /// 4 KiB ahead and streams the rest through the copy.
    /// </summary>
    internal const int MaxBytes = 4096;

    #endregion

    #region Fields

    /// <summary>Receives the touch loads so the JIT keeps them. Per thread: no shared written cache line.</summary>
    [ThreadStatic]
    private static int _Sink;

    #endregion

    #region Public API

    /// <summary>Requests every line of <paramref name="start"/> … <paramref name="start"/> + <paramref name="length"/> − 1.</summary>
    /// <remarks>Caller guarantees 0 ≤ <paramref name="length"/> ≤ <see cref="MaxBytes"/> and a readable range.</remarks>
    internal static unsafe void Range(byte* start, int length)
    {
        if (Sse.IsSupported)
        {
            for (int offset = 0; offset < length; offset += LineSize)
            {
                Sse.Prefetch0(start + offset);
            }

            return;
        }

        int sink = 0;
        for (int offset = 0; offset < length; offset += LineSize)
        {
            sink ^= start[offset];
        }

        _Sink = sink;
    }

    #endregion
}
