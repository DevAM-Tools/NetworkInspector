// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Generators;

/// <summary>
/// Utility methods for building LINKTYPE_FLEXRAY (link type 210) frame data for exporter tests.
/// Produces frames in the tcpdump / ISO 17458-2 capture format consumed by
/// <see cref="NetworkInspector.Protocols.FlexRayProtocol"/>.
/// </summary>
internal static class FlexRayGenerators
{
    /// <summary>
    /// Builds a LINKTYPE_FLEXRAY data frame with the specified parameters.
    /// </summary>
    /// <param name="channel">FlexRay channel (0 = A, 1 = B).</param>
    /// <param name="frameId">11-bit FlexRay slot/frame ID.</param>
    /// <param name="cycle">Cycle counter (0–63).</param>
    /// <param name="headerCrc">FlexRay header CRC.</param>
    /// <param name="data">Payload data bytes.</param>
    /// <param name="sync">Whether the sync frame indicator flag is set.</param>
    /// <param name="startup">Whether the startup frame indicator flag is set.</param>
    internal static byte[] BuildFlexRayFrame(
        byte channel, ushort frameId, byte cycle, ushort headerCrc,
        ReadOnlySpan<byte> data, bool sync = false, bool startup = false)
    {
        return FlexRayLinkTypeFrame.BuildFrame(
            channelB: channel != 0,
            frameId,
            cycle,
            headerCrc,
            data,
            sfi: sync,
            stfi: startup);
    }
}
