// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Capabilities;

/// <summary>
/// Phase V0 follow-up smoke test: verifies that <see cref="FrameStack.Start"/>
/// accepts <see cref="IPv6Layer"/> directly (raw-IPv6 capture, e.g. tun
/// interfaces).  This was previously rejected because the constraint was
/// <c>ILinkLayer</c>; after V0 the constraint is <c>IRootLayer</c> and IPv6
/// implements it.
/// </summary>
internal sealed class RawIPv6SmokeTests
{
    private static readonly IPv6Address _SrcIp6 =
        IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);

    private static readonly IPv6Address _DstIp6 =
        IPv6Address.FromBytes([0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2]);

    /// <summary>Raw-IPv6 stack <c>IPv6 -&gt; UDP</c> builds and patches the IPv6 NextHeader field.</summary>
    [Test]
    public async Task RawIPv6_thenUdp_BuildsAndPatchesNextHeader()
    {
        FB.IPv6Layer ip6 = new(_SrcIp6, _DstIp6);
        FB.UdpLayer udp = new(srcPort: 12345, dstPort: 53);

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv6Layer, FB.StackEnd>>,
            FB.NoTrailer,
            FB.NoInterceptor> created = FB.FrameStack
                .Start(ip6)
                .Then(udp)
                .CreateWithFixedValues();

        ReadOnlySpan<byte> payloadSpan = stackalloc byte[] { 0xCA, 0xFE };
        byte[] payloadCopy = payloadSpan.ToArray();
        byte[] frame = new byte[created.HeaderSize + payloadSpan.Length];

        // Wrap ref-struct emission in a synchronous helper so the
        // FrameSequence and ReadOnlySpan locals never cross an await boundary
        // (CS4007 — same pattern as NewFrameStackSmokeTests.EmitOnce).
        (bool emitted, int written, FB.BuildStatus status) = EmitOnce(in created, payloadCopy, frame);

        await Assert.That(emitted).IsTrue();
        await Assert.That(status).IsEqualTo(FB.BuildStatus.Success);

        // No Ethernet header — the IPv6 fixed header (40 bytes) starts at 0.
        // NextHeader byte is at offset 6 and must equal IpProtocols.Udp (17).
        await Assert.That(frame[6]).IsEqualTo((byte)IpProtocols.Udp);

        // IPv6 PayloadLength (offset 4, 2 bytes BE) = UDP header + payload.
        ushort payloadLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4, 2));
        await Assert.That(payloadLen).IsEqualTo((ushort)(8 + payloadCopy.Length));

        // Total frame length matches.
        await Assert.That(written).IsEqualTo(40 + 8 + payloadCopy.Length);
    }

    /// <summary>Synchronous emit wrapper — mirrors NewFrameStackSmokeTests.EmitOnce.</summary>
    private static (bool Emitted, int Length, FB.BuildStatus Status) EmitOnce<TStack, TTrailer, TInterceptor>(
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
}
