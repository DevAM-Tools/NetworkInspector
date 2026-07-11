// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for malformed TCP packets: truncated headers, invalid data offset,
/// and other edge cases that should trigger parse errors.
/// </summary>
internal sealed class TcpMalformedTests
{
    #region Constants

    /// <summary>Offset of TCP header within an Ethernet + IPv4 frame (14 ETH + 20 IPv4).</summary>
    private const int _TcpOffset = 34;

    /// <summary>
    /// Offset of the data offset nibble within the TCP header (byte 12).
    /// Upper nibble = DataOffset in 32-bit words.
    /// </summary>
    private const int _DataOffsetByteOffset = 12;

    #endregion

    #region Helpers

    /// <summary>Creates a valid Ethernet + IPv4 + TCP SYN frame with payload.</summary>
    private static byte[] _BuildValidFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        TcpLayer tcp = new(12345, 80, seqNum: 1000, ackNum: 0, flags: TcpFlags.Syn);
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];

        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>
    /// Creates a frame with a modified TCP data offset value.
    /// The data offset is stored in the upper nibble of TCP byte 12.
    /// </summary>
    private static byte[] _BuildFrameWithDataOffset(byte dataOffset)
    {
        byte[] frame = _BuildValidFrame();
        int tcpDataOffsetByte = _TcpOffset + _DataOffsetByteOffset;

        // Upper nibble = dataOffset, lower nibble = reserved bits (preserve them)
        frame[tcpDataOffsetByte] = (byte)((dataOffset << 4) | (frame[tcpDataOffsetByte] & 0x0F));

        return frame;
    }

    #endregion

    #region Truncated Frame Tests

    [Test]
    public async Task Truncated_EmptyTcpData_NoTcpFields()
    {
        // Ethernet(14) + IPv4(20) header only — zero TCP bytes
        byte[] frame = _BuildValidFrame()[.._TcpOffset];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // IPv4 should be present, TCP should NOT
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Truncated_OneByteTcp_NoTcpFields()
    {
        // Only 1 byte of TCP header — far below the 20-byte minimum
        byte[] frame = _BuildValidFrame()[..(_TcpOffset + 1)];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Truncated_NineteenBytesTcp_NoTcpFields()
    {
        // 19 bytes = 1 byte short of minimum TCP header (20 bytes)
        byte[] frame = _BuildValidFrame()[..(_TcpOffset + 19)];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "ip").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Truncated_ExactMinimumHeader_ParsesSuccessfully()
    {
        // Exactly 20 bytes of TCP header (data offset = 5) — should parse
        byte[] fullFrame = _BuildValidFrame();

        // Truncate to Eth(14) + IPv4(20) + TCP(20) = 54 bytes
        byte[] frame = fullFrame[..(_TcpOffset + 20)];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "tcp").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.srcport", 12345).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Truncated_HeaderLargerThanData_ErrorField()
    {
        // Set data offset = 15 (60 bytes header), but only provide 20 bytes of TCP data
        byte[] frame = _BuildFrameWithDataOffset(15);
        // Truncate to exactly 20 bytes of TCP
        frame = frame[..(_TcpOffset + 20)];

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // TCP header length claims 60 bytes, but only 20 available → error
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    #endregion

    #region Invalid Data Offset Tests

    [Test]
    public async Task DataOffset_Zero_ParseError()
    {
        // DataOffset = 0 → header length = 0 bytes, which is < 20 byte minimum
        byte[] frame = _BuildFrameWithDataOffset(0);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DataOffset_One_ParseError()
    {
        // DataOffset = 1 → header length = 4 bytes, far below minimum
        byte[] frame = _BuildFrameWithDataOffset(1);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DataOffset_Four_ParseError()
    {
        // DataOffset = 4 → header length = 16 bytes, just below the minimum 20
        byte[] frame = _BuildFrameWithDataOffset(4);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "tcp.srcport").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DataOffset_Five_ValidMinimum()
    {
        // DataOffset = 5 → header length = 20 bytes = minimum valid header
        byte[] frame = _BuildFrameWithDataOffset(5);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "tcp").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.srcport", 12345).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task DataOffset_Fifteen_MaximumValid()
    {
        // DataOffset = 15 → header length = 60 bytes (maximum)
        // Need to ensure the frame has enough data for a 60-byte TCP header
        byte[] frame = _BuildFrameWithDataOffset(15);

        // Current frame may be too short for 60-byte header.
        // Extend it with zero padding to accommodate the full header.
        byte[] extended = new byte[_TcpOffset + 60 + 5]; // ETH+IP(34) + TCP(60) + payload(5)
        Array.Copy(frame, extended, Math.Min(frame.Length, extended.Length));
        // Also fix the IPv4 total length field (bytes 16-17, big endian)
        int ipTotalLength = extended.Length - 14; // Total - Ethernet header
        extended[16] = (byte)(ipTotalLength >> 8);
        extended[17] = (byte)(ipTotalLength & 0xFF);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(extended);
        using (stack)
        {
            // Should parse successfully with DataOffset=15
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "tcp").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.srcport", 12345).ConfigureAwait(false);
        }
    }

    #endregion

    #region Edge Cases

    [Test]
    public async Task NoCrash_OnRandomGarbage()
    {
        // Build valid Ethernet + IPv4 header, then append random-looking TCP data
        byte[] validFrame = _BuildValidFrame();
        byte[] garbageFrame = validFrame[.._TcpOffset];

        // Append 30 bytes of deterministic pseudo-random TCP data
        byte[] frame = new byte[garbageFrame.Length + 30];
        Array.Copy(garbageFrame, frame, garbageFrame.Length);

        // Fill with deterministic pattern (not security-sensitive, just test data)
        for (int i = 0; i < 30; i++)
        {
            frame[garbageFrame.Length + i] = (byte)((i * 37 + 13) & 0xFF);
        }

        // Parsing must not throw — we just verify it doesn't crash
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Verify at least Ethernet parsed (survival + basic sanity)
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "eth").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ValidFrame_StillParsesAfterMalformed()
    {
        // Ensure the stack recovers after parsing a malformed frame.
        // Parse a truncated frame first, then parse a valid one.
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            // First: malformed — truncated TCP
            byte[] malformed = _BuildValidFrame()[..(_TcpOffset + 5)];
            Packet badPacket = ProtocolTestHelper.ParseFrame(stack, malformed, 0, Timestamp.FromMillis(0));
            await ProtocolTestHelper.AssertFieldNotPresent(stack, badPacket, "tcp.srcport").ConfigureAwait(false);

            // Second: valid frame — should parse successfully
            byte[] valid = _BuildValidFrame();
            Packet goodPacket = ProtocolTestHelper.ParseFrame(stack, valid, 1, Timestamp.FromMillis(100));
            await ProtocolTestHelper.AssertU64Field(stack, goodPacket, "tcp.srcport", 12345).ConfigureAwait(false);
        }
    }

    #endregion
}
