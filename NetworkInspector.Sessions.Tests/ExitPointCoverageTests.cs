// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FieldInfo = System.Reflection.FieldInfo;

namespace NetworkInspector.Sessions.Tests;

/// <summary>Exit-point coverage for session error paths.</summary>
[NotInParallel(nameof(ExitPointCoverageTests))]
internal sealed class ExitPointCoverageTests
{
    [Test]
    public async Task TryGetPacket_ReparseWithMismatchedStack_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        _GetPacketStore(session).Clear();

        Stack wrongStack = TestHarness.CreateStack();
        FieldInfo stackField = typeof(Session).GetField(
            "_Stack",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Stack originalStack = (Stack)stackField.GetValue(session)!;
        try
        {
            stackField.SetValue(session, wrongStack);

            bool found = session.TryGetPacket(new PacketId(0), out Packet? packet);

            await Assert.That(found).IsFalse();
            await Assert.That(packet).IsNull();
        }
        finally
        {
            stackField.SetValue(session, originalStack);
            wrongStack.Dispose();
        }
    }

    [Test]
    public async Task RunSourceLoop_PacketIdExhausted_FailsSourceJob()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(1);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);

        int maxPacketId = Array.MaxLength - 1;
        FieldInfo nextPacketIdField = typeof(Session).GetField(
            "_NextPacketId",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        nextPacketIdField.SetValue(session, maxPacketId + 1);

        session.TryStart();

        JobInfo sourceJob = session.GetJobs().First(j => j.UiName == source.UiName);
        WaitHelper.WaitUntil(() => sourceJob.Status == JobStatus.Failed);

        await Assert.That(sourceJob.FailureException).IsNotNull();
        await Assert.That(sourceJob.FailureException).IsTypeOf<SessionException>();
        SessionException sessionEx = (SessionException)sourceJob.FailureException!;
        await Assert.That(sessionEx.Code).IsEqualTo(SessionErrorCode.PacketIdExhausted);
        await Assert.That(sessionEx.Message)
            .Contains(maxPacketId.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task RunSourceLoop_WithoutPacketIndex_UsesNonIndexedParse()
    {
        using Stack stack = TestHarness.CreateStack();
        using BlockingTestFrameSource source = new(3);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();

        WaitHelper.WaitUntil(() => session.Phase == SessionPhase.Running);

        _SetPacketIndex(session, null);
        source.Release();

        WaitHelper.WaitUntil(() => session.PacketCount >= 3);

        await Assert.That(((ISessionReader)session).PacketIndex).IsNull();
        await Assert.That(session.PacketCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task AllocateListenerId_AtCapacity_ThrowsSessionException()
    {
        using Stack stack = TestHarness.CreateStack();
        using Session session = new(stack);

        FieldInfo stateField = typeof(Session).GetField(
            "_State",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        SessionState state = (SessionState)stateField.GetValue(session)!;
        FieldInfo nextListenerIdField = typeof(SessionState).GetField(
            "_NextListenerId",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        nextListenerIdField.SetValue(state, int.MinValue);

        TestSessionListener listener = new();

        try
        {
            session.TryAddListener(listener, out _);
            throw new InvalidOperationException("Expected SessionException was not thrown.");
        }
        catch (SessionException ex)
        {
            await Assert.That(ex.Code).IsEqualTo(SessionErrorCode.ListenerIdExhausted);
        }
    }

    [Test]
    public async Task ThrowMappingRecordFailed_InvalidPacketId_ThrowsInvalidOperationException()
    {
        try
        {
            Session.ThrowMappingRecordFailed(PacketId.Invalid);
            throw new InvalidOperationException("Expected InvalidOperationException was not thrown.");
        }
        catch (InvalidOperationException exception)
        {
            await Assert.That(exception.Message).Contains("invalid");
        }
    }

    private static void _SetPacketIndex(Session session, PacketIndex? index)
    {
        FieldInfo field = typeof(Session).GetField(
            "_PacketIndex",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(session, index);
    }

    private static PacketStore _GetPacketStore(Session session)
    {
        FieldInfo field = typeof(Session).GetField(
            "_PacketStore",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (PacketStore)field.GetValue(session)!;
    }
}
