// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for the M2.5 stateful layers and the
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/> API:
/// <see cref="IPv4LayerWithAutoIpId"/> and <see cref="TcpLayerWithAutoSequence"/>.
/// </summary>
/// <remarks>
/// Each test opens a session, emits multiple packets, and asserts that
/// per-frame state (IPv4 Identification, TCP sequence number) advances as
/// expected.  Pool reuse is exercised by the <c>Session_Pool_*</c> tests.
/// </remarks>
[NotInParallel(nameof(StatefulSessionSmokeTests))]
internal sealed class StatefulSessionSmokeTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _SrcIp4 = new(0xC0A80101);
    private static readonly IPv4Address _DstIp4 = new(0xC0A80102);

    private const int IPv4HeaderSize = 20;
    private const int TcpHeaderSize = 20;
    private const int EthHeaderSize = 14;
    private const int UdpHeaderSize = 8;

    /// <summary>Read the IPv4 Identification field from a frame whose IPv4 header starts at offset 14.</summary>
    private static ushort ReadIPv4Id(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(EthHeaderSize + 4, 2));

    /// <summary>Read the TCP sequence number from a frame whose TCP header starts at offset 14+20.</summary>
    private static uint ReadTcpSeq(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(EthHeaderSize + IPv4HeaderSize + 4, 4));

    /// <summary>Read the TCP ACK number from a frame.</summary>
    private static uint ReadTcpAck(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(EthHeaderSize + IPv4HeaderSize + 8, 4));

    #region IPv4LayerWithAutoIpId

    [Test]
    public async Task AutoIpId_IncrementsByOne_PerFrame()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 100);
        FB.UdpLayer udp = new(srcPort: 1234, dstPort: 5678);

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] payload = [0xDE, 0xAD];
        byte[] buffer = new byte[stack.HeaderSize + payload.Length];

        using Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        ushort[] ids = new ushort[3];
        for (int i = 0; i < 3; i++)
        {
            StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
                NoTrailer, NoInterceptor> seq = session.NextPacket(payload);
            bool wrote = seq.MoveNext(buffer, out int n);
            await Assert.That(wrote).IsTrue();
            await Assert.That(n).IsEqualTo(buffer.Length);
            ids[i] = ReadIPv4Id(buffer);
        }

        await Assert.That(ids[0]).IsEqualTo<ushort>(100);
        await Assert.That(ids[1]).IsEqualTo<ushort>(101);
        await Assert.That(ids[2]).IsEqualTo<ushort>(102);
    }

    [Test]
    public async Task AutoIpId_WrapsAround_AtUshortMax()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: ushort.MaxValue);
        FB.UdpLayer udp = new(1234, 5678);

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] buffer = new byte[stack.HeaderSize];
        using Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        // Frame 1: id = 0xFFFF
        StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> s1 = session.NextPacket(ReadOnlySpan<byte>.Empty);
        s1.MoveNext(buffer, out _);
        ushort id1 = ReadIPv4Id(buffer);

        // Frame 2: id wraps to 0
        StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> s2 = session.NextPacket(ReadOnlySpan<byte>.Empty);
        s2.MoveNext(buffer, out _);
        ushort id2 = ReadIPv4Id(buffer);

        await Assert.That(id1).IsEqualTo<ushort>(0xFFFF);
        await Assert.That(id2).IsEqualTo<ushort>(0);
    }

    [Test]
    public async Task AutoIpId_DistinctSessions_HaveIndependentCounters()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 42);
        FB.UdpLayer udp = new(1234, 5678);

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] buffer = new byte[stack.HeaderSize];

        using (Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> a = stack.OpenSession())
        {
            a.NextPacket(ReadOnlySpan<byte>.Empty).MoveNext(buffer, out _);
            a.NextPacket(ReadOnlySpan<byte>.Empty).MoveNext(buffer, out _);
            await Assert.That(ReadIPv4Id(buffer)).IsEqualTo<ushort>(43);
        }

        // Second session — must restart at the seed value.
        using (Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> b = stack.OpenSession())
        {
            b.NextPacket(ReadOnlySpan<byte>.Empty).MoveNext(buffer, out _);
            await Assert.That(ReadIPv4Id(buffer)).IsEqualTo<ushort>(42);
        }
    }

    #endregion

    #region TcpLayerWithAutoSequence

    [Test]
    public async Task AutoTcpSeq_AdvancesByPayloadLength_PerFrame()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.TcpLayerWithAutoSequence tcp = new(srcPort: 12345, dstPort: 80,
            initialSequence: 1000, initialAck: 5000,
            flags: TcpFlags.Ack);

        StatefulCreatedStack<
            Stack<TcpLayerWithAutoSequence,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(tcp));

        byte[] p1 = new byte[100];
        byte[] p2 = new byte[50];
        byte[] p3 = new byte[200];

        byte[] buf = new byte[stack.HeaderSize + 256];
        using Session<Stack<TcpLayerWithAutoSequence, StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        // Frame 1 — seq = 1000, advances by 100 → next 1100.
        session.NextPacket(p1).MoveNext(buf, out int n1);
        uint seq1 = ReadTcpSeq(buf.AsSpan(0, n1));
        uint ack1 = ReadTcpAck(buf.AsSpan(0, n1));

        // Frame 2 — seq = 1100, advances by 50 → next 1150.
        session.NextPacket(p2).MoveNext(buf, out int n2);
        uint seq2 = ReadTcpSeq(buf.AsSpan(0, n2));

        // Frame 3 — seq = 1150, advances by 200.
        session.NextPacket(p3).MoveNext(buf, out int n3);
        uint seq3 = ReadTcpSeq(buf.AsSpan(0, n3));

        await Assert.That(seq1).IsEqualTo(1000U);
        await Assert.That(ack1).IsEqualTo(5000U);
        await Assert.That(seq2).IsEqualTo(1100U);
        await Assert.That(seq3).IsEqualTo(1150U);
    }

    [Test]
    public async Task AutoTcpSeq_SynConsumesOneSequenceNumber()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(_SrcIp4, _DstIp4);
        FB.TcpLayerWithAutoSequence tcp = new(srcPort: 12345, dstPort: 80,
            initialSequence: 4242, flags: TcpFlags.Syn);

        StatefulCreatedStack<
            Stack<TcpLayerWithAutoSequence,
                StatelessStack<IPv4Layer,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(tcp));

        byte[] buf = new byte[stack.HeaderSize];
        using Session<Stack<TcpLayerWithAutoSequence, StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        // SYN frame — empty payload, but +1 advance.
        session.NextPacket(ReadOnlySpan<byte>.Empty).MoveNext(buf, out _);
        uint seq1 = ReadTcpSeq(buf);

        // Next SYN frame uses the advanced sequence (would be 4243).
        session.NextPacket(ReadOnlySpan<byte>.Empty).MoveNext(buf, out _);
        uint seq2 = ReadTcpSeq(buf);

        await Assert.That(seq1).IsEqualTo(4242U);
        await Assert.That(seq2).IsEqualTo(4243U);
    }

    #endregion

    #region Combined: auto-IPID + auto-TCP-Seq in one stack

    [Test]
    public async Task Combined_AutoIpId_And_AutoTcpSeq_BothAdvance()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 7);
        FB.TcpLayerWithAutoSequence tcp = new(srcPort: 1, dstPort: 2,
            initialSequence: 0, flags: TcpFlags.Ack);

        StatefulCreatedStack<
            Stack<TcpLayerWithAutoSequence,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(tcp));

        byte[] payload = new byte[10];
        byte[] buf = new byte[stack.HeaderSize + payload.Length];

        using Session<Stack<TcpLayerWithAutoSequence, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> session = stack.OpenSession();

        session.NextPacket(payload).MoveNext(buf, out int n1);
        ushort id1 = ReadIPv4Id(buf.AsSpan(0, n1));
        uint seq1 = ReadTcpSeq(buf.AsSpan(0, n1));

        session.NextPacket(payload).MoveNext(buf, out int n2);
        ushort id2 = ReadIPv4Id(buf.AsSpan(0, n2));
        uint seq2 = ReadTcpSeq(buf.AsSpan(0, n2));

        await Assert.That(id1).IsEqualTo<ushort>(7);
        await Assert.That(id2).IsEqualTo<ushort>(8);
        await Assert.That(seq1).IsEqualTo(0U);
        await Assert.That(seq2).IsEqualTo(10U);
    }

    #endregion

    #region Session pool

    [Test]
    public async Task Session_Pool_RecyclesInternalsAfterDispose()
    {
        // Verifies that the internals pool is operational: opening a second session
        // after disposing the first produces a correctly initialised session even
        // though the Session handle objects are distinct (handles are never pooled;
        // only the internal mutable state is pooled).
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 7);
        FB.UdpLayer udp = new(1, 2);

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        byte[] buf = new byte[stack.HeaderSize + 4];

        // Session A: advance the IP-ID counter beyond the seed.
        using (Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> a = stack.OpenSession())
        {
            a.NextPacket([0, 0, 0, 0]).MoveNext(buf, out _);
            a.NextPacket([0, 0, 0, 0]).MoveNext(buf, out _);
            // IP-ID is now 9 after two frames (7 → 8 → 9).
        }

        // Session B (may reuse internals from pool) must restart at the seed value,
        // not continue from A's counter — Reset initialises state from the stack
        // definition, not from the prior run.
        using Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, NoInterceptor> b = stack.OpenSession();
        b.NextPacket([0, 0, 0, 0]).MoveNext(buf, out int nb);
        ushort id = ReadIPv4Id(buf.AsSpan(0, nb));
        await Assert.That(id).IsEqualTo<ushort>(7);
    }

    [Test]
    public async Task Session_DisposedTwice_IsSafe()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(1, 2);

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, NoInterceptor> session = stack.OpenSession();
        session.Dispose();
        session.Dispose(); // must not throw — second Dispose is a no-op.
        // Reaching this line implies no exception was thrown.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Marker interceptor used to give the disposed-throws test its own
    /// generic instantiation — and therefore its own private pool, so it
    /// cannot race with other tests in this fixture.
    /// </summary>
    private readonly struct DisposedThrowsMarkerInterceptor : IFrameInterceptor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice) where TLayer : struct, IProtocolLayer
        {
        }
        public void OnFrameComplete(scoped Span<byte> frame)
        {
        }
    }

    [Test]
    public async Task Session_NextPacket_AfterDispose_Throws()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(1, 2);

        // Isolated pool via the unique interceptor instantiation.
        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            DisposedThrowsMarkerInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp),
                new DisposedThrowsMarkerInterceptor());

        Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer, DisposedThrowsMarkerInterceptor> session = stack.OpenSession();
        session.Dispose();

        bool threw = false;
        try
        {
            _ = session.NextPacket(ReadOnlySpan<byte>.Empty);
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    /// <summary>
    /// Marker interceptor used to isolate the stale-handle test pool from
    /// other pools so they do not interfere with each other.
    /// </summary>
    private readonly struct StaleHandleMarkerInterceptor : IFrameInterceptor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice) where TLayer : struct, IProtocolLayer
        {
        }

        public void OnFrameComplete(scoped Span<byte> frame)
        {
        }
    }

    /// <summary>
    /// Verifies that once disposed, a session reference throws on all public methods,
    /// and that a new caller who rents the pooled internals cannot reactivate the
    /// stale handle — the old reference must keep throwing even after the internals
    /// are re-rented by a second caller.
    /// </summary>
    [Test]
    public async Task Session_StaleHandle_AfterPoolReuse_Throws()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4);
        FB.UdpLayer udp = new(1, 2);

        // Use an isolated interceptor type so this test owns its own pool slot.
        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            StaleHandleMarkerInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp),
                new StaleHandleMarkerInterceptor());

        // Lease A: open and immediately dispose.
        Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, StaleHandleMarkerInterceptor> disposedA =
            stack.OpenSession();
        disposedA.Dispose();

        // After dispose, reference A must throw.
        bool threwBeforeReuse = false;
        try
        {
            _ = disposedA.NextPacket(ReadOnlySpan<byte>.Empty);
        }
        catch (ObjectDisposedException)
        {
            threwBeforeReuse = true;
        }
        await Assert.That(threwBeforeReuse).IsTrue();

        // Lease B: opens a new lease (may reuse the same pooled internals).
        using (Session<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, StaleHandleMarkerInterceptor> b =
            stack.OpenSession())
        {
            // B must be fully functional.
            byte[] buf = new byte[b.HeaderSize + 4];
            StatefulFrameSequence<Stack<UdpLayer, Stack<IPv4LayerWithAutoIpId, StatelessStack<EthernetLayer, StackEnd>>>,
                NoTrailer, StaleHandleMarkerInterceptor> seq =
                b.NextPacket([0x01, 0x02, 0x03, 0x04]);
            bool hasFrame = seq.MoveNext(buf, out int n);
            await Assert.That(hasFrame).IsTrue();
            await Assert.That(n).IsGreaterThan(0);

            // A must STILL throw even though the internals are now live again inside B.
            // This is the stale-handle regression: version mismatch or _Disposed flag
            // must prevent the old handle from becoming usable again.
            bool threwAfterReuse = false;
            try
            {
                _ = disposedA.NextPacket(ReadOnlySpan<byte>.Empty);
            }
            catch (ObjectDisposedException)
            {
                threwAfterReuse = true;
            }
            await Assert.That(threwAfterReuse).IsTrue();
        }
    }

    #endregion
}

