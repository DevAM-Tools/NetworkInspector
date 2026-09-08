// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>
/// Skip-tree packets are not filter input except <see cref="Filter.AlwaysMatch"/>.
/// </summary>
internal sealed class SkipFieldTreeFilterTests
{
    #region Helpers

    private static Packet _ParseSkip(Stack stack, byte[] frameData, int packetId = 0)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(0),
            frameData,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(packetId), stack, frame, FieldTreeMode.Skip);
    }

    #endregion

    [Test]
    public async Task TryIsMatch_SkipPacket_ReturnsNoFieldTreeWithoutCachingAMiss()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp", stack);
        Packet skip = _ParseSkip(stack, FilterTestHelper.BuildUdpFrame(53, 1024), packetId: 0);

        bool evaluated = filter.TryIsMatch(skip, out bool matched, out FilterError? failure);

        await Assert.That(evaluated).IsFalse();
        await Assert.That(matched).IsFalse();
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.NoFieldTree);

        Packet built = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), packetId: 0);
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, built)).IsTrue();
    }

    [Test]
    public async Task AlwaysMatch_SkipPacket_Matches()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet skip = _ParseSkip(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        bool evaluated = Filter.AlwaysMatch.TryIsMatch(skip, out bool matched, out FilterError? failure);

        await Assert.That(evaluated).IsTrue();
        await Assert.That(matched).IsTrue();
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task CompileEmpty_SkipPacket_Matches()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet skip = _ParseSkip(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterResult<Filter> compiled = Filter.Compile("");

        bool evaluated = compiled.Value.TryIsMatch(skip, out bool matched, out FilterError? failure);

        await Assert.That(compiled.IsSuccess).IsTrue();
        await Assert.That(evaluated).IsTrue();
        await Assert.That(matched).IsTrue();
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task TryIsMatch_BuildPacket_Unchanged()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
    }
}
