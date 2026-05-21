// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the TCP dissector (Plan §3.1.6).
/// Pins all core TCP header fields and option fields against tshark for both
/// plain-header and options-carrying SYN frames.
/// </summary>
/// <remarks>
/// <para>
/// All frames are emitted via <see cref="FrameStack"/>; no static byte-blob
/// fixtures are used. Field comparison goes through
/// <see cref="TsharkAssert.AssertEquivalentMany(Stack, Packet, byte[], (string, string)[])"/>
/// so a drift on either the NI parser or tshark side is immediately visible.
/// </para>
/// <para>
/// Coverage per the plan: <c>tcp.srcport</c>, <c>tcp.dstport</c>,
/// <c>tcp.seq_raw</c>, <c>tcp.ack_raw</c>, <c>tcp.hdr_len</c>,
/// <c>tcp.flags</c>, individual flag sub-fields, <c>tcp.window_size_value</c>,
/// <c>tcp.checksum</c>, <c>tcp.urgent_pointer</c>, <c>tcp.len</c>,
/// <c>tcp.stream</c>, and option fields <c>tcp.options.mss_val</c> and
/// <c>tcp.options.wscale.shift</c>.
/// </para>
/// <para>Thread safety: stateless tests over the shared parser stack.</para>
/// </remarks>
internal sealed class TcpTsharkTests
{
    #region Frame builders

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    // 192.168.1.1 and 192.168.1.2
    private static readonly IPv4Address _ClientIp = new(0xC0A80101);
    private static readonly IPv4Address _ServerIp = new(0xC0A80102);

    /// <summary>
    /// Builds a plain SYN frame (no options, no payload) with fixed ports and
    /// sequence number so every header field is fully determined.
    /// </summary>
    private static byte[] BuildSynFrame(
        ushort srcPort = 49152,
        ushort dstPort = 80,
        uint seqNum = 0x12345678,
        ushort windowSize = 65535)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(srcPort, dstPort, seqNum: seqNum, ackNum: 0,
            flags: TcpFlags.Syn, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds a SYN+ACK frame (server → client) with explicit sequence and
    /// acknowledgement numbers.
    /// </summary>
    private static byte[] BuildSynAckFrame(
        uint seqNum = 0xABCDEF01,
        uint ackNum = 0x12345679,
        ushort windowSize = 8192)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(80, 49152, seqNum: seqNum, ackNum: ackNum,
            flags: TcpFlags.SynAck, windowSize: windowSize);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds a PSH+ACK data-carrying frame with a 32-byte payload.
    /// <c>tcp.len</c> should equal the payload length; sequence and
    /// acknowledgement numbers are explicit so they match both parsers exactly.
    /// </summary>
    private static byte[] BuildDataFrame(
        uint seqNum = 0x12345679,
        uint ackNum = 0xABCDEF02,
        int payloadLength = 32)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: seqNum, ackNum: ackNum,
            flags: TcpFlags.PshAck, windowSize: 65535);
        byte[] payload = new byte[payloadLength];
        for (int i = 0; i < payloadLength; i++)
        {
            payload[i] = (byte)(i + 0x41); // 'A', 'B', 'C', …
        }
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Builds a FIN+ACK frame to exercise the FIN flag field.
    /// </summary>
    private static byte[] BuildFinAckFrame(
        uint seqNum = 0x12345699,
        uint ackNum = 0xABCDEF20)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: seqNum, ackNum: ackNum,
            flags: TcpFlags.FinAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds a SYN frame carrying the standard modern SYN option set via
    /// <see cref="TcpOptionsBuilder.SynOptions"/>:
    /// MSS(1460) + SACKPermitted + NOP+NOP+Timestamps(0,0) + NOP+WindowScale(7).
    /// tshark parses and exposes option sub-fields from the raw option bytes,
    /// so a match confirms both the encoding and the parsing.
    /// </summary>
    private static byte[] BuildSynWithOptionsFrame()
    {
        // SynOptions() encodes the 22-byte standard SYN option bundle, padded to
        // 24 bytes.  MSS=1460, WScale shift=7.
        TcpOptionsBuilder opts = new();
        opts.SynOptions();
        TcpOptions options = opts.Build();

        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayerWithOptions tcp = new(49152, 80, options, seqNum: 0x12345678, ackNum: 0,
            flags: TcpFlags.Syn, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #endregion

    #region SYN frame — full header coverage

    /// <summary>
    /// Full field-set verification for a plain TCP SYN segment with no payload.
    /// Pins all primary dissector outputs: ports, raw sequence number, header
    /// length, flags word, individual SYN/ACK flags, window, checksum,
    /// urgent pointer, payload length and stream index.
    /// </summary>
    [Test]
    public async Task Tcp_SynFrame_AllFieldsMatchTshark()
    {
        byte[] frame = BuildSynFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("tcp.srcport", "tcp.srcport"),
                ("tcp.dstport", "tcp.dstport"),
                ("tcp.seq_raw", "tcp.seq_raw"),
                ("tcp.hdr_len", "tcp.hdr_len"),
                ("tcp.flags", "tcp.flags"),
                ("tcp.flags.syn", "tcp.flags.syn"),
                ("tcp.flags.ack", "tcp.flags.ack"),
                ("tcp.window_size_value", "tcp.window_size_value"),
                ("tcp.checksum", "tcp.checksum"),
                ("tcp.urgent_pointer", "tcp.urgent_pointer"),
                ("tcp.len", "tcp.len"),
                ("tcp.stream", "tcp.stream")).ConfigureAwait(false);
        }
    }

    #endregion

    #region SYN+ACK frame — acknowledgement number and flag coverage

    /// <summary>
    /// Pins <c>tcp.ack_raw</c> (only meaningful when the ACK flag is set) and
    /// both the SYN and ACK flag sub-fields on a SYN+ACK segment.
    /// </summary>
    [Test]
    public async Task Tcp_SynAckFrame_AckAndFlagsMatchTshark()
    {
        byte[] frame = BuildSynAckFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("tcp.seq_raw", "tcp.seq_raw"),
                ("tcp.ack_raw", "tcp.ack_raw"),
                ("tcp.flags.syn", "tcp.flags.syn"),
                ("tcp.flags.ack", "tcp.flags.ack"),
                ("tcp.flags.fin", "tcp.flags.fin")).ConfigureAwait(false);
        }
    }

    #endregion

    #region PSH+ACK data segment — payload length and flag coverage

    /// <summary>
    /// Verifies <c>tcp.len</c> (the TCP payload length) on a data segment and
    /// pins the PSH and ACK flag sub-fields.  Sequence and acknowledgement
    /// numbers are explicit so the raw-number fields match both parsers.
    /// </summary>
    [Test]
    public async Task Tcp_DataSegment_AllFieldsMatchTshark()
    {
        byte[] frame = BuildDataFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("tcp.srcport", "tcp.srcport"),
                ("tcp.dstport", "tcp.dstport"),
                ("tcp.seq_raw", "tcp.seq_raw"),
                ("tcp.ack_raw", "tcp.ack_raw"),
                ("tcp.flags.push", "tcp.flags.push"),
                ("tcp.flags.ack", "tcp.flags.ack"),
                ("tcp.len", "tcp.len"),
                ("tcp.window_size_value", "tcp.window_size_value"),
                ("tcp.checksum", "tcp.checksum")).ConfigureAwait(false);
        }
    }

    #endregion

    #region FIN+ACK frame — FIN flag coverage

    /// <summary>
    /// Pins the FIN flag sub-field on a FIN+ACK segment to confirm the parser
    /// distinguishes FIN from RST/SYN correctly.
    /// </summary>
    [Test]
    public async Task Tcp_FinAckFrame_FlagsMatchTshark()
    {
        byte[] frame = BuildFinAckFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("tcp.flags.fin", "tcp.flags.fin"),
                ("tcp.flags.ack", "tcp.flags.ack"),
                ("tcp.flags.syn", "tcp.flags.syn"),
                ("tcp.flags.reset", "tcp.flags.reset"),
                ("tcp.seq_raw", "tcp.seq_raw"),
                ("tcp.ack_raw", "tcp.ack_raw")).ConfigureAwait(false);
        }
    }

    #endregion

    #region SYN with options — option field coverage

    /// <summary>
    /// Verifies that the MSS and WindowScale option sub-fields parsed by
    /// Network-Inspector match the values tshark extracts from the same
    /// option bytes. The frame is emitted via <see cref="TcpLayerWithOptions"/>
    /// so the encoding path is also exercised end-to-end.
    /// </summary>
    [Test]
    public async Task Tcp_SynWithOptions_OptionFieldsMatchTshark()
    {
        byte[] frame = BuildSynWithOptionsFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame,
                ("tcp.options.mss_val", "tcp.options.mss_val"),
                ("tcp.options.wscale.shift", "tcp.options.wscale.shift"),
                ("tcp.hdr_len", "tcp.hdr_len")).ConfigureAwait(false);
        }
    }

    #endregion
}
