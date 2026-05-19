// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Protocols.Tests.Infrastructure.Bridges;
using NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

namespace NetworkInspector.Protocols.Tests.PduTransport;

/// <summary>
/// PDU-Transport — symmetric tshark check for framing only (opaque inner payload bytes).
/// </summary>
internal sealed class PduTransportStandaloneTsharkTests
{
    [Test]
    public async Task Tshark_OverUdp_HeaderFieldsMirrorNi()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        ReadOnlyMemory<byte> opaque = new byte[] { 0xCA, 0xFE, 0xBE, 0xEF };

        SignalPduLayer rawInner = SignalPduLayer.FromRawBytes(opaque);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 18001,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalPdu: rawInner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pt_stshark_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");

        string personalRoot = Path.Combine(Path.GetTempPath(), "ni_ws_personal_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(personalRoot);
        try
        {
            await File.WriteAllTextAsync(pduJson, PduTransportConfigBridge.SerializeJson(AutomotivePduBench.PduTransportRegistry))
                .ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm =>
                {
                    sm.PreloadValue("pdu_transport.config_file", pduJson);
                    sm.PreloadValue("pdu_transport.udp_dispatch_port", (ulong)AutomotivePduBench.UdpPduTransportDestinationPort);
                });

            using (stack)
            {
                string decodeAs =
                    $"udp.port=={AutomotivePduBench.UdpPduTransportDestinationPort.ToString(System.Globalization.CultureInfo.InvariantCulture)},pdu_transport";

                string profileDir = TsharkPduTransportSignalUatProfile.CreateProfileDirectoryUnderEphemeralPersonalDir(personalRoot, "ni_standalone");
                TsharkPduTransportSignalUatProfile.EmitPduTransportUdpDescriptors(
                    profileDir, AutomotivePduBench.UdpPduTransportDestinationPort, AutomotivePduBench.PduTransportRegistry);

                await TsharkAssert.AssertEquivalentMany(
                    stack,
                    packet,
                    frame,
                    1,
                    profileDir,
                    decodeAs,
                    ("pdu_transport.id", "pdu_transport.id"),
                    ("pdu_transport.length", "pdu_transport.length")).ConfigureAwait(false);
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
