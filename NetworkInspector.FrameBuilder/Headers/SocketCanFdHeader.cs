// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Constants for the Linux SocketCAN FD frame format (<c>struct canfd_frame</c>).
/// Wire layout: CanId(4 BE) + Len(1) + Flags(1) + Res0(1) + Res1(1) + Data(64).
/// Total = 72 bytes.
/// </summary>
internal static class SocketCanFdHeader
{
    /// <summary>Size of the fixed header portion (everything before the data area), in bytes.</summary>
    internal const int HeaderSize = 8;

    /// <summary>Maximum data length for a CAN-FD frame.</summary>
    internal const int MaxDataLength = SocketCanFdLayer.MaxFdData;
}
