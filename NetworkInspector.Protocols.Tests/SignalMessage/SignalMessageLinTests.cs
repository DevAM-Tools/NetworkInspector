// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Parser round-trip coverage for Signal Message payloads carried over LIN
/// (LINKTYPE_LIN, link type 212), dispatched via the <c>lin.id</c> table.
/// LIN frames are limited to 8 data bytes (LIN 2.x specification), so the
/// signal PDU layout must have <c>ByteLength</c> ≤ 8.
/// Dispatching is performed only for standard (non-event-triggered) frames.
/// </summary>
internal sealed class SignalMessageLinTests
{
    /// <summary>LIN frame ID used for all tests (6-bit, 0–63).</summary>
    private const byte _BenchFrameId = 0x10;

    /// <summary>
    /// Compact 4-byte layout (2 × U16 LE) registered on <c>lin.id</c> at key <see cref="_BenchFrameId"/>.
    /// Reuses the shared signal definitions from <see cref="AutomotivePduBench"/> to keep
    /// scaling and expected raw values consistent across the test suite.
    /// </summary>
    private static SignalMessageLayout _LinLayout =>
        new()
        {
            PduId = 0x300,
            Name = "lin_signal",
            UiName = "LIN Signal",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding
                {
                    Table = LinProtocol.IdTableName,
                    Key = _BenchFrameId,
                }),
            Mux = null,
            MuxGroups = [],
        };

    /// <summary>
    /// Builds a standard LIN frame carrying the encoded signal bytes at frame ID 0x10,
    /// writes a JSON config, parses with a full stack, and asserts the decoded signal values.
    /// Signal EngineRpm physical 125.0 ⇒ raw (125 − 100) / 0.25 = 100.
    /// Signal Thr physical 555 ⇒ raw 555.
    /// </summary>
    [Test]
    public async Task Parses_StandardFrame_MatchesExpectedFields()
    {
        SignalMessageLayout layout = _LinLayout;

        SignalMessageValueSet vals = SignalMessageValueSet.For(layout)
            .Set("EngineRpm", 125.0)
            .Set("Thr", 555);

        SignalMessageLayer spdu = new(layout, vals);

        // Encode signal bytes first — LinLayer is IRootLayer and does not chain .Then(payloadLayer).
        byte[] signalBytes = new byte[layout.ByteLength];
        spdu.WriteHeader(signalBytes.AsSpan());

        byte[] frame = FrameStack
            .Start(new LinLayer(_BenchFrameId, signalBytes.AsSpan()))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        string jsonDir = Path.Combine(Path.GetTempPath(), "ni_signal_message_lin_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(jsonDir);
        string jsonPath = Path.Combine(jsonDir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(jsonPath, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                sm => sm.PreloadValue("signal_message.config_file", jsonPath),
                LinkType.Lin);

            using (stack)
            {
                await ProtocolTestHelper.AssertF64Field(stack, packet, "lin_signal.EngineRpm", 125.0).ConfigureAwait(false);
                await ProtocolTestHelper.AssertF64Field(stack, packet, "lin_signal.Thr", 555.0).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(jsonDir, recursive: true);
        }
    }
}
