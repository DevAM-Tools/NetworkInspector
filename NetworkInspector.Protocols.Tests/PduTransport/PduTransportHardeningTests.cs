// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.PduTransport;

/// <summary>
/// Tests for PDU Transport parsing of crafted datagrams: declared payload lengths that do not fit a signed 32-bit integer.
/// </summary>
internal sealed class PduTransportHardeningTests
{
    private const ushort _DispatchPort = 15500;

    private static byte[] _Frame(ReadOnlySpan<byte> pduTransportPayload)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(15001, _DispatchPort);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(pduTransportPayload);
    }

    private static void _ConfigureDispatch(SettingsManager settings)
    {
        settings.PreloadValue(PduTransportRegistration.UdpDispatchPortsSetting, SettingValue.U64Array([_DispatchPort]));
    }

    /// <summary>One PDU with the default 4-byte ID and 4-byte length fields followed by <paramref name="payloadByteCount"/> bytes.</summary>
    private static byte[] _Pdu(uint id, uint declaredLength, int payloadByteCount)
    {
        byte[] pdu = new byte[8 + payloadByteCount];
        BinaryPrimitives.WriteUInt32BigEndian(pdu, id);
        BinaryPrimitives.WriteUInt32BigEndian(pdu.AsSpan(4), declaredLength);
        return pdu;
    }

    [Test]
    [Arguments(0x7FFFFFFFu)]
    [Arguments(0x80000000u)]
    [Arguments(0xFFFFFFFFu)]
    public async Task DeclaredLengthAboveTheSignedRangeShowsTheBytesThatArePresent(uint declaredLength)
    {
        byte[] frame = _Frame(_Pdu(id: 7, declaredLength, payloadByteCount: 3));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, _ConfigureDispatch);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "pdu_transport.length", declaredLength).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "pdu_transport.payload", [0, 0, 0]).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task OnePduFollowedByAnotherIsStillWalkedAfterAShortLength()
    {
        // Control for the cases above: a normal length keeps walking the datagram.
        byte[] first = _Pdu(id: 1, declaredLength: 2, payloadByteCount: 2);
        byte[] second = _Pdu(id: 2, declaredLength: 1, payloadByteCount: 1);
        byte[] frame = _Frame([.. first, .. second]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, _ConfigureDispatch);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "pdu_transport.id", 1).ConfigureAwait(false);
            FieldId idFieldId = stack.GetFieldId("pdu_transport.id")!.Value; // registered by the PDU Transport protocol
            int idCount = 0;
            foreach (Field field in packet.IterFieldsDfs(materialize: true))
            {
                if (field.FieldId == idFieldId)
                {
                    idCount++;
                }
            }

            await Assert.That(idCount).IsEqualTo(2);
        }
    }
}
