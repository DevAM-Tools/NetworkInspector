// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Shared deterministic carrier port, PDU wiring and Signal Message layout for AUTOSAR-style
/// Ethernet → IPv4 → UDP → PDU-Transport → Signal Message symmetry tests.
/// </summary>
internal static class AutomotivePduBench
{
    internal const ushort UdpPduTransportDestinationPort = 47290;

    internal const uint PduTransportWireId = 0x20;

    internal const uint SignalMessageBenchId = 0x100;

    internal static PduTransportConfigFb PduTransportRegistry
    {
        get;
    } =
        new(
            ImmutableArray.Create(
                new PduEntry
                {
                    PduId = PduTransportWireId,
                    Name = "BenchPdu",
                }));

    internal static SignalMessageLayout TwoSequentialUint16LeLayout
    {
        get;
    } = new()
    {
        PduId = SignalMessageBenchId,
        Name = "fixture_message",
        UiName = "Fixture PDU",
        ByteLength = 4,
        Signals = ImmutableArray.Create(
            new SignalSpec
            {
                Name = "EngineRpm",
                UiName = "Engine RPM",
                StartBit = 0,
                BitLength = 16,
                Endian = SignalEndian.Little,
                Factor = 0.25,
                Offset = 100.0,
                Unit = string.Empty,
            },
            new SignalSpec
            {
                Name = "Thr",
                UiName = "Throttle",
                StartBit = 16,
                BitLength = 16,
                Endian = SignalEndian.Little,
                Factor = 1.0,
                Offset = 0.0,
                Unit = string.Empty,
            }),
        DispatchBindings = ImmutableArray.Create(
            new FrameDispatchBinding
            {
                Table = PduTransportProtocol.IdTableName,
                Key = PduTransportWireId,
            }),
        Mux = null,
        MuxGroups = default,
    };
}
