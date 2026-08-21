// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>JSON bridge round-trip coverage for <see cref="SignalMessageConfigBridge"/>.</summary>
internal sealed class SignalMessageConfigBridgeTests
{
    [Test]
    public async Task SerializeJson_RoundTripsDispatchBindings()
    {
        SignalMessageLayout layout = new()
        {
            Name = "bridge_test",
            UiName = "Bridge Test",
            ByteLength = 2,
            Signals = [],
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding { Table = UdpProtocol.PortTableName, Key = 4711UL }),
        };

        string json = SignalMessageConfigBridge.SerializeJson(layout);
        await Assert.That(json.Contains("dispatch_bindings", StringComparison.Ordinal)).IsTrue();

        SignalMessagesConfig? config = JsonSerializer.Deserialize(
            json,
            SignalMessagesConfigContext.Default.SignalMessagesConfig);
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Messages.Length).IsEqualTo(1);
        await Assert.That(config.Messages[0].DispatchBindings.Length).IsEqualTo(1);
        await Assert.That(config.Messages[0].DispatchBindings[0].Table).IsEqualTo(UdpProtocol.PortTableName);
        await Assert.That(config.Messages[0].DispatchBindings[0].Key).IsEqualTo(4711UL);
    }

    [Test]
    public async Task SerializeJson_WritesQualifiedSignalAndMuxNames()
    {
        SignalMessageLayout layout = new()
        {
            Name = "bridge_msg",
            UiName = "Bridge Msg",
            ByteLength = 2,
            Signals = ImmutableArray.Create(
                new SignalSpec
                {
                    Name = "rpm",
                    UiName = "RPM",
                    StartBit = 0,
                    BitLength = 8,
                    Endian = SignalEndian.Little,
                    Factor = 1.0,
                    Offset = 0.0,
                    Unit = string.Empty,
                }),
            Mux = new MuxSpec
            {
                Name = "mux",
                UiName = "Mux",
                StartBit = 8,
                BitLength = 8,
                Endian = SignalEndian.Little,
            },
            MuxGroups = ImmutableArray.Create(
                new MuxGroupSpec
                {
                    MuxValue = 0,
                    Signals = ImmutableArray.Create(
                        new SignalSpec
                        {
                            Name = "alt",
                            UiName = "Alt",
                            StartBit = 8,
                            BitLength = 8,
                            Endian = SignalEndian.Little,
                            Factor = 1.0,
                            Offset = 0.0,
                            Unit = string.Empty,
                        }),
                }),
        };

        SignalMessagesConfig config = SignalMessageConfigBridge.FromLayout(layout);
        MuxSignalConfig mux = config.Messages[0].MuxSignal!;
        await Assert.That(config.Messages[0].Signals[0].Name).IsEqualTo("bridge_msg.rpm");
        await Assert.That(config.Messages[0].Signals[0].UiName).IsEqualTo("RPM");
        await Assert.That(mux.Name).IsEqualTo("bridge_msg.mux");
        await Assert.That(mux.UiName).IsEqualTo("Mux");
        await Assert.That(config.Messages[0].MuxGroups[0].Signals[0].Name).IsEqualTo("bridge_msg.alt");
    }
}
