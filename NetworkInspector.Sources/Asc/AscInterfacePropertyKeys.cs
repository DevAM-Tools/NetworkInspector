// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Property keys for ASC frame interface metadata.
/// These keys are stored in the interface properties dictionary
/// and can be queried by consumers for source-specific information.
/// </summary>
internal static class AscInterfacePropertyKeys
{
    /// <summary>Channel number within the ASC file, stored boxed as a <see cref="long"/>.</summary>
    internal const string Channel = "asc.channel";

    /// <summary>
    /// Display name of the bus type, stored as a string: the <c>AscBusTypeExtensions.ToDisplayName</c> text
    /// (e.g. "CAN", "CAN FD", "LIN", "FlexRay", "Ethernet", "CAN XL"), not the enum member name.
    /// </summary>
    internal const string BusType = "asc.bus_type";
}
