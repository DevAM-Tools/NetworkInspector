// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Identifies one IP datagram being reassembled.
/// Per RFC 791, a datagram is uniquely identified by (Source, Destination, Identification, Protocol).
/// </summary>
/// <param name="Source">Source IPv4 address.</param>
/// <param name="Destination">Destination IPv4 address.</param>
/// <param name="Identification">16-bit identification field from IP header.</param>
/// <param name="Protocol">IP protocol number of the encapsulated payload.</param>
internal readonly record struct DatagramFragmentKey(
    uint Source,
    uint Destination,
    ushort Identification,
    byte Protocol);