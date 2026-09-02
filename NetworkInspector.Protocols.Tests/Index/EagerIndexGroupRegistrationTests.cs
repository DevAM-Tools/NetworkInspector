// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Index;

/// <summary>
/// Strict eager index-group registration invariant.
///
/// <para>
/// The presence index is the single reliable filtering layer: a filter that asks "does this
/// packet contain a <c>tls.sni</c> / <c>dns.opt</c> / <c>http.host</c> field?" must get a correct
/// answer from the index alone, without ever materializing the lazy descriptive field tree.
/// For that to hold, the contract is: <b>every field a protocol can emit must have its index group
/// recorded eagerly in <c>Parse</c></b>, content-consistent with what lazy materialization later
/// produces, with no false negatives.
/// </para>
///
/// <para>
/// This test enforces exactly that, per representative frame:
/// </para>
/// <list type="number">
///   <item>Parse the frame into a <see cref="NetworkInspector.Core.Index.PacketIndex"/> (eager only).</item>
///   <item>Snapshot — <b>before any materialization</b> — the set of index groups the parse recorded.</item>
///   <item>Materialize the entire lazy field tree.</item>
///   <item>Assert the snapshot equals the set of groups carried by emitted fields exactly: no emitted
///         field may carry a group missing from the snapshot (false negative), and no snapshot group may
///         lack a backing emitted field (false positive).</item>
/// </list>
///
/// <para>
/// Snapshotting before materialization makes the test robust even if materialization were ever to
/// mutate the index: an emitted field whose group was only recorded during materialization (a false
/// negative for the index) would fail the assertion.
/// </para>
/// </summary>
internal sealed class EagerIndexGroupRegistrationTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001); // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002); // 10.0.0.2

    #region Corpus

    /// <summary>
    /// Ethernet + IPv4 + TCP + TLS ClientHello advertising SNI, supported_groups, signature_algorithms,
    /// ALPN, supported_versions and key_share so the lazy TLS populator emits fields across the full set
    /// of content-dependent <c>tls.*</c> index groups.
    /// </summary>
    private static byte[] _BuildRichTlsClientHelloFrame()
    {
        byte[] sni = TlsRecordLayer.BuildExtension(
            TlsExtensionType.ServerName, TlsRecordLayer.BuildSniExtensionBody("example.com"));

        // supported_groups: 2-byte list length + one named group (x25519 = 0x001D).
        byte[] supportedGroups = TlsRecordLayer.BuildExtension(
            TlsExtensionType.SupportedGroups, [0x00, 0x02, 0x00, 0x1D]);

        // signature_algorithms: 2-byte list length + one scheme (rsa_pkcs1_sha256 = 0x0401).
        byte[] sigAlgs = TlsRecordLayer.BuildExtension(
            TlsExtensionType.SignatureAlgorithms, [0x00, 0x02, 0x04, 0x01]);

        byte[] alpn = TlsRecordLayer.BuildExtension(
            TlsExtensionType.Alpn, TlsRecordLayer.BuildAlpnExtensionBody("h2", "http/1.1"));

        byte[] supportedVersions = TlsRecordLayer.BuildExtension(
            TlsExtensionType.SupportedVersions, TlsRecordLayer.BuildSupportedVersionsExtensionBody(TlsRecordLayer.Tls13));

        // key_share (ClientHello form): 2-byte client_shares length + entry (group 0x001D, key length 2, key bytes).
        byte[] keyShare = TlsRecordLayer.BuildExtension(
            TlsExtensionType.KeyShare, [0x00, 0x06, 0x00, 0x1D, 0x00, 0x02, 0xAB, 0xCD]);

        byte[] extensions = [.. sni, .. supportedGroups, .. sigAlgs, .. alpn, .. supportedVersions, .. keyShare];

        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12, new byte[32], [], [0x1301, 0x1302], [0x00], extensions);
        byte[] hs = TlsRecordLayer.BuildHandshakeMessage(TlsHandshakeType.ClientHello, body);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(TlsContentType.Handshake, TlsRecordLayer.Tls10, hs);

        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 443, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
    }

    /// <summary>
    /// Ethernet + IPv4 + TCP + HTTP POST with a JSON body so the lazy HTTP populator emits header and body
    /// fields across the <c>http.*</c> index groups (and dispatches to JSON).
    /// </summary>
    private static byte[] _BuildHttpJsonFrame()
    {
        const string jsonBody = "{\"name\":\"John\",\"age\":30}";
        string httpMessage =
            "POST /api HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "User-Agent: ni-test\r\n" +
            "Content-Type: application/json\r\n" +
            "Connection: keep-alive\r\n" +
            $"Content-Length: {jsonBody.Length}\r\n" +
            "\r\n" +
            jsonBody;

        byte[] httpBytes = Encoding.ASCII.GetBytes(httpMessage);
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);
    }

    /// <summary>
    /// Ethernet + IPv4 + UDP + DNS response (one question + one A answer) so the lazy DNS populator emits
    /// question and resource-record fields across the <c>dns.*</c> index groups.
    /// </summary>
    private static byte[] _BuildDnsResponseFrame()
    {
        byte[] dns = DnsPayloadBuilder.BuildResponseSingleRR(
            id: 0x1234,
            queryName: "example.com",
            qtype: DnsPayloadBuilder.Type.A,
            rdata: [10, 0, 0, 42],
            ttlSeconds: 300);
        return DnsPayloadBuilder.WrapUdp(dns);
    }

    #endregion

    #region Systematic corpus

    // 2001:db8::1 / 2001:db8::2 — global IPv6 endpoints for the extension-header frames.
    private static readonly byte[] _V6Src = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _V6Dst = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02];

    // fe80::1 (link-local) / ff02::1 (all-nodes) — NDP frames travel link-local/multicast.
    private static readonly byte[] _V6LinkLocal = [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _V6AllNodes = [0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01];
    private static readonly byte[] _NdpPrefix = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] _NdpLinkAddr = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];

    /// <summary>Ethernet + IPv6 + UDP with a Hop-by-Hop extension header.</summary>
    private static byte[] _BuildIPv6HopByHopFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_V6Src), IPv6Address.FromBytes(_V6Dst));
        IPv6HopByHopLayer hbh = new();
        UdpLayer udp = new(1234, 5678);
        return FrameStack.Start(eth).Then(ip).Then(hbh).Then(udp).CreateWithFixedValues().EmitFrame([0xDE, 0xAD]);
    }

    /// <summary>Ethernet + IPv6 + UDP with a Routing extension header.</summary>
    private static byte[] _BuildIPv6RoutingFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_V6Src), IPv6Address.FromBytes(_V6Dst));
        IPv6RoutingLayer routing = new();
        UdpLayer udp = new(1234, 5678);
        return FrameStack.Start(eth).Then(ip).Then(routing).Then(udp).CreateWithFixedValues().EmitFrame([0xCA, 0xFE]);
    }

    /// <summary>Ethernet + IPv6 + UDP with a Destination Options extension header.</summary>
    private static byte[] _BuildIPv6DestinationOptionsFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_V6Src), IPv6Address.FromBytes(_V6Dst));
        IPv6DestinationOptionsLayer dst = new();
        UdpLayer udp = new(1234, 5678);
        return FrameStack.Start(eth).Then(ip).Then(dst).Then(udp).CreateWithFixedValues().EmitFrame([0xCA, 0xFE]);
    }

    /// <summary>Ethernet + IPv6 + UDP with a Fragment extension header (single complete fragment).</summary>
    private static byte[] _BuildIPv6FragmentFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_V6Src), IPv6Address.FromBytes(_V6Dst));
        IPv6FragmentExtensionLayer frag = new(identification: 0x12345678u);
        UdpLayer udp = new(1234, 5678);
        return FrameStack.Start(eth).Then(ip).Then(frag).Then(udp).CreateWithFixedValues().EmitFrame([0xDE, 0xAD, 0xBE, 0xEF]);
    }

    /// <summary>Ethernet + IPv6 + ICMPv6 Router Solicitation (type-only NDP message).</summary>
    private static byte[] _BuildIcmpv6RouterSolicitationFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_V6LinkLocal), IPv6Address.FromBytes(_V6AllNodes));
        IcmpV6RouterSolicitationLayer icmp = new();
        return FrameStack.Start(eth).Then(ip).Then(icmp).CreateWithFixedValues().EmitFrame([]);
    }

    /// <summary>
    /// Ethernet + IPv6 + ICMPv6 Router Advertisement carrying Source Link-Layer Address, Prefix
    /// Information and MTU options so the lazy ICMPv6 populator emits fields across the NDP option groups.
    /// </summary>
    private static byte[] _BuildIcmpv6RouterAdvertisementWithOptionsFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv6Layer ip = new(IPv6Address.FromBytes(_V6LinkLocal), IPv6Address.FromBytes(_V6AllNodes));
        IcmpV6RouterAdvertisementLayer ra = new(
            curHopLimit: 64, managed: true, other: false,
            routerLifetimeSec: 1800, reachableTimeMs: 30_000, retransTimerMs: 1000);

        byte[] sourceLinkAddr = _BuildNdpLinkLayerAddressOption(optType: 1, _NdpLinkAddr);
        byte[] prefixInfo = _BuildNdpPrefixInformationOption(
            prefixLength: 64, onLink: true, autonomous: true,
            validLifetimeSec: 86_400, preferredLifetimeSec: 14_400, _NdpPrefix);
        byte[] mtu = _BuildNdpMtuOption(1500);
        byte[] options = [.. sourceLinkAddr, .. prefixInfo, .. mtu];

        return FrameStack.Start(eth).Then(ip).Then(ra).CreateWithFixedValues().EmitFrame(options);
    }

    /// <summary>
    /// Ethernet + IPv4 + UDP + DNS response carrying DS, RRSIG, NSEC and DNSKEY answers plus an EDNS0 OPT
    /// pseudo-RR with one option, so the lazy DNS populator emits fields across every RR-type-dependent
    /// index group (<c>dns.ds</c>, <c>dns.rrsig</c>, <c>dns.nsec</c>, <c>dns.dnskey</c>, <c>dns.opt</c>,
    /// <c>dns.opt.option</c>). This is the corpus frame that exercises the eager RR-group detector whose
    /// guards mirror the populator byte-for-byte.
    /// </summary>
    private static byte[] _BuildDnssecAndOptResponseFrame()
    {
        byte[] qname = DnsPayloadBuilder.EncodeName("example.com");
        byte[] ownerPtr = DnsPayloadBuilder.EncodeNamePointer(DnsPayloadBuilder.HeaderSize); // points at QNAME

        List<byte> payload = new(160);
        byte[] header = new byte[DnsPayloadBuilder.HeaderSize];
        DnsPayloadBuilder.WriteHeader(header, id: 0x4242, flags: 0x8180, qdCount: 1, anCount: 4, nsCount: 0, arCount: 1);
        payload.AddRange(header);

        // Question: example.com IN A
        payload.AddRange(qname);
        _AppendU16(payload, DnsPayloadBuilder.Type.A);
        _AppendU16(payload, DnsPayloadBuilder.ClassIn);

        // DS (type 43): Key Tag(2) + Algorithm(1) + Digest Type(1) + Digest — RDLENGTH 8 (>= 4).
        _AppendRr(payload, ownerPtr, 43, DnsPayloadBuilder.ClassIn, 3600, [0x12, 0x34, 0x08, 0x02, 0xAA, 0xBB, 0xCC, 0xDD]);

        // RRSIG (type 46): 18-byte fixed header + signer's name (root) + signature — RDLENGTH 23 (>= 18).
        byte[] rrsig = new byte[18 + 1 + 4];
        rrsig[18] = 0x00; // signer's name = root
        _AppendRr(payload, ownerPtr, 46, DnsPayloadBuilder.ClassIn, 3600, rrsig);

        // NSEC (type 47): next domain name (root) + one type-bitmap window — RDLENGTH 4 (>= 1).
        _AppendRr(payload, ownerPtr, 47, DnsPayloadBuilder.ClassIn, 3600, [0x00, 0x00, 0x01, 0x40]);

        // DNSKEY (type 48): Flags(2) + Protocol(1) + Algorithm(1) + Public Key — RDLENGTH 6 (>= 4).
        _AppendRr(payload, ownerPtr, 48, DnsPayloadBuilder.ClassIn, 3600, [0x01, 0x00, 0x03, 0x08, 0xAB, 0xCD]);

        // OPT (type 41) pseudo-RR in the additional section: owner = root, CLASS = UDP payload size,
        // RDATA = one option TLV (code(2) + length(2) + data) so dns.opt and dns.opt.option both apply.
        _AppendRr(payload, [0x00], 41, 4096, 0, [0x00, 0x0A, 0x00, 0x02, 0xDE, 0xAD]);

        return DnsPayloadBuilder.WrapUdp(payload.ToArray());
    }

    /// <summary>Appends a big-endian 16-bit value to <paramref name="buffer"/>.</summary>
    private static void _AppendU16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)(value & 0xFF));
    }

    /// <summary>Appends one resource record (owner name, type, class, TTL, RDLENGTH, RDATA) to the payload.</summary>
    private static void _AppendRr(List<byte> buffer, ReadOnlySpan<byte> ownerName, ushort type, ushort cls, uint ttl, ReadOnlySpan<byte> rdata)
    {
        foreach (byte b in ownerName)
        {
            buffer.Add(b);
        }
        _AppendU16(buffer, type);
        _AppendU16(buffer, cls);
        buffer.Add((byte)(ttl >> 24));
        buffer.Add((byte)(ttl >> 16));
        buffer.Add((byte)(ttl >> 8));
        buffer.Add((byte)ttl);
        _AppendU16(buffer, (ushort)rdata.Length);
        foreach (byte b in rdata)
        {
            buffer.Add(b);
        }
    }

    /// <summary>Builds an NDP Source/Target Link-Layer Address option (RFC 4861 §4.6.1), 8 bytes.</summary>
    private static byte[] _BuildNdpLinkLayerAddressOption(byte optType, ReadOnlySpan<byte> mac6)
    {
        byte[] opt = new byte[8];
        opt[0] = optType;
        opt[1] = 1; // length in 8-byte units
        mac6.CopyTo(opt.AsSpan(2, 6));
        return opt;
    }

    /// <summary>Builds an NDP Prefix Information option (RFC 4861 §4.6.2), 32 bytes.</summary>
    private static byte[] _BuildNdpPrefixInformationOption(
        byte prefixLength, bool onLink, bool autonomous,
        uint validLifetimeSec, uint preferredLifetimeSec, ReadOnlySpan<byte> prefix)
    {
        byte[] opt = new byte[32];
        opt[0] = 3; // type
        opt[1] = 4; // length in 8-byte units (32 bytes)
        opt[2] = prefixLength;
        byte flags = 0;
        if (onLink)
        {
            flags |= 0x80;
        }
        if (autonomous)
        {
            flags |= 0x40;
        }
        opt[3] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(4, 4), validLifetimeSec);
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(8, 4), preferredLifetimeSec);
        prefix.CopyTo(opt.AsSpan(16, 16));
        return opt;
    }

    /// <summary>Builds an NDP MTU option (RFC 4861 §4.6.4), 8 bytes.</summary>
    private static byte[] _BuildNdpMtuOption(uint mtuBytes)
    {
        byte[] opt = new byte[8];
        opt[0] = 5; // type
        opt[1] = 1; // length in 8-byte units
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(4, 4), mtuBytes);
        return opt;
    }

    #endregion

    #region Tests

    [Test]
    public async Task RichTlsClientHello_AllEmittedFieldGroups_RecordedEagerly()
        => await _AssertAllEmittedFieldGroupsRecordedEagerly(_BuildRichTlsClientHelloFrame()).ConfigureAwait(false);

    [Test]
    public async Task HttpJsonBody_AllEmittedFieldGroups_RecordedEagerly()
        => await _AssertAllEmittedFieldGroupsRecordedEagerly(_BuildHttpJsonFrame()).ConfigureAwait(false);

    [Test]
    public async Task DnsResponse_AllEmittedFieldGroups_RecordedEagerly()
        => await _AssertAllEmittedFieldGroupsRecordedEagerly(_BuildDnsResponseFrame()).ConfigureAwait(false);

    /// <summary>
    /// Systematic, data-driven enforcement of the same eager-registration invariant across a broad
    /// protocol corpus, so the presence index is verified content-consistent for protocols and
    /// RR-type-dependent sub-groups beyond the three hand-picked frames above: IPv6 extension headers,
    /// ICMPv6 NDP with options, and a DNS response covering every DNSSEC/OPT record group. A new or
    /// changed protocol whose eager detector drifts from what its lazy populator emits fails here.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CorpusFrames))]
    public async Task Corpus_AllEmittedFieldGroups_RecordedEagerly(string name, byte[] frame)
    {
        _ = name; // surfaced as the test-case label; the assertion is frame-driven.
        await _AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
    }

    /// <summary>
    /// Broad protocol corpus for the systematic invariant. Each case is a named frame whose lazy field
    /// tree must be fully predicted by the eager presence index.
    /// </summary>
    public static IEnumerable<Func<(string, byte[])>> CorpusFrames()
    {
        yield return static () => ("tls-clienthello", _BuildRichTlsClientHelloFrame());
        yield return static () => ("http-json", _BuildHttpJsonFrame());
        yield return static () => ("dns-a-response", _BuildDnsResponseFrame());
        yield return static () => ("dns-dnssec-opt", _BuildDnssecAndOptResponseFrame());
        yield return static () => ("ipv6-hopbyhop", _BuildIPv6HopByHopFrame());
        yield return static () => ("ipv6-routing", _BuildIPv6RoutingFrame());
        yield return static () => ("ipv6-destopts", _BuildIPv6DestinationOptionsFrame());
        yield return static () => ("ipv6-fragment", _BuildIPv6FragmentFrame());
        yield return static () => ("icmpv6-router-solicitation", _BuildIcmpv6RouterSolicitationFrame());
        yield return static () => ("icmpv6-router-advertisement-options", _BuildIcmpv6RouterAdvertisementWithOptionsFrame());
    }

    /// <summary>
    /// Boundary: a freshly constructed <see cref="NetworkInspector.Core.Index.PacketIndex"/> that has not
    /// observed any packet must report no group present, so a passing invariant assertion cannot be a
    /// false positive caused by an unconditionally-populated index.
    /// </summary>
    [Test]
    public async Task FreshPacketIndex_ReportsNoGroupPresence()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        NetworkInspector.Core.Index.PacketIndex index = new(stack);

        for (int gid = 0; gid < stack.IndexGroupCount; gid++)
        {
            bool present = index.GetGroupBitmap(new IndexGroupId(gid)).Contains(0);
            await Assert.That(present).IsFalse()
                .Because("a fresh PacketIndex must not report any index group as present");
        }
    }

    #endregion

    /// <summary>
    /// Core invariant check. Parses the frame into a presence index, snapshots the eagerly-recorded
    /// index groups before any materialization, then materializes the whole field tree and asserts that
    /// every emitted, group-bearing field's group was already present in the eager snapshot.
    /// </summary>
    private static async Task _AssertAllEmittedFieldGroupsRecordedEagerly(byte[] frame)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        NetworkInspector.Core.Index.PacketIndex index = new(stack);

        Frame parsedFrame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frame,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrameIndexed(new PacketId(0), stack, parsedFrame, index);

        // Snapshot the eagerly-recorded groups BEFORE materialization. Any group an emitted field
        // depends on must already be in this set; otherwise the index would be a false negative.
        HashSet<int> eagerGroups = [];
        for (int gid = 0; gid < stack.IndexGroupCount; gid++)
        {
            if (index.GetGroupBitmap(new IndexGroupId(gid)).Contains(0))
            {
                _ = eagerGroups.Add(gid);
            }
        }

        // Build the full lazy field tree, then verify content-consistency against the eager snapshot.
        packet.MaterializeAll();

        // The set of index groups actually backed by an emitted field. The presence-index contract is
        // "a group is recorded for this packet IFF the packet emits at least one field of that group",
        // so this set must equal the eager snapshot exactly: a group in eagerGroups but absent here is a
        // false positive; a group here but absent from eagerGroups is a false negative.
        HashSet<int> materializedGroups = [];
        List<Field> materializedFields = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: false))
        {
            materializedFields.Add(field);
        }

        foreach (Field field in materializedFields)
        {
            IndexGroupId groupId = stack.GetFieldIndexGroup(field.FieldId);
            if (!groupId.IsValid)
            {
                continue;
            }

            _ = materializedGroups.Add(groupId.Value);
            string fieldName = stack.GetField(field.FieldId)?.Name ?? field.FieldId.Value.ToString(CultureInfo.InvariantCulture);
            string groupName = stack.GetIndexGroup(groupId)?.Name ?? groupId.Value.ToString(CultureInfo.InvariantCulture);
            await Assert.That(eagerGroups.Contains(groupId.Value)).IsTrue()
                .Because($"emitted field '{fieldName}' has index group '{groupName}' ({groupId.Value}), which must be recorded eagerly during ParseFrameIndexed (not only on materialization)");
        }

        await Assert.That(materializedGroups.Count > 0).IsTrue()
            .Because("the corpus frame must emit at least one group-bearing field for the invariant to be meaningful");

        // No false positives: every eagerly-recorded group must be backed by an emitted field.
        foreach (int eagerGroup in eagerGroups)
        {
            string eagerGroupName = stack.GetIndexGroup(new IndexGroupId(eagerGroup))?.Name ?? eagerGroup.ToString(CultureInfo.InvariantCulture);
            await Assert.That(materializedGroups.Contains(eagerGroup)).IsTrue()
                .Because($"index group '{eagerGroupName}' ({eagerGroup}) was recorded eagerly but no materialized field carries it — a false positive the presence index must never produce");
        }
    }
}
