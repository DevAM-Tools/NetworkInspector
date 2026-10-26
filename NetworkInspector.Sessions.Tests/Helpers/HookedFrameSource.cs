// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>
/// Random-access source whose <see cref="UiName"/> getter and <see cref="Start"/> run test callbacks on the calling
/// thread. The session reads the name while it registers the source, listener-style registrations read it while they are
/// half done, and the source thread calls <see cref="Start"/> first. A test uses the callbacks as a deterministic seam: it
/// triggers another session call (TryStart, Shutdown, TryUnsubscribe) at that exact point, without sleeps.
/// </summary>
internal sealed class HookedFrameSource : IRandomAccessFrameSource
{
    private readonly TestFrameSource _Inner;
    private int _NameReads;

    internal HookedFrameSource(TestFrameSource inner) => _Inner = inner;

    /// <summary>Called with the 1-based count of <see cref="UiName"/> reads, on the reading thread. Set before use.</summary>
    internal Action<int>? OnNameRead { get; set; }

    /// <summary>Called on the source thread inside <see cref="Start"/>, after the inner source started. Set before use.</summary>
    internal Action? OnStart { get; set; }

    /// <summary>Display name reported to the session; fixed so two instances can be told apart.</summary>
    internal string Name { get; init; } = "HookedSource";

    public string UiName
    {
        get
        {
            int reads = Interlocked.Increment(ref _NameReads);
            OnNameRead?.Invoke(reads);
            return Name;
        }
    }

    public string? Description => _Inner.Description;

    public int? EstimatedFrameCount => _Inner.EstimatedFrameCount;

    public bool IsRunning => _Inner.IsRunning;

    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _Inner.Start(sourceId, registry);
        OnStart?.Invoke();
    }

    public Frame? NextFrame(CancellationToken cancellationToken = default) => _Inner.NextFrame(cancellationToken);

    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default) =>
        _Inner.FrameById(id, cancellationToken);

    public void Dispose() => _Inner.Dispose();
}
