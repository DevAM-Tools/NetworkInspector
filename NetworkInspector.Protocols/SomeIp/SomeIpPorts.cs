// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// An immutable set of 16-bit ports with a constant-time membership test: a bitmap of 65,536 bits (8 KiB).
/// <para>
/// The parser asks "is this port one of mine" for the ports of every unit that reaches it, so the test has to be cheaper than a
/// hash lookup and must not allocate. The set is a fact of the port space: its size is fixed and does not grow with the
/// configuration, which is why it needs no limit. Rejected alternative: a frozen set of ports, which hashes on every call to
/// answer a question a single array index and a mask can answer.
/// </para>
/// <para><b>Thread-safety:</b> immutable once built; safe to read from any thread. It is filled by <see cref="Create"/> before it
/// is shared and never written afterwards.</para>
/// </summary>
internal sealed class SomeIpPortSet
{
    private const int _Words = 65536 / 64;

    private readonly ulong[] _Bits;

    private SomeIpPortSet(ulong[] bits) => _Bits = bits;

    /// <summary>Builds a set holding the given ports.</summary>
    /// <param name="ports">The ports; duplicates are harmless.</param>
    internal static SomeIpPortSet Create(ReadOnlySpan<ushort> ports)
    {
        ulong[] bits = new ulong[_Words];
        foreach (ushort port in ports)
        {
            bits[port >> 6] |= 1UL << (port & 63);
        }

        return new SomeIpPortSet(bits);
    }

    /// <summary>Whether the set holds the port.</summary>
    /// <param name="port">The port.</param>
    internal bool Contains(ushort port) => (_Bits[port >> 6] & (1UL << (port & 63))) != 0;
}

/// <summary>
/// The rule that decides which ports SOME/IP listens on, shared by the registration (which binds them) and the parser (which
/// checks the port of a unit that reached it without a dispatch key). One implementation, so the two cannot disagree.
/// </summary>
internal static class SomeIpPorts
{
    /// <summary>
    /// The ports of one transport: the configured ones without duplicates, plus the well-known port 30490 where the rule says so.
    /// UDP always includes it, because service discovery runs there whatever the configuration lists. TCP includes it only when
    /// the configuration lists no TCP port: a list is the user's statement of where SOME/IP runs, and adding the default behind
    /// it would decode unrelated traffic on 30490.
    /// </summary>
    /// <param name="configured">The ports the configuration lists for this transport.</param>
    /// <param name="alwaysWellKnown">Whether the well-known port is added even when ports are configured.</param>
    /// <returns>The ports in configuration order, the well-known port last.</returns>
    internal static List<ushort> WithDefault(ReadOnlySpan<ushort> configured, bool alwaysWellKnown)
    {
        List<ushort> ports = Distinct(configured);

        ushort wellKnown = (ushort)SomeIpProtocol.DefaultPort;
        if ((alwaysWellKnown || ports.Count == 0) && !ports.Contains(wellKnown))
        {
            ports.Add(wellKnown);
        }

        return ports;
    }

    /// <summary>
    /// The configured ports in configuration order with repetitions removed and no well-known port added: the list for a table
    /// that has no default (the plaintext tables of an encrypted transport). A repeated port would be bound twice and the second
    /// binding reported as a failure, so one entry per port is kept.
    /// </summary>
    /// <param name="configured">The ports the configuration lists for this transport.</param>
    /// <returns>The distinct ports in configuration order.</returns>
    internal static List<ushort> Distinct(ReadOnlySpan<ushort> configured)
    {
        List<ushort> ports = new(configured.Length + 1);
        foreach (ushort port in configured)
        {
            if (!ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }
}
