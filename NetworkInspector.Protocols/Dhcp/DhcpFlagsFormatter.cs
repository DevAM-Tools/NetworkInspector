// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Dhcp;

/// <summary>
/// Provides efficient display text formatting for the DHCP flags field (<c>dhcp.flags</c>).
/// <para>
/// The 16-bit DHCP flags word (RFC 2131 §2) has a single defined boolean bit at position 15
/// (0x8000): the Broadcast flag.  All remaining bits are reserved and must be zero.
/// A 2-entry bracket table eliminates conditional string building at the call site.
/// </para>
/// <para>Output: the bracket suffix appended to the caller-supplied hex prefix,
/// e.g. <c>" [Broadcast]"</c> or <c>" [None]"</c>.</para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> All members are static and read from a pre-built immutable array;
/// safe for concurrent use from multiple threads.</para>
/// </remarks>
internal static class DhcpFlagsFormatter
{
    // Index 0 = broadcast flag clear, index 1 = broadcast flag set.
    private static readonly string[] BracketTable = [" [None]", " [Broadcast]"];

    /// <summary>
    /// Returns the bracket suffix for the given DHCP flags word.
    /// Output examples: <c>" [None]"</c>, <c>" [Broadcast]"</c>.
    /// The caller is responsible for prepending the hex value.
    /// </summary>
    internal static string Format(ushort flags) => BracketTable[(flags >> 15) & 1];
}
