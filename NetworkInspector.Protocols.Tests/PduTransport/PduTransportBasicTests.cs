// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.PduTransport;

/// <summary>
/// Parser verification for PDU-Transport headers over UDP dispatch plus nested Signal Message payloads.
/// </summary>
internal sealed class PduTransportBasicTests
{
    [Test]
    public async Task Parses_SinglePduWithSignal_FieldsExpected()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 15001,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_basic_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");
        string sigJson = Path.Combine(work, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(pduJson, PduTransportConfigBridge.SerializeJson(AutomotivePduBench.PduTransportRegistry))
                .ConfigureAwait(false);

            await File.WriteAllTextAsync(sigJson, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm =>
                {
                    sm.PreloadValue("pdu_transport.config_file", pduJson);
                    sm.PreloadValue("pdu_transport.udp_dispatch_port", (ulong)AutomotivePduBench.UdpPduTransportDestinationPort);
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                await ProtocolTestHelper
                    .AssertU64Field(stack, packet, "pdu_transport.id", AutomotivePduBench.PduTransportWireId).ConfigureAwait(false);
                await ProtocolTestHelper.AssertU64Field(stack, packet, "pdu_transport.length", (ulong)layout.ByteLength).ConfigureAwait(false);
                await ProtocolTestHelper.AssertStringField(stack, packet, "pdu_transport.name", "BenchPdu").ConfigureAwait(false);
                await ProtocolTestHelper.AssertF64Field(stack, packet, "fixture_message.EngineRpm", 125.0).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
