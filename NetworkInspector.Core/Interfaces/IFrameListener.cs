// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Receives notification when a frame is captured.
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnFrame"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving an exporter from multiple threads.
/// </para>
/// </summary>
public interface IFrameListener
{
    #region Properties

    /// <summary>Gets the user-friendly display name of the listener.</summary>
    string UiName
    {
        get;
    }

    /// <summary>Gets an optional description of the listener.</summary>
    string? Description
    {
        get;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Called when a frame has been captured.
    /// Must not be called concurrently or after <see cref="OnFinish"/>.
    /// </summary>
    /// <param name="frame">The captured frame.</param>
    /// <returns><see langword="true"/> to continue receiving frames; <see langword="false"/> to unsubscribe.</returns>
    bool OnFrame(Frame frame);

    /// <summary>
    /// Called when the listener is unsubscribed or finished.
    /// Must be called exactly once, after all <see cref="OnFrame"/> calls have returned.
    /// Idempotent: safe to call more than once, subsequent calls are no-ops.
    /// </summary>
    void OnFinish();

    #endregion
}