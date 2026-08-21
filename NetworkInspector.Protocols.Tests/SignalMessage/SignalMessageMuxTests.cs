// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Mux group selection E2E: JSON bridge → stack parse → mux-selected signal fields.
/// Layout: 1 byte mux at bit 0 (LE, 8 bits) + 16-bit LE payload signal starting at bit 8.
/// </summary>
internal sealed class SignalMessageMuxTests
{
    private const ushort _UdpPort = 17100;

    private static SignalMessageLayout _MuxLayout =>
        new()
        {
            Name = "mux_msg",
            UiName = "Mux Message",
            ByteLength = 3,
            Signals = [],
            Mux = new MuxSpec
            {
                Name = "mux",
                UiName = "Multiplexer",
                StartBit = 0,
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
                            Name = "group0_val",
                            UiName = "Group0 Value",
                            StartBit = 8,
                            BitLength = 16,
                            Endian = SignalEndian.Little,
                            Factor = 1.0,
                            Offset = 0.0,
                            Unit = string.Empty,
                        }),
                },
                new MuxGroupSpec
                {
                    MuxValue = 1,
                    Signals = ImmutableArray.Create(
                        new SignalSpec
                        {
                            Name = "group1_val",
                            UiName = "Group1 Value",
                            StartBit = 8,
                            BitLength = 16,
                            Endian = SignalEndian.Little,
                            Factor = 1.0,
                            Offset = 0.0,
                            Unit = string.Empty,
                        }),
                }),
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = UdpProtocol.PortTableName,
                    Key = _UdpPort,
                }),
        };

    [Test]
    public async Task Parses_MuxValue0_SelectsGroup0Signal()
    {
        SignalMessageLayout layout = _MuxLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout)
            .SetRaw("mux", 0)
            .SetRaw("group0_val", 0x1234);

        await _RunMuxRoundTrip(layout, vals, muxValue: 0, expectedField: "mux_msg.group0_val", expectedRaw: 0x1234)
            .ConfigureAwait(false);
    }

    [Test]
    public async Task Parses_MuxValue1_SelectsGroup1Signal()
    {
        SignalMessageLayout layout = _MuxLayout;
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout)
            .SetRaw("mux", 1)
            .SetRaw("group1_val", 0xABCD);

        await _RunMuxRoundTrip(layout, vals, muxValue: 1, expectedField: "mux_msg.group1_val", expectedRaw: 0xABCD)
            .ConfigureAwait(false);
    }

    private static async Task _RunMuxRoundTrip(
        SignalMessageLayout layout,
        SignalMessageValueSet vals,
        ulong muxValue,
        string expectedField,
        ulong expectedRaw)
    {
        SignalMessageLayer spdu = new(layout, vals);
        byte[] frame = FrameStack
            .Start(AutomotiveEthUdpFrames.TestEthernet())
            .Then(AutomotiveEthUdpFrames.TestIpv4())
            .Then(new UdpLayer(15000, _UdpPort))
            .Then(spdu)
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_mux_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", path));

            using (stack)
            {
                await ProtocolTestHelper.AssertU64Field(stack, packet, "mux_msg.mux.value", muxValue)
                    .ConfigureAwait(false);
                await ProtocolTestHelper.AssertF64Field(stack, packet, expectedField, expectedRaw).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
