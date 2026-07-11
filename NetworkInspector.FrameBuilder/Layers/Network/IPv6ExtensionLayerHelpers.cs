// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Shared write/post-fix logic for the simple, fixed-8-byte IPv6 extension
/// header layers (Hop-by-Hop, Routing, Destination Options).  The Fragment
/// extension header has its own format and is implemented separately.
/// </summary>
internal static class IPv6ExtensionLayerHelpers
{
    /// <summary>Byte offset of the NextHeader field within an IPv6 ext header.</summary>
    internal const int NextHeaderOffset = 0;

    /// <summary>Writes the minimal 8-byte options-style ext header into <paramref name="dst"/>.</summary>
    /// <param name="dst">Destination span; must be at least <see cref="IPv6OptionsExtensionHeader.Size"/> bytes.</param>
    /// <param name="explicitNextHeader">
    /// Explicit NextHeader value; <c>0</c> here means "patch from inner layer"
    /// (real value 0 = HopByHop is unreachable from a chain that already
    /// places HopByHop, since HopByHop must be first per RFC 8200 §4).
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteHeader(scoped Span<byte> dst, byte explicitNextHeader)
    {
        // PadN option (type=1, len=4, four zero bytes) fills the data area
        // so the header is RFC-conformant when no real options are present.
        IPv6OptionsExtensionHeader hdr = new()
        {
            NextHeader = explicitNextHeader, // 0 = will be patched by PatchNextProtocol
            HdrExtLen = 0,                   // (HdrExtLen + 1) * 8 = 8 bytes
            Data0 = 0x01,                    // PadN option type
            Data1 = 0x04,                    // PadN option length: 4 zero bytes follow
            Data2 = 0x00,
            Data3 = 0x00,
            Data4 = 0x00,
            Data5 = 0x00,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <summary>
    /// Post-fix common to all simple ext layers: in
    /// <see cref="FixPhase.PublishPseudoHeader"/> override the upper-layer
    /// protocol byte and advance the transport offset past this ext header.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ApplyPostFix(
        FixPhase phase,
        scoped Span<byte> frame,
        int myOffset,
        int myLength,
        scoped ref PostFixContext ctx,
        int headerSize)
    {
        if (phase != FixPhase.PublishPseudoHeader)
        {
            return;
        }

        // The inner protocol number is now in our own NextHeader byte (it has
        // already been patched in by PatchNextProtocol because the outer→inner
        // header walk already completed).  Forward it as the pseudo-header
        // protocol so the transport checksum uses the correct value (RFC 8200 §8.1).
        ctx.PseudoProtocol = frame[myOffset + NextHeaderOffset];
        ctx.TransportOffset = myOffset + headerSize;
    }

    /// <summary>Patches the NextHeader byte unless the user pinned it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol, bool isExplicit)
    {
        if (!isExplicit)
        {
            frame[myOffset + NextHeaderOffset] = (byte)nextProtocol;
        }
    }
}
