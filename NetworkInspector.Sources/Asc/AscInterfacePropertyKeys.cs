// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// Property keys for ASC frame interface metadata.
/// These keys are stored in the interface properties dictionary
/// and can be queried by consumers for source-specific information.
/// </summary>
internal static class AscInterfacePropertyKeys
{
    /// <summary>Channel number within the ASC file (int).</summary>
    internal const string Channel = "asc.channel";

    /// <summary>Bus type name as string (e.g., "Can", "CanFd", "Lin").</summary>
    internal const string BusType = "asc.bus_type";
}
