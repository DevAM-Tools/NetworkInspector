// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// The source and destination port of the TCP segment being parsed on this thread.
/// <para>
/// TCP hands a payload to its application protocol in several ways: through the port table, through a cache of ports with one owner,
/// as a framed message of a reassembled stream, or through the heuristic table. Most of them reach the protocol without a dispatch
/// key, so the protocol cannot tell from its parse context whether it was reached through a port it is bound to or through content
/// detection. <see cref="TcpProtocol"/> publishes the ports here before it dispatches (and again when it replays a recorded
/// dispatch); an application protocol that decides by port reads them on the same thread during the same call. It is the TCP
/// counterpart of <see cref="UdpPortCache"/>.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the values are <see cref="ThreadStaticAttribute"/>, so every thread sees only its own. The packet id guards
/// against a stale entry left by an earlier packet on the same thread: the reader passes the id it is parsing and never receives
/// the ports of another packet. A lock or a shared table was rejected: the data is needed only on the thread that publishes it, and
/// a shared structure would cost every segment a synchronization for nothing. Residual risk: a segment nested inside another
/// segment of the same packet (a tunnel) withdraws the outer publication when its own dispatch ends, so a later payload dispatch of
/// the outer segment would find no ports and decide as a unit without a known port.
/// </para>
/// </summary>
internal static class TcpPortCache
{
    /// <summary>Packet id of the published ports; a packet id is never negative, and <see cref="_Published"/> tells whether anything is there.</summary>
    [ThreadStatic]
    private static int _PacketId;

    [ThreadStatic]
    private static ushort _Source;

    [ThreadStatic]
    private static ushort _Destination;

    /// <summary>Whether this thread published anything; the default of a thread-static is 0, which is a valid packet id.</summary>
    [ThreadStatic]
    private static bool _Published;

    /// <summary>Publishes the ports of the segment of <paramref name="packetId"/> for the parsers called from it.</summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="source">The TCP source port.</param>
    /// <param name="destination">The TCP destination port.</param>
    internal static void Publish(PacketId packetId, ushort source, ushort destination)
    {
        _PacketId = packetId.Value;
        _Source = source;
        _Destination = destination;
        _Published = true;
    }

    /// <summary>Withdraws the published ports. TCP calls it when the payload dispatch of the segment ends.</summary>
    internal static void Clear() => _Published = false;

    /// <summary>Reads the ports published for <paramref name="packetId"/> on this thread.</summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="source">The TCP source port on success.</param>
    /// <param name="destination">The TCP destination port on success.</param>
    /// <returns>False when nothing was published for this packet id on this thread.</returns>
    internal static bool TryGet(PacketId packetId, out ushort source, out ushort destination)
    {
        if (_Published && _PacketId == packetId.Value)
        {
            source = _Source;
            destination = _Destination;
            return true;
        }

        source = 0;
        destination = 0;
        return false;
    }
}
