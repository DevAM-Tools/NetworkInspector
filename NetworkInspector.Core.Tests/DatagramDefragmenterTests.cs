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
        await Assert.That(d.ReassembledCount).IsEqualTo(1);
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
        await Assert.That(d.EvictedCount).IsEqualTo(0);

        d.ProcessFragment(new IntKey(9999), 0, moreFragments: true, [0xBB]);

        await Assert.That(d.PendingCount).IsEqualTo(1024);
        await Assert.That(d.EvictedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_ResetsCounters()
    {
        DatagramDefragmenter<IntKey> d = new();
        d.ProcessFragment(new IntKey(1), 0, moreFragments: false, [1]);
        d.Clear();
        await Assert.That(d.ReassembledCount).IsEqualTo(0);
        await Assert.That(d.EvictedCount).IsEqualTo(0);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    // === OversizeDiscarded (regression for MEDIUM-5) ===

    [Test]
    public async Task ProcessFragment_OversizeNonTerminal_ReturnsNullWithoutCreatingBuffer()
    {
        DatagramDefragmenter<IntKey> d = new();
        byte[]? result = d.ProcessFragment(new IntKey(1), 70000, moreFragments: true, [0xAA]);
        await Assert.That(result).IsNull();
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task FragmentBuffer_OversizeNonTerminal_ReturnsOversizeDiscarded()
    {
        DatagramFragmentBuffer buffer = new();
        FragmentAddResult result = buffer.AddFragment(70000, moreFragments: true, [0xAA]);
        await Assert.That(result).IsEqualTo(FragmentAddResult.OversizeDiscarded);
        await Assert.That(buffer.FragmentCount).IsEqualTo(0);
    }

    [Test]
    public async Task FragmentBuffer_IntegerOverflowOffset_DoesNotCreateFragment()
    {
        DatagramFragmentBuffer buffer = new();
        FragmentAddResult result = buffer.AddFragment(int.MaxValue - 1, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        await Assert.That(result).IsEqualTo(FragmentAddResult.OversizeDiscarded);
        await Assert.That(buffer.FragmentCount).IsEqualTo(0);
    }

    [Test]
    public async Task FragmentBuffer_OverlapDiscarded_PoisonsBuffer()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        FragmentAddResult overlap = buffer.AddFragment(2, moreFragments: true, [0x05, 0x06], dropOnOverlap: true);
        FragmentAddResult after = buffer.AddFragment(8, moreFragments: false, [0x07, 0x08]);
        await Assert.That(overlap).IsEqualTo(FragmentAddResult.OverlapDiscarded);
        await Assert.That(after).IsEqualTo(FragmentAddResult.OverlapDiscarded);
    }

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
        await Assert.That(d.EvictedCount).IsEqualTo(1);
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
        byte[] expected = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        await Assert.That(payload).IsEquivalentTo(expected);
    }

    [Test]
    public async Task FragmentBuffer_OversizeTerminal_ReturnsOversizeDiscarded()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01]);
        FragmentAddResult result = buffer.AddFragment(65520, moreFragments: false, new byte[20]);
        await Assert.That(result).IsEqualTo(FragmentAddResult.OversizeDiscarded);
    }

    [Test]
    public async Task FragmentBuffer_DuplicateSmallerFragment_StaysIncomplete()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        FragmentAddResult duplicate = buffer.AddFragment(0, moreFragments: true, [0x01, 0x02]);
        await Assert.That(duplicate).IsEqualTo(FragmentAddResult.Incomplete);
    }

    [Test]
    public async Task FragmentBuffer_GappedFragments_StayIncomplete()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: false, [0x01]);
        FragmentAddResult second = buffer.AddFragment(4, moreFragments: false, [0x02]);
        await Assert.That(second).IsEqualTo(FragmentAddResult.Incomplete);
    }

    [Test]
    public async Task FragmentBuffer_Reassemble_BeforeTerminal_ReturnsNull()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02]);
        byte[]? payload = buffer.Reassemble();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task ProcessFragment_StaleQueueDrainWithoutEviction_AllowsNewEntry()
    {
        DatagramDefragmenter<IntKey> d = new(maxEntries: 1);
        d.ProcessFragment(new IntKey(1), 0, moreFragments: false, [0x01]);
        d.ProcessFragment(new IntKey(2), 0, moreFragments: true, [0x02]);
        await Assert.That(d.PendingCount).IsEqualTo(1);
        await Assert.That(d.EvictedCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvictOldestEntry_AllStaleQueueEntries_CompletesWithoutEviction()
    {
        DatagramDefragmenter<IntKey> d = new(maxEntries: 1);
        d.ProcessFragment(new IntKey(1), 0, moreFragments: false, [0x01]);

        MethodInfo? evict = typeof(DatagramDefragmenter<IntKey>).GetMethod(
            "_EvictOldestEntry", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(evict).IsNotNull();
        evict!.Invoke(d, null);

        await Assert.That(d.EvictedCount).IsEqualTo(0);
    }

    [Test]
    public async Task FragmentBuffer_DuplicateSmallerAfterComplete_ReturnsComplete()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        FragmentAddResult terminal = buffer.AddFragment(4, moreFragments: false, [0x05, 0x06]);
        FragmentAddResult duplicate = buffer.AddFragment(0, moreFragments: false, [0x01, 0x02]);

        await Assert.That(terminal).IsEqualTo(FragmentAddResult.Complete);
        await Assert.That(duplicate).IsEqualTo(FragmentAddResult.Complete);
    }

    [Test]
    public async Task FragmentBuffer_OverlapBeforeNextFragment_DiscardsOverlap()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        buffer.AddFragment(8, moreFragments: true, [0x05, 0x06]);
        FragmentAddResult result = buffer.AddFragment(6, moreFragments: true, [0x07, 0x08, 0x09, 0x0A], dropOnOverlap: true);
        await Assert.That(result).IsEqualTo(FragmentAddResult.OverlapDiscarded);
    }

    [Test]
    public async Task FragmentBuffer_Reassemble_FragmentBeyondTotalLength_ReturnsNull()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: false, [0x01, 0x02, 0x03, 0x04]);
        System.Reflection.FieldInfo? totalLengthField = typeof(DatagramFragmentBuffer).GetField(
            "_TotalLength", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(totalLengthField).IsNotNull();
        totalLengthField!.SetValue(buffer, 2);

        byte[]? payload = buffer.Reassemble();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task FragmentBuffer_Reassemble_CompletePayload_ReturnsBytes()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02]);
        buffer.AddFragment(2, moreFragments: false, [0x03, 0x04]);
        byte[]? payload = buffer.Reassemble();
        byte[] expected = [0x01, 0x02, 0x03, 0x04];
        await Assert.That(payload).IsEquivalentTo(expected);
    }

    [Test]
    public async Task FragmentBuffer_IsComplete_WithGap_ReturnsFalse()
    {
        DatagramFragmentBuffer buffer = new();
        buffer.AddFragment(0, moreFragments: true, [0x01, 0x02, 0x03, 0x04]);
        buffer.AddFragment(6, moreFragments: false, [0x05, 0x06]);
        System.Reflection.FieldInfo? receivedBytesField = typeof(DatagramFragmentBuffer).GetField(
            "_ReceivedBytes", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(receivedBytesField).IsNotNull();
        receivedBytesField!.SetValue(buffer, 8);

        MethodInfo? isComplete = typeof(DatagramFragmentBuffer).GetMethod(
            "_IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(isComplete).IsNotNull();
        bool complete = (bool)isComplete!.Invoke(buffer, null)!;
        await Assert.That(complete).IsFalse();
    }
}
