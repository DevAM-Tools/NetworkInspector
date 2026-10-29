// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The UDP ports that service discovery announced in endpoint options, as a set of 65,536 bits (8 KiB).
/// <para>
/// A port named in an offer or a subscribe is where the announcing side expects SOME/IP datagrams, so traffic on it can be decoded
/// without the port being listed in the configuration. The set belongs to one stack: it starts empty and only grows, and a new
/// stack (a new session) starts empty again, so one capture never learns from another. Ports on the ignore list are never learned.
/// A port is learned for the packets that follow the announcement, never for the ones before it: the acceptance decision of a packet
/// is recorded by its first parse and replayed afterwards.
/// </para>
/// <para>
/// <b>Thread-safety:</b> one writer, many readers, no lock. Only the ordered first parse calls <see cref="Learn"/> (the ingest gate
/// guarantees it), so a read-modify-write of a word needs no atomic exchange; the volatile write publishes the word, and a reader
/// that sees a bit set sees a port that was learned before. The set is also read by the first parse of later packets (the
/// acceptance decision) and by nothing else, because a replay reads the recorded decision, not the set. Rejected alternatives: a
/// lock (every datagram would pay it), and a concurrent set (a 64-bit word is the unit of publication anyway).
/// </para>
/// </summary>
internal sealed class SomeIpLearnedPorts
{
    private readonly ulong[] _Bits = new ulong[1024];

    private readonly SomeIpPortSet _Ignore;

    /// <summary>Creates an empty set.</summary>
    /// <param name="ignore">Ports that are never learned (<c>someip.learn_sd_ports_ignore</c>).</param>
    internal SomeIpLearnedPorts(ReadOnlySpan<ushort> ignore) => _Ignore = SomeIpPortSet.Create(ignore);

    /// <summary>Learns a port. Port zero is not a port, and an ignored port is never learned. First-parse thread only.</summary>
    /// <param name="port">The announced port.</param>
    internal void Learn(ushort port)
    {
        if (port == 0 || _Ignore.Contains(port))
        {
            return;
        }

        ref ulong word = ref _Bits[port >> 6];
        Volatile.Write(ref word, Volatile.Read(ref word) | (1UL << (port & 63)));
    }

    /// <summary>Whether the port was learned.</summary>
    /// <param name="port">The port.</param>
    internal bool Contains(ushort port) => (Volatile.Read(ref _Bits[port >> 6]) & (1UL << (port & 63))) != 0;
}
