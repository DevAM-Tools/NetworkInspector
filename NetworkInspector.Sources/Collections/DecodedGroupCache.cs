// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Collections;

/// <summary>
/// Per-thread cache of fully decoded sealed groups of 64 values (<c>PackedLongSequence</c> in Sources, the session's
/// <c>PacketFrameMap</c>), so reading ids in order (forward or backward) decodes each group once instead of once per
/// value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per thread:</b> several consumers (session readers, UI, exporters) call <c>FrameById</c> on one source at the
/// same time, each walking its own region. A cache per thread needs no lock, no atomic operation, and no shared
/// mutable state, and one thread's walk never evicts another thread's group.
/// </para>
/// <para>
/// <b>Correctness:</b> only sealed groups are cached. A sealed group never changes after the sequence published it, so a
/// decoded copy stays exact for the lifetime of the sequence. Entries are keyed by a process-unique sequence id, never by
/// reference: the cache keeps no sequence alive and a new sequence can never match an old entry.
/// </para>
/// <para>
/// <b>Cost:</b> <see cref="Ways"/> entries of 64 values (2 KiB plus headers per thread that reads), allocated on first
/// use. A group is decoded in full only on its second consecutive touch, so random access pays one entry update per
/// call and never a full decode.
/// </para>
/// <para><b>Thread-safety:</b> an instance is only reachable from its own thread (<c>[ThreadStatic]</c>).</para>
/// </remarks>
internal sealed class DecodedGroupCache
{
    #region Constants

    /// <summary>
    /// Groups cached per thread: room for two sequences per source of two interleaved sources. Must be a power of two: the
    /// victim way wraps with <c>(way + 1) &amp; (Ways − 1)</c>. Constant (tuning value, not configurable).
    /// </summary>
    internal const int Ways = 4;

    /// <summary>Values per group (the group size of <c>PackedLongSequence</c> and the session's packet → frame map).</summary>
    internal const int GroupSize = 64;

    #endregion

    #region Fields

    [ThreadStatic]
    private static DecodedGroupCache? _Current;

    private readonly Entry[] _Entries = new Entry[Ways];
    private readonly long[] _Values = new long[Ways * GroupSize];
    private int _Victim;

    /// <summary>Way of the previous hit or touch: an in-order walk stays in one group for 64 reads.</summary>
    private int _LastWay;

    #endregion

    #region Properties

    /// <summary>The calling thread's cache, created on first use.</summary>
    internal static DecodedGroupCache Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            _Current ??= new();
            return _Current;
        }
    }

    #endregion

    #region Public API

    /// <summary>The way that holds <paramref name="group"/> of sequence <paramref name="owner"/>, or −1.</summary>
    /// <param name="owner">Process-unique sequence id (never 0).</param>
    /// <param name="group">Sealed group index.</param>
    /// <param name="decoded">True when the way holds the decoded values; false when the group was only touched once.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Find(long owner, int group, out bool decoded)
    {
        // The previous hit first (an in-order walk stays in one group for 64 reads); owner and group are both compared,
        // so another sequence never matches.
        Entry[] entries = _Entries;
        int last = _LastWay;
        if (entries[last].Owner == owner && entries[last].Group == group)
        {
            decoded = entries[last].Decoded;
            return last;
        }

        for (int way = 0; way < entries.Length; way++)
        {
            if (entries[way].Owner == owner && entries[way].Group == group)
            {
                _LastWay = way;
                decoded = entries[way].Decoded;
                return way;
            }
        }

        decoded = false;
        return -1;
    }

    /// <summary>
    /// Records a first touch of <paramref name="group"/>. A way of the same sequence is reused first, so random access on
    /// one sequence never evicts the groups of other sequences read on this thread.
    /// </summary>
    internal void Touch(long owner, int group)
    {
        Entry[] entries = _Entries;
        int way = -1;
        for (int candidate = 0; candidate < entries.Length; candidate++)
        {
            if (entries[candidate].Owner == owner)
            {
                way = candidate;
                break;
            }
        }

        if (way < 0)
        {
            way = _Victim;
            _Victim = (way + 1) & (Ways - 1);
        }

        entries[way] = new Entry(owner, group, decoded: false);
        _LastWay = way;
    }

    /// <summary>The 64 value slots of <paramref name="way"/>, to be filled by the sequence's decoder.</summary>
    internal Span<long> Slots(int way) => _Values.AsSpan(way * GroupSize, GroupSize);

    /// <summary>Marks <paramref name="way"/> as holding the decoded values of its group.</summary>
    internal void MarkDecoded(int way) => _Entries[way].Decoded = true;

    /// <summary>Decoded value of <paramref name="lane"/> in <paramref name="way"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long Value(int way, int lane) => _Values[(way * GroupSize) + lane];

    #endregion

    #region Nested types

    /// <summary>Key and state of one way. Owner 0 marks an empty way (sequence ids start at 1).</summary>
    private struct Entry(long owner, int group, bool decoded)
    {
        public long Owner = owner;
        public int Group = group;
        public bool Decoded = decoded;
    }

    #endregion
}
