// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Identifies one IP datagram being reassembled.
/// Per RFC 791, a datagram is uniquely identified by (Source, Destination, Identification, Protocol).
/// A readonly record struct, so it is a dictionary key of the defragmenter without a per-fragment allocation;
/// its generated value equality compares all four parts.
/// </summary>
/// <param name="Source">Source IPv4 address.</param>
/// <param name="Destination">Destination IPv4 address.</param>
/// <param name="Identification">16-bit identification field from IP header.</param>
/// <param name="Protocol">IP protocol number of the encapsulated payload.</param>
public readonly record struct DatagramFragmentKey(
    uint Source,
    uint Destination,
    ushort Identification,
    byte Protocol);
