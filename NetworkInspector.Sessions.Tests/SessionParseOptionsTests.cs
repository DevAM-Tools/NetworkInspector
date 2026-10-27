// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// <c>TryGetPacket(id, in options, builtFor, recycle, ...)</c>: one packet parsed with the caller's observers under the
/// session's own stack lease, with options built for another generation refused by name. Every interleaving is forced with
/// gates; there are no sleeps. The only waits are bounded by <see cref="_Timeout"/>.
/// </summary>
internal sealed class SessionParseOptionsTests
{
    #region Fixtures

    /// <summary>Upper bound of every wait on a gate or a thread; far above any healthy run.</summary>
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Observer options that watch <c>ip.ttl</c> on the stack of <paramref name="view"/>.</summary>
    private static ParseOptions _TtlOptions(StackView view, RecordingFieldObserver observer, FieldTreeMode mode = FieldTreeMode.Skip)
    {
        FieldId ttl = view.Stack.GetFieldId("ip.ttl")!.Value; // Non-null: the standard stack defines it.
        ParseObserver[] observers = [new ParseObserver { Observer = observer, Watch = FieldWatch.Only(view.Stack, ttl) }];
        return new ParseOptions(mode, observers: observers);
    }

    #endregion

    #region Generation check

    [Test]
    public async Task OptionsBuiltForTheOldViewAreRefusedByNameOfBothGenerationsAndNothingIsObserved()
    {
        using RestartRig rig = new(frameCount: 6);
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions stale = _TtlOptions(view, observer);
        _ = rig.RestartOntoStandard();

        bool refused = rig.Session.TryGetPacket(new PacketId(3), in stale, view, null, out Packet? packet, out SessionFailure? failure);

        StackView fresh = rig.Session.CurrentStack;
        RecordingFieldObserver freshObserver = new();
        ParseOptions rebuilt = _TtlOptions(fresh, freshObserver);
        bool accepted = rig.Session.TryGetPacket(new PacketId(3), in rebuilt, fresh, null, out _, out _);

        await Assert.That(refused).IsFalse();
        await Assert.That(packet).IsNull();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.OptionsStackMismatch);
        await Assert.That(failure.Value.Message).Contains("generation 0");
        await Assert.That(failure.Value.Message).Contains("generation 1");
        await Assert.That(observer.BeganPacket).IsEqualTo(0);
        await Assert.That(accepted).IsTrue();
        await Assert.That(freshObserver.BeganPacket).IsEqualTo(1);
    }

    [Test]
    public async Task ARestartInProgressAnswersQueriesDisabled()
    {
        using RestartRig rig = new(frameCount: 6);
        using ManualResetEventSlim parseEntered = new(false);
        using ManualResetEventSlim parseRelease = new(false);
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions options = _TtlOptions(view, observer);

        Task<bool> restart = RestartRig.OnDedicatedThread(() => rig.Session.TryRestart(
            registry =>
            {
                Stack stack = ProbeStack.Create(registry, out ProbeProtocol probe);
                probe.SetParseHook(() =>
                {
                    parseEntered.Set();
                    _ = parseRelease.Wait(_Timeout);
                });
                return stack;
            },
            out _,
            out _));
        await Assert.That(parseEntered.Wait(_Timeout)).IsTrue();

        bool during = rig.Session.TryGetPacket(new PacketId(0), in options, view, null, out _, out SessionFailure? failure);
        parseRelease.Set();
        bool restarted = await restart.WaitAsync(_Timeout);

        await Assert.That(during).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.QueriesDisabled);
        await Assert.That(restarted).IsTrue();
    }

    [Test]
    public async Task AViewOfAnotherSessionIsRefusedEvenWithTheSameGenerationNumber()
    {
        using RestartRig rig = new(frameCount: 4);
        using Stack foreign = TestHarness.CreateStack();
        StackView foreignView = new(foreign, 0);
        RecordingFieldObserver observer = new();
        ParseOptions options = _TtlOptions(foreignView, observer);

        bool parsed = rig.Session.TryGetPacket(new PacketId(1), in options, foreignView, null, out _, out SessionFailure? failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.OptionsStackMismatch);
        await Assert.That(observer.BeganPacket).IsEqualTo(0);
    }

    #endregion

    #region Observed parse

    [Test]
    public async Task AFreshParseDeliversTheObservedFieldHonorsSkipAndAcceptsARecyclePacket()
    {
        using RestartRig rig = new(frameCount: 6);
        StackView view = rig.Session.CurrentStack;
        FieldId ttl = view.Stack.GetFieldId("ip.ttl")!.Value; // Non-null: the standard stack defines it.
        RecordingFieldObserver first = new();
        ParseOptions skip = _TtlOptions(view, first);

        bool parsed = rig.Session.TryGetPacket(new PacketId(3), in skip, view, null, out Packet? packet, out SessionFailure? failure);

        RecordingFieldObserver second = new();
        ParseOptions again = _TtlOptions(view, second);
        bool recycled = rig.Session.TryGetPacket(new PacketId(4), in again, view, packet, out Packet? sameInstance, out _);

        RecordingFieldObserver built = new();
        ParseOptions build = _TtlOptions(view, built, FieldTreeMode.Build);
        bool withTree = rig.Session.TryGetPacket(new PacketId(2), in build, view, null, out Packet? treePacket, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(first.BeganPacket).IsEqualTo(1);
        await Assert.That(first.Fields.Count).IsEqualTo(1);
        await Assert.That(first.Fields[0]).IsEqualTo(ttl);
        await Assert.That(packet!.HasFieldTree).IsFalse();
        await Assert.That(recycled).IsTrue();
        await Assert.That(ReferenceEquals(sameInstance, packet)).IsTrue();
        await Assert.That(sameInstance!.Id.Value).IsEqualTo(4);
        await Assert.That(second.Fields.Count).IsEqualTo(1);
        await Assert.That(withTree).IsTrue();
        await Assert.That(treePacket!.HasFieldTree).IsTrue();
        await Assert.That(built.Fields.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnIndexOfAnotherStackInsideTheOptionsIsAnswerAsOptionsStackMismatchInsteadOfThrowing()
    {
        using RestartRig rig = new(frameCount: 4);
        StackView view = rig.Session.CurrentStack;
        using Stack foreign = TestHarness.CreateStack();
        ParseOptions options = new(FieldTreeMode.Skip, index: new PacketIndex(foreign));

        bool parsed = rig.Session.TryGetPacket(new PacketId(1), in options, view, null, out _, out SessionFailure? failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(failure!.Value.Code).IsEqualTo(SessionErrorCode.OptionsStackMismatch);
    }

    [Test]
    public async Task AnUnannouncedIdAndAFaultedSessionAreRefusedWithTheirOwnCodes()
    {
        using RestartRig rig = new(frameCount: 4, wrapSource: static inner => new HoleRandomAccessSource(inner, holeFrameIndex: 2));
        StackView view = rig.Session.CurrentStack;
        RecordingFieldObserver observer = new();
        ParseOptions options = _TtlOptions(view, observer);

        bool unannounced = rig.Session.TryGetPacket(new PacketId(99), in options, view, null, out _, out SessionFailure? notFound);
        _ = rig.Session.TryRestart(registry => TestHarness.CreateStack(registry), out _, out _);
        bool faulted = rig.Session.TryGetPacket(new PacketId(0), in options, view, null, out _, out SessionFailure? fault);

        await Assert.That(unannounced).IsFalse();
        await Assert.That(notFound!.Value.Code).IsEqualTo(SessionErrorCode.PacketNotFound);
        await Assert.That(faulted).IsFalse();
        await Assert.That(fault!.Value.Code).IsEqualTo(SessionErrorCode.SessionFaulted);
    }

    #endregion
}
