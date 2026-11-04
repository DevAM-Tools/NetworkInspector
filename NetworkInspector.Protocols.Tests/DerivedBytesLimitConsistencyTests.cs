// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using SomeIpTpDerivedBytesTests = NetworkInspector.Protocols.Tests.SomeIp.SomeIpTpDerivedBytesTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that HTTP, WebSocket, and SOME/IP-TP apply the same kind of limit to the bytes they derive and bind to one packet: a
/// relaxed default that is a multiple of the per-item limit, one validated range, one error wording, accounting that starts at
/// zero for every packet, and a refusal that leaves the packet unchanged. The behavior of each protocol in detail is tested next
/// to it (<c>HttpBodyBufferTests</c>, <c>WebSocketPayloadBufferTests</c>, <c>SomeIpTpDerivedBytesTests</c>); this class pins
/// that the three agree, by running the same checks over the three of them.
/// </summary>
internal sealed class DerivedBytesLimitConsistencyTests
{
    /// <summary>The smallest value of every total (2 KiB): the transport buffers of a message count too.</summary>
    private const ulong _Minimum = 2048;

    /// <summary>The size of the derived buffers of the scenario tests; two of them pass the minimum total.</summary>
    private const int _PayloadSize = 1400;

    /// <summary>The size of the buffers that fit twice into the minimum total.</summary>
    private const int _FitSize = 800;

    /// <summary>
    /// What distinguishes the three protocols in these tests; everything else is shared.
    /// </summary>
    /// <param name="TotalSetting">The key of the per-packet total.</param>
    /// <param name="PerItemSetting">The key of the per-item limit that the total relaxes (a multiple of it).</param>
    /// <param name="DefaultTotal">The default of the total.</param>
    /// <param name="Maximum">The largest value the total accepts.</param>
    /// <param name="Frame">Builds one frame that makes the protocol bind one derived buffer per given size.</param>
    private sealed record _Case(string TotalSetting, string PerItemSetting, ulong DefaultTotal, ulong Maximum, Func<int[], byte[][]> Frame);

    private static _Case _CaseOf(string protocol) => protocol switch
    {
        "http" => new _Case(
            "http.max_derived_bytes_per_packet", "http.max_decompressed_bytes", 268_435_456UL, 137_438_953_472UL, sizes => [HttpBodyBufferTests.FrameWithDerivedBodies(sizes)]),
        "ws" => new _Case(
            "ws.max_derived_bytes_per_packet",
            "ws.max_decompressed_bytes",
            268_435_456UL,
            137_438_953_472UL,
            sizes => [WebSocketPayloadBufferTests.FrameWithDerivedPayloads(sizes)]),
        "someip" => new _Case(
            "someip.tp_max_derived_bytes_per_packet",
            "someip.tp_max_message_size",
            67_108_864UL,
            17_179_869_184UL,
            SomeIpTpDerivedBytesTests.FramesWithDerivedMessages),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown protocol of the consistency tests."),
    };

    private static Setting _Setting(SettingsManager? manager, string key) => manager!.GetSetting(key)!;

    /// <summary>Parses the frames in order as packets 0, 1, ... and returns the packet of the last frame (the one that binds).</summary>
    private static Packet _ParseFrames(Stack stack, byte[][] frames)
    {
        Packet last = null!; // not null: every case returns at least one frame
        for (int index = 0; index < frames.Length; index++)
        {
            last = ProtocolTestHelper.ParseFrame(stack, frames[index], index, Timestamp.FromMillis(index));
        }

        return last;
    }

    /// <summary>Parses the last frame again under the id of its first parse (a reparse of the packet that binds).</summary>
    private static Packet _ReparseLast(Stack stack, byte[][] frames) =>
        ProtocolTestHelper.ParseFrame(stack, frames[^1], frames.Length - 1, Timestamp.FromMillis(frames.Length - 1));

    /// <summary>
    /// The bytes of <paramref name="frame"/> that the transport binds as buffers before the protocol under test parses them: the TCP
    /// payload for HTTP and WebSocket (Ethernet, IPv4, and TCP headers of 14, 20, and 20 bytes in front), nothing for SOME/IP-TP (one
    /// UDP datagram is not bound). The packet's sum counts them too, whichever protocol bound them, so a test adds them to the
    /// total it means to leave for the derived buffers.
    /// </summary>
    private static ulong _TransportBytes(string protocol, byte[][] frames) => protocol == "someip"
        ? 0
        : (ulong)(frames[^1].Length - (14 + 20 + 20));

    private static int _BuffersOfLength(Packet packet, int length)
    {
        int count = 0;
        for (int index = 1; index < packet.BufferCount; index++)
        {
            if (ProtocolTestHelper.BufferBytes(packet, index).Length == length)
            {
                count++;
            }
        }

        return count;
    }

    #region Setting

    /// <summary>
    /// Every total is registered with a relaxed default: at least four times the per-item default (the per-item limit alone
    /// would let one packet hold 255 items), and the range starts at 1 KiB like the per-item limits.
    /// </summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task TheDefaultIsRelaxedAndTheRangeStartsAtOneKiB(string protocol)
    {
        _Case scenario = _CaseOf(protocol);
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);

        Setting total = _Setting(captured, scenario.TotalSetting);
        Setting perItem = _Setting(captured, scenario.PerItemSetting);

        await Assert.That(total.Value.TryGetAsU64(out ulong defaultTotal)).IsTrue();
        await Assert.That(perItem.Value.TryGetAsU64(out ulong defaultPerItem)).IsTrue();
        await Assert.That(defaultTotal).IsEqualTo(scenario.DefaultTotal);
        await Assert.That(defaultTotal).IsGreaterThanOrEqualTo(defaultPerItem * 4).Because("a relaxed total allows several items");
        await Assert.That(total.MinValue!.Value.TryGetAsU64(out ulong min) && min == _Minimum).IsTrue();
        await Assert.That(total.MaxValue!.Value.TryGetAsU64(out ulong max) && max == scenario.Maximum).IsTrue();
    }

    /// <summary>
    /// The range that the attribute-declared settings of HTTP and WebSocket repeat as literals is the range of
    /// <see cref="DerivedBytesLimit"/>, so a change of one is noticed at the other.
    /// </summary>
    [Test]
    public async Task TheSharedRangeConstantsMatchTheAttributeDeclaredSettings()
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);

        foreach (string key in (string[])["http.max_derived_bytes_per_packet", "ws.max_derived_bytes_per_packet"])
        {
            Setting total = _Setting(captured, key);

            await Assert.That(total.MinValue!.Value.TryGetAsU64(out ulong min) && min == DerivedBytesLimit.MinimumBytes).IsTrue();
            await Assert.That(total.MaxValue!.Value.TryGetAsU64(out ulong max) && max == DerivedBytesLimit.MaximumBytes).IsTrue();
        }
    }

    /// <summary>A value outside the range is rejected, and the rejection names the whole range.</summary>
    /// <param name="protocol">The protocol under test.</param>
    /// <param name="aboveMaximumBy">0 for the values below the range, 1 for the value one above the maximum.</param>
    [Test]
    [Arguments("http", 0)]
    [Arguments("http", 1)]
    [Arguments("ws", 0)]
    [Arguments("ws", 1)]
    [Arguments("someip", 0)]
    [Arguments("someip", 1)]
    public async Task AValueOutsideTheRangeIsRejectedWithTheRange(string protocol, int aboveMaximumBy)
    {
        _Case scenario = _CaseOf(protocol);
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);
        Setting total = _Setting(captured, scenario.TotalSetting);
        ulong[] rejected = aboveMaximumBy == 0
            ? [0UL, _Minimum - 1]
            : [scenario.Maximum + 1];

        foreach (ulong value in rejected)
        {
            bool valid = total.TryValidate(SettingValue.U64(value), out SettingProblem? problem);

            await Assert.That(valid).IsFalse().Because($"{value} is outside the range");
            await Assert.That(problem!.ToString()).Contains(_Minimum.ToString(CultureInfo.InvariantCulture))
                .And.Contains(scenario.Maximum.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The smallest and the largest value of the range are accepted.</summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task TheBoundariesOfTheRangeAreAccepted(string protocol)
    {
        _Case scenario = _CaseOf(protocol);
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);
        Setting total = _Setting(captured, scenario.TotalSetting);

        bool atMinimum = total.TryValidate(SettingValue.U64(_Minimum), out _);
        bool atMaximum = total.TryValidate(SettingValue.U64(scenario.Maximum), out _);

        await Assert.That(atMinimum).IsTrue();
        await Assert.That(atMaximum).IsTrue();
    }

    /// <summary>
    /// A preloaded value outside the range is refused when the protocols register, and the default stays in force (the value is
    /// external input and is validated like any other setting).
    /// </summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task APreloadedValueOutsideTheRangeKeepsTheDefault(string protocol)
    {
        _Case scenario = _CaseOf(protocol);

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((scenario.TotalSetting, SettingValue.U64(_Minimum - 1)));

        await Assert.That(stack.Settings.GetU64Setting(scenario.TotalSetting)).IsEqualTo(scenario.DefaultTotal);
    }

    #endregion

    #region Behavior

    /// <summary>
    /// The same scenario for every protocol: two derived buffers of 1400 bytes in one packet and a total of 2048. The first is
    /// bound and dispatched, the second is refused with an error that names the setting, the limit, and that it is the total per
    /// packet, in the same words; the packet holds exactly one 1400-byte buffer, so the refusal left no partial state.
    /// </summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task TheSecondBufferThatPassesTheTotalIsRefusedWithTheSameWording(string protocol)
    {
        _Case scenario = _CaseOf(protocol);
        byte[][] frame = scenario.Frame([_PayloadSize, _PayloadSize]);
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            (scenario.TotalSetting, SettingValue.U64(_Minimum + _TransportBytes(protocol, frame))));

        Packet packet = _ParseFrames(stack, frame);

        await Assert.That(_BuffersOfLength(packet, _PayloadSize)).IsEqualTo(1);
        await ProtocolTestHelper.AssertStringFieldContains(
            stack,
            packet,
            "packet.error",
            "cannot be parsed further: together they would pass "
            + (_Minimum + _TransportBytes(protocol, frame)).ToString(CultureInfo.InvariantCulture)
            + " bytes in one packet (setting " + scenario.TotalSetting + " is the total per packet)")
            .ConfigureAwait(false);
    }

    /// <summary>With the total at its relaxed default the same two buffers are both bound: the total alone stopped them.</summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task TheRelaxedDefaultBindsBothBuffers(string protocol)
    {
        _Case scenario = _CaseOf(protocol);
        using Stack stack = ProtocolTestHelper.BuildStack();

        Packet packet = _ParseFrames(stack, scenario.Frame([_PayloadSize, _PayloadSize]));

        await Assert.That(_BuffersOfLength(packet, _PayloadSize)).IsEqualTo(2);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }

    /// <summary>
    /// The accounting starts at zero for every packet: a packet that fits (two buffers of 800 bytes (400 for SOME/IP-TP) at the minimum total) binds the
    /// same buffers again when it is reparsed, because the reparse starts at zero and not at the 1600 bytes of the first parse; and
    /// a packet that used up its total on another stack leaves nothing behind that could be inherited. (The refused packet is
    /// parsed on a second stack: the two TCP-based protocols track the connection, and the same sequence numbers on one stack
    /// would be taken for a retransmission, not for a new packet.)
    /// </summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task EveryPacketStartsAtZero(string protocol)
    {
        _Case scenario = _CaseOf(protocol);
        byte[][] failing = scenario.Frame([_PayloadSize, _PayloadSize]);
        byte[][] fitting = scenario.Frame([_FitSize, _FitSize]);
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            (scenario.TotalSetting, SettingValue.U64(_Minimum + _TransportBytes(protocol, fitting))));
        using Stack other = ProtocolTestHelper.BuildStackWithSettings(
            (scenario.TotalSetting, SettingValue.U64(_Minimum + _TransportBytes(protocol, failing))));

        Packet refused = _ParseFrames(other, failing);
        Packet first = _ParseFrames(stack, fitting);
        Packet reparse = _ReparseLast(stack, fitting);

        await ProtocolTestHelper.AssertFieldExists(other, refused, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, first, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        await Assert.That(_BuffersOfLength(reparse, _FitSize)).IsEqualTo(2);
        await Assert.That(reparse.BufferCount).IsEqualTo(first.BufferCount);
    }

    /// <summary>
    /// A buffer of exactly the total is bound (the limit is inclusive) and one byte more is refused, for every protocol. Each
    /// frame is parsed on its own stack for the reason given at <see cref="EveryPacketStartsAtZero"/>.
    /// </summary>
    /// <param name="protocol">The protocol under test.</param>
    [Test]
    [Arguments("http")]
    [Arguments("ws")]
    [Arguments("someip")]
    public async Task TheTotalIsInclusiveForEveryProtocol(string protocol)
    {
        _Case scenario = _CaseOf(protocol);
        byte[][] atFrame = scenario.Frame([(int)_Minimum]);
        byte[][] overFrame = scenario.Frame([(int)_Minimum + 1]);
        using Stack atStack = ProtocolTestHelper.BuildStackWithSettings(
            (scenario.TotalSetting, SettingValue.U64(_Minimum + _TransportBytes(protocol, atFrame))));
        using Stack overStack = ProtocolTestHelper.BuildStackWithSettings(
            (scenario.TotalSetting, SettingValue.U64(_Minimum + _TransportBytes(protocol, overFrame))));

        Packet atTotal = _ParseFrames(atStack, atFrame);
        Packet over = _ParseFrames(overStack, overFrame);

        await Assert.That(_BuffersOfLength(atTotal, (int)_Minimum)).IsEqualTo(1);
        await ProtocolTestHelper.AssertFieldNotPresent(atStack, atTotal, "packet.error").ConfigureAwait(false);
        await Assert.That(_BuffersOfLength(over, (int)_Minimum + 1)).IsEqualTo(0);
        await ProtocolTestHelper.AssertFieldExists(overStack, over, "packet.error").ConfigureAwait(false);
    }

    #endregion
}
