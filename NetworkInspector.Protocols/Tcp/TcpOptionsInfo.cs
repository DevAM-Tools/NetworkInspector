// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Parsed TCP options information extracted during option parsing.
/// Used to communicate option values back to the protocol for window scaling
/// and other stateful analysis features.
/// </summary>
/// <param name="Mss">Maximum Segment Size value, or null if not present.</param>
/// <param name="WindowScale">Window Scale shift count (0-14), or null if not present.</param>
/// <param name="SackPermitted">Whether SACK Permitted option was present.</param>
/// <param name="TsVal">Timestamps TSval, or null if not present.</param>
/// <param name="TsEcr">Timestamps TSecr, or null if not present.</param>
internal readonly record struct TcpOptionsInfo(
    ushort? Mss = null,
    byte? WindowScale = null,
    bool SackPermitted = false,
    uint? TsVal = null,
    uint? TsEcr = null)
{
    #region Sentinels

    /// <summary>Default empty options info.</summary>
    internal static TcpOptionsInfo Empty => default;

    #endregion
}
