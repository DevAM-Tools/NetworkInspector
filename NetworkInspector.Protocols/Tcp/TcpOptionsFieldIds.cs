// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Groups all registered field IDs for TCP option sub-fields.
/// Passed to <see cref="TcpOptionsParser.Parse"/> to avoid passing 30+ individual parameters.
/// The actual registration (via attributes) happens on <see cref="TcpProtocol"/>.
/// </summary>
/// <remarks>
/// A readonly struct passed by <see langword="in"/> reference, so the many ids are not copied per call. Thread safety:
/// immutable after construction; safe for concurrent reads.
/// </remarks>
internal readonly struct TcpOptionsFieldIds
{
    #region Single-byte options

    /// <summary>End of Option List (kind 0).</summary>
    internal FieldId Eol
    {
        get; init;
    }

    /// <summary>No-Operation / padding (kind 1).</summary>
    internal FieldId Nop
    {
        get; init;
    }

    #endregion

    #region MSS (kind 2)

    /// <summary>MSS option container.</summary>
    internal FieldId Mss
    {
        get; init;
    }

    /// <summary>MSS value in bytes.</summary>
    internal FieldId MssVal
    {
        get; init;
    }

    #endregion

    #region Window Scale (kind 3)

    /// <summary>Window Scale option container.</summary>
    internal FieldId WindowScale
    {
        get; init;
    }

    /// <summary>Window Scale shift count (0-14).</summary>
    internal FieldId WindowScaleVal
    {
        get; init;
    }

    /// <summary>Window Scale multiplier (computed: 1 &lt;&lt; shift).</summary>
    internal FieldId WindowScaleMultiplier
    {
        get; init;
    }

    #endregion

    #region SACK Permitted (kind 4)

    /// <summary>SACK Permitted option.</summary>
    internal FieldId SackPermitted
    {
        get; init;
    }

    #endregion

    #region SACK (kind 5)

    /// <summary>SACK option container.</summary>
    internal FieldId Sack
    {
        get; init;
    }

    /// <summary>Number of SACK blocks.</summary>
    internal FieldId SackCount
    {
        get; init;
    }

    /// <summary>SACK block left edge (repeated per block).</summary>
    internal FieldId SackLeftEdge
    {
        get; init;
    }

    /// <summary>SACK block right edge (repeated per block).</summary>
    internal FieldId SackRightEdge
    {
        get; init;
    }

    #endregion

    #region Timestamps (kind 8)

    /// <summary>Timestamps option container.</summary>
    internal FieldId Timestamps
    {
        get; init;
    }

    /// <summary>Timestamp Value (TSval).</summary>
    internal FieldId TimestampTsVal
    {
        get; init;
    }

    /// <summary>Timestamp Echo Reply (TSecr).</summary>
    internal FieldId TimestampTsEcr
    {
        get; init;
    }

    #endregion

    #region User Timeout (kind 28)

    /// <summary>User Timeout option container.</summary>
    internal FieldId UserTimeout
    {
        get; init;
    }

    /// <summary>Granularity: "minutes" or "seconds".</summary>
    internal FieldId UserTimeoutGranularity
    {
        get; init;
    }

    /// <summary>User Timeout value.</summary>
    internal FieldId UserTimeoutVal
    {
        get; init;
    }

    #endregion

    #region TCP Fast Open (kind 34)

    /// <summary>TCP Fast Open option container.</summary>
    internal FieldId FastOpen
    {
        get; init;
    }

    /// <summary>TFO cookie request flag.</summary>
    internal FieldId FastOpenRequest
    {
        get; init;
    }

    /// <summary>TFO cookie bytes.</summary>
    internal FieldId FastOpenCookie
    {
        get; init;
    }

    #endregion

    #region MPTCP (kind 30)

    /// <summary>Multipath TCP option container.</summary>
    internal FieldId Mptcp
    {
        get; init;
    }

    /// <summary>MPTCP subtype (upper 4 bits of first data byte).</summary>
    internal FieldId MptcpSubtype
    {
        get; init;
    }

    #endregion

    #region MD5 Signature (kind 19)

    /// <summary>MD5 Signature option container.</summary>
    internal FieldId Md5
    {
        get; init;
    }

    /// <summary>MD5 digest (16 bytes).</summary>
    internal FieldId Md5Digest
    {
        get; init;
    }

    #endregion

    #region TCP-AO (kind 29)

    /// <summary>TCP Authentication Option container.</summary>
    internal FieldId TcpAo
    {
        get; init;
    }

    /// <summary>TCP-AO Key ID.</summary>
    internal FieldId TcpAoKeyId
    {
        get; init;
    }

    /// <summary>TCP-AO RNextKeyID.</summary>
    internal FieldId TcpAoRNextKeyId
    {
        get; init;
    }

    /// <summary>TCP-AO MAC (message authentication code).</summary>
    internal FieldId TcpAoMac
    {
        get; init;
    }

    #endregion

    #region Unknown

    /// <summary>Unknown / unrecognized option container.</summary>
    internal FieldId Unknown
    {
        get; init;
    }

    /// <summary>Unknown option raw data.</summary>
    internal FieldId UnknownData
    {
        get; init;
    }

    #endregion

    #region Echo, CC, SCPS, Quick-Start, AccECN, experimental

    /// <summary>Echo option container (kind 6, obsolete RFC 1072, 6 bytes).</summary>
    internal FieldId Echo { get; init; }

    /// <summary>Echo option 32-bit value.</summary>
    internal FieldId EchoValue { get; init; }

    /// <summary>Echo Reply option container (kind 7, obsolete RFC 1072, 6 bytes).</summary>
    internal FieldId EchoReply { get; init; }

    /// <summary>Echo Reply option 32-bit value.</summary>
    internal FieldId EchoReplyValue { get; init; }

    /// <summary>CC (connection count) option container (kind 11, RFC 1644, 6 bytes).</summary>
    internal FieldId Cc { get; init; }

    /// <summary>CC option 32-bit value.</summary>
    internal FieldId CcValue { get; init; }

    /// <summary>CC.NEW option container (kind 12, RFC 1644, 6 bytes).</summary>
    internal FieldId CcNew { get; init; }

    /// <summary>CC.NEW option 32-bit value.</summary>
    internal FieldId CcNewValue { get; init; }

    /// <summary>CC.ECHO option container (kind 13, RFC 1644, 6 bytes).</summary>
    internal FieldId CcEcho { get; init; }

    /// <summary>CC.ECHO option 32-bit value.</summary>
    internal FieldId CcEchoValue { get; init; }

    /// <summary>SCPS capabilities option container (kind 20, 4 bytes).</summary>
    internal FieldId Scps { get; init; }

    /// <summary>SCPS capability bit 0x80: BETS.</summary>
    internal FieldId ScpsBets { get; init; }

    /// <summary>SCPS capability bit 0x40: SNACK1.</summary>
    internal FieldId ScpsSnack1 { get; init; }

    /// <summary>SCPS capability bit 0x20: SNACK2.</summary>
    internal FieldId ScpsSnack2 { get; init; }

    /// <summary>SCPS capability bit 0x10: header compression.</summary>
    internal FieldId ScpsCompress { get; init; }

    /// <summary>SCPS capability bit 0x08: NLTS.</summary>
    internal FieldId ScpsNlts { get; init; }

    /// <summary>Quick-Start option container (kind 27, 8 bytes).</summary>
    internal FieldId Qs { get; init; }

    /// <summary>Quick-Start rate request (low nibble of the first data byte).</summary>
    internal FieldId QsRate { get; init; }

    /// <summary>Quick-Start TTL difference.</summary>
    internal FieldId QsTtlDiff { get; init; }

    /// <summary>Accurate ECN option container (kinds 0xAC and 0xAE, 0/3/6/9 bytes of 24-bit counters).</summary>
    internal FieldId AccEcn { get; init; }

    /// <summary>AccECN EE0B counter (24 bit).</summary>
    internal FieldId AccEcnEe0b { get; init; }

    /// <summary>AccECN ECEB counter (24 bit).</summary>
    internal FieldId AccEcnEceb { get; init; }

    /// <summary>AccECN EE1B counter (24 bit).</summary>
    internal FieldId AccEcnEe1b { get; init; }

    /// <summary>Experimental option container (kinds 0xFD and 0xFE, RFC 6994 shared experimental options).</summary>
    internal FieldId Exp { get; init; }

    /// <summary>Experiment identifier (ExID, first 16 bits of the option data).</summary>
    internal FieldId ExpExId { get; init; }

    /// <summary>Experimental option data after the ExID.</summary>
    internal FieldId ExpData { get; init; }

    #endregion
}
