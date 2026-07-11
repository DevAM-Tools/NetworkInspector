// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalPdu;

/// <summary>
/// Parser round-trip coverage for Signal-PDU payloads carried directly inside UDP without
/// a PDU-Transport header (registered on <c>udp.port</c>; single-PDU configs still resolve
/// by <c>byte_length</c> when no parent dispatch key applies).
/// </summary>
internal sealed class SignalPduBasicTests
{
    private const ushort _BenchUdpDestinationPort = 16000;

    private static SignalPduLayout _BenchUdpLayout =>
        new()
        {
            PduId = AutomotivePduBench.SignalPduMessageId,
            Name = AutomotivePduBench.TwoSequentialUint16LeLayout.Name,
            ByteLength = AutomotivePduBench.TwoSequentialUint16LeLayout.ByteLength,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            RegisterAt =
                ImmutableArray.Create(
                    new DispatchBinding
                    {
                        Table = UdpProtocol.PortTableName,
                        Key = _BenchUdpDestinationPort,
                    }),
            Mux = null,
            MuxGroups = [],
        };

    [Test]
    public async Task Parses_UdpCarrier_MatchesExpectedFields()
    {
        SignalPduLayout layout = _BenchUdpLayout;

        SignalValueSet vals = SignalValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalPduLayer spdu = new(layout, vals);

        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, _BenchUdpDestinationPort))
            .Then(spdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_signal_pdu_basic_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_pdu.json");
        try
        {
            await File.WriteAllTextAsync(jsonPath, SignalPduConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, _ConfigureSignalSettings(jsonPath));

            using (stack)
            {
                await ProtocolTestHelper.AssertU64Field(stack, packet, "signal_pdu.pdu_id", AutomotivePduBench.SignalPduMessageId).ConfigureAwait(false);
                /*
                 Decoder stores raw UInt64 scaled back from physical 125: (125-100)/0.25 = 100.
                */
                await ProtocolTestHelper.AssertU64Field(stack, packet, "signal_pdu.signal.raw", 100).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(jsonDir, recursive: true);
        }
    }

    private static Action<SettingsManager> _ConfigureSignalSettings(string jsonPath) =>
        sm => sm.PreloadValue("signal_pdu.config_file", jsonPath);
}
