// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Immutable;
using NetworkInspector.FrameBuilder;
using NetworkInspector.Protocols;

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Shared deterministic carrier port, PDU wiring and Signal-PDU layout for AUTOSAR-style
/// Ethernet → IPv4 → UDP → PDU-Transport → Signal-PDU symmetry tests.
/// </summary>
internal static class AutomotivePduBench
{
    internal const ushort UdpPduTransportDestinationPort = 47290;

    internal const uint PduTransportWireId = 0x20;

    internal const uint SignalPduMessageId = 0x100;

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

    internal static SignalPduLayout TwoSequentialUint16LeLayout
    {
        get;
    } = new()
    {
        PduId = SignalPduMessageId,
        Name = "FixturePdu",
        ByteLength = 4,
        Signals = ImmutableArray.Create(
            new SignalSpec
            {
                Name = "EngineRpm",
                StartBit = 0,
                BitLength = 16,
                Endian = SignalEndian.Little,
                Type = SignalType.Unsigned,
                Factor = 0.25,
                Offset = 100.0,
                Unit = string.Empty,
            },
            new SignalSpec
            {
                Name = "Thr",
                StartBit = 16,
                BitLength = 16,
                Endian = SignalEndian.Little,
                Type = SignalType.Unsigned,
                Factor = 1.0,
                Offset = 0.0,
                Unit = string.Empty,
            }),
        RegisterAt = ImmutableArray.Create(
            new DispatchBinding
            {
                Table = PduTransportProtocol.IdTableName,
                Key = PduTransportWireId,
            }),
        Mux = null,
        MuxGroups = default,
    };
}
