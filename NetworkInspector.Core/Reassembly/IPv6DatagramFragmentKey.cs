// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Identifies one IPv6 datagram being reassembled.
/// Per RFC 8200 §4.5, an IPv6 fragment is uniquely identified by
/// (Source Address, Destination Address, Identification).
/// Unlike IPv4, IPv6 uses 128-bit addresses and a 32-bit identification field.
/// </summary>
/// <param name="SourceHigh">Source IPv6 address high 64 bits.</param>
/// <param name="SourceLow">Source IPv6 address low 64 bits.</param>
/// <param name="DestinationHigh">Destination IPv6 address high 64 bits.</param>
/// <param name="DestinationLow">Destination IPv6 address low 64 bits.</param>
/// <param name="Identification">32-bit identification field from Fragment Header.</param>
internal readonly record struct IPv6DatagramFragmentKey(
    ulong SourceHigh,
    ulong SourceLow,
    ulong DestinationHigh,
    ulong DestinationLow,
    uint Identification);
