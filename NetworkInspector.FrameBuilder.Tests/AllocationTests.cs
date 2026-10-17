// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

using UdpStack = FB.StatelessStack<FB.UdpLayer, FB.StatelessStack<FB.IPv4Layer, FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>;

/// <summary>
/// Proves the package claim "zero heap allocations on the hot path" for the
/// single-frame and the fragmenting path of <see cref="FB.FrameSequence{TStack,TTrailer,TInterceptor}"/>
/// and for packets of an open <see cref="FB.Session{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// Runs alone (<see cref="NotInParallelAttribute"/>): the allocation counter is
/// per thread, but TUnit may schedule other tests on the same worker thread
/// between the two counter reads, which would attribute their allocations to
/// this test.
/// </remarks>
[NotInParallel(nameof(AllocationTests))]
internal sealed class AllocationTests
{
    #region Fields

    private static readonly MacAddress _Dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _Src = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly IPv4Address _SrcIp = IPv4Address.FromBytes([10, 0, 0, 1]);
    private static readonly IPv4Address _DstIp = IPv4Address.FromBytes([10, 0, 0, 2]);

    #endregion

    #region Tests

    [Test]
    public async Task SingleFrameBuildDoesNotAllocateAfterWarmup()
    {
        FB.CreatedStack<UdpStack, FB.NoTrailer, FB.NoInterceptor> stack =
            FB.FrameStack.Start(new FB.EthernetLayer(_Dst, _Src))
                .Then(new FB.IPv4Layer(_SrcIp, _DstIp))
                .Then(new FB.UdpLayer(1, 2))
                .CreateWithFixedValues();
        byte[] payload = new byte[64];
        byte[] buffer = new byte[stack.HeaderSize + payload.Length];

        long allocated = _MeasureEmit(in stack, payload, buffer, warmup: 100, measured: 1000);

        await Assert.That(allocated).IsEqualTo(0L);
    }

    [Test]
    public async Task FragmentingBuildDoesNotAllocateAfterWarmup()
    {
        FB.CreatedStack<UdpStack, FB.NoTrailer, FB.NoInterceptor> stack =
            FB.FrameStack.Start(new FB.EthernetLayer(_Dst, _Src, maxFrameSize: 1500))
                .Then(new FB.IPv4Layer(_SrcIp, _DstIp, dontFragment: false))
                .Then(new FB.UdpLayer(1, 2, FB.Auto.Explicit((ushort)0)))
                .CreateWithFixedValues();
        byte[] payload = new byte[3000];
        byte[] buffer = new byte[1500];

        long allocated = _MeasureEmit(in stack, payload, buffer, warmup: 10, measured: 100);

        await Assert.That(allocated).IsEqualTo(0L);
    }

    [Test]
    public async Task StatefulSessionPacketDoesNotAllocateAfterWarmup()
    {
        // Stateful path: Stack<> walk, IStatefulLayer dispatch, per-packet SessionState
        // snapshot for rollback.  The session itself is opened once (setup).
        FB.StatefulCreatedStack<
            FB.Stack<FB.UdpLayer, FB.Stack<FB.IPv4LayerWithAutoIpId, FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> stack = FB.StatefulFrameStack.CreateForSession(
                FB.FrameStack.Start(new FB.EthernetLayer(_Dst, _Src))
                    .Then(new FB.IPv4LayerWithAutoIpId(_SrcIp, _DstIp, initialIdentification: 1))
                    .Then(new FB.UdpLayer(1, 2)));
        byte[] payload = new byte[64];
        byte[] buffer = new byte[stack.HeaderSize + payload.Length];
        using FB.Session<
            FB.Stack<FB.UdpLayer, FB.Stack<FB.IPv4LayerWithAutoIpId, FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.NoTrailer,
            FB.NoInterceptor> session = stack.OpenSession();

        long allocated = _MeasureSession(session, payload, buffer, warmup: 100, measured: 1000);

        await Assert.That(allocated).IsEqualTo(0L);
    }

    #endregion

    #region Helpers

    /// <summary>Same measurement as <see cref="_MeasureEmit"/> for packets of an open session.</summary>
    private static long _MeasureSession<TStack>(
        FB.Session<TStack, FB.NoTrailer, FB.NoInterceptor> session, byte[] payload, byte[] buffer, int warmup, int measured)
        where TStack : struct, FB.IStackNode
    {
        _EmitPackets(session, payload, buffer, warmup);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _EmitPackets(session, payload, buffer, measured);
        long after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    private static void _EmitPackets<TStack>(FB.Session<TStack, FB.NoTrailer, FB.NoInterceptor> session, byte[] payload, byte[] buffer, int count)
        where TStack : struct, FB.IStackNode
    {
        for (int i = 0; i < count; i++)
        {
            using FB.StatefulFrameSequence<TStack, FB.NoTrailer, FB.NoInterceptor> seq = session.NextPacket(payload);
            while (seq.MoveNext(buffer, out _))
            {
            }
        }
    }

    /// <summary>
    /// Emits <paramref name="warmup"/> builds (JIT tiering, first-call statics, pooled
    /// scratch growth), then returns the bytes the calling thread allocated during
    /// <paramref name="measured"/> further builds.  Synchronous on purpose: no await
    /// between the two counter reads, so the work stays on one thread.
    /// </summary>
    private static long _MeasureEmit(
        in FB.CreatedStack<UdpStack, FB.NoTrailer, FB.NoInterceptor> stack, byte[] payload, byte[] buffer, int warmup, int measured)
    {
        _Emit(in stack, payload, buffer, warmup);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _Emit(in stack, payload, buffer, measured);
        long after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    private static void _Emit<TStack>(
        in FB.CreatedStack<TStack, FB.NoTrailer, FB.NoInterceptor> stack, ReadOnlySpan<byte> payload, Span<byte> buffer, int count)
        where TStack : struct, FB.IStackNode, FB.IStatelessStack
    {
        for (int i = 0; i < count; i++)
        {
            using FB.FrameSequence<TStack, FB.NoTrailer, FB.NoInterceptor> seq = stack.Build(payload);
            while (seq.MoveNext(buffer, out _))
            {
            }
        }
    }

    #endregion
}
