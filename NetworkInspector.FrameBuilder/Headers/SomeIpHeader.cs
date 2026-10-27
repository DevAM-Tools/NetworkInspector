// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// SOME/IP message header (16 bytes) per AUTOSAR SomeIpProtocol specification §3.1.
/// Layout: ServiceId(2) + MethodId(2) + Length(4) + ClientId(2) + SessionId(2)
/// + ProtocolVersion(1) + InterfaceVersion(1) + MessageType(1) + ReturnCode(1).
/// All multi-byte fields are big-endian.
/// </summary>
internal readonly struct SomeIpHeader
{
    /// <summary>Size of the SOME/IP header in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Service identifier.</summary>
    internal ushort ServiceId
    {
        get; init;
    }

    /// <summary>Method or event identifier (top bit distinguishes events).</summary>
    internal ushort MethodId
    {
        get; init;
    }

    /// <summary>
    /// Length field in bytes, counted from <see cref="ClientId"/> to the end
    /// of the payload (inclusive).  Patched in post-fix.
    /// </summary>
    internal uint Length
    {
        get; init;
    }

    /// <summary>Client identifier.</summary>
    internal ushort ClientId
    {
        get; init;
    }

    /// <summary>Session identifier (incremented per request).</summary>
    internal ushort SessionId
    {
        get; init;
    }

    /// <summary>Protocol version (always 1 for current SOME/IP).</summary>
    internal byte ProtocolVersion
    {
        get; init;
    }

    /// <summary>Interface version of the called service.</summary>
    internal byte InterfaceVersion
    {
        get; init;
    }

    /// <summary>Message type (request / response / notification / TP-flagged).</summary>
    internal byte MessageType
    {
        get; init;
    }

    /// <summary>Return code (0 = OK; non-zero on error responses).</summary>
    internal byte ReturnCode
    {
        get; init;
    }

    /// <summary>Writes the 16 header bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
    /// <remarks>
    /// Replaces a generated writer: one length check, then fixed-offset stores on an exact-size slice, so the JIT drops the
    /// per-store bounds checks. Layers call it directly (no interface dispatch). Writes nothing when it fails.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        // One length check up front; a short destination is an expected failure, not an exception.
        if (destination.Length < Size)
        {
            bytesWritten = 0;
            return false;
        }

        // Exact-size slice: the fixed-offset stores below need no further bounds checks after JIT.
        Span<byte> d = destination[..Size];
        BinaryPrimitives.WriteUInt16BigEndian(d, ServiceId);       // 0..2
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], MethodId);   // 2..4
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], Length);     // 4..8
        BinaryPrimitives.WriteUInt16BigEndian(d[8..], ClientId);   // 8..10
        BinaryPrimitives.WriteUInt16BigEndian(d[10..], SessionId); // 10..12
        d[12] = ProtocolVersion;                                   // 12
        d[13] = InterfaceVersion;                                  // 13
        d[14] = MessageType;                                       // 14
        d[15] = ReturnCode;                                        // 15
        bytesWritten = Size;
        return true;
    }
}
