// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for DNS protocol parsing (RFC 1035).
/// Verifies header fields, flag extraction, query/answer sections,
/// name compression, display text, and edge cases.
/// </summary>
internal sealed class DnsProtocolTests
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

    // ─── DNS Query Tests ──────────────────────────────────────────────────

    [Test]
    public async Task Parse_DnsQuery_HeaderFieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateDnsQueryFrame(
            queryName: "www.example.com",
            transactionId: 0xABCD);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // DNS protocol detected
            ProtocolId? dnsId = stack.GetProtocolId("dns");
            await Assert.That(dnsId).IsNotNull();

            // Transaction ID
            FieldId? idField = stack.GetFieldId("dns.id");
            await Assert.That(idField).IsNotNull();
            bool hasId = packet.TryGetFieldValue(idField!.Value, out FieldValue idValue);
            await Assert.That(hasId).IsTrue();
            idValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xABCDUL);

            // Question count
            FieldId? qdCount = stack.GetFieldId("dns.count.queries");
            bool hasQd = packet.TryGetFieldValue(qdCount!.Value, out FieldValue qdValue);
            await Assert.That(hasQd).IsTrue();
            qdValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(1UL);

            // Answer count = 0
            FieldId? anCount = stack.GetFieldId("dns.count.answers");
            bool hasAn = packet.TryGetFieldValue(anCount!.Value, out FieldValue anValue);
            await Assert.That(hasAn).IsTrue();
            anValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(0UL);
        }
    }

    [Test]
    public async Task Parse_DnsQuery_FlagsCorrect()
    {
        // Standard query with recursion desired: flags = 0x0100
        byte[] frameData = FrameBuilders.GenerateDnsQueryFrame();

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // QR flag = false (query)
            FieldId? responseFlag = stack.GetFieldId("dns.flags.response");
            bool hasResponse = packet.TryGetFieldValue(responseFlag!.Value, out FieldValue responseValue);
            await Assert.That(hasResponse).IsTrue();
            responseValue.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();

            // Opcode = 0 (standard query)
            FieldId? opcodeFlag = stack.GetFieldId("dns.flags.opcode");
            bool hasOpcode = packet.TryGetFieldValue(opcodeFlag!.Value, out FieldValue opcodeValue);
            await Assert.That(hasOpcode).IsTrue();
            opcodeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0UL);

            // Recursion desired = true
            FieldId? rdFlag = stack.GetFieldId("dns.flags.recdesired");
            bool hasRd = packet.TryGetFieldValue(rdFlag!.Value, out FieldValue rdValue);
            await Assert.That(hasRd).IsTrue();
            rdValue.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsTrue();

            // Recursion available = false (query)
            FieldId? raFlag = stack.GetFieldId("dns.flags.recavail");
            bool hasRa = packet.TryGetFieldValue(raFlag!.Value, out FieldValue raValue);
            await Assert.That(hasRa).IsTrue();
            raValue.Data.TryGetAsBool(out bool boolVal3);
            await Assert.That(boolVal3).IsFalse();

            // Truncated = false
            FieldId? tcFlag = stack.GetFieldId("dns.flags.truncated");
            bool hasTc = packet.TryGetFieldValue(tcFlag!.Value, out FieldValue tcValue);
            await Assert.That(hasTc).IsTrue();
            tcValue.Data.TryGetAsBool(out bool boolVal4);
            await Assert.That(boolVal4).IsFalse();
        }
    }

    [Test]
    public async Task Parse_DnsQuery_QueryNameCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateDnsQueryFrame(
            queryName: "www.example.com",
            queryType: 1, // A
            queryClass: 1); // IN

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Query name
            FieldId? qryName = stack.GetFieldId("dns.qry.name");
            await Assert.That(qryName).IsNotNull();
            bool hasName = packet.TryGetFieldValue(qryName!.Value, out FieldValue nameValue);
            await Assert.That(hasName).IsTrue();
            nameValue.Data.TryGetAsString(out string strVal);
            await Assert.That(strVal).IsEqualTo("www.example.com");

            // Query type = A (1)
            FieldId? qryType = stack.GetFieldId("dns.qry.type");
            bool hasType = packet.TryGetFieldValue(qryType!.Value, out FieldValue typeValue);
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL);

            // Query class = IN (1)
            FieldId? qryClass = stack.GetFieldId("dns.qry.class");
            bool hasClass = packet.TryGetFieldValue(qryClass!.Value, out FieldValue classValue);
            await Assert.That(hasClass).IsTrue();
            classValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(1UL);

            // Query name length
            FieldId? qryNameLen = stack.GetFieldId("dns.qry.name.len");
            bool hasLen = packet.TryGetFieldValue(qryNameLen!.Value, out FieldValue lenValue);
            await Assert.That(hasLen).IsTrue();
            lenValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(15UL); // "www.example.com".Length
        }
    }

    // ─── DNS Response Tests ───────────────────────────────────────────────

    [Test]
    public async Task Parse_DnsResponse_HeaderFieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateDnsResponseFrame(
            queryName: "www.example.com",
            transactionId: 0x5678);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Transaction ID
            FieldId? idField = stack.GetFieldId("dns.id");
            bool hasId = packet.TryGetFieldValue(idField!.Value, out FieldValue idValue);
            await Assert.That(hasId).IsTrue();
            idValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x5678UL);

            // QR flag = true (response)
            FieldId? responseFlag = stack.GetFieldId("dns.flags.response");
            bool hasResponse = packet.TryGetFieldValue(responseFlag!.Value, out FieldValue responseValue);
            await Assert.That(hasResponse).IsTrue();
            responseValue.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            // Recursion desired = true
            FieldId? rdFlag = stack.GetFieldId("dns.flags.recdesired");
            bool hasRd = packet.TryGetFieldValue(rdFlag!.Value, out FieldValue rdValue);
            await Assert.That(hasRd).IsTrue();
            rdValue.Data.TryGetAsBool(out bool boolVal2);
            await Assert.That(boolVal2).IsTrue();

            // Recursion available = true (in response)
            FieldId? raFlag = stack.GetFieldId("dns.flags.recavail");
            bool hasRa = packet.TryGetFieldValue(raFlag!.Value, out FieldValue raValue);
            await Assert.That(hasRa).IsTrue();
            raValue.Data.TryGetAsBool(out bool boolVal3);
            await Assert.That(boolVal3).IsTrue();

            // RCODE = 0 (no error)
            FieldId? rcodeFlag = stack.GetFieldId("dns.flags.rcode");
            bool hasRcode = packet.TryGetFieldValue(rcodeFlag!.Value, out FieldValue rcodeValue);
            await Assert.That(hasRcode).IsTrue();
            rcodeValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(0UL);

            // Section counts
            FieldId? qdCount = stack.GetFieldId("dns.count.queries");
            bool hasQd = packet.TryGetFieldValue(qdCount!.Value, out FieldValue qdValue);
            await Assert.That(hasQd).IsTrue();
            qdValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(1UL);

            FieldId? anCount = stack.GetFieldId("dns.count.answers");
            bool hasAn = packet.TryGetFieldValue(anCount!.Value, out FieldValue anValue);
            await Assert.That(hasAn).IsTrue();
            anValue.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Parse_DnsResponse_ARecord_AddressCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateDnsResponseFrame(
            queryName: "www.example.com",
            ip1: 93, ip2: 184, ip3: 216, ip4: 34,
            ttl: 300);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Answer name
            FieldId? respName = stack.GetFieldId("dns.resp.name");
            await Assert.That(respName).IsNotNull();
            bool hasName = packet.TryGetFieldValue(respName!.Value, out FieldValue nameValue);
            await Assert.That(hasName).IsTrue();
            // Name comes via compression pointer — should resolve to same query name
            nameValue.Data.TryGetAsString(out string strVal);
            await Assert.That(strVal).IsEqualTo("www.example.com");

            // Answer type = A (1)
            FieldId? respType = stack.GetFieldId("dns.resp.type");
            bool hasType = packet.TryGetFieldValue(respType!.Value, out FieldValue typeValue);
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(1UL);

            // Answer class = IN (1)
            FieldId? respClass = stack.GetFieldId("dns.resp.class");
            bool hasClass = packet.TryGetFieldValue(respClass!.Value, out FieldValue classValue);
            await Assert.That(hasClass).IsTrue();
            classValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val2).IsEqualTo(1UL);

            // TTL
            FieldId? respTtl = stack.GetFieldId("dns.resp.ttl");
            bool hasTtl = packet.TryGetFieldValue(respTtl!.Value, out FieldValue ttlValue);
            await Assert.That(hasTtl).IsTrue();
            ttlValue.Data.TryGetAsU64(out ulong u64Val3);
            await Assert.That(u64Val3).IsEqualTo(300UL);

            // RDLENGTH = 4
            FieldId? respLen = stack.GetFieldId("dns.resp.len");
            bool hasLen = packet.TryGetFieldValue(respLen!.Value, out FieldValue lenValue);
            await Assert.That(hasLen).IsTrue();
            lenValue.Data.TryGetAsU64(out ulong u64Val4);
            await Assert.That(u64Val4).IsEqualTo(4UL);

            // A record address
            FieldId? aField = stack.GetFieldId("dns.a");
            await Assert.That(aField).IsNotNull();
            bool hasA = packet.TryGetFieldValue(aField!.Value, out FieldValue aValue);
            await Assert.That(hasA).IsTrue();
            aValue.Data.TryGetAsIPv4(out IPv4Address ipv4Val);
            await Assert.That(ipv4Val.Format()).IsEqualTo("93.184.216.34");
        }
    }

    // ─── Edge Cases ───────────────────────────────────────────────────────

    [Test]
    public async Task Parse_DnsShortData_DoesNotCrash()
    {
        // Ethernet(14) + IPv4(20) + UDP(8) + only 6 bytes of DNS (too short, needs 12)
        byte[] shortFrame = new byte[48];
        // Ethernet: ethertype IPv4
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(12), 0x0800);
        // IPv4 header (minimal)
        shortFrame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(16), 34); // total len = 20+8+6
        shortFrame[23] = 17; // protocol = UDP
        shortFrame[26] = 10;
        shortFrame[30] = 10; // src/dst IP
        // UDP dest port 53
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(36), 53);
        BinaryPrimitives.WriteUInt16BigEndian(shortFrame.AsSpan(38), 14); // UDP length
        // Only 6 bytes of "DNS" data — insufficient for 12-byte header

        (Stack stack, Packet packet) = BuildAndParse(shortFrame);
        using (stack)
        {
            // Should not crash; DNS fields may not be present due to insufficient data
            await Assert.That(packet.IsFinalized).IsTrue();
        }
    }

    [Test]
    public async Task Parse_DnsQuery_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateDnsQueryFrame();

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

            // DNS protocol should be present in the index
            ProtocolId? dnsId = stack.GetProtocolId("dns");
            await Assert.That(dnsId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(dnsId!.Value).Contains(0)).IsTrue();

            // DNS header fields should be indexed (all share "dns" index group)
            FieldId? idField = stack.GetFieldId("dns.id");
            await Assert.That(idField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(idField!.Value).Contains(0)).IsTrue();

            // Query fields should be indexed (share "dns.qry" group)
            FieldId? qryName = stack.GetFieldId("dns.qry.name");
            await Assert.That(qryName).IsNotNull();
            await Assert.That(index.GetFieldBitmap(qryName!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_DnsResponse_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateDnsResponseFrame();

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

            // DNS answer fields should be indexed (share "dns.ans" group)
            FieldId? aField = stack.GetFieldId("dns.a");
            await Assert.That(aField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(aField!.Value).Contains(0)).IsTrue();

            FieldId? respTtl = stack.GetFieldId("dns.resp.ttl");
            await Assert.That(respTtl).IsNotNull();
            await Assert.That(index.GetFieldBitmap(respTtl!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_DnsQuery_AAAARecord_TypeCorrect()
    {
        // Query for AAAA record (type 28)
        byte[] frameData = FrameBuilders.GenerateDnsQueryFrame(
            queryName: "ipv6.example.com",
            queryType: 28); // AAAA

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? qryType = stack.GetFieldId("dns.qry.type");
            bool hasType = packet.TryGetFieldValue(qryType!.Value, out FieldValue typeValue);
            await Assert.That(hasType).IsTrue();
            typeValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(28UL);
        }
    }

    [Test]
    public async Task Parse_DnsQuery_DifferentDomains()
    {
        // Test various domain name structures
        string[] domains = ["a.com", "sub.domain.example.org", "deep.nested.sub.example.co.uk"];

        foreach (string domain in domains)
        {
            byte[] frameData = FrameBuilders.GenerateDnsQueryFrame(queryName: domain);
            (Stack stack, Packet packet) = BuildAndParse(frameData);
            using (stack)
            {
                FieldId? qryName = stack.GetFieldId("dns.qry.name");
                bool hasName = packet.TryGetFieldValue(qryName!.Value, out FieldValue nameValue);
                await Assert.That(hasName).IsTrue();
                nameValue.Data.TryGetAsString(out string strVal);
                await Assert.That(strVal).IsEqualTo(domain);
            }
        }
    }
}