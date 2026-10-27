// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>
/// A finished capture on a session that starts with a stack the session does not own, for the restart tests. Every restart
/// onto a stack the factory builds makes the session own that stack. The rig disposes the session, the initial stack and the
/// source. Thread-safety: not applicable; a rig belongs to one test, its <see cref="Session"/> is shared with the test's
/// worker threads.
/// </summary>
internal sealed class RestartRig : IDisposable
{
    private readonly TestFrameSource _Source;

    /// <summary>
    /// Builds the rig and waits until the capture finished (all frames parsed, phase Stopped).
    /// </summary>
    /// <param name="frameCount">Frames of the capture.</param>
    /// <param name="options">Session options; <see langword="null"/> for the defaults.</param>
    /// <param name="wrapSource">Wraps the source before it is registered (a hole, a counter, a hook).</param>
    internal RestartRig(int frameCount, SessionOptions? options = null, Func<TestFrameSource, IFrameSource>? wrapSource = null)
    {
        FrameCount = frameCount;
        InitialStack = TestHarness.CreateStack();
        _Source = TestFrameSource.WithUdpFrames(frameCount);
        Session = new Session(InitialStack, options);
        IFrameSource registered = _Source;
        if (wrapSource is not null)
        {
            registered = wrapSource(_Source);
        }

        _ = Session.TryAddFrameSource(registered, out _, out _);
        _ = Session.TryStart(out _);
        Session.WaitForCompletion();
        WaitHelper.WaitUntil(() => Session.PacketCount == frameCount && Session.Phase == SessionPhase.Stopped);
    }

    /// <summary>The session under test.</summary>
    internal Session Session { get; }

    /// <summary>The stack the session was constructed with; the caller (this rig) owns it.</summary>
    internal Stack InitialStack { get; }

    /// <summary>Frames of the capture.</summary>
    internal int FrameCount { get; }

    /// <summary>Restarts onto a fresh standard stack and returns it.</summary>
    internal Stack RestartOntoStandard(RestartOptions? options = null)
    {
        Stack? created = null;
        _ = Session.RestartOrThrow(registry => created = TestHarness.CreateStack(registry), options);
        return created!; // Non-null: the factory ran, otherwise RestartOrThrow threw.
    }

    /// <summary>Restarts onto a fresh probe stack and returns it with its probe.</summary>
    internal (Stack Stack, ProbeProtocol Probe) RestartOntoProbe(RestartOptions? options = null)
    {
        Stack? created = null;
        ProbeProtocol? probe = null;
        _ = Session.RestartOrThrow(registry => created = ProbeStack.Create(registry, out probe), options);
        return (created!, probe!); // Non-null: the factory ran, otherwise RestartOrThrow threw.
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Errors of the shutdown are the subject of dedicated tests; a rig only has to clean up.
        Session.Dispose();
        InitialStack.Dispose();
        _Source.Dispose();
    }

    /// <summary>Starts <paramref name="work"/> on its own thread so a gate inside it cannot starve the thread pool.</summary>
    internal static Task<T> OnDedicatedThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
