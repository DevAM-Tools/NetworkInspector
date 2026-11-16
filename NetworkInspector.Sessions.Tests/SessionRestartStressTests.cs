// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Restarts under load: readers, a job adder, a listener with a filter, and a <see cref="ISession.Restarted"/> handler run
/// against repeated restarts. The test proves the properties that only exist in the interplay of the pieces: nothing throws,
/// the generation counts every restart, no retired stack is left, every handler saw every notification in order, and after the
/// last restart the whole capture reads on the last stack.
/// </summary>
[Category("Slow")]
internal sealed class SessionRestartStressTests
{
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task RepeatedRestartsUnderReadJobAndListenerLoadStayConsistent()
    {
        const int frames = 300;
        const int restarts = 20;
        using RestartRig rig = new(frameCount: frames);
        PacketFilter filter = SessionFixture.CompileOrThrow(rig.InitialStack, "udp.dstport == 53");
        TestSessionListener listener = new();
        _ = rig.Session.TryAddListener(listener, filter, out _, out _);

        List<int> notified = [];
        Action<RestartNotification> handler = notification =>
        {
            lock (notified)
            {
                notified.Add(notification.Generation);
            }
        };
        rig.Session.Restarted += handler;

        using CancellationTokenSource stop = new();
        int failures = 0;
        int successfulReads = 0;
        int jobsAdded = 0;

        // Readers: range reads and single reads. Any exception, or a packet of a stack that is not the one the call leased,
        // is a failure; a refused or shortened read (a restart overtook it) is fine.
        Task[] readers = new Task[3];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = RestartRig.OnDedicatedThread(() =>
            {
                Packet?[] buffer = new Packet?[64];
                try
                {
                    for (int i = seed; !stop.IsCancellationRequested; i++)
                    {
                        int filled = rig.Session.ReadPackets(0, buffer);
                        Stack? common = null;
                        for (int k = 0; k < filled; k++)
                        {
                            if (buffer[k] is Packet packet)
                            {
                                common ??= packet.Stack;
                                if (!ReferenceEquals(common, packet.Stack))
                                {
                                    _ = Interlocked.Increment(ref failures);
                                }
                            }
                        }

                        if (filled > 0)
                        {
                            _ = Interlocked.Increment(ref successfulReads);
                        }

                        _ = rig.Session.TryGetPacket(i % frames, out _, out _);
                    }
                }
                catch (Exception)
                {
                    _ = Interlocked.Increment(ref failures);
                }

                return 0;
            });
        }

        // Job adder: stack-bound jobs that end on their own or by the restart; refusals (RestartPending) are fine.
        Task adder = RestartRig.OnDedicatedThread(() =>
        {
            try
            {
                for (int i = 0; !stop.IsCancellationRequested; i++)
                {
                    if (rig.Session.TryAddJob(
                        "j" + i.ToString(CultureInfo.InvariantCulture),
                        "short job",
                        static token => token.WaitHandle.WaitOne(1),
                        out JobInfo? job,
                        out _))
                    {
                        _ = Interlocked.Increment(ref jobsAdded);
                        job!.Join(); // Non-null: the add succeeded.
                        _ = rig.Session.TryRemoveJob(job, out _);
                    }
                }
            }
            catch (Exception)
            {
                _ = Interlocked.Increment(ref failures);
            }

            return 0;
        });

        for (int i = 0; i < restarts; i++)
        {
            bool restarted = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out SessionFailure? failure);
            if (!restarted)
            {
                _ = Interlocked.Increment(ref failures);
                Console.Error.WriteLine("restart refused: " + failure?.Message);
            }
        }

        // One more full pass on the final stack before the load stops.
        int readsAtEnd = Volatile.Read(ref successfulReads);
        WaitHelper.WaitUntil(() => Volatile.Read(ref successfulReads) > readsAtEnd + 3, timeoutMs: 30_000);
        await stop.CancelAsync();
        await Task.WhenAll([.. readers, adder]).WaitAsync(_Timeout);
        WaitHelper.WaitUntil(
            () =>
            {
                lock (notified)
                {
                    return notified.Count == restarts;
                }
            },
            timeoutMs: 30_000);
        rig.Session.Restarted -= handler;

        Packet?[] finalRead = new Packet?[frames];
        int finalCount = rig.Session.ReadPackets(0, finalRead);
        Stack finalStack = rig.Session.CurrentStack.Stack;
        bool allOnFinalStack = finalRead.All(packet => packet is not null && ReferenceEquals(packet.Stack, finalStack));
        int[] order;
        lock (notified)
        {
            order = [.. notified];
        }

        await Assert.That(Volatile.Read(ref failures)).IsEqualTo(0);
        await Assert.That(rig.Session.Generation).IsEqualTo(restarts);
        await Assert.That(rig.Session.RetiredStackCount).IsEqualTo(0);
        await Assert.That(order.SequenceEqual(Enumerable.Range(1, restarts))).IsTrue();
        await Assert.That(finalCount).IsEqualTo(frames);
        await Assert.That(allOnFinalStack).IsTrue();
        await Assert.That(Volatile.Read(ref jobsAdded)).IsGreaterThan(0);
    }
}
