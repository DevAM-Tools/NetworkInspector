// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="FragmentReassembler{TKey}"/>: the former IPv4/IPv6 defragmenter cases run against the two presets
/// with their original expectations, and the options, outcomes, byte budget, eviction orders, gap policy and allocation
/// behavior that the generic type adds.
/// </summary>
internal sealed class FragmentReassemblerTests
{
    private readonly struct IntKey(int v) : IEquatable<IntKey>
    {
        public readonly int V = v;
        public bool Equals(IntKey other) => V == other.V;
        public override bool Equals(object? obj) => obj is IntKey k && Equals(k);
        public override int GetHashCode() => V;
    }

    /// <summary>The options of the TP-like users: first wins, aligned, restart, gap prefix. Alignment 1 keeps the byte arrays small.</summary>
    private static FragmentReassemblyOptions _KeepFirstOptions { get; } = new()
    {
        MaxPending = 16,
        MaxBufferedBytes = long.MaxValue,
        MaxDatagramLength = 1 << 20,
        MaxFragments = FragmentReassemblyOptions.Unbounded,
        NonLastAlignment = 1,
        OverlapPolicy = FragmentOverlapPolicy.KeepFirst,
        EvictionPolicy = FragmentEvictionPolicy.LeastRecentlyUsed,
        RestartOnZeroOffset = false,
        GapPolicy = FragmentGapPolicy.PrefixOnLastThenComplete,
    };

    private static FragmentOutcome _Add(FragmentReassembler<IntKey> reassembler, int key, long offset, bool more, byte[] data, int contributor = 0) =>
        reassembler.Add(new IntKey(key), offset, more, data, contributor);

    private static FragmentReassembler<IntKey> _Ipv4(int maxPending = 1024) =>
        new(FragmentReassemblyOptions.IPv4 with { MaxPending = maxPending });

    private static byte[] _Pattern(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    /// <summary>An int array built per call, so the expectation is not a constant array argument (CA1861).</summary>
    private static int[] _Ints(params ReadOnlySpan<int> values) => values.ToArray();

    private static FragmentBuffer<IntKey> _Buffer() => new(new IntKey(0), FragmentPools.Shared);

    private static FragmentDiscardReason _BufferAdd(FragmentBuffer<IntKey> buffer, int start, bool more, byte[] data, in FragmentReassemblyOptions options) =>
        buffer.Add(start, more, data, 0, in options, out _, out _);

    #region IP regression (former DatagramDefragmenterTests)

    [Test]
    public async Task SingleFragmentNoMoreReassemblesImmediately()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome result = _Add(d, 1, 0, false, [1, 2, 3, 4]);
        await Assert.That(result.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(result.Payload!.Length).IsEqualTo(4);
        await Assert.That(d.ReassembledCount).IsEqualTo(1);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task TwoFragmentsInOrderReassemble()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome a = _Add(d, 1, 0, true, [1, 2, 3, 4]);
        await Assert.That(a.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        FragmentOutcome b = _Add(d, 1, 4, false, [5, 6, 7, 8]);
        await Assert.That(b.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(b.Payload).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    [Test]
    public async Task TwoFragmentsOutOfOrderReassemble()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome a = _Add(d, 1, 4, false, [5, 6, 7, 8]);
        await Assert.That(a.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        FragmentOutcome b = _Add(d, 1, 0, true, [1, 2, 3, 4]);
        await Assert.That(b.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(b.Payload).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    [Test]
    public async Task EvictedCountIncrementsWhenCapacityReached()
    {
        FragmentReassembler<IntKey> d = _Ipv4();

        // Push 1024 in-progress entries (no MF=false, so they stay pending), then push one more to force an eviction.
        for (int i = 0; i < 1024; i++)
        {
            _ = _Add(d, i, 0, true, [0xAA]);
        }

        await Assert.That(d.PendingCount).IsEqualTo(1024);
        await Assert.That(d.EvictedCount).IsEqualTo(0);

        FragmentOutcome outcome = _Add(d, 9999, 0, true, [0xBB]);

        await Assert.That(d.PendingCount).IsEqualTo(1024);
        await Assert.That(d.EvictedCount).IsEqualTo(1);
        await Assert.That(outcome.Flags).IsEqualTo(FragmentOutcomeFlags.EvictedForCount);
        await Assert.That(outcome.EvictedCount).IsEqualTo(1);
    }

    [Test]
    public async Task ClearResetsCounters()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        _ = _Add(d, 1, 0, false, [1]);
        d.Clear();
        await Assert.That(d.ReassembledCount).IsEqualTo(0);
        await Assert.That(d.EvictedCount).IsEqualTo(0);
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task OversizeNonTerminalIsRefusedWithoutCreatingBuffer()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome result = _Add(d, 1, 70000, true, [0xAA]);
        await Assert.That(result.Kind).IsEqualTo(FragmentOutcomeKind.Discarded);
        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task OffsetBeyondTheIntRangeIsRefusedWithoutCreatingBuffer()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome result = _Add(d, 1, int.MaxValue - 1, true, [0x01, 0x02, 0x03, 0x04]);
        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task OffsetAtTheLimitIsRefusedEvenForAnEmptyFragment()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome result = _Add(d, 1, 65535, false, []);
        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task OverlapPoisonsBufferUnderTheDiscardPolicy()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv6;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        FragmentDiscardReason overlap = _BufferAdd(buffer, 2, true, [0x05, 0x06], in options);
        FragmentDiscardReason after = _BufferAdd(buffer, 8, false, [0x07, 0x08], in options);
        await Assert.That(overlap).IsEqualTo(FragmentDiscardReason.Overlap);
        await Assert.That(after).IsEqualTo(FragmentDiscardReason.Overlap);
        buffer.Release();
    }

    [Test]
    public async Task OversizeTerminalRemovesBufferImmediately()
    {
        // A terminal fragment whose offset + length exceeds 65535 (IPv4 max datagram payload) must be discarded immediately
        // and the pending buffer removed, preventing memory accumulation under malformed traffic.
        FragmentReassembler<IntKey> d = _Ipv4();
        _ = _Add(d, 1, 0, true, [0xAA, 0xBB]);
        await Assert.That(d.PendingCount).IsEqualTo(1);

        // offset = 65520 and 16 data bytes: total = 65536 > 65535.
        FragmentOutcome result = _Add(d, 1, 65520, false, new byte[16]);

        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task EvictionDuringReassemblyDiscardsPartialDatagram()
    {
        FragmentReassembler<IntKey> d = _Ipv4(maxPending: 2);
        _ = _Add(d, 1, 0, true, [0x01]);
        _ = _Add(d, 2, 0, true, [0x02]);
        FragmentOutcome result = _Add(d, 3, 0, true, [0x03]);
        await Assert.That(result.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(d.EvictedCount).IsEqualTo(1);
        await Assert.That(d.PendingCount).IsEqualTo(2);
    }

    [Test]
    public async Task Ipv6OverlapDiscardsTheDatagram()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv6);
        _ = _Add(d, 1, 0, true, [0x01, 0x02, 0x03, 0x04]);
        FragmentOutcome result = _Add(d, 1, 2, false, [0x05, 0x06]);
        await Assert.That(result.Kind).IsEqualTo(FragmentOutcomeKind.Discarded);
        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.Overlap);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task DuplicateOffsetKeepsLargerPayload()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        FragmentDiscardReason second = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        await Assert.That(buffer.FragmentCount).IsEqualTo(1);
        await Assert.That(buffer.StoredBytes).IsEqualTo(4);
        await Assert.That(second).IsEqualTo(FragmentDiscardReason.None);
        await Assert.That(buffer.IsComplete()).IsFalse();
        buffer.Release();
    }

    [Test]
    public async Task OverlapUnderTheDiscardPolicyIsRefused()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv6;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        FragmentDiscardReason result = _BufferAdd(buffer, 2, true, [0x05, 0x06], in options);
        await Assert.That(result).IsEqualTo(FragmentDiscardReason.Overlap);
        buffer.Release();
    }

    [Test]
    public async Task AssembleContiguousFragments()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        _ = _BufferAdd(buffer, 4, false, [0x05, 0x06], in options);
        await Assert.That(buffer.IsComplete()).IsTrue();
        await Assert.That(buffer.Assemble()).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 });
        buffer.Release();
    }

    [Test]
    public async Task DuplicateSmallerFragmentStaysIncomplete()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        FragmentDiscardReason duplicate = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        await Assert.That(duplicate).IsEqualTo(FragmentDiscardReason.None);
        await Assert.That(buffer.StoredBytes).IsEqualTo(4);
        await Assert.That(buffer.IsComplete()).IsFalse();
        buffer.Release();
    }

    [Test]
    public async Task GappedFragmentsStayIncomplete()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01], in options);
        _ = _BufferAdd(buffer, 4, false, [0x02], in options);
        await Assert.That(buffer.IsComplete()).IsFalse();
        buffer.Release();
    }

    [Test]
    public async Task AssembleBeforeTerminalReturnsNull()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        await Assert.That(buffer.Assemble()).IsNull();
        buffer.Release();
    }

    [Test]
    public async Task CompletedEntryDoesNotBlockTheNextOneAtTheLimit()
    {
        FragmentReassembler<IntKey> d = _Ipv4(maxPending: 1);
        _ = _Add(d, 1, 0, false, [0x01]);
        _ = _Add(d, 2, 0, true, [0x02]);
        await Assert.That(d.PendingCount).IsEqualTo(1);
        await Assert.That(d.EvictedCount).IsEqualTo(0);
    }

    [Test]
    public async Task EvictingWithoutOpenDatagramsEvictsNothing()
    {
        FragmentReassembler<IntKey> d = _Ipv4(maxPending: 1);
        _ = _Add(d, 1, 0, false, [0x01]);

        MethodInfo? evict = typeof(FragmentReassembler<IntKey>).GetMethod("_EvictOne", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(evict).IsNotNull();
        object? evicted = evict!.Invoke(d, [null]);

        await Assert.That((bool)evicted!).IsFalse();
        await Assert.That(d.EvictedCount).IsEqualTo(0);
    }

    [Test]
    public async Task DuplicateSmallerAfterCompleteStaysComplete()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        _ = _BufferAdd(buffer, 4, false, [0x05, 0x06], in options);
        bool terminal = buffer.IsComplete();
        FragmentDiscardReason duplicate = _BufferAdd(buffer, 0, false, [0x01, 0x02], in options);

        await Assert.That(terminal).IsTrue();
        await Assert.That(duplicate).IsEqualTo(FragmentDiscardReason.None);
        await Assert.That(buffer.IsComplete()).IsTrue();
        buffer.Release();
    }

    [Test]
    public async Task DuplicateWithoutMoreFragmentsSetsAnUnknownTotalLength()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        _ = _BufferAdd(buffer, 2, true, [0x03, 0x04], in options);
        bool incomplete = buffer.IsComplete();

        // The same offset again, now marked as the last fragment: it is the first fragment to say where the datagram ends.
        _ = _BufferAdd(buffer, 2, false, [0x03, 0x04], in options);

        await Assert.That(incomplete).IsFalse();
        await Assert.That(buffer.IsComplete()).IsTrue();
        await Assert.That(buffer.Assemble()).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        buffer.Release();
    }

    [Test]
    public async Task DuplicateWithoutMoreFragmentsDoesNotReplaceAKnownTotalLength()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        _ = _BufferAdd(buffer, 2, false, [0x03, 0x04], in options);

        // A duplicate at an existing offset claims a different end; the known end stays and the datagram is not discarded.
        FragmentDiscardReason duplicate = _BufferAdd(buffer, 0, false, [0x01], in options);

        await Assert.That(duplicate).IsEqualTo(FragmentDiscardReason.None);
        await Assert.That(buffer.IsComplete()).IsTrue();
        await Assert.That(buffer.Assemble()).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        buffer.Release();
    }

    [Test]
    public async Task SecondTerminalFragmentWithOtherEndDiscardsTheDatagram()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        _ = _BufferAdd(buffer, 2, false, [0x03, 0x04], in options);

        // A last fragment at another offset with another end contradicts the known total length.
        FragmentDiscardReason conflicting = _BufferAdd(buffer, 4, false, [0x05, 0x06], in options);
        FragmentDiscardReason after = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);

        await Assert.That(conflicting).IsEqualTo(FragmentDiscardReason.ConflictingEnd);
        await Assert.That(after).IsEqualTo(FragmentDiscardReason.ConflictingEnd);
        buffer.Release();
    }

    [Test]
    public async Task SecondTerminalFragmentWithSameEndIsAccepted()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02], in options);
        _ = _BufferAdd(buffer, 2, false, [0x03, 0x04], in options);

        // Re-fragmented retransmission: a different offset, the same end of the datagram.
        FragmentDiscardReason resent = _BufferAdd(buffer, 3, false, [0x04], in options);

        await Assert.That(resent).IsEqualTo(FragmentDiscardReason.None);
        await Assert.That(buffer.IsComplete()).IsTrue();
        buffer.Release();
    }

    [Test]
    public async Task SecondTerminalFragmentWithOtherEndRemovesTheDatagram()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        _ = _Add(d, 1, 0, true, [0x01, 0x02]);
        _ = _Add(d, 1, 8, false, [0x09]);
        await Assert.That(d.PendingCount).IsEqualTo(1);

        FragmentOutcome result = _Add(d, 1, 16, false, [0x10]);

        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.ConflictingEnd);
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task OverlapBeforeTheNextFragmentIsRefusedUnderTheDiscardPolicy()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv6;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        _ = _BufferAdd(buffer, 8, true, [0x05, 0x06], in options);
        FragmentDiscardReason result = _BufferAdd(buffer, 6, true, [0x07, 0x08, 0x09, 0x0A], in options);
        await Assert.That(result).IsEqualTo(FragmentDiscardReason.Overlap);
        buffer.Release();
    }

    [Test]
    public async Task AssembleFragmentBeyondTheTotalLengthReturnsNull()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, false, [0x01, 0x02, 0x03, 0x04], in options);
        System.Reflection.FieldInfo? totalLengthField = typeof(FragmentBuffer<IntKey>).GetField("_TotalLength", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(totalLengthField).IsNotNull();
        totalLengthField!.SetValue(buffer, 2);

        await Assert.That(buffer.Assemble()).IsNull();
        buffer.Release();
    }

    [Test]
    public async Task IsCompleteWithAHoleReturnsFalseEvenWhenTheByteCountReachesTheTotal()
    {
        FragmentBuffer<IntKey> buffer = _Buffer();
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4;
        _ = _BufferAdd(buffer, 0, true, [0x01, 0x02, 0x03, 0x04], in options);
        _ = _BufferAdd(buffer, 6, false, [0x05, 0x06], in options);
        System.Reflection.FieldInfo? storedBytesField = typeof(FragmentBuffer<IntKey>).GetField("_StoredBytes", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(storedBytesField).IsNotNull();
        storedBytesField!.SetValue(buffer, 8L);

        await Assert.That(buffer.IsComplete()).IsFalse();
        buffer.Release();
    }

    [Test]
    public async Task KeyRestartedAfterDiscardEvictsTheOlderDatagramFirst()
    {
        FragmentReassembler<IntKey> d = _Ipv4(maxPending: 2);

        // A starts, then is dropped (a fragment past 65535 bytes); it leaves nothing behind.
        _ = _Add(d, 1, 0, true, [1, 2, 3, 4, 5, 6, 7, 8]);
        _ = _Add(d, 1, 65528, false, new byte[16]);
        // B starts, then A starts again: B is now the older pending datagram.
        _ = _Add(d, 2, 0, true, [1, 2, 3, 4, 5, 6, 7, 8]);
        _ = _Add(d, 1, 0, true, [9, 9, 9, 9, 9, 9, 9, 9]);

        // C at the limit evicts B, not the restarted A.
        _ = _Add(d, 3, 0, true, [1, 2, 3, 4, 5, 6, 7, 8]);
        FragmentOutcome completedA = _Add(d, 1, 8, false, [7, 7]);

        await Assert.That(d.EvictedCount).IsEqualTo(1);
        await Assert.That(completedA.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(completedA.Payload!.Length).IsEqualTo(10);
        await Assert.That(_Add(d, 2, 8, false, [7, 7]).Kind).IsEqualTo(FragmentOutcomeKind.Pending);
    }

    [Test]
    public async Task Ipv4KeepsTheLargerDuplicateAndLaterBytesOverwrite()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        _ = _Add(d, 1, 0, true, [1, 2]);
        _ = _Add(d, 1, 0, true, [1, 2, 3, 4]);
        FragmentOutcome result = _Add(d, 1, 4, false, [5, 6]);

        await Assert.That(result.Payload).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 });
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task IpPresetsHaveTheOriginalValues()
    {
        FragmentReassemblyOptions v4 = FragmentReassemblyOptions.IPv4;
        FragmentReassemblyOptions v6 = FragmentReassemblyOptions.IPv6;

        await Assert.That(v4.MaxPending).IsEqualTo(1024);
        await Assert.That(v4.MaxDatagramLength).IsEqualTo(65535);
        await Assert.That(v4.MaxFragments).IsEqualTo(8192);
        await Assert.That(v4.MaxBufferedBytes).IsEqualTo(long.MaxValue);
        await Assert.That(v4.OverlapPolicy).IsEqualTo(FragmentOverlapPolicy.KeepLargestDuplicate);
        await Assert.That(v6.OverlapPolicy).IsEqualTo(FragmentOverlapPolicy.DiscardDatagram);
        await Assert.That(v6.EvictionPolicy).IsEqualTo(FragmentEvictionPolicy.OldestCreated);
        await Assert.That(v6.RestartOnZeroOffset).IsFalse();
        await Assert.That(v6.GapPolicy).IsEqualTo(FragmentGapPolicy.WaitForCompletion);
    }

    [Test]
    public async Task FragmentLimitReleasesTheDatagram()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv4 with { MaxFragments = 2 });
        _ = _Add(d, 1, 0, true, [1]);
        _ = _Add(d, 1, 8, true, [2]);
        FragmentOutcome result = _Add(d, 1, 16, true, [3]);

        await Assert.That(result.DiscardReason).IsEqualTo(FragmentDiscardReason.FragmentLimit);
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    #endregion

    #region Options

    [Test]
    [Arguments(0, 1L, 1, 1, 1, "MaxPending")]
    [Arguments(1, 0L, 1, 1, 1, "MaxBufferedBytes")]
    [Arguments(1, 1L, 0, 1, 1, "MaxDatagramLength")]
    [Arguments(1, 1L, int.MaxValue, 1, 1, "MaxDatagramLength")]
    [Arguments(1, 1L, 1, 0, 1, "MaxFragments")]
    [Arguments(1, 1L, 1, 1, 0, "NonLastAlignment")]
    public async Task InvalidOptionsAreRefusedByName(int maxPending, long maxBuffered, int maxLength, int maxFragments, int alignment, string name)
    {
        FragmentReassemblyOptions options = new()
        {
            MaxPending = maxPending,
            MaxBufferedBytes = maxBuffered,
            MaxDatagramLength = maxLength,
            MaxFragments = maxFragments,
            NonLastAlignment = alignment,
        };

        bool valid = options.TryValidate(out string? problem);
        ArgumentException? thrown = null;
        try
        {
            _ = new FragmentReassembler<IntKey>(in options);
        }
        catch (ArgumentException ex)
        {
            thrown = ex;
        }

        await Assert.That(valid).IsFalse();
        await Assert.That(problem!.Contains(name, StringComparison.Ordinal)).IsTrue();
        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message.Contains(name, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task UndefinedPolicyValuesAreRefused()
    {
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4 with { OverlapPolicy = (FragmentOverlapPolicy)99 };
        FragmentReassemblyOptions eviction = FragmentReassemblyOptions.IPv4 with { EvictionPolicy = (FragmentEvictionPolicy)99 };
        FragmentReassemblyOptions gap = FragmentReassemblyOptions.IPv4 with { GapPolicy = (FragmentGapPolicy)99 };

        await Assert.That(options.TryValidate(out string? overlapProblem)).IsFalse();
        await Assert.That(eviction.TryValidate(out string? evictionProblem)).IsFalse();
        await Assert.That(gap.TryValidate(out string? gapProblem)).IsFalse();
        await Assert.That(overlapProblem!.Contains("OverlapPolicy", StringComparison.Ordinal)).IsTrue();
        await Assert.That(evictionProblem!.Contains("EvictionPolicy", StringComparison.Ordinal)).IsTrue();
        await Assert.That(gapProblem!.Contains("GapPolicy", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task PresetsAndBoundaryValuesAreValid()
    {
        FragmentReassemblyOptions minimal = FragmentReassemblyOptions.IPv4 with { MaxDatagramLength = 1, NonLastAlignment = 1 };
        FragmentReassemblyOptions maximal = FragmentReassemblyOptions.IPv4 with { MaxDatagramLength = Array.MaxLength };

        await Assert.That(FragmentReassemblyOptions.IPv4.TryValidate(out _)).IsTrue();
        await Assert.That(FragmentReassemblyOptions.IPv6.TryValidate(out _)).IsTrue();
        await Assert.That(minimal.TryValidate(out _)).IsTrue();
        await Assert.That(maximal.TryValidate(out _)).IsTrue();
    }

    [Test]
    public async Task DefaultOptionsAreRefused()
    {
        FragmentReassemblyOptions options = default;
        await Assert.That(options.TryValidate(out string? problem)).IsFalse();
        await Assert.That(problem).IsNotNull();
    }

    #endregion

    #region Outcomes

    [Test]
    public async Task PendingOutcomeCarriesNoArrays()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        FragmentOutcome pending = _Add(d, 1, 0, true, [1, 2, 3, 4]);

        await Assert.That(pending.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(pending.Payload).IsNull();
        await Assert.That(pending.Contributors).IsNull();
        await Assert.That(pending.Gaps).IsNull();
        await Assert.That(pending.CoveredAfterGap).IsNull();
        await Assert.That(pending.DeliversPayload).IsFalse();
    }

    [Test]
    public async Task DefaultOutcomeIsNotRecorded()
    {
        FragmentOutcome outcome = default;
        await Assert.That(outcome.Kind).IsEqualTo(FragmentOutcomeKind.NotRecorded);
        await Assert.That(FragmentOutcome.NotRecorded.Kind).IsEqualTo(FragmentOutcomeKind.NotRecorded);
        await Assert.That(outcome.DeliversPayload).IsFalse();
    }

    [Test]
    public async Task CompleteOutcomeListsContributorsInOffsetOrder()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);
        _ = _Add(d, 1, 8, false, [9, 10], contributor: 30);
        _ = _Add(d, 1, 0, true, [1, 2, 3, 4], contributor: 10);
        FragmentOutcome complete = _Add(d, 1, 4, true, [5, 6, 7, 8], contributor: 20);

        // The last call was the middle fragment; the datagram only completes when every hole is closed.
        await Assert.That(complete.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(complete.Contributors).IsEquivalentTo(_Ints(10, 20, 30));
        await Assert.That(complete.FragmentCount).IsEqualTo(3);
        await Assert.That(complete.DatagramLength).IsEqualTo(10);
        await Assert.That(complete.Payload).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        await Assert.That(complete.DeliversPayload).IsTrue();
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ExtentLimitReleasesAnOpenDatagramOfTheKey()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        _ = _Add(d, 1, 0, true, [1, 2, 3, 4]);
        await Assert.That(d.BufferedBytes).IsEqualTo(4);

        FragmentOutcome outcome = _Add(d, 1, 70000, true, [1]);

        await Assert.That(outcome.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task AlignmentRefusesAndLeavesAnOpenDatagramIntact()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { NonLastAlignment = 16 });
        _ = _Add(d, 1, 0, true, new byte[16]);

        FragmentOutcome unaligned = _Add(d, 1, 16, true, new byte[20]);
        FragmentOutcome empty = _Add(d, 2, 0, true, []);

        await Assert.That(unaligned.DiscardReason).IsEqualTo(FragmentDiscardReason.Alignment);
        await Assert.That(empty.DiscardReason).IsEqualTo(FragmentDiscardReason.Alignment);
        await Assert.That(d.PendingCount).IsEqualTo(1);
        await Assert.That(d.BufferedBytes).IsEqualTo(16);
    }

    [Test]
    public async Task BeyondEndRefusesTheFragmentAndKeepsTheOpenDatagram()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion });
        _ = _Add(d, 1, 0, true, new byte[8]);
        _ = _Add(d, 1, 8, false, new byte[8]);

        // Complete at 16 bytes already; open a datagram whose end is 16 and then send a fragment behind it.
        _ = _Add(d, 2, 8, false, new byte[8]);
        FragmentOutcome beyond = _Add(d, 2, 16, true, new byte[8]);

        await Assert.That(beyond.DiscardReason).IsEqualTo(FragmentDiscardReason.BeyondEnd);
        await Assert.That(d.PendingCount).IsEqualTo(1);
        await Assert.That(d.BufferedBytes).IsEqualTo(8);
    }

    [Test]
    public async Task RestartOnZeroOffsetReleasesTheEarlierFragments()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion, RestartOnZeroOffset = true });
        _ = _Add(d, 1, 0, true, [1, 2, 3, 4]);
        _ = _Add(d, 1, 4, true, [5, 6, 7, 8]);
        await Assert.That(d.BufferedBytes).IsEqualTo(8);

        FragmentOutcome restarted = _Add(d, 1, 0, true, [9, 9, 9, 9]);

        await Assert.That(restarted.Flags.HasFlag(FragmentOutcomeFlags.Restarted)).IsTrue();
        await Assert.That(restarted.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(d.BufferedBytes).IsEqualTo(4);

        FragmentOutcome complete = _Add(d, 1, 4, false, [1, 2]);
        await Assert.That(complete.Payload).IsEquivalentTo(new byte[] { 9, 9, 9, 9, 1, 2 });
        await Assert.That(complete.Flags.HasFlag(FragmentOutcomeFlags.Restarted)).IsFalse();
    }

    [Test]
    public async Task AnOffsetZeroFragmentThatArrivesLateIsTheMissingStartAndNotARestart()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion, RestartOnZeroOffset = true });
        _ = _Add(d, 1, 4, true, [5, 6, 7, 8]);

        FragmentOutcome start = _Add(d, 1, 0, true, [1, 2, 3, 4]);
        FragmentOutcome complete = _Add(d, 1, 8, false, [9, 9]);

        await Assert.That(start.Flags.HasFlag(FragmentOutcomeFlags.Restarted)).IsFalse();
        await Assert.That(complete.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(complete.Payload).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 9 });
    }
    [Test]
    public async Task DifferingOverlapBytesAreAConflictAndKeepTheFirstBytes()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion });
        _ = _Add(d, 1, 0, true, [1, 1, 1, 1, 1, 1, 1, 1]);

        // [4, 12) overlaps [4, 8) with other bytes: only [8, 12) is new.
        FragmentOutcome conflict = _Add(d, 1, 4, true, [2, 2, 2, 2, 2, 2, 2, 2]);
        await Assert.That(conflict.Flags.HasFlag(FragmentOutcomeFlags.OverlapConflict)).IsTrue();
        await Assert.That(d.BufferedBytes).IsEqualTo(12);

        FragmentOutcome complete = _Add(d, 1, 12, false, [3, 3]);
        await Assert.That(complete.Payload).IsEquivalentTo(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3 });
    }

    [Test]
    public async Task IdenticalOverlapBytesAreNoConflict()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);
        byte[] bytes = _Pattern(16, 1);
        _ = _Add(d, 1, 0, true, bytes[..8]);

        FragmentOutcome same = _Add(d, 1, 4, true, bytes[4..12]);

        await Assert.That(same.Flags.HasFlag(FragmentOutcomeFlags.OverlapConflict)).IsFalse();
        await Assert.That(d.BufferedBytes).IsEqualTo(12);
    }

    [Test]
    public async Task StraddlingFragmentIsStoredAsItsUncoveredPieces()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion });
        _ = _Add(d, 1, 4, true, [4, 5, 6, 7], contributor: 1);

        // [0, 12) straddles the kept [4, 8): the pieces [0, 4) and [8, 12) are stored, the kept fragments stay disjoint.
        _ = _Add(d, 1, 0, true, _Pattern(12, 0), contributor: 2);
        await Assert.That(d.BufferedBytes).IsEqualTo(12);

        FragmentOutcome complete = _Add(d, 1, 12, false, [12], contributor: 3);
        await Assert.That(complete.Payload).IsEquivalentTo(_Pattern(13, 0));
        await Assert.That(complete.Contributors).IsEquivalentTo(_Ints(2, 1, 3));
        await Assert.That(complete.FragmentCount).IsEqualTo(3);
    }

    [Test]
    public async Task AnEmptyNonLastFragmentLeavesNoDatagramBehind()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);
        for (int key = 0; key < 100; key++)
        {
            FragmentOutcome outcome = _Add(d, key, 0, true, []);
            await Assert.That(outcome.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        }

        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.EvictedCount).IsEqualTo(0);
    }

    [Test]
    public async Task FirstWinsRefusesALastFragmentThatContradictsTheEnd()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion });

        // A last fragment that ends before bytes that are already stored.
        _ = _Add(d, 1, 0, true, new byte[16]);
        FragmentOutcome before = _Add(d, 1, 0, false, new byte[8]);

        // A second last fragment with another end.
        _ = _Add(d, 2, 8, false, new byte[8]);
        FragmentOutcome other = _Add(d, 2, 8, false, new byte[4]);

        await Assert.That(before.DiscardReason).IsEqualTo(FragmentDiscardReason.ConflictingEnd);
        await Assert.That(other.DiscardReason).IsEqualTo(FragmentDiscardReason.ConflictingEnd);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task OverlapUnderTheDiscardPolicyAndAConflictingEndKeepTheirMeaning()
    {
        FragmentReassembler<IntKey> v6 = new(FragmentReassemblyOptions.IPv6);
        _ = _Add(v6, 1, 0, true, new byte[8]);
        FragmentOutcome overlap = _Add(v6, 1, 4, true, new byte[8]);

        FragmentReassembler<IntKey> v4 = _Ipv4();
        _ = _Add(v4, 1, 0, true, new byte[8]);
        _ = _Add(v4, 1, 16, false, new byte[8]);
        FragmentOutcome end = _Add(v4, 1, 32, false, new byte[8]);

        await Assert.That(overlap.DiscardReason).IsEqualTo(FragmentDiscardReason.Overlap);
        await Assert.That(end.DiscardReason).IsEqualTo(FragmentDiscardReason.ConflictingEnd);
    }

    [Test]
    public async Task FragmentsPastTheEndOfAnIpDatagramReleaseIt()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        _ = _Add(d, 1, 8, true, new byte[8]);

        // The last fragment ends before the stored fragment: the stored bytes lie past the end, the datagram cannot be built.
        FragmentOutcome outcome = _Add(d, 1, 0, false, new byte[8]);

        await Assert.That(outcome.DiscardReason).IsEqualTo(FragmentDiscardReason.BeyondEnd);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    #endregion

    #region Budget

    [Test]
    public async Task ByteBudgetEvictsInPolicyOrderAndAccountsEveryByte()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv4 with { MaxBufferedBytes = 4096 });
        _ = _Add(d, 1, 0, true, new byte[2048]);
        await Assert.That(d.BufferedBytes).IsEqualTo(2048);
        _ = _Add(d, 2, 0, true, new byte[2048]);
        await Assert.That(d.BufferedBytes).IsEqualTo(4096);

        // Does not fit: the datagram created first goes.
        FragmentOutcome third = _Add(d, 3, 0, true, new byte[2048]);

        await Assert.That(third.Flags.HasFlag(FragmentOutcomeFlags.EvictedForBudget)).IsTrue();
        await Assert.That(third.EvictedCount).IsEqualTo(1);
        await Assert.That(d.EvictedCount).IsEqualTo(1);
        await Assert.That(d.PendingCount).IsEqualTo(2);
        await Assert.That(d.BufferedBytes).IsEqualTo(4096);

        // Clear returns the counter to zero.
        d.Clear();
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ByteCounterReturnsToZeroAfterCompletionAndDiscard()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv4 with { MaxBufferedBytes = 4096 });
        _ = _Add(d, 1, 0, true, new byte[2048]);
        await Assert.That(d.BufferedBytes).IsEqualTo(2048);

        FragmentOutcome complete = _Add(d, 1, 2048, false, new byte[100]);
        await Assert.That(complete.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);

        _ = _Add(d, 2, 0, true, new byte[100]);
        FragmentOutcome discarded = _Add(d, 2, 70000, true, [1]);
        await Assert.That(discarded.Kind).IsEqualTo(FragmentOutcomeKind.Discarded);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);

        // A replaced duplicate adjusts the counter by the difference.
        _ = _Add(d, 3, 0, true, new byte[100]);
        _ = _Add(d, 3, 0, true, new byte[300]);
        await Assert.That(d.BufferedBytes).IsEqualTo(300);
    }

    [Test]
    public async Task ADatagramThatAloneExceedsTheBudgetIsRefused()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv4 with { MaxBufferedBytes = 4096 });
        _ = _Add(d, 1, 0, true, new byte[2048]);

        FragmentOutcome outcome = _Add(d, 2, 0, true, new byte[5000]);

        await Assert.That(outcome.DiscardReason).IsEqualTo(FragmentDiscardReason.BufferLimit);
        await Assert.That(outcome.Flags.HasFlag(FragmentOutcomeFlags.EvictedForBudget)).IsTrue();
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    [Test]
    public async Task TheDatagramBeingFilledIsNeverTheBudgetVictim()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv4 with { MaxBufferedBytes = 4096 });
        _ = _Add(d, 1, 0, true, new byte[2048]);
        _ = _Add(d, 2, 0, true, new byte[1024]);

        // Datagram 1 is the oldest, but it receives the next fragment: datagram 2 is evicted instead.
        FragmentOutcome outcome = _Add(d, 1, 2048, true, new byte[2048]);

        await Assert.That(outcome.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(outcome.EvictedCount).IsEqualTo(1);
        await Assert.That(d.PendingCount).IsEqualTo(1);
        await Assert.That(d.BufferedBytes).IsEqualTo(4096);
    }

    [Test]
    public async Task ExtentIsCheckedBeforeAnyCopy()
    {
        FragmentReassembler<IntKey> d = _Ipv4();
        byte[] data = new byte[16];
        _ = _Add(d, 1, 0xFFFFFFF0L, false, data); // warm-up: JIT and first-use allocations

        long before = GC.GetAllocatedBytesForCurrentThread();
        FragmentOutcome outcome = _Add(d, 2, 0xFFFFFFF0L, false, data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(outcome.DiscardReason).IsEqualTo(FragmentDiscardReason.ExtentLimit);
        await Assert.That(allocated).IsLessThan(1024);
        await Assert.That(d.PendingCount).IsEqualTo(0);
        await Assert.That(d.BufferedBytes).IsEqualTo(0);
    }

    #endregion

    #region Eviction order

    [Test]
    public async Task LeastRecentlyUsedEvictsTheUntouchedDatagram()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { MaxPending = 2, GapPolicy = FragmentGapPolicy.WaitForCompletion });
        _ = _Add(d, 1, 0, true, [1]);
        _ = _Add(d, 2, 0, true, [2]);
        _ = _Add(d, 1, 1, true, [3]); // touch A
        _ = _Add(d, 3, 0, true, [4]); // open C: B is the least recently used

        FragmentOutcome a = _Add(d, 1, 2, false, [5]);
        FragmentOutcome b = _Add(d, 2, 1, false, [6]);

        await Assert.That(a.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(b.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
    }

    [Test]
    public async Task OldestCreatedIgnoresTouches()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with
        {
            MaxPending = 2,
            EvictionPolicy = FragmentEvictionPolicy.OldestCreated,
            GapPolicy = FragmentGapPolicy.WaitForCompletion,
        });
        _ = _Add(d, 1, 0, true, [1]);
        _ = _Add(d, 2, 0, true, [2]);
        _ = _Add(d, 1, 1, true, [3]); // touch A: does not matter
        _ = _Add(d, 3, 0, true, [4]); // open C: A was created first

        FragmentOutcome b = _Add(d, 2, 1, false, [6]);
        FragmentOutcome a = _Add(d, 1, 2, false, [5]);

        await Assert.That(a.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(b.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
    }

    [Test]
    public async Task RepeatedTouchesDoNotGrowTheEvictionOrder()
    {
        FragmentReassembler<IntKey> d = new(FragmentReassemblyOptions.IPv4 with
        {
            MaxPending = 2,
            EvictionPolicy = FragmentEvictionPolicy.LeastRecentlyUsed,
        });
        byte[] data = [1];
        _ = _Add(d, 1, 0, true, data);
        _ = _Add(d, 2, 0, true, data);

        // A duplicate of an equal size changes nothing but the recency; a stamp-based queue would grow with every touch.
        for (int i = 0; i < 1_000_000; i++)
        {
            _ = d.Add(new IntKey((i & 1) + 1), 0, true, data, 0);
        }

        await Assert.That(d.EvictionOrderLength).IsLessThanOrEqualTo((2 * d.PendingCount) + 64);
        await Assert.That(d.EvictionOrderLength).IsEqualTo(d.PendingCount);
        await Assert.That(d.PendingCount).IsEqualTo(2);
    }

    #endregion

    #region Gap policy

    [Test]
    public async Task PartialPrefixIsReportedOnceThenTheDatagramCompletes()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);
        byte[] all = _Pattern(3072, 7);

        _ = _Add(d, 1, 0, true, all[..1024], contributor: 100);
        FragmentOutcome partial = _Add(d, 1, 2048, false, all[2048..], contributor: 102);

        await Assert.That(partial.Kind).IsEqualTo(FragmentOutcomeKind.PartialPrefix);
        await Assert.That(partial.PrefixLength).IsEqualTo(1024);
        await Assert.That(partial.DatagramLength).IsEqualTo(3072);
        await Assert.That(partial.Gaps).IsEquivalentTo(new FragmentRange[] { new(1024, 1024) });
        await Assert.That(partial.CoveredAfterGap).IsEquivalentTo(new FragmentRange[] { new(2048, 1024) });
        await Assert.That(partial.Payload!.Length).IsEqualTo(2048);
        await Assert.That(partial.Payload[..1024]).IsEquivalentTo(all[..1024]);
        await Assert.That(partial.Payload[1024..]).IsEquivalentTo(all[2048..]);
        await Assert.That(partial.Contributors).IsEquivalentTo(_Ints(100, 102));
        await Assert.That(partial.DeliversPayload).IsTrue();
        await Assert.That(d.PendingCount).IsEqualTo(1);

        // A further fragment that does not close the hole reports no second partial.
        FragmentOutcome another = _Add(d, 1, 1024, true, all[1024..1536], contributor: 101);
        await Assert.That(another.Kind).IsEqualTo(FragmentOutcomeKind.Pending);

        FragmentOutcome complete = _Add(d, 1, 1536, true, all[1536..2048], contributor: 103);
        await Assert.That(complete.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(complete.Payload).IsEquivalentTo(all);
        await Assert.That(complete.Contributors).IsEquivalentTo(_Ints(100, 101, 103, 102));
        await Assert.That(d.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task LateFragmentClosesTheGapWithThreeContributors()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);
        byte[] all = _Pattern(3072, 3);
        _ = _Add(d, 1, 0, true, all[..1024], contributor: 1);
        FragmentOutcome partial = _Add(d, 1, 2048, false, all[2048..], contributor: 3);
        FragmentOutcome complete = _Add(d, 1, 1024, true, all[1024..2048], contributor: 2);

        await Assert.That(partial.Kind).IsEqualTo(FragmentOutcomeKind.PartialPrefix);
        await Assert.That(complete.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(complete.Payload!.Length).IsEqualTo(3072);
        await Assert.That(complete.Contributors).IsEquivalentTo(_Ints(1, 2, 3));

        // A duplicate afterwards opens a new datagram (the old one is gone) and delivers nothing.
        FragmentOutcome duplicate = _Add(d, 1, 1024, true, all[1024..2048], contributor: 4);
        await Assert.That(duplicate.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
    }

    [Test]
    public async Task InOrderArrivalNeverReportsAPartial()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);
        FragmentOutcome first = _Add(d, 1, 0, true, new byte[1024]);
        FragmentOutcome second = _Add(d, 1, 1024, true, new byte[1024]);
        FragmentOutcome last = _Add(d, 1, 2048, false, new byte[1024]);

        await Assert.That(first.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(second.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(last.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
    }

    [Test]
    public async Task WaitForCompletionReportsPendingUntilComplete()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = FragmentGapPolicy.WaitForCompletion });
        _ = _Add(d, 1, 0, true, new byte[1024]);
        FragmentOutcome last = _Add(d, 1, 2048, false, new byte[1024]);
        FragmentOutcome complete = _Add(d, 1, 1024, true, new byte[1024]);

        await Assert.That(last.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(complete.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
    }

    [Test]
    public async Task ALastFragmentWithoutTheStartStaysPendingAndTheLateStartReportsThePrefix()
    {
        FragmentReassembler<IntKey> d = new(_KeepFirstOptions);

        // Nothing is decodable without the bytes from offset 0, so a lone last fragment is not reported as a partial datagram.
        FragmentOutcome last = _Add(d, 1, 1024, false, new byte[512]);
        FragmentOutcome start = _Add(d, 1, 0, true, new byte[256]);

        await Assert.That(last.Kind).IsEqualTo(FragmentOutcomeKind.Pending);
        await Assert.That(start.Kind).IsEqualTo(FragmentOutcomeKind.PartialPrefix);
        await Assert.That(start.PrefixLength).IsEqualTo(256);
        await Assert.That(start.Gaps).IsEquivalentTo(new FragmentRange[] { new(256, 768) });
        await Assert.That(start.CoveredAfterGap).IsEquivalentTo(new FragmentRange[] { new(1024, 512) });
        await Assert.That(start.DatagramLength).IsEqualTo(1536);
    }

    #endregion

    #region Randomized consistency

    /// <summary>
    /// Feeds random, overlapping, duplicated fragments of one known datagram in random order under the first-wins policy. The
    /// bytes of every fragment are consistent with the datagram, so whatever the arrival order the result must be the datagram,
    /// no conflict may be reported, every contributor appears once, and the byte counter returns to zero.
    /// </summary>
    [Test]
    [Arguments(1, FragmentGapPolicy.WaitForCompletion)]
    [Arguments(2, FragmentGapPolicy.WaitForCompletion)]
    [Arguments(3, FragmentGapPolicy.PrefixOnLastThenComplete)]
    [Arguments(4, FragmentGapPolicy.PrefixOnLastThenComplete)]
    public async Task RandomOverlappingFragmentsAlwaysRebuildTheDatagram(int seed, FragmentGapPolicy gapPolicy)
    {
        Random random = new(seed);
        for (int round = 0; round < 40; round++)
        {
            int total = random.Next(1, 600);
            byte[] original = new byte[total];
            random.NextBytes(original);

            // A cover without holes (random cut points) plus random extra ranges that overlap it.
            List<(int Start, int End)> ranges = [];
            int cursor = 0;
            while (cursor < total)
            {
                int end = Math.Min(total, cursor + random.Next(1, 90));
                ranges.Add((cursor, end));
                cursor = end;
            }

            int extra = random.Next(0, 12);
            for (int i = 0; i < extra; i++)
            {
                int start = random.Next(0, total);
                ranges.Add((start, Math.Min(total, start + random.Next(1, 120))));
            }

            // The range that ends at the total length is the last fragment (more = false); copies of it stay "last" too.
            List<int> order = [.. Enumerable.Range(0, ranges.Count).OrderBy(_ => random.Next())];
            FragmentReassembler<IntKey> d = new(_KeepFirstOptions with { GapPolicy = gapPolicy });
            FragmentOutcome result = default;
            foreach (int index in order)
            {
                (int start, int end) = ranges[index];
                FragmentOutcome outcome = d.Add(new IntKey(round), start, end != total, original.AsSpan(start, end - start), index);
                await Assert.That(outcome.Flags.HasFlag(FragmentOutcomeFlags.OverlapConflict)).IsFalse();
                if (outcome.Kind == FragmentOutcomeKind.PartialPrefix)
                {
                    await Assert.That(outcome.Payload![..outcome.PrefixLength]).IsEquivalentTo(original[..outcome.PrefixLength]);
                }

                if (outcome.Kind == FragmentOutcomeKind.Complete)
                {
                    result = outcome;
                    break; // later copies would open a new datagram under the same key
                }
            }

            await Assert.That(result.Kind).IsEqualTo(FragmentOutcomeKind.Complete).Because($"seed {seed} round {round}");
            await Assert.That(result.Payload).IsEquivalentTo(original);
            int[] contributors = result.Contributors!;
            await Assert.That(contributors.Distinct().Count()).IsEqualTo(contributors.Length);
            await Assert.That(d.PendingCount).IsEqualTo(0);
            await Assert.That(d.BufferedBytes).IsEqualTo(0);
        }
    }

    #endregion

    #region Allocation

    [Test]
    public async Task InOrderFragmentsAllocateNothingPerFragment()
    {
        // Injected pools that keep every array: the shared pools retain only a few arrays per size, so 7,500 simultaneous
        // rentals would miss them and the measurement would depend on the machine.
        FragmentPools pools = new(ArrayPool<byte>.Create(4096, 16_384), ArrayPool<FragmentSlot>.Create(1 << 16, 4));
        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4 with
        {
            MaxDatagramLength = 11_000_000,
            MaxFragments = 10_000,
            MaxPending = 4,
        };
        FragmentReassembler<IntKey> d = new(in options, pools);
        byte[] fragment = new byte[1392];
        const int Count = 7500;

        // Warm-up datagram: fills the pools, the dictionary and the JIT.
        for (int i = 0; i < Count; i++)
        {
            _ = d.Add(new IntKey(1), (long)i * fragment.Length, i < Count - 1, fragment, i);
        }

        FragmentOutcome last = default;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Count; i++)
        {
            last = d.Add(new IntKey(2), (long)i * fragment.Length, i < Count - 1, fragment, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The completing payload and the contributor array leave the type (array headers added); everything else must be small.
        long allowed = last.Payload!.Length + 24 + (last.Contributors!.Length * 4L) + 24;
        long beyond = allocated - allowed;
        await Assert.That(last.Kind).IsEqualTo(FragmentOutcomeKind.Complete);
        await Assert.That(last.Payload.Length).IsEqualTo(Count * fragment.Length);
        await Assert.That(beyond).IsLessThan(64 * 1024).Because($"allocated {allocated} bytes, {beyond} beyond the payload and the contributor array");
    }

    #endregion
}
