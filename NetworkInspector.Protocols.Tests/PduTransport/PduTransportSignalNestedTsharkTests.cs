// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.PduTransport;

/// <summary>
/// Nested PDU-Transport → Signal Message symmetry against tshark with generated UAT profile.
/// </summary>
internal sealed class PduTransportSignalNestedTsharkTests
{
    [Test]
    public async Task Tshark_Nested_PduTransportAndSignalMessageFieldsMatchNi()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);

        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 18002,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pt_nested_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");
        string sigJson = Path.Combine(work, "signal_message.json");

        string personalRoot = Path.Combine(Path.GetTempPath(), "ni_ws_personal_nested_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(personalRoot);

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
                    sm.PreloadValue("signal_message.show_raw", true);
                });

            using (stack)
            {
                string decodeAs =
                    $"udp.port=={AutomotivePduBench.UdpPduTransportDestinationPort.ToString(System.Globalization.CultureInfo.InvariantCulture)},pdu_transport";

                string profileDir = TsharkPduTransportSignalMessageUatProfile.CreateProfileDirectoryUnderEphemeralPersonalDir(personalRoot, "nested");
                TsharkPduTransportSignalMessageUatProfile.EmitPduTransportOverUdpWithSignalMessage(
                    profileDir,
                    AutomotivePduBench.UdpPduTransportDestinationPort,
                    AutomotivePduBench.PduTransportRegistry,
                    AutomotivePduBench.PduTransportWireId,
                    layout);

                await TsharkAssert.AssertEquivalentMany(
                    stack,
                    packet,
                    frame,
                    1,
                    profileDir,
                    decodeAs,
                    ("pdu_transport.id", "pdu_transport.id"),
                    ("pdu_transport.length", "pdu_transport.length"),
                    ("fixture_message.EngineRpm.raw", "signal_pdu.signals.enginerpm_raw")).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex)
            {
                if (TestContext.Current?.OutputWriter is TextWriter tw)
                {
                    await tw.WriteLineAsync($"Warning: failed to delete temp directory '{work}': {ex.Message}").ConfigureAwait(false);
                }
            }

            try
            {
                Directory.Delete(personalRoot, recursive: true);
            }
            catch (Exception ex)
            {
                if (TestContext.Current?.OutputWriter is TextWriter tw)
                {
                    await tw.WriteLineAsync($"Warning: failed to delete temp directory '{personalRoot}': {ex.Message}").ConfigureAwait(false);
                }
            }
        }
    }
}
