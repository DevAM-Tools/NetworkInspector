// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Test-only convenience helpers for the new cons-list FrameBuilder API.
/// Each test still composes its frame with the explicit
/// <c>FrameStack.Start(...).Then(...).CreateWithFixedValues()</c> chain;
/// this helper only encapsulates the boilerplate of allocating a buffer
/// large enough for the header + payload, driving the
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/> ref-struct
/// iterator once and slicing the written portion of the buffer.
/// </summary>
internal static class FrameBuilderTestExtensions
{
    /// <summary>
    /// Emits a single frame from a <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>
    /// and returns it as a freshly-allocated byte array sized to the actual frame length.
    /// </summary>
    /// <typeparam name="TStack">Cons-list stack root type (innermost layer at <c>THead</c>).</typeparam>
    /// <typeparam name="TTrailer">Trailer type (typically <see cref="NoTrailer"/>).</typeparam>
    /// <typeparam name="TInterceptor">Interceptor type (typically <see cref="NoInterceptor"/>).</typeparam>
    /// <param name="created">A <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> with fixed values.</param>
    /// <param name="payload">Payload bytes to follow the innermost layer's header.</param>
    /// <returns>A new byte array containing exactly the bytes of the emitted frame.</returns>
    internal static byte[] EmitFrame<TStack, TTrailer, TInterceptor>(
        this in CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload)
        where TStack : struct, IStackNode, IStatelessStack
        where TTrailer : struct, ITrailerLayer
        where TInterceptor : struct, IFrameInterceptor
    {
        // Allocate exactly headerSize + payloadSize. For non-fragmented frames
        // (the common test case) MoveNext writes exactly that much.
        byte[] buffer = new byte[created.HeaderSize + payload.Length];
        FrameSequence<TStack, TTrailer, TInterceptor> seq = created.Build(payload);
        seq.MoveNext(buffer, out int written);
        // Slice to the actual written length so callers can rely on buffer.Length.
        if (written == buffer.Length)
        {
            return buffer;
        }
        byte[] sized = new byte[written];
        Buffer.BlockCopy(buffer, 0, sized, 0, written);
        return sized;
    }

    /// <summary>
    /// Wraps raw bytes in a standard Eth+IPv4+TCP frame.
    /// Uses the canonical test addresses (192.168.1.1 → 192.168.1.2, AA:BB:… → 11:22:…).
    /// </summary>
    /// <param name="payload">Raw TCP segment payload bytes.</param>
    /// <param name="srcPort">TCP source port (default 12345).</param>
    /// <param name="dstPort">TCP destination port (default 443).</param>
    internal static byte[] WrapRawTcp(ReadOnlySpan<byte> payload, ushort srcPort = 12345, ushort dstPort = 443)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(srcPort, dstPort, seqNum: 1, ackNum: 0, flags: 0x18);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Wraps raw bytes in a standard Eth+IPv4+UDP frame.
    /// Uses the canonical test addresses (192.168.1.1 → 192.168.1.2, AA:BB:… → 11:22:…).
    /// </summary>
    /// <param name="payload">Raw UDP datagram payload bytes.</param>
    /// <param name="srcPort">UDP source port (default 12345).</param>
    /// <param name="dstPort">UDP destination port (default 443).</param>
    internal static byte[] WrapRawUdp(ReadOnlySpan<byte> payload, ushort srcPort = 12345, ushort dstPort = 443)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(srcPort, dstPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }
}
