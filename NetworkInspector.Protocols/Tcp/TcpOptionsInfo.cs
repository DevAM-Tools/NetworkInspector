// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Parsed TCP options information extracted during option parsing.
/// Used to communicate option values back to the protocol for window scaling
/// and other stateful analysis features.
/// </summary>
internal readonly struct TcpOptionsInfo
{
    /// <summary>Maximum Segment Size value, or null if not present.</summary>
    internal ushort? Mss
    {
        get; init;
    }

    /// <summary>Window Scale shift count (0-14), or null if not present.</summary>
    internal byte? WindowScale
    {
        get; init;
    }

    /// <summary>Whether SACK Permitted option was present.</summary>
    internal bool SackPermitted
    {
        get; init;
    }

    /// <summary>Timestamps TSval, or null if not present.</summary>
    internal uint? TsVal
    {
        get; init;
    }

    /// <summary>Timestamps TSecr, or null if not present.</summary>
    internal uint? TsEcr
    {
        get; init;
    }

    /// <summary>Default empty options info.</summary>
    internal static TcpOptionsInfo Empty => default;
}
