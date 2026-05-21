// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Identifies the bus type of an ASC channel for interface registration.
/// </summary>
internal enum AscBusType : byte
{
    /// <summary>Unknown or unsupported bus type.</summary>
    Unknown = 0,

    /// <summary>Classical CAN (Controller Area Network).</summary>
    Can = 1,

    /// <summary>CAN FD (Flexible Data-rate).</summary>
    CanFd = 2,

    /// <summary>LIN (Local Interconnect Network).</summary>
    Lin = 3,

    /// <summary>FlexRay automotive bus.</summary>
    FlexRay = 4,

    /// <summary>Ethernet / AFDX.</summary>
    Ethernet = 5,
}

/// <summary>
/// Extension methods for <see cref="AscBusType"/>.
/// </summary>
internal static class AscBusTypeExtensions
{
    /// <summary>
    /// Returns the human-readable UI name for a bus type (e.g., "CAN FD", "LIN").
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string ToDisplayName(this AscBusType busType) => busType switch
    {
        AscBusType.Can => "CAN",
        AscBusType.CanFd => "CAN FD",
        AscBusType.Lin => "LIN",
        AscBusType.FlexRay => "FlexRay",
        AscBusType.Ethernet => "Ethernet",
        _ => "Unknown",
    };

    /// <summary>
    /// Returns the <see cref="LinkType"/> used when registering a frame interface for this bus.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LinkType ToLinkType(this AscBusType busType) => busType switch
    {
        AscBusType.Can => LinkType.CanSocketcan,
        AscBusType.CanFd => LinkType.CanSocketcan,
        AscBusType.Lin => LinkType.Lin,
        AscBusType.FlexRay => LinkType.Flexray,
        AscBusType.Ethernet => LinkType.Ethernet,
        _ => LinkType.Null,
    };
}
