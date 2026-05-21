// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.Bridges;

/// <summary>
/// Converts a FrameBuilder-side <see cref="PduTransportConfigFb"/> registry into parser JSON (<see cref="PduTransportConfig"/>).
/// </summary>
internal static class PduTransportConfigBridge
{
    internal static PduTransportConfig FromFb(PduTransportConfigFb fb)
    {
        ArgumentNullException.ThrowIfNull(fb);

        ImmutableArray<PduEntry> pdus = fb.Pdus;
        PduTransportPduEntry[] entries;
        if (pdus.IsDefault || pdus.Length == 0)
        {
            entries = [];
        }
        else
        {
            entries = new PduTransportPduEntry[pdus.Length];
            for (int i = 0; i < pdus.Length; i++)
            {
                PduEntry entry = pdus[i];
                entries[i] = new PduTransportPduEntry
                {
                    Id = entry.PduId,
                    Name = entry.Name,
                    Comment = null,
                };
            }
        }

        return new PduTransportConfig { Pdus = entries };
    }

    internal static string SerializeJson(PduTransportConfigFb fb) =>
        JsonSerializer.Serialize(
            FromFb(fb),
            PduTransportConfigContext.Default.PduTransportConfig);
}
