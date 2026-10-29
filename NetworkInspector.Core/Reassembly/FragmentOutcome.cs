// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>What one <see cref="FragmentReassembler{TKey}.Add"/> call did.</summary>
public enum FragmentOutcomeKind : byte
{
    /// <summary>
    /// No reassembler ran for this fragment: the value a replay without a recorded outcome uses (the stateless path).
    /// This is the default value of the type.
    /// </summary>
    NotRecorded,

    /// <summary>The fragment was stored (or changed nothing); the datagram is not complete. Carries no arrays.</summary>
    Pending,

    /// <summary>The datagram is complete; payload, contributors and fragment count are set.</summary>
    Complete,

    /// <summary>
    /// The last fragment arrived but a hole remains and the gap policy asks for the decodable part; the datagram stays open
    /// and a later fragment that closes the last hole yields <see cref="Complete"/>.
    /// </summary>
    PartialPrefix,

    /// <summary>The fragment was refused; <see cref="FragmentOutcome.DiscardReason"/> says why.</summary>
    Discarded,
}

/// <summary>Why a fragment was refused.</summary>
public enum FragmentDiscardReason : byte
{
    /// <summary>The outcome is not a discard.</summary>
    None,

    /// <summary>The fragment's extent (offset plus length) exceeds the maximum datagram length, or its offset is negative. Any open datagram of the key is released.</summary>
    ExtentLimit,

    /// <summary>A non-last fragment is empty or not a multiple of the required alignment. Open datagrams stay untouched.</summary>
    Alignment,

    /// <summary>The stored bytes of this one datagram alone would exceed the byte budget; the datagram is released.</summary>
    BufferLimit,

    /// <summary>The datagram already holds the maximum number of fragments; the datagram is released.</summary>
    FragmentLimit,

    /// <summary>The fragment overlaps a stored fragment under the discard-datagram policy; the datagram is released.</summary>
    Overlap,

    /// <summary>A last fragment contradicts the total length that an earlier last fragment fixed; the datagram is released.</summary>
    ConflictingEnd,

    /// <summary>
    /// The fragment starts at or beyond the known end of the datagram (the fragment is refused, an open datagram stays), or
    /// the stored fragments cannot form a datagram (one of them extends past the end, or the datagram is empty; released).
    /// </summary>
    BeyondEnd,
}

/// <summary>Additional facts about a call, independent of its <see cref="FragmentOutcomeKind"/>.</summary>
[Flags]
public enum FragmentOutcomeFlags : byte
{
    /// <summary>No additional fact.</summary>
    None = 0,

    /// <summary>The fragment started the datagram over (restart on offset 0); the earlier fragments were released.</summary>
    Restarted = 1,

    /// <summary>Overlapping bytes differ from the bytes that were kept (first-wins policy).</summary>
    OverlapConflict = 2,

    /// <summary>The call evicted at least one other datagram because the datagram count limit was reached.</summary>
    EvictedForCount = 4,

    /// <summary>The call evicted at least one other datagram to make room under the byte budget.</summary>
    EvictedForBudget = 8,
}

/// <summary>A byte range of a datagram: the first byte and the number of bytes.</summary>
/// <param name="Start">Offset of the first byte in the datagram.</param>
/// <param name="Length">Number of bytes.</param>
public readonly record struct FragmentRange(int Start, int Length);

/// <summary>
/// The result of one <see cref="FragmentReassembler{TKey}.Add"/> call. An outcome is a value and not an exception, because
/// a hostile fragment stream must not cost an exception per fragment.
/// <para>
/// A pending outcome carries no arrays (no allocation per fragment). The arrays of a delivering outcome are never written
/// after the outcome is returned, so an outcome can be stored in a published effect and read from any thread.
/// </para>
/// <para><b>Thread-safety:</b> an immutable value; safe to share.</para>
/// </summary>
public readonly struct FragmentOutcome
{
    #region Constructors

    private FragmentOutcome(
        FragmentOutcomeKind kind,
        FragmentDiscardReason discardReason,
        FragmentOutcomeFlags flags,
        int evictedCount)
    {
        Kind = kind;
        DiscardReason = discardReason;
        Flags = flags;
        EvictedCount = evictedCount;
        Payload = null;
        PrefixLength = 0;
        DatagramLength = 0;
        FragmentCount = 0;
        Contributors = null;
        Gaps = null;
        CoveredAfterGap = null;
    }

    private FragmentOutcome(
        FragmentOutcomeKind kind,
        FragmentOutcomeFlags flags,
        int evictedCount,
        byte[] payload,
        int prefixLength,
        int datagramLength,
        int[] contributors,
        FragmentRange[]? gaps,
        FragmentRange[]? coveredAfterGap)
    {
        Kind = kind;
        DiscardReason = FragmentDiscardReason.None;
        Flags = flags;
        EvictedCount = evictedCount;
        Payload = payload;
        PrefixLength = prefixLength;
        DatagramLength = datagramLength;
        FragmentCount = contributors.Length;
        Contributors = contributors;
        Gaps = gaps;
        CoveredAfterGap = coveredAfterGap;
    }

    #endregion

    #region Properties

    /// <summary>What the call did.</summary>
    public FragmentOutcomeKind Kind { get; }

    /// <summary>Why the fragment was refused; <see cref="FragmentDiscardReason.None"/> unless <see cref="Kind"/> is <see cref="FragmentOutcomeKind.Discarded"/>.</summary>
    public FragmentDiscardReason DiscardReason { get; }

    /// <summary>Additional facts about the call.</summary>
    public FragmentOutcomeFlags Flags { get; }

    /// <summary>Number of other datagrams this call evicted (count limit and byte budget together).</summary>
    public int EvictedCount { get; }

    /// <summary>
    /// <see cref="FragmentOutcomeKind.Complete"/>: the datagram. <see cref="FragmentOutcomeKind.PartialPrefix"/>: the
    /// decodable prefix followed by the covered ranges behind the first hole (<see cref="CoveredAfterGap"/>). Otherwise null.
    /// A plain array and not a pooled one: it is bound to a packet and lives as long as that packet does.
    /// </summary>
    public byte[]? Payload { get; }

    /// <summary>Number of bytes at the start of <see cref="Payload"/> that form the contiguous prefix of the datagram.</summary>
    public int PrefixLength { get; }

    /// <summary>The total length of the datagram once a last fragment fixed it; 0 while unknown or for outcomes without a payload.</summary>
    public int DatagramLength { get; }

    /// <summary>
    /// For delivering outcomes the number of distinct contributors (the length of <see cref="Contributors"/>); 0 otherwise.
    /// </summary>
    public int FragmentCount { get; }

    /// <summary>
    /// <see cref="FragmentOutcomeKind.Complete"/> and <see cref="FragmentOutcomeKind.PartialPrefix"/>: the contributor tags
    /// of the stored fragments in offset order, each tag once. Otherwise null.
    /// </summary>
    public int[]? Contributors { get; }

    /// <summary><see cref="FragmentOutcomeKind.PartialPrefix"/>: the holes inside the datagram, in offset order. Otherwise null.</summary>
    public FragmentRange[]? Gaps { get; }

    /// <summary>
    /// <see cref="FragmentOutcomeKind.PartialPrefix"/>: the covered ranges behind the first hole (start in the datagram, length);
    /// their bytes follow the prefix in <see cref="Payload"/>. Otherwise null.
    /// </summary>
    public FragmentRange[]? CoveredAfterGap { get; }

    /// <summary>True when the outcome carries a payload (<see cref="FragmentOutcomeKind.Complete"/> or <see cref="FragmentOutcomeKind.PartialPrefix"/>).</summary>
    public bool DeliversPayload => Kind is FragmentOutcomeKind.Complete or FragmentOutcomeKind.PartialPrefix;

    /// <summary>The outcome of a replay that has no recorded result (the stateless path); the default value.</summary>
    public static FragmentOutcome NotRecorded => default;

    #endregion

    #region Factories

    internal static FragmentOutcome Pending(FragmentOutcomeFlags flags, int evictedCount) =>
        new(FragmentOutcomeKind.Pending, FragmentDiscardReason.None, flags, evictedCount);

    internal static FragmentOutcome Discarded(FragmentDiscardReason reason, FragmentOutcomeFlags flags, int evictedCount) =>
        new(FragmentOutcomeKind.Discarded, reason, flags, evictedCount);

    internal static FragmentOutcome Complete(FragmentOutcomeFlags flags, int evictedCount, byte[] payload, int[] contributors) =>
        new(FragmentOutcomeKind.Complete, flags, evictedCount, payload, payload.Length, payload.Length, contributors, null, null);

    internal static FragmentOutcome PartialPrefix(
        FragmentOutcomeFlags flags,
        int evictedCount,
        byte[] payload,
        int prefixLength,
        int datagramLength,
        int[] contributors,
        FragmentRange[] gaps,
        FragmentRange[] coveredAfterGap) =>
        new(FragmentOutcomeKind.PartialPrefix, flags, evictedCount, payload, prefixLength, datagramLength, contributors, gaps, coveredAfterGap);

    #endregion
}
