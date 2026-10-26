// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for DTLS parsing of records that do not follow the plaintext layout: records of a later epoch (normally encrypted)
/// and records that carry a connection ID (content type 25, RFC 9146), whose header has a variable length.
/// </summary>
internal sealed class DtlsHardeningTests
{
    private const byte _Handshake = 22;
    private const byte _ConnectionId = 25;
    private const ushort _Dtls12 = 0xFEFD;

    private static byte[] _Record(byte contentType, ushort epoch, byte[] body)
    {
        byte[] record = new byte[13 + body.Length];
        record[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(1), _Dtls12);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), epoch);
        record[10] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(11), (ushort)body.Length);
        body.CopyTo(record, 13);
        return record;
    }

    /// <summary>A complete, unfragmented ClientHello-sized handshake header (12 bytes) with a 4-byte body.</summary>
    private static byte[] _HandshakeMessage()
    {
        byte[] message = new byte[12 + 4];
        message[0] = 1;
        message[3] = 4;
        message[11] = 4;
        return message;
    }

    private static byte[] _Frame(byte[] datagram)
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 443);
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(datagram);
    }

    #region Epoch

    [Test]
    [Arguments((ushort)1)]
    [Arguments((ushort)2)]
    [Arguments((ushort)0xFFFF)]
    public async Task HandshakeRecordOfALaterEpochIsNotDecodedAsAHandshake(ushort epoch)
    {
        // Records after the first key change are encrypted: their first bytes are ciphertext, not a handshake header.
        byte[] frame = _Frame(_Record(_Handshake, epoch, _HandshakeMessage()));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.epoch", epoch).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dtls.handshake.type").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task HandshakeRecordOfEpochZeroIsStillDecoded()
    {
        byte[] frame = _Frame(_Record(_Handshake, 0, _HandshakeMessage()));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.handshake.type", 1).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task EpochZeroHandshakeBehindALaterEpochRecordIsStillDecoded()
    {
        // The datagram holds an encrypted record followed by a plaintext one; each record is judged by its own epoch.
        byte[] datagram = [.. _Record(_Handshake, 1, _HandshakeMessage()), .. _Record(_Handshake, 0, _HandshakeMessage())];
        byte[] frame = _Frame(datagram);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.handshake.type", 1).ConfigureAwait(false);
        }
    }

    #endregion

    #region Connection ID records

    [Test]
    public async Task ConnectionIdRecordIsNotGivenALengthField()
    {
        // Bytes 11 and 12 of a connection ID record belong to the connection ID, not to a length field.
        byte[] frame = _Frame(_Record(_ConnectionId, 1, new byte[8]));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.content_type", _ConnectionId).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.epoch", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dtls.record.length").ConfigureAwait(false);
            await ProtocolTestHelper.AssertDisplayTextContains(stack, packet, "dtls.record.content_type", "Connection ID").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task RecordsAfterAConnectionIdRecordAreNotWalked()
    {
        // The record boundaries behind a connection ID record cannot be known (the connection ID length is negotiated),
        // so the bytes that would be a plaintext handshake record if the 13-byte header applied are not decoded as one.
        byte[] cidRecord = _Record(_ConnectionId, 1, new byte[3]);
        byte[] datagram = [.. cidRecord, .. _Record(_Handshake, 0, _HandshakeMessage())];
        byte[] frame = _Frame(datagram);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dtls.handshake.type").ConfigureAwait(false);
        }
    }

    #endregion
}
