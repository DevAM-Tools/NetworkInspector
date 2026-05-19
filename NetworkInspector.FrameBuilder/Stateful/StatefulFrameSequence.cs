// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Iterator over the frames produced by a single
/// <see cref="Session{TStack,TTrailer,TInterceptor}.NextPacket(System.ReadOnlySpan{byte})"/>
/// call when the cons-list contains at least one <see cref="IStatefulLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/> for the
/// stateful path: the only structural difference is that the write walk
/// receives a <c>ref <see cref="SessionState"/></c> so stateful layer heads
/// can read and update their slot.
/// </para>
/// <para>
/// Single-frame fast path: when the unfragmented frame fits the smallest MTU
/// asserted along the cons-list, the iterator emits exactly one frame —
/// <see cref="MoveNext"/> returns <c>true</c> once, then <c>false</c>.
/// </para>
/// <para>
/// Multi-fragment path: when the unfragmented frame does <em>not</em> fit
/// and an <see cref="IFragmentable"/> layer with
/// <see cref="IFragmentable.CanFragment"/> set is present, the iterator runs
/// the stateful write walk once into a per-thread scratch buffer (so per-flow
/// counters such as IPv4 Identification or TCP sequence advance exactly once
/// per logical packet, not once per fragment), pre-computes
/// inner-of-fragmentable checksums on the unfragmented datagram, then emits
/// one frame per <see cref="MoveNext"/> by copying the cached headers, the
/// next payload slice and re-running the per-fragment length / outer-checksum
/// / trailer post-fix phases.  The inner checksum (UDP/TCP/SOME-IP) lives
/// only in fragment 0.
/// </para>
/// <para>
/// <see cref="MoveNext"/> is guaranteed throw-free.  All expected runtime
/// situations are surfaced via <see cref="Status"/> and a <c>false</c> return:
/// <see cref="BuildStatus.BufferTooSmall"/>,
/// <see cref="BuildStatus.FragmentationRequired"/>,
/// <see cref="BuildStatus.StackTooDeep"/>,
/// <see cref="BuildStatus.InvalidLayerState"/>.  Callers detect normal
/// completion versus failure by reading <see cref="Status"/> after
/// <see cref="MoveNext"/> returns <c>false</c>: a value of
/// <see cref="BuildStatus.Success"/> means the sequence drained cleanly,
/// any other value identifies the failure.
/// </para>
/// <para>Thread safety: instance is single-use and not thread-safe.</para>
/// </remarks>
/// <typeparam name="TStack">Concrete cons-list type carried into the iterator.</typeparam>
/// <typeparam name="TTrailer">Trailer type (use <see cref="NoTrailer"/> for none).</typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public ref struct StatefulFrameSequence<TStack, TTrailer, TInterceptor>
    where TStack : struct, IStackNode
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
{
    /// <summary>Hard upper bound on cons-list depth (matches the stateless sequence).</summary>
    public const int MaxSupportedDepth = FrameLimits.MaxSupportedDepth;

    private readonly TStack _Values;
    private readonly TTrailer _Trailer;
    private TInterceptor _Interceptor;
    private readonly ReadOnlySpan<byte> _Payload;

    /// <summary>Pointer to the session-owned state; mutated during the write walk.</summary>
    private readonly ref SessionState _State;

    /// <summary>State machine.</summary>
    private SequenceState _Phase;

    // --- Multi-fragment state (only populated when _Phase == SequenceState.Fragmenting) ---
    private byte[]? _Scratch;
    private int[]? _ScratchOffsets;
    private int _HeaderEndOffset;
    private int _InnerPayloadLength;
    private int _MaxFragmentInnerPayload;
    private int _FragmentCursor;
    private int _Depth;
    private int _TrailerSize;
    private bool _OwnsScratch;
    private FragmentationKind _FragKind;

    /// <summary>
    /// Outcome of the build operation.  Read after <see cref="MoveNext"/>
    /// returns <c>false</c> to distinguish normal completion
    /// (<see cref="BuildStatus.Success"/>) from any of the surfaced error
    /// conditions.
    /// </summary>
    public BuildStatus Status
    {
        get; private set;
    }

    /// <summary>Creates a new iterator for the stateful path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StatefulFrameSequence(
        TStack values,
        TTrailer trailer,
        TInterceptor interceptor,
        ref SessionState state,
        ReadOnlySpan<byte> payload)
    {
        _Values = values;
        _Trailer = trailer;
        _Interceptor = interceptor;
        _State = ref state;
        _Payload = payload;
        _Phase = SequenceState.NotStarted;
        _Scratch = null;
        _ScratchOffsets = null;
        _HeaderEndOffset = 0;
        _InnerPayloadLength = 0;
        _MaxFragmentInnerPayload = 0;
        _FragmentCursor = 0;
        _Depth = 0;
        _TrailerSize = 0;
        _OwnsScratch = false;
        _FragKind = FragmentationKind.NetworkLayer;
        Status = BuildStatus.Success;
    }

    /// <inheritdoc cref="FrameSequence{TStack,TTrailer,TInterceptor}.MoveNext"/>
    public bool MoveNext(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;
        switch (_Phase)
        {
            case SequenceState.Done:
                return false;
            case SequenceState.Fragmenting:
                return EmitNextFragment(dst, out bytesWritten);
            case SequenceState.NotStarted:
            default:
                return EmitFirst(dst, out bytesWritten);
        }
    }

    /// <summary>First-call dispatcher: validates depth, picks single-frame or multi-fragment path.</summary>
    private bool EmitFirst(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        int depth = _Values.Depth;
        if (depth > MaxSupportedDepth)
        {
            Status = BuildStatus.StackTooDeep;
            Finish();
            return false;
        }

        int totalHdr = _Values.TotalHeaderSize;
        int trailerSize = _Trailer.TrailerSize;
        int total = totalHdr + _Payload.Length + trailerSize;
        int maxFrameLen = _Values.MaxFrameLength;

        if (total <= maxFrameLen)
        {
            return EmitSingleFrame(dst, depth, totalHdr, trailerSize, total, out bytesWritten);
        }

        if (!_Values.HasFragmentable)
        {
            Status = BuildStatus.FragmentationRequired;
            Finish();
            return false;
        }

        return BeginFragmenting(dst, depth, totalHdr, trailerSize, total, maxFrameLen, out bytesWritten);
    }

    /// <summary>Builds the entire frame in <paramref name="dst"/> in the non-fragmenting case.</summary>
    private bool EmitSingleFrame(Span<byte> dst, int depth, int totalHdr, int trailerSize, int total, out int bytesWritten)
    {
        bytesWritten = 0;

        if (dst.Length < total)
        {
            Status = BuildStatus.BufferTooSmall;
            Finish();
            return false;
        }

        Span<int> offsets = stackalloc int[MaxSupportedDepth];
        offsets = offsets[..depth];

        // Tell stateful layers (e.g. TcpLayerWithAutoSequence) how much payload
        // is in this frame so they can advance their counters by the right amount.
        _State.CurrentPayloadLength = _Payload.Length;

        _Values.WriteHeaders(dst, 0, offsets, ref _Interceptor, ref _State);
        _Payload.CopyTo(dst.Slice(totalHdr, _Payload.Length));

        int dataLength = totalHdr + _Payload.Length;
        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, depth, dataLength);

        _Values.ApplyPostFix(FixPhase.Length, dst, offsets, dataLength, ref ctx);
        _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, dst, offsets, dataLength, ref ctx);
        _Values.ApplyPostFix(FixPhase.InnerChecksum, dst, offsets, dataLength, ref ctx);
        _Values.ApplyPostFix(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx);
        _Values.ApplyPostFix(FixPhase.Trailer, dst, offsets, dataLength, ref ctx);

        if (trailerSize > 0)
        {
            _Trailer.WriteTrailer(dst[..total], dataLength);
        }

        _Interceptor.OnFrameComplete(dst[..total]);

        bytesWritten = total;
        Finish();
        return true;
    }

    /// <summary>
    /// Builds the unfragmented frame into per-thread scratch (advancing the
    /// session counters exactly once for the whole logical packet) and emits
    /// fragment 0.
    /// </summary>
    private bool BeginFragmenting(Span<byte> dst, int depth, int totalHdr, int trailerSize, int total, int maxFrameLen, out int bytesWritten)
    {
        bytesWritten = 0;

        _OwnsScratch = FrameSequenceScratch.TryAcquire(total, out byte[] scratchArray, out int[] offsetsArray);
        Span<byte> scratch = scratchArray.AsSpan(0, total);
        Span<int> offsets = offsetsArray.AsSpan(0, depth);

        // Suppress per-header interceptor calls during the scratch build; the
        // public hook contract is one OnFrameComplete per emitted fragment.
        NoInterceptor noInterceptor = default;

        // Tell stateful layers the FULL payload length (so e.g. TCP advances
        // its sequence number by the entire datagram payload, exactly once).
        _State.CurrentPayloadLength = _Payload.Length;
        // Snapshot session state so we can roll back the auto-counters in the
        // recoverable error paths below (FragmentationRequired / InvalidLayerState).
        // Without this, a failed BeginFragmenting would leave TcpNextSeq,
        // IPv4NextId, IPv6NextFragId and SomeIpNextSessionId advanced for a
        // packet that was never emitted, poisoning the session.
        SessionState stateSnapshot = _State;
        _Values.WriteHeaders(scratch, 0, offsets, ref noInterceptor, ref _State);
        _Payload.CopyTo(scratch.Slice(totalHdr, _Payload.Length));

        int dataLength = totalHdr + _Payload.Length;

        // Locate the innermost fragmentable layer to determine the active
        // fragmentation kind BEFORE running any post-fix on the scratch.
        if (!_Values.TryGetFragmentableInfo(
            offsets, out int headerOffset, out int headerEndOffset,
            out bool canFragment, out FragmentationKind kind, out int alignment))
        {
            _State = stateSnapshot;
            Status = BuildStatus.InvalidLayerState;
            Finish();
            return false;
        }
        _ = headerOffset;

        if (!canFragment)
        {
            _State = stateSnapshot;
            Status = BuildStatus.FragmentationRequired;
            Finish();
            return false;
        }

        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            // Power-of-two alignment is required so '& ~(alignment-1)' rounds correctly.
            _State = stateSnapshot;
            Status = BuildStatus.InvalidLayerState;
            Finish();
            return false;
        }

        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, depth, dataLength);

        // Network-layer fragmentation: pre-run pseudo-header + inner-checksum
        // ONCE on the unfragmented scratch so e.g. UDP/TCP checksums cover the
        // whole datagram (fragment 0 carries the transport header).
        // Application-layer segmentation: per-segment payload differs, so the
        // pseudo-header and inner-checksum walks are deferred to EmitNextFragment.
        if (kind == FragmentationKind.NetworkLayer)
        {
            _Values.ApplyPostFix(FixPhase.Length, scratch, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, scratch, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.InnerChecksum, scratch, offsets, dataLength, ref ctx);
        }
        // OuterChecksum / Trailer are recomputed per emitted frame regardless of kind.

        BuildStatus geoStatus = FragmentGeometryHelper.TryComputeFragmentGeometry(
            canFragment: true, alignment, headerEndOffset, dataLength, maxFrameLen, trailerSize,
            out int innerLen, out int maxFragInner);
        if (geoStatus != BuildStatus.Success)
        {
            _State = stateSnapshot;
            Status = geoStatus;
            Finish();
            return false;
        }

        _Scratch = scratchArray;
        _ScratchOffsets = offsetsArray;
        _HeaderEndOffset = headerEndOffset;
        _InnerPayloadLength = innerLen;
        _MaxFragmentInnerPayload = maxFragInner;
        _FragmentCursor = 0;
        _Depth = depth;
        _TrailerSize = trailerSize;
        _FragKind = kind;

        _Phase = SequenceState.Fragmenting;
        return EmitNextFragment(dst, out bytesWritten);
    }

    /// <summary>Copies cached headers + next slice into <paramref name="dst"/> and runs per-fragment post-fix.</summary>
    private bool EmitNextFragment(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        if (_Scratch is null || _ScratchOffsets is null)
        {
            Status = BuildStatus.InvalidLayerState;
            Finish();
            return false;
        }

        if (_FragmentCursor >= _InnerPayloadLength)
        {
            Finish();
            return false;
        }

        int remaining = _InnerPayloadLength - _FragmentCursor;
        int sliceLen = Math.Min(_MaxFragmentInnerPayload, remaining);
        bool moreFragments = sliceLen < remaining;

        int dataLength = _HeaderEndOffset + sliceLen;
        int total = dataLength + _TrailerSize;

        if (dst.Length < total)
        {
            Status = BuildStatus.BufferTooSmall;
            Finish();
            return false;
        }

        Span<byte> scratch = _Scratch.AsSpan();
        Span<int> offsets = _ScratchOffsets.AsSpan(0, _Depth);

        scratch.Slice(0, _HeaderEndOffset).CopyTo(dst);
        scratch.Slice(_HeaderEndOffset + _FragmentCursor, sliceLen).CopyTo(dst.Slice(_HeaderEndOffset));

        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, _Depth, dataLength);

        if (_FragKind == FragmentationKind.ApplicationSegmentation)
        {
            // Per-segment full post-fix walk: every segment is its own complete
            // network-layer datagram with a per-segment transport checksum.
            // Patch the segmenter's per-segment header BEFORE Length so any
            // length-derived fields see the segmented value.
            _Values.PatchFragmentable(dst, offsets, dataLength, _FragmentCursor, moreFragments, _FragKind);
            _Values.ApplyPostFix(FixPhase.Length, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.InnerChecksum, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.Trailer, dst, offsets, dataLength, ref ctx);
        }
        else
        {
            // IP-style: only Length + OuterChecksum + Trailer for the headers up
            // to and including the fragmentable layer; pseudo-header and inner
            // checksum belong to the already-finalised unfragmented datagram.
            _Values.ApplyPostFixUpTo(FixPhase.Length, dst, offsets, dataLength, ref ctx, _HeaderEndOffset);
            _Values.PatchFragmentable(dst, offsets, dataLength, _FragmentCursor, moreFragments, _FragKind);
            _Values.ApplyPostFixUpTo(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx, _HeaderEndOffset);
            _Values.ApplyPostFix(FixPhase.Trailer, dst, offsets, dataLength, ref ctx);
        }

        if (_TrailerSize > 0)
        {
            _Trailer.WriteTrailer(dst[..total], dataLength);
        }

        _Interceptor.OnFrameComplete(dst[..total]);

        _FragmentCursor += sliceLen;
        bytesWritten = total;
        if (!moreFragments)
        {
            Finish();
        }
        return true;
    }

    /// <summary>State machine for the iterator.</summary>
    private enum SequenceState : byte
    {
        NotStarted = 0,
        Done = 1,
        Fragmenting = 2,
    }

    /// <summary>
    /// Transitions to <see cref="SequenceState.Done"/> and releases the
    /// pooled per-thread scratch reservation if owned.  Idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Finish()
    {
        if (_OwnsScratch)
        {
            FrameSequenceScratch.Release();
            _OwnsScratch = false;
        }
        _Phase = SequenceState.Done;
    }
}
