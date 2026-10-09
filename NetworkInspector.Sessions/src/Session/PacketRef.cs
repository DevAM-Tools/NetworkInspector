// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// One slot of a pull result: a packet together with the id it was read from.
/// </summary>
/// <remarks>
/// <para>
/// A filtered pull skips ids, so the position inside the destination span no longer identifies the
/// packet. Pairing both values in one slot keeps the read allocation-free while still letting the
/// caller address the packet by id.
/// </para>
/// <para><b>Thread-safety:</b> readonly struct; the referenced <see cref="Packet"/> follows its own rules.</para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PacketRef
{
    #region Properties

    /// <summary>Packet id this slot was read from.</summary>
    public PacketId Id { get; init; }

    /// <summary>Parsed packet, or <see langword="null"/> when the frame for <see cref="Id"/> cannot be re-read.</summary>
    public Packet? Packet { get; init; }

    #endregion

    /// <summary>Creates a pull-result slot for <paramref name="id"/> and <paramref name="packet"/>.</summary>
    public PacketRef(PacketId id, Packet? packet)
    {
        Id = id;
        Packet = packet;
    }
}
