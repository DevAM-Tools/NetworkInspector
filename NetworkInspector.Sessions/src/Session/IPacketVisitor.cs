// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Receives each packet that <see cref="ISessionReader.TryVisitPackets"/> parsed, on the thread that called it.
/// <para>
/// <b>Lifetime:</b> the packet is valid only during <see cref="Visit"/>; the session parses the next id into the same instance
/// (recycling), so a visitor copies what it keeps (exporters do: <c>OnPacket</c> copies into its batch). Why a callback and not a
/// buffer of packets: an observer-driven parse stages fields for exactly one packet, and the consumer must commit that packet
/// (<c>OnPacket</c>) before the next parse begins; a buffered read would parse 64 packets first and discard 63 stagings.
/// </para>
/// <para><b>Thread-safety:</b> called sequentially from one thread; no synchronization is needed inside.</para>
/// </summary>
public interface IPacketVisitor
{
    /// <summary>One parsed packet.</summary>
    /// <param name="packet">The packet; valid only for this call.</param>
    /// <returns><see langword="true"/> to continue with the next id; <see langword="false"/> to stop the read after this packet.</returns>
    bool Visit(Packet packet);
}
