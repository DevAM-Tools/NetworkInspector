// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Covers the reassembling protocols whose cross-packet buffers would be corrupted by a concurrent
/// re-parse: IPv4 and IPv6 fragmentation and SOME/IP-TP segmentation.
/// <para>
/// The decisive case is re-parsing the <i>completing</i> fragment on its own, the way a UI does when
/// the user selects that packet. Its reassembled payload exists only because the earlier fragments
/// were fed to the reassembler during the first parse. A protocol that re-fed the reassembler on
/// re-parse would find nothing buffered and silently drop the reassembled payload, so field identity
/// here is the proof that the recorded effect is replayed instead.
/// </para>
/// </summary>
internal sealed class ReparseReassemblyIdentityTests
{
    private const int _MtuFrameBytes = 1500;

    /// <summary>Ports outside every dispatch table, so the reassembled datagram stays raw UDP payload.</summary>
    private const ushort _UdpPort = 40000;

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static readonly IPv4Address _SrcIp4 = IPv4Address.FromBytes([10, 0, 0, 1]);
    private static readonly IPv4Address _DstIp4 = IPv4Address.FromBytes([10, 0, 0, 2]);

    private static readonly IPv6Address _SrcIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);

    private static readonly IPv6Address _DstIp6 = IPv6Address.FromBytes(
        [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);

    [Test]
    public async Task Ipv4Fragments_ReparseWholeSequence_Identical()
    {
        List<byte[]> fragments = _BuildIpv4Fragments();
        using Stack stack = _BuildStack();

        await _AssertSequenceReparseIdentical(stack, fragments);
    }

    /// <summary>
    /// Re-parsing only the completing fragment must still yield the reassembled payload, which is
    /// only possible from the recorded effect — the fragment buffers no longer hold the earlier
    /// fragments at that point.
    /// </summary>
    [Test]
    public async Task Ipv4Fragments_ReparseCompletingFragmentAlone_Identical()
    {
        List<byte[]> fragments = _BuildIpv4Fragments();
        using Stack stack = _BuildStack();
        Packet completingFirstParse = _ParseAll(stack, fragments)[^1];

        // Sanity: the first parse really did reassemble, otherwise the test proves nothing.
        await Assert.That(_HasUdpPayload(stack, completingFirstParse)).IsTrue();

        int lastId = fragments.Count - 1;
        Packet reparse = Packet.ParseFrame(
            new PacketId(lastId), stack, _CreateFrame(stack, fragments[lastId], lastId));

        await PacketFieldComparer.AssertFieldIdentical(stack, completingFirstParse, reparse);
    }

    /// <summary>
    /// Several threads re-parse the completing fragment at once. Any access to the shared fragment
    /// buffers on this path would race; replaying the recorded effect does not touch them.
    /// </summary>
    [Test]
    public async Task Ipv4Fragments_ConcurrentReparseOfCompletingFragment_Identical()
    {
        const int readers = 4;
        List<byte[]> fragments = _BuildIpv4Fragments();
        using Stack stack = _BuildStack();
        int lastId = fragments.Count - 1;
        Packet completingFirstParse = _ParseAll(stack, fragments)[^1];
        PacketFieldComparer.PacketFieldSnapshot reference = PacketFieldComparer.CaptureFields(completingFirstParse);

        await Parallel.ForAsync(0, readers, async (_, _) =>
        {
            Packet reparse = Packet.ParseFrame(
                new PacketId(lastId), stack, _CreateFrame(stack, fragments[lastId], lastId));
            await PacketFieldComparer.AssertMatchesSnapshot(stack, reference, reparse);
        });
    }

    [Test]
    public async Task Ipv6Fragments_ReparseWholeSequence_Identical()
    {
        List<byte[]> fragments = _BuildIpv6Fragments();
        using Stack stack = _BuildStack();

        await _AssertSequenceReparseIdentical(stack, fragments);
    }

    [Test]
    public async Task Ipv6Fragments_ReparseCompletingFragmentAlone_Identical()
    {
        List<byte[]> fragments = _BuildIpv6Fragments();
        using Stack stack = _BuildStack();
        Packet completingFirstParse = _ParseAll(stack, fragments)[^1];

        await Assert.That(_HasUdpPayload(stack, completingFirstParse)).IsTrue();

        int lastId = fragments.Count - 1;
        Packet reparse = Packet.ParseFrame(
            new PacketId(lastId), stack, _CreateFrame(stack, fragments[lastId], lastId));

        await PacketFieldComparer.AssertFieldIdentical(stack, completingFirstParse, reparse);
    }

    [Test]
    public async Task SomeIpTpSegments_ReparseWholeSequence_Identical()
    {
        List<byte[]> segments = _BuildSomeIpTpSegments();
        using Stack stack = _BuildStack();

        await _AssertSequenceReparseIdentical(stack, segments);
    }

    /// <summary>
    /// The SOME/IP-TP counterpart: the last segment carries the reassembled payload only because the
    /// earlier segments were added to the reassembler during their first parse.
    /// </summary>
    [Test]
    public async Task SomeIpTpSegments_ReparseLastSegmentAlone_Identical()
    {
        List<byte[]> segments = _BuildSomeIpTpSegments();
        using Stack stack = _BuildStack();
        Packet lastFirstParse = _ParseAll(stack, segments)[^1];

        // Sanity: the reassembled payload must be present on the last segment.
        FieldId payloadFieldId = stack.GetFieldId("someip.payload")!.Value;
        await Assert.That(lastFirstParse.TryGetFieldValue(payloadFieldId, out _, materialize: true)).IsTrue();

        int lastId = segments.Count - 1;
        Packet reparse = Packet.ParseFrame(
            new PacketId(lastId), stack, _CreateFrame(stack, segments[lastId], lastId));

        await PacketFieldComparer.AssertFieldIdentical(stack, lastFirstParse, reparse);
    }

    /// <summary>Parses every frame once in order, mimicking the ordered first parse of a capture.</summary>
    private static Packet[] _ParseAll(Stack stack, List<byte[]> frames)
    {
        Packet[] parsed = new Packet[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            parsed[i] = Packet.ParseFrame(new PacketId(i), stack, _CreateFrame(stack, frames[i], i));
        }

        return parsed;
    }

    private static async Task _AssertSequenceReparseIdentical(Stack stack, List<byte[]> frames)
    {
        Packet[] firstParsed = _ParseAll(stack, frames);

        for (int i = 0; i < frames.Count; i++)
        {
            Packet reparse = Packet.ParseFrame(new PacketId(i), stack, _CreateFrame(stack, frames[i], i));
            await PacketFieldComparer.AssertFieldIdentical(stack, firstParsed[i], reparse);
        }
    }

    /// <summary>True when the packet carries a UDP payload, i.e. the reassembled datagram was dispatched.</summary>
    private static bool _HasUdpPayload(Stack stack, Packet packet)
    {
        FieldId payloadFieldId = stack.GetFieldId("udp.payload")!.Value;
        return packet.TryGetFieldValue(payloadFieldId, out _, materialize: true);
    }

    /// <summary>Eth / IPv4 / UDP with a 3000-byte payload — three IPv4 fragments at MTU 1500.</summary>
    private static List<byte[]> _BuildIpv4Fragments()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        UdpLayer udp = new(_UdpPort, _UdpPort, Auto.Explicit((ushort)0));

        CreatedStack<
            StatelessStack<UdpLayer,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues();

        return _CollectFrames(in stack, _Payload(3000));
    }

    /// <summary>Eth / IPv6 / Fragment ext / UDP with a 3000-byte payload — three IPv6 fragments.</summary>
    private static List<byte[]> _BuildIpv6Fragments()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        IPv6Layer ip = new(_SrcIp6, _DstIp6);
        IPv6FragmentExtensionLayer frag = new(identification: 0xCAFEBABE);
        UdpLayer udp = new(_UdpPort, _UdpPort);

        CreatedStack<
            StatelessStack<UdpLayer,
                StatelessStack<IPv6FragmentExtensionLayer,
                    StatelessStack<IPv6Layer,
                        StatelessStack<EthernetLayer, StackEnd>>>>,
            NoTrailer,
            NoInterceptor> stack = FrameStack.Start(eth).Then(ip).Then(frag).Then(udp).CreateWithFixedValues();

        return _CollectFrames(in stack, _Payload(3000));
    }

    /// <summary>
    /// Eth / IPv4 / UDP / SOME/IP-TP with a 3000-byte payload. Application segmentation emits three
    /// self-contained datagrams, each a complete SOME/IP-TP segment.
    /// </summary>
    private static List<byte[]> _BuildSomeIpTpSegments()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: _MtuFrameBytes);
        IPv4Layer ip = new(_SrcIp4, _DstIp4, dontFragment: false);
        const ushort someIpPort = (ushort)SomeIpProtocol.UdpPortKey;
        UdpLayer udp = new(someIpPort, someIpPort, Auto.Explicit((ushort)0));
        SomeIpTpLayer someIpTp = new(serviceId: 0x0123, methodId: 0x4567, sessionId: 1);

        CreatedStack<
            StatelessStack<SomeIpTpLayer,
                StatelessStack<UdpLayer,
                    StatelessStack<IPv4Layer,
                        StatelessStack<EthernetLayer, StackEnd>>>>,
            NoTrailer,
            NoInterceptor> stack = FrameStack.Start(eth).Then(ip).Then(udp).Then(someIpTp).CreateWithFixedValues();

        return _CollectFrames(in stack, _Payload(3000));
    }

    private static byte[] _Payload(int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        return payload;
    }

    /// <summary>Drains a frame sequence into one byte array per emitted fragment or segment.</summary>
    private static List<byte[]> _CollectFrames<TStack, TTrailer, TInterceptor>(
        in CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload)
        where TStack : struct, IStackNode, IStatelessStack
        where TTrailer : struct, ITrailerLayer
        where TInterceptor : struct, IFrameInterceptor
    {
        FrameSequence<TStack, TTrailer, TInterceptor> sequence = created.Build(payload);
        List<byte[]> frames = [];
        byte[] scratch = new byte[_MtuFrameBytes];
        while (sequence.MoveNext(scratch, out int written))
        {
            frames.Add(scratch.AsSpan(0, written).ToArray());
        }

        // A silently empty or single-frame sequence would make the reassembly tests pass without ever
        // reaching the reassembler, so the geometry is asserted here once for all callers.
        if (sequence.Status != BuildStatus.Success || frames.Count < 2)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Frame build produced {frames.Count} frame(s) with status {sequence.Status}; expected a multi-frame Success."));
        }

        return frames;
    }

    private static Stack _BuildStack()
    {
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _CreateFrame(Stack stack, byte[] frameData, int index) =>
        Frame.Create(
            new FrameId(index),
            Timestamp.FromSecs(index),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
}
