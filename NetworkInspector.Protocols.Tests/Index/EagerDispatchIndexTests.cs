// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Index;

/// <summary>
/// Verifies that sub-protocol dispatch is performed eagerly during <see cref="Packet.ParseFrameIndexed(PacketId, Stack, Frame, NetworkInspector.Core.Index.PacketIndex)"/>
/// so that the dispatched sub-protocol's group and protocol presence are recorded in the
/// <see cref="NetworkInspector.Core.Index.PacketIndex"/> and become queryable WITHOUT triggering
/// materialization of the lazy descriptive field tree.
///
/// <para>
/// This is the load-bearing guarantee that allows the presence index to be the single reliable
/// filtering layer: a filter that asks "does this packet contain JSON / a Signal-PDU?" must get a
/// correct answer from the index alone, even though the human-readable fields of the carrying
/// protocol (HTTP, PDU-Transport) are only built lazily on demand.
/// </para>
/// </summary>
internal sealed class EagerDispatchIndexTests
{
    #region HTTP-over-TCP → JSON

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static readonly IPv4Address _ClientIp = new(0x0A000001); // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002); // 10.0.0.2

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP + HTTP POST frame carrying a JSON body with
    /// Content-Type "application/json" so HTTP dispatches the body to the JSON protocol.
    /// </summary>
    private static byte[] _BuildJsonHttpFrame(string jsonBody)
    {
        string httpMessage =
            "POST /api HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {jsonBody.Length}\r\n" +
            "\r\n" +
            jsonBody;

        byte[] httpBytes = Encoding.ASCII.GetBytes(httpMessage);
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);
    }

    [Test]
    public async Task HttpJsonBody_SubProtocolPresence_QueryableFromIndexWithoutMaterialization()
    {
        byte[] frame = _BuildJsonHttpFrame("{\"name\":\"John\"}");

        using Stack stack = ProtocolTestHelper.BuildStack();

        NetworkInspector.Core.Index.PacketIndex index = new(stack);

        Frame parsedFrame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frame,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrameIndexed(new PacketId(0), stack, parsedFrame, index);

        ProtocolId? jsonId = stack.GetProtocolId("json");
        await Assert.That(jsonId).IsNotNull().Because("JSON protocol must be registered");

        IndexGroupId? jsonGroupId = stack.GetIndexGroupId("json");
        await Assert.That(jsonGroupId).IsNotNull().Because("JSON index group must be registered");

        FieldId? jsonKeyFieldId = stack.GetFieldId("json.key");
        await Assert.That(jsonKeyFieldId).IsNotNull().Because("json.key field must be registered");

        // The dispatched JSON sub-protocol must be queryable from the index BEFORE any materialization,
        // proving HTTP body dispatch ran eagerly during ParseFrameIndexed.
        await Assert.That(index.GetProtocolBitmap(jsonId!.Value).Contains(0)).IsTrue()
            .Because("eager HTTP body dispatch must record JSON protocol presence in the index");
        await Assert.That(index.GetGroupBitmap(jsonGroupId!.Value).Contains(0)).IsTrue()
            .Because("eager HTTP body dispatch must record the JSON index group in the index");

        // The lazy JSON descriptive field tree must NOT have been materialized yet: querying without
        // materialization must report the field as absent.
        bool jsonKeyBeforeMaterialize = packet.TryGetFieldValue(jsonKeyFieldId!.Value, out _, materialize: false);
        await Assert.That(jsonKeyBeforeMaterialize).IsFalse()
            .Because("index presence must be recorded without materializing the lazy JSON field tree");

        // After explicit materialization, the lazy field tree becomes available.
        packet.MaterializeAll();
        bool jsonKeyAfterMaterialize = packet.TryGetFieldValue(jsonKeyFieldId!.Value, out _, materialize: false);
        await Assert.That(jsonKeyAfterMaterialize).IsTrue()
            .Because("materialization must build the previously deferred JSON fields");
    }

    #endregion

    #region PDU-Transport → Signal-PDU

    [Test]
    public async Task PduTransportSignal_SubProtocolPresence_QueryableFromIndexWithoutMaterialization()
    {
        SignalPduLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalValueSet vals = SignalValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalPduLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 15001,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalPdu: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_idx_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");
        string sigJson = Path.Combine(work, "signal_pdu.json");
        try
        {
            await File.WriteAllTextAsync(pduJson, PduTransportConfigBridge.SerializeJson(AutomotivePduBench.PduTransportRegistry))
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(sigJson, SignalPduConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            using Stack stack = ProtocolTestHelper.BuildStack(sm =>
            {
                sm.PreloadValue("pdu_transport.config_file", pduJson);
                sm.PreloadValue("pdu_transport.udp_dispatch_port", (ulong)AutomotivePduBench.UdpPduTransportDestinationPort);
                sm.PreloadValue("signal_pdu.config_file", sigJson);
            });

            NetworkInspector.Core.Index.PacketIndex index = new(stack);

            Frame parsedFrame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frame,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet packet = Packet.ParseFrameIndexed(new PacketId(0), stack, parsedFrame, index);

            ProtocolId? signalPduId = stack.GetProtocolId("signal_pdu");
            await Assert.That(signalPduId).IsNotNull().Because("Signal-PDU protocol must be registered");

            IndexGroupId? signalPduGroupId = stack.GetIndexGroupId("signal_pdu");
            await Assert.That(signalPduGroupId).IsNotNull().Because("Signal-PDU index group must be registered");

            FieldId? pduTransportIdField = stack.GetFieldId("pdu_transport.id");
            await Assert.That(pduTransportIdField).IsNotNull().Because("pdu_transport.id field must be registered");

            // The dispatched Signal-PDU sub-protocol must be queryable from the index BEFORE any
            // materialization, proving PDU-Transport dispatch ran eagerly during ParseFrameIndexed.
            await Assert.That(index.GetProtocolBitmap(signalPduId!.Value).Contains(0)).IsTrue()
                .Because("eager PDU-Transport dispatch must record Signal-PDU protocol presence in the index");
            await Assert.That(index.GetGroupBitmap(signalPduGroupId!.Value).Contains(0)).IsTrue()
                .Because("eager PDU-Transport dispatch must record the Signal-PDU index group in the index");

            // The lazy PDU-Transport descriptive field tree must NOT have been materialized yet.
            bool pduIdBeforeMaterialize = packet.TryGetFieldValue(pduTransportIdField!.Value, out _, materialize: false);
            await Assert.That(pduIdBeforeMaterialize).IsFalse()
                .Because("index presence must be recorded without materializing the lazy PDU-Transport field tree");

            // After explicit materialization, the lazy PDU-Transport descriptive fields become available.
            packet.MaterializeAll();
            bool pduIdAfterMaterialize = packet.TryGetFieldValue(pduTransportIdField!.Value, out FieldValue pduIdValue, materialize: false);
            await Assert.That(pduIdAfterMaterialize).IsTrue()
                .Because("materialization must build the previously deferred PDU-Transport fields");
            bool isU64 = pduIdValue.Data.TryGetAsU64(out ulong pduIdNumeric);
            await Assert.That(isU64).IsTrue().Because("pdu_transport.id must be a U64 value");
            await Assert.That(pduIdNumeric).IsEqualTo(AutomotivePduBench.PduTransportWireId);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    #endregion
}
