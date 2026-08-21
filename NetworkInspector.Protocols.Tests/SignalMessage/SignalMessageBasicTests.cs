// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Parser round-trip for Signal Message payloads on UDP (<c>udp.port</c> dispatch).
/// </summary>
internal sealed class SignalMessageBasicTests
{
    private const ushort _BenchUdpDestinationPort = 16000;

    private static SignalMessageLayout _BenchUdpLayout =>
        new()
        {
            PduId = AutomotivePduBench.SignalMessageBenchId,
            Name = AutomotivePduBench.TwoSequentialUint16LeLayout.Name,
            UiName = AutomotivePduBench.TwoSequentialUint16LeLayout.UiName,
            ByteLength = AutomotivePduBench.TwoSequentialUint16LeLayout.ByteLength,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings =
                ImmutableArray.Create(
                    new FrameDispatchBinding
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
        SignalMessageLayout layout = _BenchUdpLayout;

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer spdu = new(layout, vals);

        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, _BenchUdpDestinationPort))
            .Then(spdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_signal_message_basic_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(jsonPath, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, _ConfigureSignalSettings(jsonPath));

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(stack, packet, "fixture_message.EngineRpm", 125.0).ConfigureAwait(false);
                await ProtocolTestHelper.AssertF64Field(stack, packet, "fixture_message.Thr", 555.0).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(jsonDir, recursive: true);
        }
    }

    private static Action<SettingsManager> _ConfigureSignalSettings(string jsonPath) =>
        sm => sm.PreloadValue("signal_message.config_file", jsonPath);
}
