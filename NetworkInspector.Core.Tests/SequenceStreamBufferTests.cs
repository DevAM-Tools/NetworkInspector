// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Sequence-ordered stream buffer: holes, overlap, and retransmission.</summary>
internal sealed class SequenceStreamBufferTests
{
    private static SequenceStreamBuffer _Buffer() => new(new StreamReassemblyConfig
    {
        MaxBufferSize = 1024,
    });

    [Test]
    public async Task InOrderHalves_FormOnePrefix()
    {
        SequenceStreamBuffer buffer = _Buffer();
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.TryAppend(110, second)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
    }

    [Test]
    public async Task RetransmissionOfTheFirstHalf_DoesNotGrowThePrefix()
    {
        SequenceStreamBuffer buffer = _Buffer();
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.TryAppend(110, second)).IsTrue();
        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
    }

    [Test]
    public async Task HoleBlocksThePrefixUntilFilled()
    {
        SequenceStreamBuffer buffer = _Buffer();
        byte[] second = [11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        byte[] first = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        await Assert.That(buffer.TryAppend(110, second)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(10);

        await Assert.That(buffer.TryAppend(100, first)).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(20);
        await Assert.That(buffer.ContiguousSpan[0]).IsEqualTo((byte)1);
        await Assert.That(buffer.ContiguousSpan[10]).IsEqualTo((byte)11);
    }

    [Test]
    public async Task OverlapKeepsOnlyTheNewTail()
    {
        SequenceStreamBuffer buffer = _Buffer();
        await Assert.That(buffer.TryAppend(100, new byte[] { 1, 2, 3, 4, 5 })).IsTrue();
        await Assert.That(buffer.TryAppend(103, new byte[] { 4, 5, 6, 7 })).IsTrue();
        await Assert.That(buffer.ContiguousLength).IsEqualTo(7);
        await Assert.That(buffer.ContiguousSpan[5]).IsEqualTo((byte)6);
        await Assert.That(buffer.ContiguousSpan[6]).IsEqualTo((byte)7);
    }
}
