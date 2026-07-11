// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="DatagramDefragmenter{TKey}"/> covering reassembly, eviction,
/// and the new <c>EvictedCount</c> diagnostic counter (F-RA-03).
/// </summary>
internal sealed class DatagramDefragmenterTests
{
    private readonly struct IntKey(int v) : IEquatable<IntKey>
    {
        public readonly int V = v;
        public bool Equals(IntKey other) => V == other.V;
        public override bool Equals(object? obj) => obj is IntKey k && Equals(k);
        public override int GetHashCode() => V;
    }

    [Test]
    public async Task SingleFragment_NoMore_ReassemblesImmediately()
    {
        DatagramDefragmenter<IntKey> d = new();
        byte[]? result = d.ProcessFragment(new IntKey(1), 0, moreFragments: false, [1, 2, 3, 4]);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Length).IsEqualTo(4);
        await Assert.That(d.ReassembledCount).IsEqualTo(1L);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task TwoFragments_InOrder_Reassemble()
    {
        DatagramDefragmenter<IntKey> d = new();
        byte[]? a = d.ProcessFragment(new IntKey(1), 0, moreFragments: true, [1, 2, 3, 4]);
        await Assert.That(a).IsNull();
        byte[]? b = d.ProcessFragment(new IntKey(1), 4, moreFragments: false, [5, 6, 7, 8]);
        await Assert.That(b).IsNotNull();
        await Assert.That(b!.Length).IsEqualTo(8);
        for (int i = 0; i < 8; i++)
        {
            await Assert.That(b![i]).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task TwoFragments_OutOfOrder_Reassemble()
    {
        DatagramDefragmenter<IntKey> d = new();
        byte[]? a = d.ProcessFragment(new IntKey(1), 4, moreFragments: false, [5, 6, 7, 8]);
        await Assert.That(a).IsNull();
        byte[]? b = d.ProcessFragment(new IntKey(1), 0, moreFragments: true, [1, 2, 3, 4]);
        await Assert.That(b).IsNotNull();
        await Assert.That(b!.Length).IsEqualTo(8);
        for (int i = 0; i < 8; i++)
        {
            await Assert.That(b![i]).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task EvictedCount_IncrementsWhenCapacityReached()
    {
        DatagramDefragmenter<IntKey> d = new();

        // Push 1024 in-progress entries (no MF=false, so they stay pending), then push one more
        // to force an eviction. Each entry uses a distinct key.
        for (int i = 0; i < 1024; i++)
        {
            d.ProcessFragment(new IntKey(i), 0, moreFragments: true, [0xAA]);
        }
        await Assert.That(d.PendingCount).IsEqualTo(1024);
        await Assert.That(d.EvictedCount).IsEqualTo(0L);

        d.ProcessFragment(new IntKey(9999), 0, moreFragments: true, [0xBB]);

        await Assert.That(d.PendingCount).IsEqualTo(1024);
        await Assert.That(d.EvictedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task Clear_ResetsCounters()
    {
        DatagramDefragmenter<IntKey> d = new();
        d.ProcessFragment(new IntKey(1), 0, moreFragments: false, [1]);
        d.Clear();
        await Assert.That(d.ReassembledCount).IsEqualTo(0L);
        await Assert.That(d.EvictedCount).IsEqualTo(0L);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    // === OversizeDiscarded (regression for MEDIUM-5) ===

    [Test]
    public async Task ProcessFragment_OversizeTerminal_ReturnsNullAndRemovesBufferImmediately()
    {
        // Regression for MEDIUM-5: a terminal fragment whose offset + length exceeds
        // 65535 (IPv4 max datagram payload) must be discarded immediately and the pending
        // buffer removed, preventing memory accumulation under malformed traffic.
        DatagramDefragmenter<IntKey> d = new();

        // First fragment: creates the pending buffer.
        d.ProcessFragment(new IntKey(1), 0, moreFragments: true, [0xAA, 0xBB]);
        await Assert.That(d.PendingCount).IsEqualTo(1);

        // Terminal fragment with offset = 65520 and 16 data bytes:
        // total = 65520 + 16 = 65536 > 65535 → OversizeDiscarded.
        byte[] bigData = new byte[16];
        byte[]? result = d.ProcessFragment(new IntKey(1), 65520, moreFragments: false, bigData);

        await Assert.That(result).IsNull();
        // Buffer must be removed immediately, not left pending until eviction.
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ProcessFragment_EvictionDuringReassembly_DiscardsPartialDatagram()
    {
        DatagramDefragmenter<IntKey> d = new(maxEntries: 2);
        d.ProcessFragment(new IntKey(1), 0, moreFragments: true, [0x01]);
        d.ProcessFragment(new IntKey(2), 0, moreFragments: true, [0x02]);
        byte[]? result = d.ProcessFragment(new IntKey(3), 0, moreFragments: true, [0x03]);
        await Assert.That(result).IsNull();
        await Assert.That(d.EvictedCount).IsEqualTo(1L);
        await Assert.That(d.PendingCount).IsEqualTo(2);
    }

    [Test]
    public async Task ProcessFragment_OverlapDiscarded_ReturnsNull()
    {
        DatagramDefragmenter<IntKey> d = new(dropOnOverlap: true);
        d.ProcessFragment(new IntKey(1), 0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        byte[]? result = d.ProcessFragment(new IntKey(1), 2, moreFragments: false, [0x05, 0x06]);
        await Assert.That(result).IsNull();
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    // === DatagramFragmentBuffer direct API ===

    [Test]
    public async Task FragmentBuffer_DuplicateOffset_KeepsLargerPayload()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02]);
        FragmentAddResult second = buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        await Assert.That(buffer.FragmentCount).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(FragmentAddResult.Incomplete);
    }

    [Test]
    public async Task FragmentBuffer_OverlapWithDropOnOverlap_ReturnsOverlapDiscarded()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        FragmentAddResult result = buffer.AddFragment(2, moreFragments: true, [0x05, 0x06], dropOnOverlap: true);
        await Assert.That(result).IsEqualTo(FragmentAddResult.OverlapDiscarded);
    }

    [Test]
    public async Task FragmentBuffer_Reassemble_ContiguousFragments()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        FragmentAddResult last = buffer.AddFragment(4, moreFragments: false, [0x05, 0x06]);
        await Assert.That(last).IsEqualTo(FragmentAddResult.Complete);
        byte[]? payload = buffer.Reassemble();
        await Assert.That(payload).IsEqualTo([0x01, 0x02, 0x03, 0x04, 0x05, 0x06]);
    }

    [Test]
    public async Task FragmentBuffer_OversizeTerminal_ReturnsOversizeDiscarded()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01]);
        FragmentAddResult result = buffer.AddFragment(65520, moreFragments: false, new byte[20]);
        await Assert.That(result).IsEqualTo(FragmentAddResult.OversizeDiscarded);
    }
}
