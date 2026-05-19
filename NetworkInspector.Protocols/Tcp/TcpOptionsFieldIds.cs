// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Groups all registered field IDs for TCP option sub-fields.
/// Passed to <see cref="TcpOptionsParser.Parse"/> to avoid passing 30+ individual parameters.
/// The actual registration (via attributes) happens on <see cref="TcpProtocol"/>.
/// </summary>
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
}
