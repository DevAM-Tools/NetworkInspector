// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Constants for the Linux SocketCAN XL frame format (<c>struct canxl_frame</c>).
/// Wire layout: Prio(4 LE) + Flags(1) + Sdt(1) + Len(2 LE) + Af(4 LE) + Data(2048, zero-padded).
/// Total = 2060 bytes.
/// </summary>
internal static class SocketCanXlHeader
{
    /// <summary>Size of the fixed header portion (everything before the data area), in bytes.</summary>
    internal const int HeaderSize = SocketCanXlLayer.HeaderBytes;

    /// <summary>Maximum data length for a CAN-XL frame.</summary>
    internal const int MaxDataLength = SocketCanXlLayer.MaxXlData;

    /// <summary>XLF flag in the flags byte (CAN-XL frame indicator).</summary>
    internal const byte XlfFlag = SocketCanXlLayer.XlfFlag;

    /// <summary>SEC flag in the flags byte (Simple Extended Content).</summary>
    internal const byte SecFlag = SocketCanXlLayer.SecFlag;
}
