// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// The small captures that the export shape tests of the field exporters share, so every format is checked against the same packets:
/// a tunnel packet with two IPv4 layers (a repeated field), a plain UDP packet, and a CAN frame without any IP field.
/// </summary>
/// <remarks>Thread safety: stateless; every call builds new frames.</remarks>
internal static class ShapeFrames
{
    #region Shared captures

    /// <summary>
    /// A tunnel frame (two <c>ip.src</c> occurrences), a plain Ethernet, IPv4, UDP frame, and a CAN frame (neither field), in this order.
    /// </summary>
    /// <returns>A new list of three frames with the ids 0 to 2.</returns>
    internal static List<Frame> TunnelUdpCan() =>
    [
        PacketGenerators.CreateTunnelFrame(0),
        TestHarness.CreateFrame(1, 1_000_000, FrameGenerators.BuildEthernetIpv4UdpFrame(8)),
        PacketGenerators.CreateCanFrame(2),
    ];

    #endregion
}
