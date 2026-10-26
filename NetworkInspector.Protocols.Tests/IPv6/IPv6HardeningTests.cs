// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the IPv6 extension header walk at fragment headers: the bytes after the header of a real fragment are
/// fragment data, not a header chain.
/// </summary>
internal sealed class IPv6HardeningTests
{
    private const byte _NextHeaderFragment = 44;
    private const byte _NextHeaderDestinationOptions = 60;
    private const byte _NextHeaderExperimental = 253;

    private static readonly byte[] _SrcAddress = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _DstAddress = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02];

    /// <summary>Records the length of the last payload it received on the IP protocol number it is registered for.</summary>
    private sealed class PayloadProbeProtocol : IProtocol
    {
        public string Name => "payload.probe";

        public string UiName => "Payload Probe";

        public int Calls { get; private set; }

        public int LastLength { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            Calls++;
            LastLength = data.Length;
            return data.Length;
        }
    }

    private static byte[] _Frame(byte[] ipv6Payload)
    {
        byte[] frame = new byte[14 + 40 + ipv6Payload.Length];
        new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }.CopyTo(frame, 0);
        new byte[] { 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB }.CopyTo(frame, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x86DD);

        frame[14] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14 + 4), (ushort)ipv6Payload.Length);
        frame[14 + 6] = _NextHeaderFragment;
        frame[14 + 7] = 64;
        _SrcAddress.CopyTo(frame, 14 + 8);
        _DstAddress.CopyTo(frame, 14 + 24);
        ipv6Payload.CopyTo(frame, 14 + 40);
        return frame;
    }

    /// <summary>A fragment header (8 bytes) followed by <paramref name="fragmentData"/>.</summary>
    private static byte[] _Fragment(byte innerNextHeader, ushort offsetUnitsAndFlags, byte[] fragmentData)
    {
        byte[] payload = new byte[8 + fragmentData.Length];
        payload[0] = innerNextHeader;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), offsetUnitsAndFlags);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0xCAFEBABE);
        fragmentData.CopyTo(payload, 8);
        return payload;
    }

    /// <summary>16 payload bytes that start like a Destination Options header: next header 17, length 0.</summary>
    private static byte[] _DataThatLooksLikeAnExtensionHeader()
    {
        byte[] data = new byte[16];
        data[0] = 17;
        data[1] = 0;
        return data;
    }

    [Test]
    public async Task NonFirstFragmentPayloadIsNotReadAsAnExtensionHeader()
    {
        // Fragment header (offset 1 unit, no More flag) names Destination Options as the first header of the datagram,
        // and the fragment data happens to look like such a header. It is data from the middle of the datagram.
        byte[] frame = _Frame(_Fragment(_NextHeaderDestinationOptions, 0x0008, _DataThatLooksLikeAnExtensionHeader()));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "ipv6.fraghdr").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "ipv6.dstopts").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task FirstFragmentEndsTheChainAtTheFragmentHeader()
    {
        byte[] frame = _Frame(_Fragment(_NextHeaderDestinationOptions, 0x0001, _DataThatLooksLikeAnExtensionHeader()));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "ipv6.dstopts").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task AtomicFragmentStillContinuesTheChain()
    {
        // Offset 0 and no More flag: the packet carries the whole datagram, so the Destination Options header is real.
        byte[] frame = _Frame(_Fragment(_NextHeaderDestinationOptions, 0x0000, _DataThatLooksLikeAnExtensionHeader()));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "ipv6.dstopts").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task FragmentableHeadersInTheFirstFragmentAreReassembledWithTheOtherFragments()
    {
        // The datagram is 24 bytes: an 8-byte Destination Options header followed by 16 bytes of data. The first fragment
        // carries 16 bytes (the header and 8 data bytes), the second the remaining 8 bytes at offset 2 (16 bytes). The
        // reassembled datagram is handed on under the next header named by the fragment headers, 60.
        PayloadProbeProtocol probe = new();
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        builder.RegisterProtocol(probe, static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(IPv4Protocol.IpProtoTableName, _NextHeaderDestinationOptions, id);
        });
        using Stack stack = builder.Build();

        byte[] destinationOptions = [_NextHeaderExperimental, 0, 0x01, 0x04, 0, 0, 0, 0];
        byte[] first = _Frame(_Fragment(_NextHeaderDestinationOptions, 0x0001, [.. destinationOptions, .. new byte[8]]));
        byte[] second = _Frame(_Fragment(_NextHeaderDestinationOptions, 0x0010, new byte[8]));

        _ = ProtocolTestHelper.ParseFrame(stack, first, 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stack, second, 1, Timestamp.FromMillis(1));

        await Assert.That(probe.Calls).IsEqualTo(1);
        await Assert.That(probe.LastLength).IsEqualTo(24);
    }
}
