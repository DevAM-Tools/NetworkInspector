// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Parser round-trip coverage for Signal Message payloads carried over classic CAN
/// (LINKTYPE_CAN_SOCKETCAN, link type 227), dispatched via the <c>can.id</c> table.
/// Classic CAN frames are limited to 8 data bytes, so the signal PDU layout must have
/// <c>ByteLength</c> ≤ 8. The message container is a sibling of <c>can</c>, not a child.
/// </summary>
internal sealed class SignalMessageCanTests
{
    /// <summary>Standard 11-bit CAN ID used for the happy-path round-trip.</summary>
    private const uint _BenchCanId = 0x100;

    private static SignalMessageLayout _CanLayout =>
        new()
        {
            PduId = 0x100,
            Name = "can_signal",
            UiName = "CAN Signal",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = CanProtocol.IdTableName,
                    Key = _BenchCanId,
                }),
            Mux = null,
            MuxGroups = [],
        };

    /// <summary>
    /// Builds a classic standard-ID CAN frame carrying the encoded signal bytes,
    /// writes a JSON config, parses with a full stack, and asserts the decoded signal values.
    /// Signal EngineRpm physical 125.0 ⇒ raw (125 − 100) / 0.25 = 100.
    /// Signal Thr physical 555 ⇒ raw 555.
    /// </summary>
    [Test]
    public async Task Parses_StandardId_MatchesExpectedFields()
    {
        SignalMessageLayout layout = _CanLayout;

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout)
            .Set("EngineRpm", 125.0)
            .Set("Thr", 555);

        SignalMessageLayer spdu = new(layout, vals);

        byte[] signalBytes = new byte[layout.ByteLength];
        spdu.WriteHeader(signalBytes.AsSpan());

        byte[] frame = FrameStack
            .Start(new SocketCanLayer(_BenchCanId, signalBytes))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_signal_message_can_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(jsonPath, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", jsonPath),
                LinkType.CanSocketcan);

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
