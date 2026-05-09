// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Receives notification when a packet is parsed.
/// <para>
/// <b>Thread safety:</b> Not thread-safe. <see cref="OnPacket"/> and <see cref="OnFinish"/>
/// must be called sequentially from a single thread. The caller is responsible for
/// synchronization when driving an exporter from multiple threads.
/// </para>
/// </summary>
public interface IPacketListener
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
    /// Called when a packet has been parsed.
    /// Must not be called concurrently or after <see cref="OnFinish"/>.
    /// </summary>
    /// <param name="packet">The parsed packet.</param>
    /// <returns><see langword="true"/> to continue receiving packets; <see langword="false"/> to unsubscribe.</returns>
    bool OnPacket(Packet packet);

    /// <summary>
    /// Called when the listener is unsubscribed or finished.
    /// Must be called exactly once, after all <see cref="OnPacket"/> calls have returned.
    /// Idempotent: safe to call more than once, subsequent calls are no-ops.
    /// </summary>
    void OnFinish();

    #endregion
}
