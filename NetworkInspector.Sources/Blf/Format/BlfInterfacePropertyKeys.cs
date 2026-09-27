// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Keys for BLF metadata on <see cref="FrameInterfaceInfo.Properties"/>.
/// BLF readers write these keys. BLF export reads <see cref="Channel"/> and
/// <see cref="HardwareChannel"/>. ASC export reads <see cref="Channel"/>.
/// </summary>
internal static class BlfInterfacePropertyKeys
{
    #region Property keys

    /// <summary>BLF channel number (value type: <see cref="long"/>).</summary>
    internal const string Channel = "blf.channel";

    /// <summary>
    /// Ethernet hardware channel from BLF Type 120 or Type 102 (value type: <see cref="ushort"/>).
    /// Present only when the object marked the hardware channel as valid.
    /// </summary>
    internal const string HardwareChannel = "blf.hw_channel";

    /// <summary>BLF object type identifier (value type: <see cref="uint"/>).</summary>
    internal const string ObjectType = "blf.object_type";

    /// <summary>BLF bus type constant (value type: <see cref="byte"/>).</summary>
    internal const string BusType = "blf.bus_type";

    #endregion
}
