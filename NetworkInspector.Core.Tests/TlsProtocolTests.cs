// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for TLS protocol parsing (RFC 8446).
/// Verifies record layer, handshake messages, cipher suites, SNI, and edge cases.
/// </summary>
internal sealed class TlsProtocolTests
{
    /// <summary>
    /// Builds a full stack with standard protocols and parses the given frame data.
    /// </summary>
    private static (Stack Stack, Packet Packet) BuildAndParse(byte[] frameData)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    // ─── Record Layer Tests ───────────────────────────────────────────────

    [Test]
    public async Task Parse_TlsClientHello_RecordFieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTlsClientHelloFrame();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // TLS protocol detected
            ProtocolId? tlsId = stack.GetProtocolId("tls");
            await Assert.That(tlsId).IsNotNull();

            // Content type = 22 (Handshake)
            FieldId? ctField = stack.GetFieldId("tls.record.content_type");
            await Assert.That(ctField).IsNotNull();
            bool hasCt = packet.TryGetFieldValue(ctField!.Value, out FieldValue ctValue);
            await Assert.That(hasCt).IsTrue();
            ctValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(22UL);

            // Record version
            FieldId? versionField = stack.GetFieldId("tls.record.version");
            bool hasVersion = packet.TryGetFieldValue(versionField!.Value, out FieldValue versionValue);
            await Assert.That(hasVersion).IsTrue();
            // Client Hello record version is TLS 1.0 (0x0301) for maximum compatibility
            versionValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0x0301UL);

            // Record length > 0
            FieldId? lenField = stack.GetFieldId("tls.record.length");
            bool hasLen = packet.TryGetFieldValue(lenField!.Value, out FieldValue lenValue);
            await Assert.That(hasLen).IsTrue();
            lenValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsGreaterThan(0UL);
        }
    }

    // ─── Handshake Tests ──────────────────────────────────────────────────

    [Test]
    public async Task Parse_TlsClientHello_HandshakeFieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTlsClientHelloFrame(
            serverName: "test.example.com",
            tlsVersion: 0x0303);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Handshake type = 1 (Client Hello)
            FieldId? hsType = stack.GetFieldId("tls.handshake.type");
            await Assert.That(hsType).IsNotNull();
            bool hasHsType = packet.TryGetFieldValue(hsType!.Value, out FieldValue hsTypeValue);
            await Assert.That(hasHsType).IsTrue();
            hsTypeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL);

            // Handshake version = TLS 1.2 (0x0303)
            FieldId? hsVersion = stack.GetFieldId("tls.handshake.version");
            bool hasHsVersion = packet.TryGetFieldValue(hsVersion!.Value, out FieldValue hsVersionValue);
            await Assert.That(hasHsVersion).IsTrue();
            hsVersionValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0x0303UL);

            // Handshake length > 0
            FieldId? hsLen = stack.GetFieldId("tls.handshake.length");
            bool hasHsLen = packet.TryGetFieldValue(hsLen!.Value, out FieldValue hsLenValue);
            await Assert.That(hasHsLen).IsTrue();
            hsLenValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsGreaterThan(0UL);
        }
    }

    [Test]
    public async Task Parse_TlsClientHello_CipherSuitesPresent()
    {
        ushort[] suites = [0x1301, 0x1302, 0xC02F];
        byte[] frameData = FrameBuilders.GenerateTlsClientHelloFrame(cipherSuites: suites);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Cipher suites length = 6 (3 suites x 2 bytes)
            FieldId? csLen = stack.GetFieldId("tls.handshake.cipher_suites_length");
            await Assert.That(csLen).IsNotNull();
            bool hasCsLen = packet.TryGetFieldValue(csLen!.Value, out FieldValue csLenValue);
            await Assert.That(hasCsLen).IsTrue();
            csLenValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(6UL);

            // At least one cipher suite field present
            FieldId? csField = stack.GetFieldId("tls.handshake.ciphersuite");
            await Assert.That(csField).IsNotNull();
            bool hasCs = packet.TryGetFieldValue(csField!.Value, out FieldValue csValue);
            await Assert.That(hasCs).IsTrue();
        }
    }

    [Test]
    public async Task Parse_TlsClientHello_SniCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTlsClientHelloFrame(
            serverName: "www.github.com");

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? sniField = stack.GetFieldId("tls.handshake.extensions.server_name");
            await Assert.That(sniField).IsNotNull();
            bool hasSni = packet.TryGetFieldValue(sniField!.Value, out FieldValue sniValue);
            await Assert.That(hasSni).IsTrue();
            sniValue.Data.TryGetAsString(out string strVal);
            await Assert.That(strVal).IsEqualTo("www.github.com");
        }
    }

    [Test]
    public async Task Parse_TlsServerHello_FieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateTlsServerHelloFrame(
            selectedCipherSuite: 0x1301,
            tlsVersion: 0x0303);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Handshake type = 2 (Server Hello)
            FieldId? hsType = stack.GetFieldId("tls.handshake.type");
            bool hasHsType = packet.TryGetFieldValue(hsType!.Value, out FieldValue hsTypeValue);
            await Assert.That(hasHsType).IsTrue();
            hsTypeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(2UL);

            // Selected cipher suite
            FieldId? csField = stack.GetFieldId("tls.handshake.ciphersuite");
            bool hasCs = packet.TryGetFieldValue(csField!.Value, out FieldValue csValue);
            await Assert.That(hasCs).IsTrue();
            csValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0x1301UL);

            // Session ID length = 32
            FieldId? sidLen = stack.GetFieldId("tls.handshake.session_id_length");
            bool hasSidLen = packet.TryGetFieldValue(sidLen!.Value, out FieldValue sidLenValue);
            await Assert.That(hasSidLen).IsTrue();
            sidLenValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(32UL);

            // Compression method = 0 (null)
            FieldId? compField = stack.GetFieldId("tls.handshake.comp_method");
            bool hasComp = packet.TryGetFieldValue(compField!.Value, out FieldValue compValue);
            await Assert.That(hasComp).IsTrue();
            compValue.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(0UL);
        }
    }

    // ─── Edge Cases ───────────────────────────────────────────────────────

    [Test]
    public async Task Parse_TlsShortData_DoesNotCrash()
    {
        // Ethernet(14) + IPv4(20) + TCP(20) + only 3 bytes of TLS (needs at least 5)
        byte[] frame = new byte[57];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        frame[14] = 0x45;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 43); // total len
        frame[23] = 6; // TCP
        frame[26] = 10;
        frame[30] = 10;
        // TCP dst port = 443
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), 443);
        frame[46] = 0x50; // TCP data offset

        (Stack stack, Packet packet) = BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.IsFinalized).IsTrue();
        }
    }

    [Test]
    public async Task Parse_TlsClientHello_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateTlsClientHelloFrame();

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(0), Timestamp.FromSecs(0), frameData,
                LinkType.Ethernet, FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            NetworkInspector.Core.Index.PacketIndex index = new(stack);
            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // TLS protocol present
            ProtocolId? tlsId = stack.GetProtocolId("tls");
            await Assert.That(tlsId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(tlsId!.Value).Contains(0)).IsTrue();

            // TLS record fields indexed
            FieldId? ctField = stack.GetFieldId("tls.record.content_type");
            await Assert.That(ctField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(ctField!.Value).Contains(0)).IsTrue();

            // Handshake fields indexed (content type = 22)
            FieldId? hsType = stack.GetFieldId("tls.handshake.type");
            await Assert.That(hsType).IsNotNull();
            await Assert.That(index.GetFieldBitmap(hsType!.Value).Contains(0)).IsTrue();
        }
    }
}