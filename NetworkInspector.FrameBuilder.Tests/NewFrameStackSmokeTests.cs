// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for the cons-list <see cref="NetworkInspector.FrameBuilder.Frames.FrameStack"/>
/// composition API.  Verifies the canonical Eth/IPv4/UDP path end-to-end
/// (length, EtherType, IP-Protocol, IPv4 header checksum, UDP length) without
/// relying on a legacy reference implementation.
/// </summary>
internal sealed class NewFrameStackSmokeTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);

    /// <summary>Sample payload used to make every variable-width field non-zero.</summary>
    private static readonly byte[] _Payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];

    [Test]
    public async Task Eth_IPv4_Udp_HasCorrectStructuralFields()
    {
        IPv4Address srcIp = new(0x0A000001);
        IPv4Address dstIp = new(0x0A000002);

        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(srcIp, dstIp);
        // Explicit checksum 0 = "no checksum" (UDP/IPv4 only); avoids depending on
        // pseudo-header computation for the structural-fields assertion.
        FB.UdpLayer udp = new(5353, 5353, FB.Auto.Explicit((ushort)0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> created = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .CreateWithFixedValues();

        byte[] frame = new byte[created.HeaderSize + _Payload.Length];
        (_, int len, _) = _EmitOnce(in created, _Payload, frame);

        // Total length = 14 (Eth) + 20 (IPv4) + 8 (UDP) + payload.
        await Assert.That(len).IsEqualTo(14 + 20 + 8 + _Payload.Length);

        // EtherType = IPv4 (auto-patched from IPv4Layer.ProtocolType).
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2));
        await Assert.That(etherType).IsEqualTo((ushort)EtherTypes.IPv4);

        // IP Protocol field = UDP (auto-patched from UdpLayer.ProtocolType).
        await Assert.That(frame[14 + 9]).IsEqualTo((byte)IpProtocols.Udp);

        // IP TotalLength = 20 + 8 + payload (post-fix Length phase).
        ushort totalLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 2, 2));
        await Assert.That(totalLen).IsEqualTo((ushort)(20 + 8 + _Payload.Length));

        // IP header checksum verifies to 0 (post-fix OuterChecksum phase).
        ushort ipChecksum = ChecksumUtils.IPv4Header(frame.AsSpan(14, 20));
        await Assert.That(ipChecksum).IsEqualTo((ushort)0);

        // UDP Length = 8 + payload (post-fix Length phase).
        ushort udpLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 20 + 4, 2));
        await Assert.That(udpLen).IsEqualTo((ushort)(8 + _Payload.Length));
    }

    /// <summary>
    /// Emits a single frame from a <see cref="FB.CreatedStack{TStack,TTrailer,TInterceptor}"/>.
    /// </summary>
    /// <remarks>
    /// Wraps the <see cref="FB.FrameSequence{TStack,TTrailer,TInterceptor}"/> ref struct
    /// in a synchronous helper so test methods can <c>await</c> their assertions
    /// without keeping the ref struct alive across the await boundary (CS4007).
    /// </remarks>
    private static (bool Emitted, int Length, FB.BuildStatus Status) _EmitOnce<TStack, TTrailer, TInterceptor>(
        in FB.CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload,
        Span<byte> dst)
        where TStack : struct, FB.IStackNode, FB.IStatelessStack
        where TTrailer : struct, FB.ITrailerLayer
        where TInterceptor : struct, FB.IFrameInterceptor
    {
        FB.FrameSequence<TStack, TTrailer, TInterceptor> seq = created.Build(payload);
        bool emitted = seq.MoveNext(dst, out int written);
        return (emitted, written, seq.Status);
    }

    /// <summary>
    /// IP-in-IP tunnel (RFC 2003): outer IPv4 carries an inner IPv4 datagram
    /// as protocol 4.  This composition is now permitted by the
    /// <c>Then(...)</c> overload set because the inner IPv4 is
    /// <see cref="IPseudoHeaderIndependent"/> and the outer IPv4 is
    /// <see cref="IInteriorLayer"/>.  The auto-patch facility writes the
    /// inner IPv4's <see cref="IPv4Layer.ProtocolType"/> (which is the
    /// EtherType value 0x0800) into the outer IPv4's Protocol field, which
    /// would be wrong; the test pins the outer Protocol to 4 explicitly.
    /// </summary>
    [Test]
    public async Task IPv4_in_IPv4_compiles_with_explicit_protocol_field()
    {
        IPv4Address tunnelSrc = new(0x0A000001);
        IPv4Address tunnelDst = new(0x0A000002);
        IPv4Address innerSrc = new(0xAC100001);
        IPv4Address innerDst = new(0xAC100002);

        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        // Outer IPv4: pin protocol=4 (IP-in-IP per IANA / RFC 2003).
        FB.IPv4Layer outerIp = new(tunnelSrc, tunnelDst, protocol: FB.Auto.Explicit((byte)4));
        FB.IPv4Layer innerIp = new(innerSrc, innerDst);

        FB.CreatedStack<
            FB.StatelessStack<FB.IPv4Layer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> created = FB.FrameStack
                .Start(eth)
                .Then(outerIp)
                .Then(innerIp)
                .CreateWithFixedValues();

        // Inner payload is empty for this minimal smoke test.
        byte[] frame = new byte[created.HeaderSize];
        (_, int len, _) = _EmitOnce(in created, [], frame);

        // Total length = 14 (Eth) + 20 (outer IPv4) + 20 (inner IPv4).
        await Assert.That(len).IsEqualTo(14 + 20 + 20);

        // Outer EtherType auto-patched to IPv4.
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12, 2));
        await Assert.That(etherType).IsEqualTo((ushort)EtherTypes.IPv4);

        // Outer IPv4 Protocol must be 4 (IP-in-IP), pinned by the user.
        await Assert.That(frame[14 + 9]).IsEqualTo((byte)4);

        // Outer IPv4 TotalLength = 20 + 20 = 40.
        ushort outerTotalLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 2, 2));
        await Assert.That(outerTotalLen).IsEqualTo((ushort)(20 + 20));

        // Inner IPv4 TotalLength = 20 (header only, no payload).
        ushort innerTotalLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(14 + 20 + 2, 2));
        await Assert.That(innerTotalLen).IsEqualTo((ushort)20);

        // Both IPv4 header checksums must verify to 0.
        await Assert.That(ChecksumUtils.IPv4Header(frame.AsSpan(14, 20))).IsEqualTo((ushort)0);
        await Assert.That(ChecksumUtils.IPv4Header(frame.AsSpan(14 + 20, 20))).IsEqualTo((ushort)0);
    }
}
