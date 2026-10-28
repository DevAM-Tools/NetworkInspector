// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TLS over TCP segments: records and handshake messages that span segments or records are reassembled before the
/// TLS parser runs, and the unit boundary detector behaves on partial, spanning, split-header and non-TLS input.
/// </summary>
/// <remarks>
/// The conversations go through the whole stack (TCP reassembly, then TLS) with the segmentation chosen by the test; the
/// detector tests call <c>TlsRecordDetector</c> directly. Thread safety: every test owns its stack.
/// </remarks>
internal sealed class TlsReassemblyTests
{
    #region Helpers

    private static byte[] _ClientHelloRecord(string serverName)
    {
        byte[] extensions = TlsPayloadBuilder.BuildExtension(
            TlsPayloadBuilder.ExtensionType.ServerName, TlsPayloadBuilder.BuildSniExtensionBody(serverName));
        byte[] hello = TlsPayloadBuilder.BuildClientHelloBody(
            TlsPayloadBuilder.Tls12, new byte[32], new byte[32], [0x1301, 0x1302], [0], extensions);
        return TlsPayloadBuilder.BuildRecord(
            TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12,
            TlsPayloadBuilder.BuildHandshakeMessage(TlsPayloadBuilder.HandshakeType.ClientHello, hello));
    }

    /// <summary>A Certificate handshake message with one certificate of <paramref name="certificateLength"/> bytes.</summary>
    private static byte[] _CertificateMessage(int certificateLength)
    {
        // certificate_list length (3) + [certificate length (3) + certificate]
        byte[] body = new byte[3 + 3 + certificateLength];
        int listLength = 3 + certificateLength;
        body[0] = (byte)(listLength >> 16);
        body[1] = (byte)(listLength >> 8);
        body[2] = (byte)listLength;
        body[3] = (byte)(certificateLength >> 16);
        body[4] = (byte)(certificateLength >> 8);
        body[5] = (byte)certificateLength;
        return TlsPayloadBuilder.BuildHandshakeMessage(TlsPayloadBuilder.HandshakeType.Certificate, body);
    }

    /// <summary>Cuts one handshake message into records whose payload is at most <paramref name="recordPayload"/> bytes.</summary>
    private static byte[] _RecordsOf(byte[] handshakeMessage, int recordPayload)
    {
        List<byte> bytes = [];
        for (int offset = 0; offset < handshakeMessage.Length; offset += recordPayload)
        {
            int length = Math.Min(recordPayload, handshakeMessage.Length - offset);
            bytes.AddRange(TlsPayloadBuilder.BuildRecord(
                TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12, handshakeMessage.AsSpan(offset, length)));
        }

        return [.. bytes];
    }

    private static async Task _AssertNoPacketError(Stack stack, Packet[] packets)
    {
        foreach (Packet packet in packets)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    #endregion

    #region Reassembly through the stack

    [Test]
    public async Task ClientHelloAcrossThreeSegmentsYieldsSni()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] record = _ClientHelloRecord("three.example.com");
        TcpTestConversation conversation = new(serverPort: 443);
        conversation.Handshake();
        conversation.ClientSend(record, mss: (record.Length / 3) + 1);

        Packet[] packets = conversation.ParseAll(stack);

        // Nothing is shown while the record is incomplete; the completing segment shows the whole hello.
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[3], "tls").ConfigureAwait(false);
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[4], "tls").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, packets[^1], "tls.handshake.extensions.server_name", "three.example.com")
            .ConfigureAwait(false);
        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
    }

    [Test]
    public async Task CertificateAcrossTwoRecordsYieldsTheChain()
    {
        // 20000 bytes of certificate do not fit one record (limit 16384): the handshake message spans two records and,
        // at an MSS of 1460, about fourteen segments.
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] records = _RecordsOf(_CertificateMessage(20_000), recordPayload: 16_384);
        TcpTestConversation conversation = new(serverPort: 443);
        conversation.Handshake();
        conversation.ServerSend(records);

        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "tls.handshake.certificate.length", 20_000).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "tls.handshake.type", 11).ConfigureAwait(false);
        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
    }

    [Test]
    public async Task HandshakeHeaderSplitByARecordBoundaryIsJoined()
    {
        // A record boundary may fall inside the 4-byte handshake header. The first record carries only 2 header bytes.
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] message = _CertificateMessage(300);
        byte[] records =
        [
            .. TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12, message.AsSpan(0, 2)),
            .. TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12, message.AsSpan(2)),
        ];
        TcpTestConversation conversation = new(serverPort: 443);
        conversation.Handshake();
        conversation.ServerSend(records);

        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "tls.handshake.certificate.length", 300).ConfigureAwait(false);
        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
    }

    [Test]
    public async Task ThreeByteTailSegmentProducesNoError()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] record = _ClientHelloRecord("tail.example.com");

        // Segment size chosen so that the last segment holds exactly 3 bytes: shorter than a record header.
        int mss = (record.Length - 3) / 2;
        TcpTestConversation conversation = new(serverPort: 443);
        conversation.Handshake();
        conversation.ClientSend(record, mss);

        Packet[] packets = conversation.ParseAll(stack);

        await Assert.That(conversation.Frames.Count).IsGreaterThan(5);
        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packets[^1], "tls.handshake.extensions.server_name").ConfigureAwait(false);
    }

    [Test]
    public async Task ConsecutiveCompleteRecordsOfOneSegmentAreEachParsed()
    {
        // The common case: a server sends ServerHello and Certificate in separate records, one segment. No message spans a
        // record boundary, so no joining happens and every message still shows.
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] hello = TlsPayloadBuilder.BuildHandshakeMessage(
            TlsPayloadBuilder.HandshakeType.ServerHello,
            TlsPayloadBuilder.BuildServerHelloBody(TlsPayloadBuilder.Tls12, new byte[32], [], 0x1301, 0, []));
        byte[] records =
        [
            .. TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12, hello),
            .. TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12, _CertificateMessage(100)),
        ];
        TcpTestConversation conversation = new(serverPort: 443);
        conversation.Handshake();
        conversation.ServerSend(records);

        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertFieldExists(stack, packets[3], "tls.handshake.ciphersuite").ConfigureAwait(false);
        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
    }

    #endregion

    #region Boundary detector

    private static PduBoundaryResult _Detect(byte[] data) => new TlsRecordDetector().Detect(data);

    [Test]
    public async Task DetectorFramesOneHandshakeRecord()
    {
        byte[] record = _ClientHelloRecord("a.example.com");

        PduBoundaryResult result = _Detect(record);

        await Assert.That(result.Length).IsEqualTo(record.Length);
    }

    [Test]
    public async Task DetectorWaitsForTheRestOfARecord()
    {
        byte[] record = _ClientHelloRecord("a.example.com");

        PduBoundaryResult result = _Detect(record[..^1]);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectorWaitsForTheRestOfAHeader()
    {
        PduBoundaryResult result = _Detect([0x16, 0x03, 0x03]);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectorExtendsTheUnitOverRecordsOfASpanningMessage()
    {
        byte[] records = _RecordsOf(_CertificateMessage(40_000), recordPayload: 16_384);
        byte[] applicationData = TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.ApplicationData, TlsPayloadBuilder.Tls12, new byte[10]);
        byte[] followedByApplicationData = [.. records, .. applicationData];

        PduBoundaryResult whole = _Detect(followedByApplicationData);
        PduBoundaryResult cutInTheLastRecord = _Detect(records[..^5]);

        // The unit ends exactly where the handshake message ends; the application data record after it is the next unit.
        await Assert.That(whole.Length).IsEqualTo(records.Length);
        await Assert.That(cutInTheLastRecord.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectorFramesANonHandshakeRecordAlone()
    {
        byte[] data = TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.ApplicationData, TlsPayloadBuilder.Tls12, new byte[100]);

        PduBoundaryResult result = _Detect(data);

        await Assert.That(result.Length).IsEqualTo(data.Length);
    }

    [Test]
    public async Task DetectorRejectsAHandshakeThatIsInterruptedByAnotherRecordType()
    {
        // RFC 8446 section 5.1: handshake messages must not be interleaved with other record types.
        byte[] message = _CertificateMessage(300);
        byte[] data =
        [
            .. TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.Handshake, TlsPayloadBuilder.Tls12, message.AsSpan(0, 100)),
            .. TlsPayloadBuilder.BuildRecord(TlsPayloadBuilder.ContentType.ApplicationData, TlsPayloadBuilder.Tls12, new byte[5]),
        ];

        PduBoundaryResult result = _Detect(data);

        await Assert.That(result.IsInvalid).IsTrue();
    }

    [Test]
    public async Task DetectorRejectsBytesThatAreNotTls()
    {
        // Plain text on a TLS port: no record starts like this, so the detector must not hold the bytes back.
        PduBoundaryResult http = _Detect("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        PduBoundaryResult oversizedLength = _Detect([0x16, 0x03, 0x03, 0xFF, 0xFF, 0x00]);
        PduBoundaryResult unknownVersion = _Detect([0x16, 0x09, 0x09, 0x00, 0x01, 0x00]);

        await Assert.That(http.IsInvalid).IsTrue();
        await Assert.That(oversizedLength.IsInvalid).IsTrue();
        await Assert.That(unknownVersion.IsInvalid).IsTrue();
    }

    [Test]
    public async Task TryGetUnitReportsTheRecordCountOfAnUnit()
    {
        byte[] records = _RecordsOf(_CertificateMessage(40_000), recordPayload: 16_384);

        TlsRecordDetector.UnitState state = TlsRecordDetector.TryGetUnit(records, 0, out int end, out int count);

        await Assert.That(state).IsEqualTo(TlsRecordDetector.UnitState.Complete);
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(end).IsEqualTo(records.Length);
    }

    #endregion
}
