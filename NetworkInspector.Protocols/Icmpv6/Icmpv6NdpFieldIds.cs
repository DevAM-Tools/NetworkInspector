// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Groups all registered field IDs for ICMPv6 Neighbor Discovery Protocol (NDP) sub-fields.
/// Populated in <see cref="Icmpv6Protocol"/> (its start hook) from the attribute-registered field IDs.
/// <para>
/// <b>Why a struct:</b> the ids are bundled in one immutable struct that <see cref="Icmpv6NdpParser"/> receives by
/// <see langword="in"/> reference, so the parser methods keep a short parameter list and no per-packet object is
/// created. The struct is filled once at start and only read afterwards, so concurrent parses may share it.
/// </para>
/// <para>
/// The member names follow the registered field names without the <c>icmpv6.</c> prefix (for example
/// <c>icmpv6.nd.ra.flag.m</c> is <see cref="RaFlagM"/>), so a reader can map a field to its id without a lookup table.
/// </para>
/// </summary>
internal readonly struct Icmpv6NdpFieldIds
{
    #region Router Advertisement fields

    /// <summary>Current hop limit from Router Advertisement.</summary>
    internal FieldId RaCurHopLimit
    {
        get; init;
    }

    /// <summary>Router Advertisement flags container.</summary>
    internal FieldId RaFlags
    {
        get; init;
    }

    /// <summary>Managed address configuration flag (M).</summary>
    internal FieldId RaFlagM
    {
        get; init;
    }

    /// <summary>Other configuration flag (O).</summary>
    internal FieldId RaFlagO
    {
        get; init;
    }

    /// <summary>Home agent flag (H, RFC 6275).</summary>
    internal FieldId RaFlagH
    {
        get; init;
    }

    /// <summary>Default router preference (Prf, RFC 4191), the two bits shifted down to 0..3.</summary>
    internal FieldId RaFlagPrf
    {
        get; init;
    }

    /// <summary>ND proxy flag (P, RFC 4389).</summary>
    internal FieldId RaFlagP
    {
        get; init;
    }

    /// <summary>SNAC router flag (S).</summary>
    internal FieldId RaFlagS
    {
        get; init;
    }

    /// <summary>The reserved bit of the flags byte.</summary>
    internal FieldId RaFlagRsv
    {
        get; init;
    }

    /// <summary>Router lifetime in seconds.</summary>
    internal FieldId RaRouterLifetime
    {
        get; init;
    }

    /// <summary>Reachable time in milliseconds.</summary>
    internal FieldId RaReachableTime
    {
        get; init;
    }

    /// <summary>Retransmission timer in milliseconds.</summary>
    internal FieldId RaRetransTimer
    {
        get; init;
    }

    #endregion

    #region Target and destination addresses

    /// <summary>Target address of a Neighbor Solicitation.</summary>
    internal FieldId NsTargetAddress
    {
        get; init;
    }

    /// <summary>Target address of a Neighbor Advertisement.</summary>
    internal FieldId NaTargetAddress
    {
        get; init;
    }

    /// <summary>Target address of a Redirect (the better next hop).</summary>
    internal FieldId RdTargetAddress
    {
        get; init;
    }

    /// <summary>Destination address of a Redirect (the address that is redirected).</summary>
    internal FieldId RdDestinationAddress
    {
        get; init;
    }

    #endregion

    #region Neighbor Advertisement flags

    /// <summary>NA flags container.</summary>
    internal FieldId NaFlags
    {
        get; init;
    }

    /// <summary>Router flag (R).</summary>
    internal FieldId NaFlagR
    {
        get; init;
    }

    /// <summary>Solicited flag (S).</summary>
    internal FieldId NaFlagS
    {
        get; init;
    }

    /// <summary>Override flag (O).</summary>
    internal FieldId NaFlagO
    {
        get; init;
    }

    /// <summary>The 29 reserved bits of the flags word.</summary>
    internal FieldId NaFlagRsv
    {
        get; init;
    }

    #endregion

    #region Option header and link-layer address

    /// <summary>Option container.</summary>
    internal FieldId Opt
    {
        get; init;
    }

    /// <summary>Option type.</summary>
    internal FieldId OptType
    {
        get; init;
    }

    /// <summary>Option length in 8-byte units.</summary>
    internal FieldId OptLength
    {
        get; init;
    }

    /// <summary>Link-layer address of an 8-byte option (Ethernet MAC).</summary>
    internal FieldId OptLinkAddr
    {
        get; init;
    }

    /// <summary>Link-layer address of a 16-byte option whose last six bytes are zero (EUI-64).</summary>
    internal FieldId OptLinkAddrEui64
    {
        get; init;
    }

    /// <summary>Link-layer address of any other option length, shown as raw bytes.</summary>
    internal FieldId OptLinkAddrRaw
    {
        get; init;
    }

    #endregion

    #region Prefix Information option

    /// <summary>Prefix length.</summary>
    internal FieldId PrefixLength
    {
        get; init;
    }

    /// <summary>On-link flag (L).</summary>
    internal FieldId PrefixFlagL
    {
        get; init;
    }

    /// <summary>Autonomous address configuration flag (A).</summary>
    internal FieldId PrefixFlagA
    {
        get; init;
    }

    /// <summary>Router address flag (R, RFC 6275).</summary>
    internal FieldId PrefixFlagR
    {
        get; init;
    }

    /// <summary>Prefix is a DHCPv6-PD delegated prefix flag (P).</summary>
    internal FieldId PrefixFlagP
    {
        get; init;
    }

    /// <summary>The four reserved bits below the flags.</summary>
    internal FieldId PrefixFlagReserved
    {
        get; init;
    }

    /// <summary>Valid lifetime (seconds).</summary>
    internal FieldId PrefixValidLifetime
    {
        get; init;
    }

    /// <summary>Preferred lifetime (seconds).</summary>
    internal FieldId PrefixPreferredLifetime
    {
        get; init;
    }

    /// <summary>Prefix IPv6 address.</summary>
    internal FieldId Prefix
    {
        get; init;
    }

    #endregion

    #region Other option fields

    /// <summary>MTU value of the MTU option.</summary>
    internal FieldId OptMtu
    {
        get; init;
    }

    /// <summary>RDNSS lifetime (seconds).</summary>
    internal FieldId OptRdnssLifetime
    {
        get; init;
    }

    /// <summary>RDNSS server IPv6 address (repeated).</summary>
    internal FieldId OptRdnss
    {
        get; init;
    }

    /// <summary>Quoted packet of a Redirected Header option.</summary>
    internal FieldId OptRedirectedPacket
    {
        get; init;
    }

    /// <summary>Advertisement interval in milliseconds (RFC 6275).</summary>
    internal FieldId OptAdvertisementInterval
    {
        get; init;
    }

    /// <summary>Home agent preference (RFC 6275).</summary>
    internal FieldId OptHomeAgentPreference
    {
        get; init;
    }

    /// <summary>Home agent lifetime in seconds (RFC 6275).</summary>
    internal FieldId OptHomeAgentLifetime
    {
        get; init;
    }

    /// <summary>Nonce bytes (RFC 3971).</summary>
    internal FieldId OptNonce
    {
        get; init;
    }

    /// <summary>Prefix length of a Route Information option (RFC 4191).</summary>
    internal FieldId OptRouteInfoPrefixLength
    {
        get; init;
    }

    /// <summary>Route preference of a Route Information option, shifted down to 0..3.</summary>
    internal FieldId OptRouteInfoPreference
    {
        get; init;
    }

    /// <summary>Route lifetime in seconds.</summary>
    internal FieldId OptRouteLifetime
    {
        get; init;
    }

    /// <summary>Route prefix, zero-extended to 128 bits.</summary>
    internal FieldId OptRouteInfoPrefix
    {
        get; init;
    }

    /// <summary>Flags bytes of an RA Flags Extension option (RFC 5175).</summary>
    internal FieldId OptFlagsExtension
    {
        get; init;
    }

    /// <summary>DNSSL lifetime in seconds (RFC 8106).</summary>
    internal FieldId OptDnsslLifetime
    {
        get; init;
    }

    /// <summary>One DNSSL domain name (repeated).</summary>
    internal FieldId OptDnssl
    {
        get; init;
    }

    /// <summary>URI of a Captive-Portal option (RFC 8910).</summary>
    internal FieldId OptCaptivePortal
    {
        get; init;
    }

    /// <summary>Scaled lifetime of a PREF64 option (RFC 8781), the 13 bits as transmitted.</summary>
    internal FieldId OptPref64ScaledLifetime
    {
        get; init;
    }

    /// <summary>Prefix length code of a PREF64 option, the 3 bits as transmitted.</summary>
    internal FieldId OptPref64Plc
    {
        get; init;
    }

    /// <summary>NAT64 prefix of a PREF64 option, zero-extended to 128 bits.</summary>
    internal FieldId OptPref64Prefix
    {
        get; init;
    }

    #endregion

    #region Error fields

    /// <summary>Description of a zero-length or truncated option that ended the option walk.</summary>
    internal FieldId ErrorOptionLength
    {
        get; init;
    }

    #endregion
}
