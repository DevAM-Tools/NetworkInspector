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
/// filtering layer: a filter that asks "does this packet contain JSON / a Signal Message?" must get a
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
        bool jsonKeyBeforeMaterialize = packet.TryGetFieldValue(jsonKeyFieldId!.Value, out _, materialize: false); // materialize: false — prove field absent without triggering lazy
        await Assert.That(jsonKeyBeforeMaterialize).IsFalse()
            .Because("index presence must be recorded without materializing the lazy JSON field tree");

        // After explicit materialization, the lazy field tree becomes available.
        packet.MaterializeAll();
        bool jsonKeyAfterMaterialize = packet.TryGetFieldValue(jsonKeyFieldId!.Value, out _, materialize: false); // materialize: false — already MaterializeAll(); lookup only
        await Assert.That(jsonKeyAfterMaterialize).IsTrue()
            .Because("materialization must build the previously deferred JSON fields");
    }

    #endregion

    #region PDU-Transport → Signal Message

    [Test]
    public async Task PduTransportSignal_SubProtocolPresence_QueryableFromIndexWithoutMaterialization()
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

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_idx_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");
        string sigJson = Path.Combine(work, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(pduJson, PduTransportConfigBridge.SerializeJson(AutomotivePduBench.PduTransportRegistry))
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(sigJson, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            using Stack stack = ProtocolTestHelper.BuildStack(sm =>
            {
                sm.PreloadValue("pdu_transport.config_file", pduJson);
                sm.PreloadValue("pdu_transport.udp_dispatch_port", (ulong)AutomotivePduBench.UdpPduTransportDestinationPort);
                sm.PreloadValue("signal_message.config_file", sigJson);
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

            ProtocolId? messageId = stack.GetProtocolId("fixture_message");
            await Assert.That(messageId).IsNotNull().Because("Signal message protocol must be registered");

            IndexGroupId? messageGroupId = stack.GetIndexGroupId("fixture_message");
            await Assert.That(messageGroupId).IsNotNull().Because("Signal message index group must be registered");

            FieldId? pduTransportIdField = stack.GetFieldId("pdu_transport.id");
            await Assert.That(pduTransportIdField).IsNotNull().Because("pdu_transport.id field must be registered");

            // The dispatched message protocol must be queryable from the index BEFORE any
            // materialization, proving PDU-Transport dispatch ran eagerly during ParseFrameIndexed.
            await Assert.That(index.GetProtocolBitmap(messageId!.Value).Contains(0)).IsTrue()
                .Because("eager PDU-Transport dispatch must record message protocol presence in the index");
            await Assert.That(index.GetGroupBitmap(messageGroupId!.Value).Contains(0)).IsTrue()
                .Because("eager PDU-Transport dispatch must record the message index group in the index");

            // Header fields are eager (Ethernet / UDP pattern). Index presence for the
            // dispatched Signal Message must still be recorded without materializing that
            // message's lazy signal tree.
            bool pduIdBeforeMaterialize = packet.TryGetFieldValue(pduTransportIdField!.Value, out FieldValue pduIdEager, materialize: false);
            await Assert.That(pduIdBeforeMaterialize).IsTrue()
                .Because("pdu_transport.id is appended eagerly during Parse, like eth.type / udp.srcport");
            bool isU64Eager = pduIdEager.Data.TryGetAsU64(out ulong pduIdNumericEager);
            await Assert.That(isU64Eager).IsTrue();
            await Assert.That(pduIdNumericEager).IsEqualTo(AutomotivePduBench.PduTransportWireId);

            FieldId? engineRpmField = stack.GetFieldId("fixture_message.EngineRpm");
            await Assert.That(engineRpmField).IsNotNull();
            bool rpmBeforeMaterialize = packet.TryGetFieldValue(engineRpmField!.Value, out _, materialize: false);
            await Assert.That(rpmBeforeMaterialize).IsFalse()
                .Because("Signal Message signal fields stay lazy until materialization");

            packet.MaterializeAll();
            bool rpmAfterMaterialize = packet.TryGetFieldValue(engineRpmField.Value, out _, materialize: false);
            await Assert.That(rpmAfterMaterialize).IsTrue()
                .Because("materialization must build the previously deferred Signal Message fields");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    #endregion
}
