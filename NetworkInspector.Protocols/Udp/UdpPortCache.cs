// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Udp;

/// <summary>
/// The source and destination port of the UDP datagram being parsed on this thread.
/// <para>
/// A parser reached through the port table receives its port as the dispatch key. A parser reached through the heuristic
/// table (<c>udp.heuristic</c>) has no key, yet some of them need the ports (a parser that accepts a payload because it arrived
/// on a port it learned earlier). <see cref="UdpProtocol"/> publishes the ports here before it dispatches; the heuristic parser
/// reads them on the same thread during the same call.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the values are <see cref="ThreadStaticAttribute"/>, so every thread sees only its own. The packet id
/// guards against a stale entry: an entry left by an earlier packet on the same thread (for example another session, or a later
/// expansion of an earlier packet) is never taken for the current packet, because the reader passes the id it is parsing. A lock
/// or a shared table was rejected: the data is needed only on the thread that publishes it, and a shared structure would cost
/// every datagram a synchronization for nothing. Residual risk: a nested transport of the same packet shares the id, so a cache of
/// another transport can hold a value for the same packet; readers consult every cache they care about.
/// </para>
/// </summary>
internal static class UdpPortCache
{
    /// <summary>Packet id of the published ports; -1 when nothing is published (a packet id is never negative).</summary>
    [ThreadStatic]
    private static int _PacketId;

    [ThreadStatic]
    private static ushort _Source;

    [ThreadStatic]
    private static ushort _Destination;

    /// <summary>Whether this thread published anything; the default of a thread-static is 0, which is a valid packet id.</summary>
    [ThreadStatic]
    private static bool _Published;

    /// <summary>Publishes the ports of the datagram of <paramref name="packetId"/> for the parsers called from it.</summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="source">The UDP source port.</param>
    /// <param name="destination">The UDP destination port.</param>
    internal static void Publish(PacketId packetId, ushort source, ushort destination)
    {
        _PacketId = packetId.Value;
        _Source = source;
        _Destination = destination;
        _Published = true;
    }

    /// <summary>
    /// Withdraws the published ports. UDP calls it when the dispatch of the datagram ends, so a parser that is reached later
    /// without UDP (for example through the TCP heuristic table) can never find the ports of an earlier datagram that happened
    /// to carry the same packet id on this thread.
    /// </summary>
    internal static void Clear() => _Published = false;

    /// <summary>Reads the ports published for <paramref name="packetId"/> on this thread.</summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="source">The UDP source port on success.</param>
    /// <param name="destination">The UDP destination port on success.</param>
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
