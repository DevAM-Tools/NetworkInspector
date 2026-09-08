// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Session wrap of frame sources, RA cache opt-in, and reparse packet access.
/// </summary>
internal sealed class SessionFrameCacheTests
{
    #region Tests

    [Test]
    public async Task RandomAccessSource_WithoutCacheOption_TryGetPacketSucceeds()
    {
        const int frameCount = 3;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        for (int i = 0; i < frameCount; i++)
        {
            bool got = session.TryGetPacket(new PacketId(i), out Packet? packet);
            await Assert.That(got).IsTrue();
            await Assert.That(packet!.HasFieldTree).IsTrue();
        }

        session.Shutdown();
    }

    [Test]
    public async Task RandomAccess_CacheRandomAccess_TryGetFramePayloadMatches()
    {
        const int frameCount = 3;
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(frameCount);

        using Session session = new(stack);
        bool added = session.TryAddFrameSource(
            source,
            new FrameSourceAddOptions { CacheRandomAccess = true },
            out _);
        await Assert.That(added).IsTrue();

        session.TryStart();
        session.WaitForCompletion();

        for (int i = 0; i < frameCount; i++)
        {
            Frame? inner = source.FrameById(new FrameId(i));
            await Assert.That(inner.HasValue).IsTrue();

            bool got = session.TryGetFrame(new PacketId(i), out Frame cached);
            await Assert.That(got).IsTrue();
            await Assert.That(cached.Id).IsEqualTo(inner!.Value.Id);
            await Assert.That(cached.Timestamp).IsEqualTo(inner.Value.Timestamp);
            await Assert.That(cached.LinkType).IsEqualTo(inner.Value.LinkType);
            await Assert.That(cached.InterfaceId).IsEqualTo(inner.Value.InterfaceId);
            await Assert.That(cached.Data.ToArray()).IsEquivalentTo(inner.Value.Data.ToArray());
        }

        session.Shutdown();
    }

    [Test]
    public async Task TryAddFrameSource_WhenNotIdle_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource first = TestFrameSource.WithUdpFrames(1);
        using TestFrameSource second = TestFrameSource.WithUdpFrames(1);

        using Session session = new(stack);
        session.TryAddFrameSource(first, out _);
        session.TryStart();

        bool added = session.TryAddFrameSource(second, out FrameSourceInfo? info);
        await Assert.That(added).IsFalse();
        await Assert.That(info).IsNull();

        session.Shutdown();
    }

    [Test]
    public async Task TryGetPacket_InvalidId_ReturnsFalse()
    {
        using Stack stack = TestHarness.CreateStack();
        using TestFrameSource source = TestFrameSource.WithUdpFrames(1);
        using Session session = new(stack);
        session.TryAddFrameSource(source, out _);
        session.TryStart();
        session.WaitForCompletion();

        bool found = session.TryGetPacket(PacketId.Invalid, out Packet? packet);
        await Assert.That(found).IsFalse();
        await Assert.That(packet).IsNull();

        session.Shutdown();
    }

    #endregion
}
