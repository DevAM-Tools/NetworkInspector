// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The total of bytes that SOME/IP-TP may bind to one packet (<c>someip.tp_max_derived_bytes_per_packet</c>). The earlier segments
/// of several segmented messages arrive in earlier frames; the last frame carries the last segment of every message, so one
/// packet completes all of them and binds one reassembled payload per message. (One Ethernet frame holds less than the 2 KiB
/// minimum of the total, so a single frame cannot reach it.) The setting's default and range are tested together with the other
/// two protocols in <c>DerivedBytesLimitConsistencyTests</c>.
/// </summary>
internal sealed class SomeIpTpDerivedBytesTests
{
    private const string _TotalSetting = "someip.tp_max_derived_bytes_per_packet";

    /// <summary>The smallest value the setting accepts (2 KiB).</summary>
    private const ulong _SmallLimit = 2048;

    /// <summary>Length of a non-final segment: a multiple of 16 (the TP offset unit) that fits one Ethernet frame.</summary>
    private const int _SegmentLength = 1280;

    /// <summary>
    /// The frames that make one packet complete one segmented message per given size: first the frames with the earlier segments
    /// (each in its own frame, so the frame fits the Ethernet size), then one frame with the last segment of every message. The
    /// last element is the frame whose packet binds the reassembled payloads, in the order of the sizes.
    /// </summary>
    /// <param name="payloadSizes">The size of each message; every message gets its own session.</param>
    internal static byte[][] FramesCompletingMessages(params int[] payloadSizes)
    {
        List<byte[]> prefixFrames = [];
        List<byte[]> lastSegments = [];
        for (int index = 0; index < payloadSizes.Length; index++)
        {
            ushort session = (ushort)(index + 1);
            int size = payloadSizes[index];

            // Every non-final segment is a multiple of 16 bytes; the final segment holds the remaining 1 to 16 bytes.
            int prefixLength = (size - 1) / 16 * 16;
            for (int offset = 0; offset < prefixLength; offset += _SegmentLength)
            {
                int length = Math.Min(_SegmentLength, prefixLength - offset);
                prefixFrames.Add(SomeIpFrames.Udp(SomeIpFrames.TpMessage(
                    (uint)offset, new byte[length], more: true, session: session, messageType: 0x20)));
            }

            lastSegments.Add(SomeIpFrames.TpMessage(
                (uint)prefixLength, new byte[size - prefixLength], more: false, session: session, messageType: 0x20));
        }

        return [.. prefixFrames, SomeIpFrames.Udp(SomeIpBytes.Concat([.. lastSegments]))];
    }

    /// <summary>The frames of <see cref="FramesCompletingMessages"/>, for the tests that treat the three protocols alike.</summary>
    /// <param name="payloadSizes">The size of each message.</param>
    internal static byte[][] FramesWithDerivedMessages(params int[] payloadSizes) => FramesCompletingMessages(payloadSizes);

    private static SomeIpTestStack _Stack(ulong total) => SomeIpTestStack.Create(null, (_TotalSetting, SettingValue.U64(total)));

    /// <summary>Parses every frame in order as the next packet and returns the packet of the last frame.</summary>
    private static Packet _ParseAll(SomeIpTestStack stack, byte[][] frames)
    {
        Packet last = null!; // not null: FramesCompletingMessages always returns at least the last frame
        foreach (byte[] frame in frames)
        {
            last = stack.Parse(frame);
        }

        return last;
    }

    /// <summary>Messages whose sum stays at or below the total are all bound; the sum may equal the total.</summary>
    /// <param name="first">Payload size of the first message.</param>
    /// <param name="second">Payload size of the second message.</param>
    [Test]
    [Arguments(800, 800)]
    [Arguments(1024, 1024)]
    public async Task MessagesWhoseSumStaysWithinTheTotalAreAllBound(int first, int second)
    {
        using SomeIpTestStack s = _Stack(_SmallLimit);

        Packet packet = _ParseAll(s, FramesCompletingMessages(first, second));

        await Assert.That(packet.BufferCount).IsEqualTo(3);
        await Assert.That(s.Count(packet, "someip.tp")).IsEqualTo(2);
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, packet, "packet.error").ConfigureAwait(false);
    }

    /// <summary>
    /// The message that would pass the total is not bound or parsed: the earlier message stays bound, the packet reports an error
    /// that names the setting and says it is the total per packet, and the TP fields of the refused message stay shown.
    /// </summary>
    [Test]
    public async Task AMessageThatPassesTheTotalIsRefusedWithTheNamedError()
    {
        using SomeIpTestStack s = _Stack(_SmallLimit);

        Packet packet = _ParseAll(s, FramesCompletingMessages(1400, 1400, 20));

        await Assert.That(packet.BufferCount).IsEqualTo(2).Because("only the first message is bound; the refused one left no buffer");
        await Assert.That(ProtocolTestHelper.BufferBytes(packet, 1).Length).IsEqualTo(1400);
        await Assert.That(s.Count(packet, "someip.tp")).IsGreaterThanOrEqualTo(2).Because("the TP word of the refused segment is shown");
        await ProtocolTestHelper.AssertStringFieldContains(s.Stack, packet, "packet.error", _TotalSetting).ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(s.Stack, packet, "packet.error", "total per packet").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(s.Stack, packet, "packet.error", "2048").ConfigureAwait(false);
    }

    /// <summary>One message of exactly the total is bound (the limit is inclusive); one byte more is refused with the same named error.</summary>
    [Test]
    public async Task ASingleMessageAtTheTotalIsBoundAndOneByteMoreIsRefused()
    {
        using SomeIpTestStack atLimitStack = _Stack(_SmallLimit);
        using SomeIpTestStack overStack = _Stack(_SmallLimit);

        Packet atLimit = _ParseAll(atLimitStack, FramesCompletingMessages((int)_SmallLimit));
        Packet over = _ParseAll(overStack, FramesCompletingMessages((int)_SmallLimit + 1));

        await Assert.That(atLimit.BufferCount).IsEqualTo(2);
        await ProtocolTestHelper.AssertFieldNotPresent(atLimitStack.Stack, atLimit, "packet.error").ConfigureAwait(false);
        await Assert.That(over.BufferCount).IsEqualTo(1);
        await ProtocolTestHelper.AssertStringFieldContains(overStack.Stack, over, "packet.error", _TotalSetting).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="SomeIpSettings"/> is a public record, so a caller can hand the protocol any number. A negative total must not
    /// wrap to an unlimited one (a bare cast to <see cref="ulong"/> would): it counts as 0 and refuses every message.
    /// </summary>
    /// <param name="configured">The value the caller put into the record.</param>
    /// <param name="expected">The total the protocol counts against.</param>
    [Test]
    [Arguments(-1L, 0UL)]
    [Arguments(long.MinValue, 0UL)]
    [Arguments(0L, 0UL)]
    [Arguments(2048L, 2048UL)]
    [Arguments(long.MaxValue, (ulong)long.MaxValue)]
    public async Task ANegativeTotalInTheSettingsRecordNeverBecomesUnlimited(long configured, ulong expected)
    {
        SomeIpSettings settings = SomeIpSettings.Default with { TpMaxDerivedBytesPerPacket = configured };

        SomeIpProtocol protocol = new(SomeIpCompiledModel.Empty, in settings);

        await Assert.That(protocol.DerivedBytesTotal).IsEqualTo(expected);
    }

    /// <summary>
    /// The total is its own setting: a message above it is refused although the message size limit (10 MiB by default) allows
    /// it, and a message above the message size limit is still refused by that limit, with that limit's field and no total error.
    /// </summary>
    [Test]
    public async Task TheTotalAndTheMessageSizeLimitAreIndependent()
    {
        using SomeIpTestStack totalStack = _Stack(_SmallLimit);
        using SomeIpTestStack sizeStack = SomeIpTestStack.Create(
            null, ("someip.tp_max_message_size", SettingValue.U64(2048)), (_TotalSetting, SettingValue.U64(_SmallLimit)));

        Packet byTotal = _ParseAll(totalStack, FramesCompletingMessages(2500));
        Packet bySize = _ParseAll(sizeStack, FramesCompletingMessages(2500));

        await ProtocolTestHelper.AssertStringFieldContains(totalStack.Stack, byTotal, "packet.error", _TotalSetting).ConfigureAwait(false);
        await Assert.That(sizeStack.Count(bySize, "someip.tp.error.size_limit")).IsGreaterThanOrEqualTo(1);
        await ProtocolTestHelper.AssertFieldNotPresent(sizeStack.Stack, bySize, "packet.error").ConfigureAwait(false);
    }

    /// <summary>
    /// The count belongs to the packet: a packet that used up its total does not affect the next one, and a reparse of the failing
    /// packet fails the same way (the replay binds the recorded message in the same order).
    /// </summary>
    [Test]
    public async Task TheTotalIsCountedPerPacketAndAReparseDecidesTheSame()
    {
        using SomeIpTestStack s = _Stack(_SmallLimit);
        byte[][] failing = FramesCompletingMessages(1400, 1400);
        byte[][] fitting = FramesCompletingMessages(1400);

        Packet first = _ParseAll(s, failing);
        Packet next = _ParseAll(s, fitting);
        Packet reparse = s.Reparse(failing[^1], first.Id.Value);

        await ProtocolTestHelper.AssertStringFieldContains(s.Stack, first, "packet.error", _TotalSetting).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, next, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(s.Stack, reparse, "packet.error", _TotalSetting).ConfigureAwait(false);
        await Assert.That(reparse.BufferCount).IsEqualTo(first.BufferCount);
    }

    /// <summary>
    /// Several threads reparse a packet whose messages (800 and 800 of 2048 bytes) fit the total at the same time. The count lives
    /// on each packet; if it were shared between threads or parses, a later reparse would pass the total and be refused.
    /// </summary>
    [Test]
    public async Task ConcurrentReparsesEachKeepTheirOwnTotal()
    {
        const int threads = 8;
        using SomeIpTestStack s = _Stack(_SmallLimit);
        byte[][] frames = FramesCompletingMessages(800, 800);
        Packet first = _ParseAll(s, frames);
        int expectedCount = first.BufferCount;

        await Parallel.ForAsync(0, threads, async (_, _) =>
        {
            Packet packet = s.Reparse(frames[^1], first.Id.Value);

            await Assert.That(packet.BufferCount).IsEqualTo(expectedCount);
            await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, packet, "packet.error").ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
