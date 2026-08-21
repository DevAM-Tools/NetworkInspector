// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Parser round-trip coverage for Signal Message payloads carried over FlexRay
/// (LINKTYPE_FLEXRAY, link type 210), dispatched via the <c>flexray.id</c> table.
/// Dispatch key encodes slot (bits [10:0]), channel B (bit 11), and cycle (bits [17:12])
/// via <see cref="FlexRayLinkTypeFrame.EncodeDispatchKey"/>.
/// </summary>
internal sealed class SignalMessageFlexRayTests
{
    /// <summary>FlexRay Channel A, slot 42, cycle 0.</summary>
    private const ushort _BenchFrameIdChannelA = 42;

    private const byte _BenchCycle = 0;

    /// <summary>FlexRay Channel A, slot 42, cycle 7.</summary>
    private const byte _BenchCycleNonZero = 7;

    /// <summary>FlexRay Channel B, slot 42, cycle 0.</summary>
    private static ulong _BenchKeyChannelB =>
        FlexRayLinkTypeFrame.EncodeDispatchKey(_BenchFrameIdChannelA, channelB: true, _BenchCycle);

    private static ulong _BenchKeyChannelACycle7 =>
        FlexRayLinkTypeFrame.EncodeDispatchKey(_BenchFrameIdChannelA, channelB: false, _BenchCycleNonZero);

    private static SignalMessageLayout _ChannelALayout =>
        new()
        {
            PduId = 0x200,
            Name = "flexray_signal_a",
            UiName = "FlexRay Signal A",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = FlexRayProtocol.IdTableName,
                    Key = FlexRayLinkTypeFrame.EncodeDispatchKey(_BenchFrameIdChannelA, channelB: false, _BenchCycle),
                }),
            Mux = null,
            MuxGroups = [],
        };

    private static SignalMessageLayout _ChannelBLayout =>
        new()
        {
            PduId = 0x201,
            Name = "flexray_signal_b",
            UiName = "FlexRay Signal B",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = FlexRayProtocol.IdTableName,
                    Key = _BenchKeyChannelB,
                }),
            Mux = null,
            MuxGroups = [],
        };

    /// <summary>
    /// Builds a FlexRay frame carrying the encoded signal bytes at the given slot and channel,
    /// writes a JSON config, parses with a full stack, and asserts the decoded signal values.
    /// Signal EngineRpm physical 125.0 ⇒ raw (125 − 100) / 0.25 = 100.
    /// Signal Thr physical 555 ⇒ raw 555.
    /// </summary>
    [Test]
    public async Task Parses_ChannelA_MatchesExpectedFields()
    {
        SignalMessageLayout layout = _ChannelALayout;
        await _RunFlexRayRoundTripTest(layout, frameId: _BenchFrameIdChannelA, channelB: false, cycle: _BenchCycle).ConfigureAwait(false);
    }

    /// <summary>
    /// Same as the Channel A test but with Channel B set in the FlexRay layer.
    /// The dispatch key must include <see cref="FlexRayProtocol.ChannelBKeyBit"/>.
    /// </summary>
    [Test]
    public async Task Parses_ChannelB_MatchesExpectedFields()
    {
        SignalMessageLayout layout = _ChannelBLayout;
        await _RunFlexRayRoundTripTest(layout, frameId: _BenchFrameIdChannelA, channelB: true, cycle: _BenchCycle).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatch key must include the cycle count in bits [17:12] so that cycle-multiplexed
    /// PDUs on the same slot resolve to distinct <c>flexray.id</c> entries.
    /// </summary>
    [Test]
    public async Task Parses_ChannelA_NonZeroCycle_MatchesExpectedFields()
    {
        SignalMessageLayout layout = new()
        {
            PduId = 0x202,
            Name = "flexray_signal_cycle7",
            UiName = "FlexRay Signal Cycle 7",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = FlexRayProtocol.IdTableName,
                    Key = _BenchKeyChannelACycle7,
                }),
            Mux = null,
            MuxGroups = [],
        };

        await _RunFlexRayRoundTripTest(
            layout,
            frameId: _BenchFrameIdChannelA,
            channelB: false,
            cycle: _BenchCycleNonZero).ConfigureAwait(false);
    }

    private static async Task _RunFlexRayRoundTripTest(
        SignalMessageLayout layout, ushort frameId, bool channelB, byte cycle = 0)
    {
        SignalMessageValueSet vals = SignalMessageValueSet.For(layout)
            .Set("EngineRpm", 125.0)
            .Set("Thr", 555);

        SignalMessageLayer spdu = new(layout, vals);

        // Encode signal bytes first — FlexRayLayer is IRootLayer and does not chain .Then(payloadLayer).
        byte[] signalBytes = new byte[layout.ByteLength];
        spdu.WriteHeader(signalBytes.AsSpan());

        byte[] frame = FrameStack
            .Start(new FlexRayLayer(frameId, cycleCount: cycle, payload: signalBytes.AsMemory(), channelB: channelB))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_signal_message_flexray_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(jsonPath, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", jsonPath),
                LinkType.Flexray);

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(
                    stack, packet, layout.Name + ".EngineRpm", 125.0).ConfigureAwait(false);
                await ProtocolTestHelper.AssertF64Field(
                    stack, packet, layout.Name + ".Thr", 555.0).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(jsonDir, recursive: true);
        }
    }
}
