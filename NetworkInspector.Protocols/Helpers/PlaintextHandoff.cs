// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>The encrypted transport that a plaintext unit was delivered from.</summary>
internal enum PlaintextTransport : byte
{
    /// <summary>The plaintext of a TLS record (the transport is a TCP stream).</summary>
    Tls,

    /// <summary>The plaintext of a DTLS record (the transport is a UDP datagram).</summary>
    Dtls,
}

/// <summary>
/// The marker that a protocol which decrypts a transport leaves for the application protocol it hands the plaintext to.
/// <para>
/// An application protocol that rides on TLS or DTLS (SOME/IP is one) needs to know that its unit is plaintext of such a record and
/// on which port the connection runs, because that is how it decides whether the unit is its own: the port is listed in the
/// configuration for exactly this transport. The dispatch key cannot carry that, since the plaintext is handed on by the
/// decrypting protocol itself. The decrypting protocol therefore publishes the marker here before it hands a plaintext unit on and
/// withdraws it afterwards; the receiving protocol reads it during the same call. This is the same pattern as the IP address
/// caches and the UDP port cache: a per-packet fact that only the thread currently parsing the packet needs.
/// </para>
/// <para>
/// <b>Thread-safety:</b> the values are <see cref="ThreadStaticAttribute"/>, so every thread sees only its own. The packet id guards
/// against a stale entry left by an earlier packet on the same thread: a reader passes the id it is parsing and never receives
/// the marker of another packet. A lock or a shared table was rejected: the marker is needed only on the thread that publishes it,
/// and a shared structure would cost every unit a synchronization for nothing. Residual risk: a publisher that forgets
/// <see cref="Clear"/> leaves a marker that a later parse of the <em>same packet id</em> on the same thread could see; the
/// publisher's contract is to clear it in a <c>finally</c>.
/// </para>
/// <para>
/// <b>Contract of a decrypting protocol:</b> it owns the dispatch table of its transport (<see cref="TlsPortTableName"/> or
/// <see cref="DtlsPortTableName"/>, key type U64, key = the server port of the encrypted connection) and registers it in its
/// <c>RegisterFields</c>, before the application protocols register. An application protocol binds the ports its configuration
/// lists in that table and, when the table is absent at its own registration, reports that the list has no effect. The decrypting
/// protocol looks the server port up in its table, publishes the marker, calls the protocol it found and clears the marker. The
/// table is what makes the absence of a decrypting protocol visible at build time; the marker is what makes a unit accepted at
/// parse time. The TLS and DTLS parsers of this version do not decrypt and register no table.
/// </para>
/// </summary>
internal static class PlaintextHandoff
{
    /// <summary>Dispatch table of the plaintext of a TLS connection, keyed by the server port; owned by the protocol that decrypts TLS.</summary>
    internal const string TlsPortTableName = "tls.port";

    /// <summary>Dispatch table of the plaintext of a DTLS flow, keyed by the server port; owned by the protocol that decrypts DTLS.</summary>
    internal const string DtlsPortTableName = "dtls.port";

    [ThreadStatic]
    private static int _PacketId;

    [ThreadStatic]
    private static PlaintextTransport _Transport;

    [ThreadStatic]
    private static ushort _Port;

    /// <summary>Whether this thread published anything; the default of a thread-static is 0, which is a valid packet id.</summary>
    [ThreadStatic]
    private static bool _Published;

    /// <summary>Publishes the marker for the plaintext unit that the caller is about to hand on.</summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="transport">The transport the plaintext came from.</param>
    /// <param name="port">The server port of the connection or datagram flow that carried the encrypted records.</param>
    internal static void Publish(PacketId packetId, PlaintextTransport transport, ushort port)
    {
        _PacketId = packetId.Value;
        _Transport = transport;
        _Port = port;
        _Published = true;
    }

    /// <summary>Withdraws the marker. The publisher calls it in a <c>finally</c> when the hand-off ends.</summary>
    internal static void Clear() => _Published = false;

    /// <summary>Reads the marker published for <paramref name="packetId"/> on this thread.</summary>
    /// <param name="packetId">The packet being parsed.</param>
    /// <param name="transport">The transport on success.</param>
    /// <param name="port">The port on success.</param>
    /// <returns>False when nothing was published for this packet id on this thread.</returns>
    internal static bool TryGet(PacketId packetId, out PlaintextTransport transport, out ushort port)
    {
        if (_Published && _PacketId == packetId.Value)
        {
            transport = _Transport;
            port = _Port;
            return true;
        }

        transport = default;
        port = 0;
        return false;
    }
}
