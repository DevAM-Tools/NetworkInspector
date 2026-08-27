// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Construction-time options for <see cref="Session"/>.
/// </summary>
/// <remarks>
/// Immutable after construction. Thread-safety is not applicable.
/// </remarks>
public sealed class SessionOptions
{
    #region Presets

    /// <summary>Default options: parsed packets are stored for lock-free listener reads.</summary>
    public static SessionOptions Default { get; } = new();

    /// <summary>
    /// Options that skip the packet store, so every
    /// <see cref="ISessionReader.TryGetPacket(PacketId, out Packet)"/>
    /// re-parses from the frame source. Stateful protocols replay the state they recorded during the
    /// first parse of that packet, which makes those re-parses lock-free and field-identical.
    /// </summary>
    public static SessionOptions WithoutPacketStore { get; } = new() { StoreParsedPackets = false };

    /// <summary>
    /// Store off and no packet index — listeners re-parse in parallel without the roaring-bitmap
    /// index overhead. Lowest memory footprint per packet at the cost of re-parsing on every read.
    /// </summary>
    public static SessionOptions RedissectOnly { get; } = new()
    {
        StoreParsedPackets = false,
        IndexPackets = false,
    };

    #endregion

    #region Properties

    /// <summary>
    /// When <see langword="true"/> (default), the first parse writes sealed packets into the session
    /// store and listeners typically hit that cache.
    /// When <see langword="false"/>, only the frame mapping is kept and concurrent listeners re-parse
    /// independently; the protocol-local replay state recorded during the first parse keeps those
    /// re-parses identical.
    /// </summary>
    public bool StoreParsedPackets { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), the first parse populates the session packet index.
    /// When <see langword="false"/>, <see cref="ISessionReader.PacketIndex"/> stays
    /// <see langword="null"/> and packets are parsed via the plain <c>ParseFrame</c> path.
    /// </summary>
    public bool IndexPackets { get; init; } = true;

    #endregion
}
