// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Parsed TCP options information extracted during option parsing.
/// Used to communicate option values back to the protocol for window scaling
/// and other stateful analysis features.
/// </summary>
/// <remarks>
/// Only options that stateful analysis needs are returned; every other option is just appended to the field tree by the
/// parser. A value is null when the option was absent or malformed. Thread safety: immutable value type.
/// </remarks>
/// <param name="Mss">Maximum Segment Size value, or null if not present.</param>
/// <param name="WindowScale">Window Scale shift count, capped to 14 per RFC 7323, or null if not present.</param>
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
