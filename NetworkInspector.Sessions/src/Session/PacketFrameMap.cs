// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Dense packet index → (frame id, source id) in groups of 64. One source with consecutive frames costs a single data
/// word plus one info word per group (16 B per 64 packets = 0.25 B per packet); alternating sources add one slot plane
/// per doubling; skipped frames add residual planes. A group whose frame ids do not strictly ascend per source is
/// stored raw (8 B per packet), so any input reads back exactly.
/// <para>
/// Why it is compressed: the session keeps no parsed packets, so it must remember for every packet id which captured
/// frame (and source) it came from, for captures of very many packets. A plain <c>long</c> per packet would cost 8 B;
/// the common case is consecutive frames of one source, which this encoding reduces to a base value and no further
/// planes. The raw fallback guarantees correctness for input that does not fit the encoding.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Group layout:</b> <c>info = (wordOffset &lt;&lt; 17) | (raw &lt;&lt; 16) | ((sourceCount − 1) &lt;&lt; 10) |
/// (slotWidth &lt;&lt; 7) | residualWidth</c>. Words: one base per source in first-appearance order
/// (<see cref="Session.PackFrameMapping"/> of the source's first frame in the group), then the slot planes (per
/// packet: index into that list), then the residual planes (per packet: frame id − previous frame id of the same
/// source − 1; 0 for the first packet of a source). Lookup of lane <c>j</c>: <c>slot = Extract(slots, j)</c>,
/// <c>same = EqualMask(slots, slot) &amp; mask(j)</c>, <c>frame = base[slot] + PopCount(same) − 1 +
/// PrefixSum(residuals, same)</c>. A raw group stores the 64 packed values.
/// </para>
/// <para>
/// <b>Thread-safety:</b> single writer (<see cref="Record"/>, called under the session's parse mutex); any number of
/// readers. Same publish protocol as <c>PackedLongSequence</c> in Sources: staging slot (volatile) → count (volatile);
/// group words → group info (store publish) → sealed count (volatile); a reader re-checks the sealed count after a
/// staging read. The session never completes the map, so the last partial group always lives in the staging buffer.
/// Readers decode sealed groups through a per-thread <see cref="DecodedGroupCache"/> (no shared state, no lock): a
/// session consumer that walks packets in order decodes each group of 64 once.
/// </para>
/// </remarks>
internal sealed class PacketFrameMap
{
    #region Constants

    // Group of 64 packets: one 64-bit word per bit plane holds one bit of every lane, which is what lets the plane
    // helpers (BitPlanes) work on whole words. The size is a format fact of the plane layout, not a tuning value.
    private const int _GroupShift = 6;
    private const int _GroupSize = 1 << _GroupShift;
    private const int _GroupMask = _GroupSize - 1;

    // Bit layout of a group's info word (see the class remarks): bits 0..6 residual width, 7..9 slot width,
    // 10..15 source count − 1 (up to 64 sources), bit 16 raw flag, bits 17.. word offset in the word store.
    // Widths: slot width ≤ 6 (64 sources) and residual width ≤ 31 (frame ids are non-negative ints), so they fit.
    private const int _InfoOffsetShift = 17;
    private const long _InfoRawBit = 1L << 16;
    private const int _InfoSourcesShift = 10;
    private const int _InfoSlotWidthShift = 7;
    private const long _InfoResidualWidthMask = 0x7F;

    #endregion

    #region Fields

    // Variable-length encoded groups (bases, slot planes, residual planes), addressed by the offset in the info word.
    private readonly PackedWordStore _Words = new();

    // One info word per sealed group, indexed by group number. Chunked so it grows without copying already published
    // entries (readers hold no lock); 1 << 13 slots = 8192 longs = 64 KiB per chunk.
    private readonly ChunkedGrowOnlyStore<long> _GroupInfo = new(chunkShift: 13);

    // The open (not yet full) group: packed (frame, source) values by lane. Written by the single writer, read by any
    // thread through Volatile; a lane is reused by the next group only after the full group was sealed and published.
    private readonly long[] _Staging = new long[_GroupSize];

    // Writer-only scratch of _SealGroup: no allocation per group.
    private readonly ulong[] _Slots = new ulong[_GroupSize];
    private readonly ulong[] _Residuals = new ulong[_GroupSize];
    private readonly long[] _SourceBases = new long[_GroupSize];
    private readonly int[] _SourceIds = new int[_GroupSize];
    private readonly int[] _LastFrames = new int[_GroupSize];

    /// <summary>Last issued map id; ids start at 1, so 0 marks an empty <see cref="DecodedGroupCache"/> way.</summary>
    private static long _LastId;

    /// <summary>Process-unique key of this map in the per-thread <see cref="DecodedGroupCache"/>.</summary>
    private readonly long _Id = Interlocked.Increment(ref _LastId);

    // Recorded packets. Published (volatile write) after the packet's staging slot, so a reader that sees the count
    // sees the slot.
    private volatile int _Count;

    // Number of groups whose info word is published and readable; the groups at and above it live in _Staging.
    // Published after the group's words and info word.
    private volatile int _SealedGroups;

    #endregion

    #region Properties

    /// <summary>Number of recorded packets. Any thread.</summary>
    internal int Count => _Count;

    /// <summary>Bytes used by words, group infos, and the staging buffer. Writer thread (tests, diagnostics).</summary>
    internal long UsedBytes =>
        (_Words.UsedWords * sizeof(ulong)) + ((long)_GroupInfo.Count * sizeof(long)) + (_GroupSize * sizeof(long));

    #endregion

    #region Public API

    /// <summary>Appends the mapping of packet <paramref name="packetIndex"/>, which must equal <see cref="Count"/>. Single writer.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="packetIndex"/> is not the next index.</exception>
    internal void Record(int packetIndex, FrameId frameId, FrameSourceId sourceId)
    {
        // Dense sequential appends are an invariant of the encoding (group = index / 64, lane = index % 64): a gap or
        // repeat would corrupt every later lookup, so it is a bug, not an expected failure.
        if (packetIndex != _Count)
        {
            throw new InvalidOperationException("Packet-to-frame mapping requires dense sequential PacketId appends.");
        }

        // Slot first, then the count: a reader that sees the count sees the slot. The 64th lane seals the group.
        int lane = packetIndex & _GroupMask;
        Volatile.Write(ref _Staging[lane], Session.PackFrameMapping(frameId, sourceId));
        _Count = packetIndex + 1;
        if (lane == _GroupMask)
        {
            _SealGroup();
        }
    }

    /// <summary>
    /// Reads the mapping of packet <paramref name="packetIndex"/>. Any thread; false for an index not recorded. Sealed
    /// groups are read through the calling thread's <see cref="DecodedGroupCache"/>, so a reader that walks packets in
    /// order (forward or backward) decodes each group of 64 once.
    /// </summary>
    internal bool TryGet(int packetIndex, out FrameId frameId, out FrameSourceId sourceId)
    {
        // One unsigned compare rejects negative and not-yet-recorded indexes. The count is read once; the writer
        // only increases it, so an index below it stays readable.
        if ((uint)packetIndex >= (uint)_Count)
        {
            frameId = FrameId.Invalid;
            sourceId = FrameSourceId.Invalid;
            return false;
        }

        int group = packetIndex >> _GroupShift;
        int lane = packetIndex & _GroupMask;
        long packed;
        if (group < _SealedGroups)
        {
            packed = _ReadSealedCached(group, lane);
        }
        else
        {
            // Open group: read the slot, then re-check that the group was not sealed (and the slot reused) meanwhile.
            // Without the re-check a reader could read a lane that the writer already overwrote with a packet of the
            // next group; once the group is sealed, the sealed copy is the authoritative value.
            packed = Volatile.Read(ref _Staging[lane]);
            if (group < _SealedGroups)
            {
                packed = _ReadSealedCached(group, lane);
            }
        }

        Session.UnpackFrameMapping(packed, out frameId, out sourceId);
        return true;
    }

    #endregion

    #region Private helpers

    /// <summary>Encodes the 64 staged packets as the next sealed group and publishes it. Writer thread.</summary>
    private void _SealGroup()
    {
        // Pass 1: build the source list in first-appearance order plus the slot and residual of every packet, using the
        // writer-only scratch arrays (no allocation per group). Stop at the first packet that cannot be encoded and
        // fall back to the raw form for the whole group.
        int sources = 0;
        ulong maxResidual = 0;
        bool raw = false;
        for (int lane = 0; lane < _GroupSize && !raw; lane++)
        {
            Session.UnpackFrameMapping(_Staging[lane], out FrameId frameId, out FrameSourceId sourceId);
            int frame = frameId.Value;
            int slot = Array.IndexOf(_SourceIds, sourceId.Value, 0, sources);
            if (frame < 0)
            {
                // An invalid frame id has no residual: store the whole group raw (exact, 8 B per packet).
                raw = true;
            }
            else if (slot < 0)
            {
                // First packet of a source in this group: remember its packed value as the base; its residual is 0.
                slot = sources++;
                _SourceIds[slot] = sourceId.Value;
                _SourceBases[slot] = _Staging[lane];
                _LastFrames[slot] = frame;
                _Slots[lane] = (ulong)slot;
                _Residuals[lane] = 0;
            }
            else if (frame <= _LastFrames[slot])
            {
                // The source stepped back or repeated an id: a residual would be negative, so the group is raw. This keeps
                // every input exact; real captures never take this branch.
                raw = true;
            }
            else
            {
                // Regular step: store frame − previous frame of the same source − 1, which is 0 for consecutive frames.
                // frame > last ≥ 0, so the residual is in 0 … int.MaxValue − 1.
                ulong residual = (ulong)(frame - _LastFrames[slot] - 1);
                _LastFrames[slot] = frame;
                _Slots[lane] = (ulong)slot;
                _Residuals[lane] = residual;
                maxResidual |= residual;
            }
        }

        // Pass 2: write the words of the chosen form into the word store, then compose the info word.
        long offset;
        long info;
        if (raw)
        {
            // Exact fallback: the packed values themselves.
            offset = _Words.Reserve(_GroupSize);
            Span<ulong> rawWords = _Words.GetWritable(offset, _GroupSize);
            for (int lane = 0; lane < _GroupSize; lane++)
            {
                rawWords[lane] = unchecked((ulong)_Staging[lane]);
            }

            info = (offset << _InfoOffsetShift) | _InfoRawBit;
        }
        else
        {
            // Plane counts: slotWidth = bits needed for the largest slot index (sources − 1), 0 for a single source.
            // residualWidth = bit length of the largest residual; maxResidual is the OR of all residuals, which has
            // the same highest set bit as the maximum, so no per-lane max is needed. A group of consecutive frames
            // has residual width 0 and stores no residual plane at all.
            int slotWidth = sources == 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(sources - 1));
            int residualWidth = maxResidual == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(maxResidual);
            int wordCount = sources + slotWidth + residualWidth;
            offset = _Words.Reserve(wordCount);
            Span<ulong> words = _Words.GetWritable(offset, wordCount);
            for (int slot = 0; slot < sources; slot++)
            {
                words[slot] = unchecked((ulong)_SourceBases[slot]);
            }

            BitPlanes.Scatter(_Slots, slotWidth, words.Slice(sources, slotWidth));
            BitPlanes.Scatter(_Residuals, residualWidth, words.Slice(sources + slotWidth, residualWidth));

            // offset < 2^44 words, so offset << 17 stays below 2^61; the other fields fit their bit ranges.
            info = (offset << _InfoOffsetShift)
                | ((long)(sources - 1) << _InfoSourcesShift)
                | ((long)slotWidth << _InfoSlotWidthShift)
                | (long)residualWidth;
        }

        // Words first; the info publish and the sealed count make them visible.
        _GroupInfo.Append(info);
        _SealedGroups = ((_Count - 1) >> _GroupShift) + 1;
    }

    /// <summary>
    /// Lane <paramref name="lane"/> of the sealed group <paramref name="group"/> through the per-thread cache: the first
    /// touch decodes one lane, the second consecutive touch decodes the whole group, later touches are an array load.
    /// Sealed groups never change, so a cached copy stays exact. The two-touch rule keeps a random single lookup cheap
    /// (one lane decoded, no 64-lane decode) while a reader that walks packets in order decodes each group once; the
    /// cache is thread-local, so it needs no lock (see <see cref="DecodedGroupCache"/>).
    /// </summary>
    private long _ReadSealedCached(int group, int lane)
    {
        DecodedGroupCache cache = DecodedGroupCache.Current;
        int way = cache.Find(_Id, group, out bool decoded);
        if (decoded)
        {
            return cache.Value(way, lane);
        }

        if (way < 0)
        {
            cache.Touch(_Id, group);
            return _ReadSealed(group, lane);
        }

        _DecodeGroup(group, cache.Slots(way));
        cache.MarkDecoded(way);
        return cache.Value(way, lane);
    }

    /// <summary>Decodes all 64 packed mappings of the sealed (always full) group <paramref name="group"/>.</summary>
    private void _DecodeGroup(int group, Span<long> target)
    {
        long info = _GroupInfo.Get(group);
        long offset = info >>> _InfoOffsetShift;
        if ((info & _InfoRawBit) != 0)
        {
            MemoryMarshal.Cast<ulong, long>(_Words.Get(offset, _GroupSize)).CopyTo(target);
            return;
        }

        int sources = (int)((info >> _InfoSourcesShift) & 0x3F) + 1;
        int slotWidth = (int)((info >> _InfoSlotWidthShift) & 0x7);
        int residualWidth = (int)(info & _InfoResidualWidthMask);
        ReadOnlySpan<ulong> words = _Words.Get(offset, sources + slotWidth + residualWidth);

        // Slots on the stack, residuals in the target (each lane is read before it is overwritten).
        Span<ulong> slots = stackalloc ulong[_GroupSize];
        Span<ulong> residuals = MemoryMarshal.Cast<long, ulong>(target);
        BitPlanes.ExtractAll(words.Slice(sources, slotWidth), slots);
        BitPlanes.ExtractAll(words.Slice(sources + slotWidth, residualWidth), residuals);

        // Per source: the first packet takes the base frame, every later one the previous frame + 1 + its residual.
        Span<long> lastFrames = stackalloc long[_GroupSize];
        ulong seen = 0;
        for (int lane = 0; lane < _GroupSize; lane++)
        {
            int slot = (int)slots[lane];
            Session.UnpackFrameMapping(unchecked((long)words[slot]), out FrameId baseFrame, out FrameSourceId sourceId);
            long frame = (seen & (1UL << slot)) == 0
                ? baseFrame.Value
                : lastFrames[slot] + 1 + (long)residuals[lane];
            seen |= 1UL << slot;
            lastFrames[slot] = frame;
            target[lane] = Session.PackFrameMapping((int)frame, sourceId);
        }
    }

    /// <summary>Decodes lane <paramref name="lane"/> of the sealed group <paramref name="group"/> as a packed mapping.</summary>
    private long _ReadSealed(int group, int lane)
    {
        long info = _GroupInfo.Get(group);
        long offset = info >>> _InfoOffsetShift;
        if ((info & _InfoRawBit) != 0)
        {
            return unchecked((long)_Words.Get(offset, _GroupSize)[lane]);
        }

        int sources = (int)((info >> _InfoSourcesShift) & 0x3F) + 1;
        int slotWidth = (int)((info >> _InfoSlotWidthShift) & 0x7);
        int residualWidth = (int)(info & _InfoResidualWidthMask);
        ReadOnlySpan<ulong> words = _Words.Get(offset, sources + slotWidth + residualWidth);
        ReadOnlySpan<ulong> slotPlanes = words.Slice(sources, slotWidth);
        ReadOnlySpan<ulong> residualPlanes = words.Slice(sources + slotWidth, residualWidth);

        // Packets of the same source up to this lane: their count and residuals rebuild the frame id from the base.
        int slot = (int)BitPlanes.Extract(slotPlanes, lane);
        ulong same = BitPlanes.EqualMask(slotPlanes, (ulong)slot) & BitPlanes.InclusivePrefixMask(lane);
        Session.UnpackFrameMapping(unchecked((long)words[slot]), out FrameId baseFrame, out FrameSourceId sourceId);
        long frame = baseFrame.Value + BitOperations.PopCount(same) - 1 + (long)BitPlanes.PrefixSum(residualPlanes, same);
        return Session.PackFrameMapping((int)frame, sourceId);
    }

    #endregion
}
