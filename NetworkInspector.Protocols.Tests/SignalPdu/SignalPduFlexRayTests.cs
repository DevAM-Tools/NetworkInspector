// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalPdu;

/// <summary>
/// Parser round-trip coverage for Signal-PDU payloads carried over FlexRay
/// (LINKTYPE_FLEXRAY, link type 210), dispatched via the <c>flexray.id</c> table.
/// Tests cover both Channel A and Channel B dispatch, using the channel-encoded key:
/// bits [10:0] = Frame ID, bit 11 = Channel B (<see cref="FlexRayProtocol.ChannelBKeyBit"/>).
/// </summary>
internal sealed class SignalPduFlexRayTests
{
    /// <summary>FlexRay Channel A, slot 42.</summary>
    private const ushort BenchFrameIdChannelA = 42;

    /// <summary>FlexRay Channel B, slot 42 — key = 42 | ChannelBKeyBit = 2090.</summary>
    private const ulong BenchKeyChannelB = BenchFrameIdChannelA | FlexRayProtocol.ChannelBKeyBit;

    private static SignalPduLayout ChannelALayout =>
        new()
        {
            PduId = 0x200,
            Name = "FlexRaySignalA",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            RegisterAt = ImmutableArray.Create(
                new DispatchBinding
                {
                    Table = FlexRayProtocol.IdTableName,
                    Key = BenchFrameIdChannelA,
                }),
            Mux = null,
            MuxGroups = [],
        };

    private static SignalPduLayout ChannelBLayout =>
        new()
        {
            PduId = 0x201,
            Name = "FlexRaySignalB",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            RegisterAt = ImmutableArray.Create(
                new DispatchBinding
                {
                    Table = FlexRayProtocol.IdTableName,
                    Key = BenchKeyChannelB,
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
        SignalPduLayout layout = ChannelALayout;
        await RunFlexRayRoundTripTest(layout, frameId: BenchFrameIdChannelA, channelB: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Same as the Channel A test but with Channel B set in the FlexRay layer.
    /// The dispatch key must include <see cref="FlexRayProtocol.ChannelBKeyBit"/>.
    /// </summary>
    [Test]
    public async Task Parses_ChannelB_MatchesExpectedFields()
    {
        SignalPduLayout layout = ChannelBLayout;
        await RunFlexRayRoundTripTest(layout, frameId: BenchFrameIdChannelA, channelB: true).ConfigureAwait(false);
    }

    private static async Task RunFlexRayRoundTripTest(SignalPduLayout layout, ushort frameId, bool channelB)
    {
        SignalValueSet vals = SignalValueSet.For(layout)
            .Set("EngineRpm", 125.0)
            .Set("Thr", 555);

        SignalPduLayer spdu = new(layout, vals);

        // Encode signal bytes first — FlexRayLayer is IRootLayer and does not chain .Then(payloadLayer).
        byte[] signalBytes = new byte[layout.ByteLength];
        spdu.WriteHeader(signalBytes.AsSpan());

        byte[] frame = FrameStack
            .Start(new FlexRayLayer(frameId, cycleCount: 0, payload: signalBytes.AsMemory(), channelB: channelB))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_signal_pdu_flexray_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_pdu.json");
        try
        {
            await File.WriteAllTextAsync(jsonPath, SignalPduConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_pdu.config_file", jsonPath),
                LinkType.Flexray);

            using (stack)
            {
                await ProtocolTestHelper.AssertU64Field(stack, packet, "signal_pdu.pdu_id", layout.PduId).ConfigureAwait(false);
                // EngineRpm physical 125.0 ⇒ raw (125 − 100) / 0.25 = 100.
                await ProtocolTestHelper.AssertU64Field(stack, packet, "signal_pdu.signal.raw", 100).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(jsonDir, recursive: true);
        }
    }
}
