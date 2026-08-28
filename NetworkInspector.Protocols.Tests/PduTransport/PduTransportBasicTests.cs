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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array([(ulong)AutomotivePduBench.UdpPduTransportDestinationPort]));
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

    [Test]
    public async Task Parses_SinglePduWithSignal_SignalMessageIsSiblingOfPduTransport()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 15002,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_tree_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array([(ulong)AutomotivePduBench.UdpPduTransportDestinationPort]));
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                FieldId pduTransportId = stack.GetFieldId("pdu_transport")!.Value;
                FieldId pduNodeId = stack.GetFieldId("pdu_transport.pdu")!.Value;
                FieldId pduIdField = stack.GetFieldId("pdu_transport.id")!.Value;
                FieldId fixtureId = stack.GetFieldId("fixture_message")!.Value;

                Field root = packet.RootField();
                await Assert.That(_TryFindField(root, pduTransportId, out Field pduTransport)).IsTrue();
                await Assert.That(_TryFindField(root, fixtureId, out Field fixture)).IsTrue();

                bool pduHasParent = pduTransport.TryGetParent(out Field pduParent);
                bool fixtureHasParent = fixture.TryGetParent(out Field fixtureParent);
                await Assert.That(pduHasParent).IsTrue();
                await Assert.That(fixtureHasParent).IsTrue();
                await Assert.That(fixtureParent == pduParent).IsTrue()
                    .Because("Signal Message must be a sibling of pdu_transport (dispatch on parentField), like UDP under IPv6.");

                bool fixtureIsChildOfPduTransport = false;
                bool hasPduNode = false;
                bool idIsUnderPduNode = false;
                foreach (Field child in pduTransport.Children(materialize: true))
                {
                    if (child.FieldId == fixtureId)
                    {
                        fixtureIsChildOfPduTransport = true;
                    }

                    if (child.FieldId == pduNodeId)
                    {
                        hasPduNode = true;
                        foreach (Field grand in child.Children(materialize: true))
                        {
                            if (grand.FieldId == pduIdField)
                            {
                                idIsUnderPduNode = true;
                            }
                        }
                    }
                }

                await Assert.That(fixtureIsChildOfPduTransport).IsFalse()
                    .Because("fixture_message must not hang under the pdu_transport container.");
                await Assert.That(hasPduNode).IsTrue();
                await Assert.That(idIsUnderPduNode).IsTrue()
                    .Because("pdu_transport.id must be a child of pdu_transport.pdu.");

                FieldId engineRpmId = stack.GetFieldId("fixture_message.EngineRpm")!.Value;
                await Assert.That(_TryFindField(fixture, engineRpmId, out Field rpm)).IsTrue()
                    .Because("Signal fields must materialize under the message container.");
                bool rpmHasParent = rpm.TryGetParent(out Field rpmParent);
                await Assert.That(rpmHasParent).IsTrue();
                await Assert.That(rpmParent == fixture).IsTrue()
                    .Because("fixture_message.EngineRpm must be a child of fixture_message, not of pdu_transport.pdu.");

                bool rpmUnderPdu = false;
                foreach (Field child in pduTransport.Children(materialize: true))
                {
                    if (child.FieldId == pduNodeId)
                    {
                        foreach (Field grand in child.Children(materialize: true))
                        {
                            if (grand.FieldId == engineRpmId || grand.FieldId == fixtureId)
                            {
                                rpmUnderPdu = true;
                            }
                        }
                    }
                }

                await Assert.That(rpmUnderPdu).IsFalse()
                    .Because("Signal Message fields must not hang under pdu_transport.pdu.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parses_ConcatenatedPdus_PayloadHangsUnderEachPduNode()
    {
        PduTransportConfigFb fb = AutomotivePduBench.PduTransportRegistry;
        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportMulti(
            udpSrcPort: 15003,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: fb,
            new PduTransportSlot(AutomotivePduBench.PduTransportWireId, new byte[] { 0x11 }),
            new PduTransportSlot(0x21, new byte[] { 0x22, 0x33 }));

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_multi_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");
        try
        {
            await File.WriteAllTextAsync(pduJson, PduTransportConfigBridge.SerializeJson(fb)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm =>
                {
                    sm.PreloadValue("pdu_transport.config_file", pduJson);
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array([(ulong)AutomotivePduBench.UdpPduTransportDestinationPort]));
                });

            using (stack)
            {
                FieldId pduTransportId = stack.GetFieldId("pdu_transport")!.Value;
                FieldId pduNodeId = stack.GetFieldId("pdu_transport.pdu")!.Value;
                FieldId payloadId = stack.GetFieldId("pdu_transport.payload")!.Value;

                Field root = packet.RootField();
                await Assert.That(_TryFindField(root, pduTransportId, out Field pduTransport)).IsTrue();

                int pduNodes = 0;
                int payloadsUnderPdu = 0;
                int payloadsDirectlyOnContainer = 0;
                foreach (Field child in pduTransport.Children(materialize: true))
                {
                    if (child.FieldId == payloadId)
                    {
                        payloadsDirectlyOnContainer++;
                    }

                    if (child.FieldId != pduNodeId)
                    {
                        continue;
                    }

                    pduNodes++;
                    foreach (Field grand in child.Children(materialize: true))
                    {
                        if (grand.FieldId == payloadId)
                        {
                            payloadsUnderPdu++;
                        }
                    }
                }

                await Assert.That(pduNodes).IsEqualTo(2);
                await Assert.That(payloadsUnderPdu).IsEqualTo(2)
                    .Because("Undispatched payload must hang under the PDU that owns it, not as a sibling of all PDU nodes.");
                await Assert.That(payloadsDirectlyOnContainer).IsEqualTo(0);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parses_WithoutUdpDispatchPort_DoesNotSelectPduTransport()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 15004,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_noport_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                FieldId pduIdField = stack.GetFieldId("pdu_transport.id")!.Value;
                FieldId rpmId = stack.GetFieldId("fixture_message.EngineRpm")!.Value;
                bool hasPdu = packet.TryGetFieldValue(pduIdField, out _, materialize: true);
                bool hasRpm = packet.TryGetFieldValue(rpmId, out _, materialize: true);
                await Assert.That(hasPdu).IsFalse()
                    .Because("pdu_transport.udp_dispatch_ports default empty must not bind PDU Transport on UDP.");
                await Assert.That(hasRpm).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parses_WrongDispatchBindingKey_LeavesPayloadUnderPdu()
    {
        SignalMessageLayout layout = new()
        {
            PduId = AutomotivePduBench.SignalMessageBenchId,
            Name = "fixture_message",
            UiName = "Fixture PDU",
            ByteLength = AutomotivePduBench.TwoSequentialUint16LeLayout.ByteLength,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = PduTransportProtocol.IdTableName,
                    Key = 0x99UL,
                }),
            Mux = null,
            MuxGroups = default,
        };

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 15005,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_badkey_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array([(ulong)AutomotivePduBench.UdpPduTransportDestinationPort]));
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                await ProtocolTestHelper
                    .AssertU64Field(stack, packet, "pdu_transport.id", AutomotivePduBench.PduTransportWireId)
                    .ConfigureAwait(false);

                FieldId rpmId = stack.GetFieldId("fixture_message.EngineRpm")!.Value;
                bool hasRpm = packet.TryGetFieldValue(rpmId, out _, materialize: true);
                await Assert.That(hasRpm).IsFalse()
                    .Because("dispatch_bindings.key must match the on-wire PDU ID.");

                FieldId payloadId = stack.GetFieldId("pdu_transport.payload")!.Value;
                FieldId pduNodeId = stack.GetFieldId("pdu_transport.pdu")!.Value;
                FieldId pduTransportId = stack.GetFieldId("pdu_transport")!.Value;
                Field root = packet.RootField();
                await Assert.That(_TryFindField(root, pduTransportId, out Field pduTransport)).IsTrue();

                bool payloadUnderPdu = false;
                foreach (Field child in pduTransport.Children(materialize: true))
                {
                    if (child.FieldId != pduNodeId)
                    {
                        continue;
                    }

                    foreach (Field grand in child.Children(materialize: true))
                    {
                        if (grand.FieldId == payloadId)
                        {
                            payloadUnderPdu = true;
                        }
                    }
                }

                await Assert.That(payloadUnderPdu).IsTrue()
                    .Because("Unmatched PDU payload must hang under pdu_transport.pdu.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parse_TwoConfiguredUdpPorts_BothDatagramsHavePduTransportId()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        const ushort secondPort = 47291;
        byte[] frameA = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 16001,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);
        byte[] frameB = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 16002,
            udpDstPort: secondPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_twoport_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        string pduJson = Path.Combine(work, "pdutr.json");
        string sigJson = Path.Combine(work, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(pduJson, PduTransportConfigBridge.SerializeJson(AutomotivePduBench.PduTransportRegistry))
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(sigJson, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            Action<SettingsManager> configure = sm =>
            {
                sm.PreloadValue("pdu_transport.config_file", pduJson);
                sm.PreloadValue(
                    PduTransportRegistration.UdpDispatchPortsSetting,
                    SettingValue.U64Array(
                    [
                        (ulong)AutomotivePduBench.UdpPduTransportDestinationPort,
                        secondPort,
                    ]));
                sm.PreloadValue("signal_message.config_file", sigJson);
            };

            (Stack stackA, Packet packetA) = ProtocolTestHelper.BuildAndParse(frameA, configure);
            using (stackA)
            {
                await ProtocolTestHelper
                    .AssertU64Field(stackA, packetA, "pdu_transport.id", AutomotivePduBench.PduTransportWireId)
                    .ConfigureAwait(false);
            }

            (Stack stackB, Packet packetB) = ProtocolTestHelper.BuildAndParse(frameB, configure);
            using (stackB)
            {
                await ProtocolTestHelper
                    .AssertU64Field(stackB, packetB, "pdu_transport.id", AutomotivePduBench.PduTransportWireId)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parse_MixedInvalidUdpDispatchPorts_ValidPortStillDissects()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 16003,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_mixedport_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array(
                        [
                            (ulong)AutomotivePduBench.UdpPduTransportDestinationPort,
                            0UL,
                            65536UL,
                        ]));
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                await ProtocolTestHelper
                    .AssertU64Field(stack, packet, "pdu_transport.id", AutomotivePduBench.PduTransportWireId)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parse_ScalarPreloadOnUdpDispatchPorts_DoesNotDissect()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 16004,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_scalarport_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        (ulong)AutomotivePduBench.UdpPduTransportDestinationPort);
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                FieldId pduIdField = stack.GetFieldId("pdu_transport.id")!.Value;
                bool hasPdu = packet.TryGetFieldValue(pduIdField, out _, materialize: true);
                await Assert.That(hasPdu).IsFalse()
                    .Because("A scalar ulong preload on pdu_transport.udp_dispatch_ports must not bind PDU Transport.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parse_ListedSourcePort_DissectsPduTransportId()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            udpDstPort: 16005,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_srcport_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array([(ulong)AutomotivePduBench.UdpPduTransportDestinationPort]));
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                await ProtocolTestHelper
                    .AssertU64Field(stack, packet, "pdu_transport.id", AutomotivePduBench.PduTransportWireId)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parse_LeftoverUdpDispatchPortSetting_DoesNotDissect()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 16006,
            udpDstPort: AutomotivePduBench.UdpPduTransportDestinationPort,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_oldkey_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue("pdu_transport.udp_dispatch_port", 47290UL);
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                FieldId pduIdField = stack.GetFieldId("pdu_transport.id")!.Value;
                bool hasPdu = packet.TryGetFieldValue(pduIdField, out _, materialize: true);
                await Assert.That(hasPdu).IsFalse()
                    .Because("Leftover pdu_transport.udp_dispatch_port must not bind PDU Transport.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Test]
    public async Task Parse_ZeroDestinationWithMixedList_DoesNotDissect()
    {
        SignalMessageLayout layout = AutomotivePduBench.TwoSequentialUint16LeLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        SignalMessageLayer inner = new(layout, vals);

        byte[] frame = AutomotiveEthUdpFrames.EncapsulatePduTransportSignal(
            udpSrcPort: 16007,
            udpDstPort: 0,
            pduFb: AutomotivePduBench.PduTransportRegistry,
            pduWireId: AutomotivePduBench.PduTransportWireId,
            signalMessage: inner);

        string work = Path.Combine(Path.GetTempPath(), "ni_pdu_tr_zerodst_" + Guid.NewGuid().ToString("N"));
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
                    sm.PreloadValue(
                        PduTransportRegistration.UdpDispatchPortsSetting,
                        SettingValue.U64Array(
                        [
                            (ulong)AutomotivePduBench.UdpPduTransportDestinationPort,
                            0UL,
                            65536UL,
                        ]));
                    sm.PreloadValue("signal_message.config_file", sigJson);
                });

            using (stack)
            {
                FieldId pduIdField = stack.GetFieldId("pdu_transport.id")!.Value;
                bool hasPdu = packet.TryGetFieldValue(pduIdField, out _, materialize: true);
                await Assert.That(hasPdu).IsFalse()
                    .Because("Filtered-out port 0 must not bind PDU Transport even when listed next to a valid port.");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static bool _TryFindField(in Field start, FieldId id, out Field found)
    {
        if (start.FieldId == id)
        {
            found = start;
            return true;
        }

        foreach (Field child in start.Children(materialize: true))
        {
            if (_TryFindField(in child, id, out found))
            {
                return true;
            }
        }

        found = default;
        return false;
    }
}
