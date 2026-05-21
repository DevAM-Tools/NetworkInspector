// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered frame source (e.g., a pcap file, live capture device).
///
/// <para>
/// <b>Stop lifecycle:</b>
/// A running source can be stopped via <see cref="Stop"/> without disposing it.
/// The source thread exits after the current frame, but the source remains available
/// for random access (<see cref="IRandomAccessFrameSource.FrameById"/>) and reparse.
/// Final disposal happens during session shutdown.
/// </para>
/// </summary>
public sealed class FrameSourceInfo(FrameSourceId id, IFrameSource? source)
{
    #region Fields
    // Callback set by the session to implement the Stop convenience API.
    // Invoked at most once; null-guarded for safety.
    private Action? _StopCallback;

    #endregion

    #region Methods

    /// <summary>
    /// Registers a callback that will be invoked when <see cref="Stop"/> is called.
    /// Typically wired by the session to unsubscribe the source's job.
    /// </summary>
    /// <param name="callback">The stop callback to register.</param>
    public void RegisterStopCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _StopCallback = callback;
    }

    #endregion

    #region Properties

    /// <summary>Unique frame source identifier.</summary>
    public FrameSourceId Id { get; } = id;

    /// <summary>
    /// The frame source instance that was registered.
    /// May be <c>null</c> for synthetic or test scenarios where no real source exists.
    /// </summary>
    public IFrameSource? Source { get; } = source;

    /// <summary>
    /// Human-readable display name.
    /// Delegated from <see cref="IFrameSource.UiName"/> if available, otherwise the string
    /// representation of the <see cref="Id"/>.
    /// </summary>
    public string UiName => Source?.UiName ?? Id.ToString();

    /// <summary>Optional description text (delegated from <see cref="IFrameSource.Description"/>).</summary>
    public string? Description => Source?.Description;

    /// <summary>
    /// Whether this source can be stopped via <see cref="Stop"/>.
    /// Returns <see langword="true"/> when the source is registered with a session
    /// that supports stopping individual sources.
    /// </summary>
    public bool IsStoppable => _StopCallback is not null;

    /// <summary>
    /// Convenience API: requests that the session stop this source's frame-reading loop.
    /// The source thread exits after the current frame. The source remains available
    /// for random access and reparse; final disposal happens during session shutdown.
    ///
    /// <para>
    /// Equivalent to calling <c>ISession.TryUnsubscribe</c> with this source's job.
    /// </para>
    /// </summary>
    public void Stop() => _StopCallback?.Invoke();

    #endregion
}
