// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

    /// <summary>
    /// Called when a packet has been parsed.
    /// Must not be called concurrently or after <see cref="OnFinish"/>.
    /// </summary>
    /// <param name="packet">The parsed packet.</param>
    /// <returns><see langword="true"/> to continue receiving packets; <see langword="false"/> to unsubscribe.</returns>
    bool OnPacket(Packet packet);

    /// <summary>
    /// Called when the listener is unsubscribed or finished.
    /// Caller: call it exactly once, after the last packet (after all <see cref="OnPacket"/> calls have returned).
    /// Implementer: tolerate a second call (do nothing).
    /// </summary>
    void OnFinish();
}
