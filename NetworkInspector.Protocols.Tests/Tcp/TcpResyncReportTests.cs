// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that a TCP stream reports the bytes it skipped to resynchronize, on the packet that carried them, and repeats the
/// report on a re-parse.
/// </summary>
internal sealed class TcpResyncReportTests
{
    private const ushort _Port = 30777;

    /// <summary>A message is a 4-byte frame; a first byte of 0xFF means the stream lost its boundary.</summary>
    private sealed class FourByteFrameDetector : IPduBoundaryDetector
    {
        public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return PduBoundaryResult.Incomplete;
            }

            if (data[0] == 0xFF)
            {
                return PduBoundaryResult.Invalid;
            }

            return data.Length >= 4
                ? PduBoundaryResult.Complete(4)
                : PduBoundaryResult.Incomplete;
        }
    }

    /// <summary>Skips the leading garbage (every 0xFF byte at the front) in one step.</summary>
    private sealed class SkipSevenResync : IResyncHeuristic
    {
        public ResyncResult Resync(ReadOnlySpan<byte> data) =>
            data.Length >= 7
                ? ResyncResult.Skip(7)
                : ResyncResult.Failure;
    }

    private static Stack _BuildStack(out CountingProbeProtocol probe) =>
        CountingProbeProtocol.BuildStack(
            _Port,
            out probe,
            new StreamReassemblyConfig { BoundaryDetector = new FourByteFrameDetector(), ResyncHeuristic = new SkipSevenResync() });

    [Test]
    public async Task SkippedBytesAreShownOnThePacketThatCarriedThemAndTheNextMessageIsDispatched()
    {
        using Stack stack = _BuildStack(out CountingProbeProtocol probe);
        TcpTestConversation conversation = new(_Port);
        conversation.Handshake();

        // Seven garbage bytes (first byte 0xFF) followed by one valid message; the stream resynchronizes while it frames the
        // first segment and delivers the message it finds behind the garbage together with the next segment's message.
        byte[] payload = [0xFF, 1, 2, 3, 4, 5, 6, 0xA0, 0xA1, 0xA2, 0xA3];
        conversation.ClientSend(payload);
        conversation.ClientSend([0xB0, 0xB1, 0xB2, 0xB3]);

        Packet[] packets = conversation.ParseAll(stack);

        Packet carrier = packets[3];
        await ProtocolTestHelper.AssertStringFieldContains(stack, carrier, "tcp.error.resync", "7 bytes skipped");
        await Assert.That(probe.ParseCount).IsEqualTo(2).Because(Invariant($"calls {probe.ParseCount}, lengths {string.Join(",", probe.Lengths)}"));
        await Assert.That(probe.Lengths[0]).IsEqualTo(4);
        await Assert.That(probe.Lengths[1]).IsEqualTo(4);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "tcp.error.resync");
    }

    [Test]
    public async Task TheReportIsRepeatedOnAReparseAndAbsentWithoutResynchronization()
    {
        using Stack stack = _BuildStack(out CountingProbeProtocol _);
        TcpTestConversation conversation = new(_Port);
        conversation.Handshake();
        conversation.ClientSend([0xFF, 1, 2, 3, 4, 5, 6, 0xA0, 0xA1, 0xA2, 0xA3]);
        conversation.ClientSend([0xB0, 0xB1, 0xB2, 0xB3]);
        Packet[] packets = conversation.ParseAll(stack);

        Packet again = ProtocolTestHelper.ParseFrame(stack, conversation.Frames[3], 3, Timestamp.FromSecs(0));

        await ProtocolTestHelper.AssertStringFieldContains(stack, again, "tcp.error.resync", "7 bytes skipped");
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "tcp.error.resync");
    }
}
