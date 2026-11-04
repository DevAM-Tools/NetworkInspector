// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Builds frames whose interface carries a chosen name and property bag, for exporter tests that depend on the
/// interface metadata (channel numbers, custom channel names).
/// </summary>
internal static class InterfaceFrameFactory
{
    /// <summary>
    /// Registers a new interface on this thread's test stack and creates a frame on it.
    /// </summary>
    /// <param name="interfaceName">Display name of the interface.</param>
    /// <param name="properties">Interface property bag, or <see langword="null"/> for none.</param>
    /// <param name="linkType">Link type of the frame and the interface.</param>
    /// <param name="data">Raw frame bytes.</param>
    /// <param name="timestampNanos">Frame timestamp in nanoseconds.</param>
    /// <param name="frameId">Frame identifier.</param>
    /// <returns>The frame; its interface is registered in the thread's registry.</returns>
    internal static Frame Create(
        string interfaceName,
        Dictionary<string, object>? properties,
        LinkType linkType,
        byte[] data,
        long timestampNanos = 1_000_000_000L,
        int frameId = 0)
    {
        Stack stack = TestHarness.GetStack();
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        if (registry.SourceCount == 0)
        {
            registry.RegisterSource(TestHarness.CreateNullFrameSource());
        }

        FrameInterfaceId interfaceId = registry.Register(new FrameSourceId(0), interfaceName, null, linkType, properties);
        return Frame.Create(frameId, Timestamp.FromNanos(timestampNanos), data, linkType, interfaceId, registry).Value;
    }
}
