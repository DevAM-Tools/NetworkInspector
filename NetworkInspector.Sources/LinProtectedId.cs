// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources;

/// <summary>
/// LIN protected identifier (PID): the 6-bit frame id plus the two parity bits the bus transmits.
/// Both LIN readers (BLF, ASC) write the PID into byte 5 of the <c>DLT_LIN</c> frame with this one rule; both writers
/// recover the 6-bit id with <c>&amp; 0x3F</c>, which works for frames with and without parity.
/// </summary>
/// <remarks>Stateless; thread-safe.</remarks>
internal static class LinProtectedId
{
    #region Public API

    /// <summary>Mask of the 6-bit frame id inside a protected identifier.</summary>
    internal const byte FrameIdMask = 0x3F;

    /// <summary>
    /// Computes the protected identifier of a frame id (LIN 2.x): P0 = ID0 ⊕ ID1 ⊕ ID2 ⊕ ID4 (bit 6),
    /// P1 = ¬(ID1 ⊕ ID3 ⊕ ID4 ⊕ ID5) (bit 7). Bits 6–7 of <paramref name="frameId"/> are ignored.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Compute(byte frameId)
    {
        // Branch-free: shifts and XORs only (a few register operations, no lookup table, no allocation), and inlined into
        // the per-frame LIN parse paths. Parity is taken over the 6 id bits only; P1 is the inverted XOR, and the final
        // "& 1" after the complement keeps a single bit.
        int id = frameId & FrameIdMask;
        int p0 = (id ^ (id >> 1) ^ (id >> 2) ^ (id >> 4)) & 1;
        int p1 = ~((id >> 1) ^ (id >> 3) ^ (id >> 4) ^ (id >> 5)) & 1;
        return (byte)(id | (p0 << 6) | (p1 << 7));
    }

    #endregion
}
