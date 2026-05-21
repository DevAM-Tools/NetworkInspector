// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Groups all registered field IDs for ICMPv6 Neighbor Discovery Protocol (NDP) sub-fields.
/// Populated in <see cref="Icmpv6Protocol.OnStartCustom"/> from attribute-registered field IDs.
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
    internal FieldId RaFlagManaged
    {
        get; init;
    }

    /// <summary>Other configuration flag (O).</summary>
    internal FieldId RaFlagOther
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

    #region Neighbor Solicitation/Advertisement target address

    /// <summary>Target address for NS/NA/Redirect.</summary>
    internal FieldId TargetAddress
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
    internal FieldId NaFlagRouter
    {
        get; init;
    }

    /// <summary>Solicited flag (S).</summary>
    internal FieldId NaFlagSolicited
    {
        get; init;
    }

    /// <summary>Override flag (O).</summary>
    internal FieldId NaFlagOverride
    {
        get; init;
    }

    #endregion

    #region Redirect fields

    /// <summary>Destination address for Redirect messages.</summary>
    internal FieldId RedirectDstAddress
    {
        get; init;
    }

    #endregion

    #region NDP Option fields

    /// <summary>Option container.</summary>
    internal FieldId OptContainer
    {
        get; init;
    }

    /// <summary>Option type.</summary>
    internal FieldId OptType
    {
        get; init;
    }

    /// <summary>Option length in 8-byte units.</summary>
    internal FieldId OptLen
    {
        get; init;
    }

    /// <summary>Link-layer address (Source or Target).</summary>
    internal FieldId OptLinkAddr
    {
        get; init;
    }

    /// <summary>Prefix length for Prefix Information option.</summary>
    internal FieldId OptPrefixLength
    {
        get; init;
    }

    /// <summary>On-link flag (L) for Prefix Information.</summary>
    internal FieldId OptPrefixFlagOnLink
    {
        get; init;
    }

    /// <summary>Autonomous flag (A) for Prefix Information.</summary>
    internal FieldId OptPrefixFlagAuto
    {
        get; init;
    }

    /// <summary>Valid lifetime for Prefix Information (seconds).</summary>
    internal FieldId OptPrefixValidLifetime
    {
        get; init;
    }

    /// <summary>Preferred lifetime for Prefix Information (seconds).</summary>
    internal FieldId OptPrefixPreferredLifetime
    {
        get; init;
    }

    /// <summary>Prefix IPv6 address.</summary>
    internal FieldId OptPrefix
    {
        get; init;
    }

    /// <summary>MTU value for MTU option.</summary>
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
    internal FieldId OptRdnssAddress
    {
        get; init;
    }
    #endregion
}
