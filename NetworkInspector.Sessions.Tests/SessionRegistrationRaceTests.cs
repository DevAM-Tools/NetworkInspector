// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Races between the registration members (<c>TryAddFrameSource</c>, <c>TryAddListener</c>, <c>TryAddValueCache</c>,
/// <c>TryAddJob</c>, <c>TryUnsubscribe</c>) and <c>TryStart</c> / <c>Shutdown</c>. Where a user callback runs inside the
/// racy window (a <c>UiName</c> getter, <c>IFrameSource.Start</c>), the test fires the competing call from that callback so
/// the interleaving is fixed instead of timing dependent. Where no callback exists, the test releases all contenders
/// together with a barrier and checks an invariant that must hold under every interleaving.
/// </summary>
internal sealed class SessionRegistrationRaceTests
{
    #region Helpers

    /// <summary>Listener whose <c>UiName</c> getter runs a callback with the 1-based read count.</summary>
    private sealed class NameHookListener(Action<int> onNameRead) : ISessionListener
    {
        private int _NameReads;
        private int _Unsubscribed;

        public string UiName
        {
            get
            {
                onNameRead(Interlocked.Increment(ref _NameReads));
                return "HookListener";
            }
        }

        internal int UnsubscribedCount => Volatile.Read(ref _Unsubscribed);

        public void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive)
        {
        }

        public void OnUnsubscribed() => Interlocked.Increment(ref _Unsubscribed);
    }

    /// <summary>Value-cache listener whose <c>UiName</c> getter runs a callback with the 1-based read count.</summary>
    private sealed class NameHookCacheListener(Action<int> onNameRead) : IValueCacheListener
    {
        private int _NameReads;

        public string UiName
        {
            get
            {
                onNameRead(Interlocked.Increment(ref _NameReads));
                return "HookCacheListener";
            }
        }

        public void OnNewRows(ISessionReader session, ReadOnlyValueCache cache, int fromIndex, int toIndexExclusive)
        {
        }
    }

    #endregion

    #region Random-access table writers

    [Test]
    public async Task ConcurrentAddsOfRandomAccessSourcesKeepEveryEntry()
    {
        // Interleaving: two adders both read the same table, each builds a copy with its own source, the later store wins
        // and the other source is lost from the table (its packets would be unreadable). There is no callback inside
        // that window, so many rounds with all adders released together stand in for a fixed interleaving.
        const int rounds = 20;
        const int adders = 8;
        int lostEntries = 0;
        for (int round = 0; round < rounds; round++)
        {
            using Stack stack = TestHarness.CreateStack();
            using Session session = new(stack);
            ConcurrencyHelper.RunTogether(adders, adder =>
            {
                TestFrameSource source = TestFrameSource.WithUdpFrames(1);
                if (!session.TryAddFrameSource(source, out _, out _))
                {
                    throw new InvalidOperationException("The source was refused in the Idle phase.");
                }
            });

            lostEntries += adders - TestHarness.RandomAccessSourceCountOf(session);
        }

        await Assert.That(lostEntries).IsEqualTo(0);
    }

    #endregion

    #region TryStart

    [Test]
    public async Task ConcurrentTryStartSucceedsExactlyOnce()
    {
        // Interleaving: two callers pass the Idle check before either published Running; both then run the start
        // sequence, which resets the active-source count a second time and never reaches Stopped.
        const int callers = 8;
        const int framesPerSource = 3;
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        session.TryAddFrameSource(TestFrameSource.WithUdpFrames(framesPerSource), out _, out _);
        session.TryAddFrameSource(TestFrameSource.WithUdpFrames(framesPerSource), out _, out _);
        int successes = 0;
        int invalidPhaseRefusals = 0;

        ConcurrencyHelper.RunTogether(callers, caller =>
        {
            if (session.TryStart(out SessionFailure? failure))
            {
                _ = Interlocked.Increment(ref successes);
            }
            else if (failure.Value.Code == SessionErrorCode.InvalidPhase)
            {
                _ = Interlocked.Increment(ref invalidPhaseRefusals);
            }
        });

        session.WaitForCompletion();
        WaitHelper.WaitUntil(() => session.Phase == SessionPhase.Stopped);

        await Assert.That(successes).IsEqualTo(1);
        await Assert.That(invalidPhaseRefusals).IsEqualTo(callers - 1);
        await Assert.That(session.PacketCount).IsEqualTo(2 * framesPerSource);
        await Assert.That(session.MorePacketsExpected).IsFalse();
    }

    [Test]
    public async Task TryAddFrameSourceThatLosesTheRaceWithTryStartIsRefused()
    {
        // Interleaving: the phase check passed (Idle), then TryStart runs to completion while the add is still reading the
        // source name; the add must not slip a never-started source into the started session.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        HookedFrameSource source = new(TestFrameSource.WithUdpFrames(1));
        bool startSucceeded = false;
        source.OnNameRead = reads =>
        {
            if (reads == 1)
            {
                startSucceeded = session.TryStart(out _);
            }
        };

        bool added = session.TryAddFrameSource(source, out FrameSourceInfo? info, out SessionFailure? failure);

        await Assert.That(startSucceeded).IsTrue();
        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
        await Assert.That(session.GetFrameSources().Count).IsEqualTo(0);
        await Assert.That(session.GetJobs().Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnsubscribingASourceWhoseStartWasPublishedCancelsItInsteadOfDisposingIt()
    {
        // Interleaving: TryStart published the job array and the phase, but has not yet started the second job. Source A
        // (already running) unsubscribes source B from its own thread in that window. B is still Pending there; removing
        // and disposing it would drop its random-access entry although TryStart goes on to start it. Many rounds, because
        // the window sits between two thread starts inside TryStart and cannot be pinned without a hook in the product.
        const int rounds = 30;
        int removedSources = 0;
        for (int round = 0; round < rounds; round++)
        {
            using Stack stack = TestHarness.CreateStack();
            using Session session = new(stack);
            using ManualResetEventSlim unsubscribed = new(false);
            HookedFrameSource first = new(TestFrameSource.WithUdpFrames(2)) { Name = "First" };
            HookedFrameSource second = new(TestFrameSource.WithUdpFrames(2)) { Name = "Second" };
            session.TryAddFrameSource(first, out _, out _);
            session.TryAddFrameSource(second, out _, out _);
            JobInfo secondJob = session.GetJobs().First(job => job.UiName == "Second");
            first.OnStart = () =>
            {
                _ = session.TryUnsubscribe(secondJob, out _);
                unsubscribed.Set();
            };

            session.TryStart(out _);
            await Assert.That(unsubscribed.Wait(TimeSpan.FromSeconds(30))).IsTrue();
            session.WaitForCompletion();
            WaitHelper.WaitUntil(() => session.Phase == SessionPhase.Stopped);

            removedSources += 2 - session.GetFrameSources().Count;
            await Assert.That(TestHarness.RandomAccessSourceCountOf(session)).IsEqualTo(2);
            await Assert.That(secondJob.Status is JobStatus.Cancelled or JobStatus.Completed).IsTrue();
        }

        await Assert.That(removedSources).IsEqualTo(0);
    }

    #endregion

    #region Listener registration

    [Test]
    public async Task ListenerJobIsNotVisibleBeforeItsPublicViewIsLinked()
    {
        // Interleaving: the slot is already in the job list while TryAddListener still builds the public ListenerInfo
        // (it reads UiName there). A TryUnsubscribe on that job, finding no linked ListenerInfo, would treat the slot as a
        // user job. The job must not be visible until the registration is complete.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        bool visibleEarly = false;
        NameHookListener listener = new(_ =>
        {
            if (session.GetJobs().Any(job => job.UiName == "HookListener"))
            {
                visibleEarly = true;
            }
        });

        bool added = session.TryAddListener(listener, out ListenerInfo? info, out _);
        bool visibleAfter = session.GetJobs().Any(job => job.UiName == "HookListener");

        await Assert.That(added).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(visibleEarly).IsFalse();
        await Assert.That(visibleAfter).IsTrue();
    }

    [Test]
    public async Task TryAddListenerThatLosesTheRaceWithShutdownIsRefusedAndLeavesNothingBehind()
    {
        // Interleaving: the shutdown-phase check passed, then Shutdown runs to completion while the add builds its slot;
        // the add must not register (and start) a slot nobody will cancel or join any more.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        NameHookListener listener = new(reads =>
        {
            // Read 1 is the validation read, before the phase check; read 2 is inside the slot construction.
            if (reads == 2)
            {
                session.Shutdown();
            }
        });

        bool added = session.TryAddListener(listener, out ListenerInfo? info, out SessionFailure? failure);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
        await Assert.That(session.GetListeners().Count).IsEqualTo(0);
        await Assert.That(session.GetJobs().Count).IsEqualTo(0);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryAddListenerWhileTryStartRunsEndsUpStartedAndNotified()
    {
        // Interleaving: TryStart runs while the listener slot is being built (the phase check saw Idle). Whether TryStart's
        // slot snapshot includes the slot or the add starts it itself, the listener must end up running and notified.
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        NameHookListener listener = new(reads =>
        {
            if (reads == 2)
            {
                session.TryStart(out _);
            }
        });

        bool added = session.TryAddListener(listener, out ListenerInfo? info, out _);

        await Assert.That(added).IsTrue();
        JobInfo listenerJob = session.GetJobs().First(job => job.UiName == "HookListener");
        WaitHelper.WaitUntil(() => listenerJob.Status == JobStatus.Running);
        session.Shutdown();
        await Assert.That(listenerJob.Status).IsEqualTo(JobStatus.Cancelled);
        await Assert.That(listener.UnsubscribedCount).IsEqualTo(1);
        await Assert.That(info!.Status).IsEqualTo(SubscriptionStatus.SessionEnded);
    }

    #endregion

    #region Value-cache registration

    [Test]
    public async Task TryAddValueCacheThatLosesTheRaceWithShutdownIsRefusedAndLeavesNothingBehind()
    {
        // Same interleaving as for listeners: Shutdown completes while the add builds its slot (UiName read 2).
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        NameHookCacheListener listener = new(reads =>
        {
            if (reads == 2)
            {
                session.Shutdown();
            }
        });

        bool added = session.TryAddValueCache(
            listener,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            out ValueCacheInfo? info,
            out SessionFailure? failure);

        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.InvalidPhase);
        await Assert.That(session.GetValueCaches().Count).IsEqualTo(0);
        await Assert.That(session.GetJobs().Count).IsEqualTo(0);
    }

    #endregion

    #region User jobs

    [Test]
    public async Task EveryJobAcceptedWhileShutdownRunsIsCancelledAndJoined()
    {
        // Interleaving: adders and Shutdown overlap. A job is either refused or visible to the shutdown snapshot; an
        // accepted job that shutdown missed would stay Running forever. Also covers the cancel-and-join of user jobs.
        const int adders = 4;
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);
        List<JobInfo> accepted = [];
        using ManualResetEventSlim firstAccepted = new(false);

        Thread shutdownThread = new(() =>
        {
            firstAccepted.Wait(TimeSpan.FromSeconds(30));
            session.Shutdown();
        })
        {
            IsBackground = true,
        };
        shutdownThread.Start();

        ConcurrencyHelper.RunTogether(adders, adder =>
        {
            for (int i = 0; i < 25; i++)
            {
                bool added = session.TryAddJob(
                    "RaceJob",
                    "waits for cancellation",
                    ct =>
                    {
                        _ = ct.WaitHandle.WaitOne();
                        ct.ThrowIfCancellationRequested();
                    },
                    out JobInfo? info,
                    out _);
                if (!added)
                {
                    return;
                }

                lock (accepted)
                {
                    accepted.Add(info!);
                }

                firstAccepted.Set();
            }
        });

        shutdownThread.Join();

        await Assert.That(accepted.Count).IsGreaterThan(0);
        foreach (JobInfo job in accepted)
        {
            await Assert.That(job.Status).IsEqualTo(JobStatus.Cancelled);
        }
    }

    #endregion
}
