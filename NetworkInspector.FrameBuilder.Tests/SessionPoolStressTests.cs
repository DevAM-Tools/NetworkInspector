// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Multi-thread stress test for the lock-free
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/> pool.
/// </summary>
/// <remarks>
/// <para>
/// Verifies that concurrent rent-and-return cycles across more threads than
/// the pool capacity (8) do not corrupt session state or produce invalid wire
/// output. The pool is implemented as a fixed-size slot array reconciled with
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> /
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>; this test is the
/// explicit harness that guards against returns racing with concurrent rents.
/// </para>
/// <para>
/// Strategy: 16 threads (2× pool capacity) each rent a session, build one
/// small UDP frame, validate the IPv4 header bytes, then return the session.
/// They repeat for 200 iterations each, starting simultaneously via a
/// <see cref="CountdownEvent"/> barrier so contention is maximal.  Any frame
/// with wrong header bytes or build failure increments a shared failure counter.
/// </para>
/// </remarks>
[NotInParallel(nameof(SessionPoolStressTests))]
internal sealed class SessionPoolStressTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _SrcIp4 = IPv4Address.FromBytes([10, 0, 0, 1]);
    private static readonly IPv4Address _DstIp4 = IPv4Address.FromBytes([10, 0, 0, 2]);

    private const int _EthHeaderSize = 14;
    private const int _IPv4HeaderSize = 20;
    private const int _UdpHeaderSize = 8;

    [Test]
    public async Task SessionPool_ConcurrentRentAndReturn_ProducesValidFrames()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4LayerWithAutoIpId ip = new(_SrcIp4, _DstIp4, initialIdentification: 0, dontFragment: true);
        UdpLayer udp = new(srcPort: 1234, dstPort: 5678, checksum: Auto.Explicit((ushort)0));

        StatefulCreatedStack<
            Stack<UdpLayer,
                Stack<IPv4LayerWithAutoIpId,
                    StatelessStack<EthernetLayer, StackEnd>>>,
            NoTrailer,
            NoInterceptor> stack = StatefulFrameStack.CreateForSession(
                FrameStack.Start(eth).Then(ip).Then(udp));

        const int Threads = 16; // intentionally 2× pool capacity
        const int Iterations = 200;

        int failCount = 0;
        using CountdownEvent barrier = new(Threads);
        Thread[] threads = new Thread[Threads];

        for (int t = 0; t < Threads; t++)
        {
            threads[t] = new Thread(() =>
            {
                // All threads start simultaneously to maximise pool contention.
                barrier.Signal();
                barrier.Wait();

                byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
                int expectedFrameLen = _EthHeaderSize + _IPv4HeaderSize + _UdpHeaderSize + payload.Length;
                byte[] buf = new byte[expectedFrameLen];

                for (int i = 0; i < Iterations; i++)
                {
                    using Session<
                        Stack<UdpLayer,
                            Stack<IPv4LayerWithAutoIpId,
                                StatelessStack<EthernetLayer, StackEnd>>>,
                        NoTrailer,
                        NoInterceptor> session = stack.OpenSession();

                    StatefulFrameSequence<
                        Stack<UdpLayer,
                            Stack<IPv4LayerWithAutoIpId,
                                StatelessStack<EthernetLayer, StackEnd>>>,
                        NoTrailer,
                        NoInterceptor> seq = session.NextPacket(payload);

                    bool wrote = seq.MoveNext(buf, out int written);

                    // Validate: build must succeed and produce the expected frame size.
                    if (!wrote || written != expectedFrameLen || seq.Status != BuildStatus.Success)
                    {
                        Interlocked.Increment(ref failCount);
                        continue;
                    }

                    // Validate IP version + IHL byte (0x45 = version 4, header length 5).
                    if (buf[_EthHeaderSize] != 0x45)
                    {
                        Interlocked.Increment(ref failCount);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"SessionPool-Stress-{t}",
            };
        }

        foreach (Thread t in threads)
        {
            t.Start();
        }

        foreach (Thread t in threads)
        {
            // A 30-second join timeout is generous; the test should complete in < 1 s.
            // Assert the join succeeded — a false return indicates a hung thread.
            bool joined = t.Join(TimeSpan.FromSeconds(30));
            await Assert.That(joined).IsTrue();
        }

        await Assert.That(failCount).IsEqualTo(0);
    }
}
