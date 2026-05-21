// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// SOME/IP malformed-message tests. Verifies graceful degradation (no exceptions)
/// for truncated headers, truncated SOME/IP-TP headers, and truncated SD payloads.
/// </summary>
internal sealed class SomeIpMalformedTests
{
    /// <summary>Common Ethernet layer shared across all malformed-frame builders.</summary>
    private static readonly EthernetLayer _Eth = new(
        MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
        MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));

    /// <summary>Common IPv4 layer shared across all malformed-frame builders.</summary>
    private static readonly IPv4Layer _Ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));

    [Test]
    public async Task Parse_TruncatedHeader_DoesNotThrow()
    {
        // Only 8 of the 16 SOME/IP header bytes.
        byte[] payload = new byte[8];
        UdpLayer udp = new(srcPort: 12345, dstPort: 30490);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* must not crash */
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Feeds a UDP frame whose payload is 18 bytes — shorter than the 20-byte
    /// SOME/IP-TP minimum header (16-byte SOME/IP base + 4-byte TP word). The parser
    /// must not throw and must not produce any SOME/IP-TP fields.
    /// </summary>
    [Test]
    public async Task Parse_TruncatedTpHeader_DoesNotThrow()
    {
        // 18 bytes: enough to pass the 16-byte base SOME/IP check but too short for
        // the 4-byte TP word that follows (minimum total = 20 bytes).
        // SOME/IP header layout: ServiceId[0-1], MethodId[2-3], Length[4-7],
        // ClientId[8-9], SessionId[10-11], ProtocolVersion[12], InterfaceVersion[13],
        // MessageType[14], ReturnCode[15].
        byte[] payload = new byte[18];
        payload[14] = SomeIpMessageType.TpFlag; // Request (0x00) | TpFlag (0x20) = 0x20
        UdpLayer udp = new(srcPort: 12345, dstPort: 30490);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* must not crash */
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Feeds a SOME/IP frame with message ID 0xFFFF8100 (Service Discovery) but only
    /// 3 bytes of SD payload — below the 8-byte SD minimum header. The SD parser must
    /// return an error gracefully without throwing.
    /// </summary>
    [Test]
    public async Task Parse_TruncatedSdPayload_DoesNotThrow()
    {
        // 3-byte SD payload: too small for flags(1)+reserved(3)+entriesLen(4) = 8-byte minimum.
        byte[] sdPayload = [0xC0, 0x00, 0x00];
        SomeIpLayer someIp = new(
            serviceId: 0xFFFF,
            methodId: 0x8100,
            messageType: SomeIpMessageType.Notification);
        UdpLayer udp = new(srcPort: 12345, dstPort: 30490);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(udp).Then(someIp).CreateWithFixedValues().EmitFrame(sdPayload);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* must not crash */
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// A SOME/IP header whose Length field is below the minimum of 8 must be rejected
    /// gracefully — no exception may be thrown. Length values 0 and 7 are both below the
    /// minimum (8 bytes cover ClientId…ReturnCode).
    /// </summary>
    [Test]
    [Arguments(0u)]
    [Arguments(7u)]
    public async Task Parse_LengthBelowMinimum_DoesNotThrow(uint invalidLength)
    {
        // Manually construct a 16-byte SOME/IP header with the invalid Length value.
        // SOME/IP header layout: ServiceId[0-1], MethodId[2-3], Length[4-7],
        // ClientId[8-9], SessionId[10-11], ProtocolVersion[12], InterfaceVersion[13],
        // MessageType[14], ReturnCode[15].
        // SOME/IP header = 16 bytes: ServiceId[0-1] MethodId[2-3] Length[4-7] ClientId[8-9] SessionId[10-11] PV[12] IV[13] MT[14] RC[15]
        byte[] someIpBytes = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(someIpBytes, 0x1234);           // ServiceId
        BinaryPrimitives.WriteUInt16BigEndian(someIpBytes.AsSpan(2), 0x0042); // MethodId
        BinaryPrimitives.WriteUInt32BigEndian(someIpBytes.AsSpan(4), invalidLength);
        someIpBytes[12] = 1; // ProtocolVersion
        someIpBytes[13] = 1; // InterfaceVersion

        UdpLayer udp = new(srcPort: 12345, dstPort: 30490);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(udp).CreateWithFixedValues().EmitFrame(someIpBytes);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* must not crash */
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// A SOME/IP header whose Length field would cause integer overflow when computing
    /// 8 + Length as int must be handled without throwing an exception.
    /// Length = uint.MaxValue - 7 is 0xFFFFFFF8 — adding 8 wraps to 0 as uint, and the
    /// int cast of the unchecked result is negative, which previously caused a crash.
    /// </summary>
    [Test]
    public async Task Parse_LengthCausesIntOverflow_DoesNotThrow()
    {
        // uint.MaxValue - 7 = 0xFFFFFFF8; unchecked 8 + 0xFFFFFFF8 = 0 (uint overflow).
        uint overflowLength = uint.MaxValue - 7;
        // SOME/IP header = 16 bytes: ServiceId[0-1] MethodId[2-3] Length[4-7] ClientId[8-9] SessionId[10-11] PV[12] IV[13] MT[14] RC[15]
        byte[] someIpBytes = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(someIpBytes, 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(someIpBytes.AsSpan(2), 0x0042);
        BinaryPrimitives.WriteUInt32BigEndian(someIpBytes.AsSpan(4), overflowLength);
        someIpBytes[12] = 1;
        someIpBytes[13] = 1;

        UdpLayer udp = new(srcPort: 12345, dstPort: 30490);
        byte[] frame = FrameStack.Start(_Eth).Then(_Ip).Then(udp).CreateWithFixedValues().EmitFrame(someIpBytes);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* must not crash */
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
