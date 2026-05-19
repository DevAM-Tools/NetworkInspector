// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Iterator over the frames produced by a single
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}.Build(System.ReadOnlySpan{byte})"/>
/// call.  Yields one frame per <see cref="MoveNext"/> for the non-fragmenting
/// path and one frame per fragment for stacks that contain an
/// <see cref="IFragmentable"/> layer with <see cref="IFragmentable.CanFragment"/>
/// set.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MoveNext"/> is guaranteed throw-free.  Erroneous runtime
/// situations (buffer too small, fragmentation required without an
/// <see cref="IFragmentable"/> layer in the stack) are surfaced via
/// <see cref="Status"/> and a <c>false</c> return.
/// </para>
/// <para>
/// Fragmentation strategy ("build-once, slice-many"): the unfragmented frame
/// is materialised exactly once into a per-thread scratch buffer so
/// inner-of-fragmentable checksums (UDP/TCP/SOME-IP) cover the complete
/// datagram and live only in fragment 0.  Each subsequent
/// <see cref="MoveNext"/> copies the cached header bytes (frame start through
/// the end of the fragmentable layer's header) into the caller's buffer,
/// appends the next slice of the inner-of-fragmentable payload, re-runs the
/// length / outer-checksum / trailer post-fix phases on the per-fragment
/// frame, and finally invokes <see cref="IFragmentable.PatchFragmentHeader"/>
/// to set the per-fragment fields (FragmentOffset / MoreFragments / DF=0).
/// </para>
/// <para>Thread safety: instance is single-use and not thread-safe.</para>
/// </remarks>
/// <typeparam name="TStack">Concrete cons-list type carried into the iterator.</typeparam>
/// <typeparam name="TTrailer">Trailer type (use <see cref="NoTrailer"/> for none).</typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public ref struct FrameSequence<TStack, TTrailer, TInterceptor>
    where TStack : struct, IStackNode, IStatelessStack
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
{
    /// <summary>
    /// Hard upper bound on cons-list depth.  Stacks deeper than this set
    /// <see cref="BuildStatus.StackTooDeep"/> instead of allocating.
    /// </summary>
    /// <remarks>Mirrors <see cref="FrameLimits.MaxSupportedDepth"/>.</remarks>
    internal const int MaxSupportedDepth = FrameLimits.MaxSupportedDepth;

    /// <summary>The fully-populated cons-list whose layers describe the frame.</summary>
    private readonly TStack _Values;

    /// <summary>Optional trailer; default <see cref="NoTrailer"/> writes nothing.</summary>
    private readonly TTrailer _Trailer;

    /// <summary>Optional interceptor; default <see cref="NoInterceptor"/> is a no-op.</summary>
    private TInterceptor _Interceptor;

    /// <summary>Frame payload (after every layer's header).</summary>
    private readonly ReadOnlySpan<byte> _Payload;

    /// <summary>State machine for <see cref="MoveNext"/>.</summary>
    private SequenceState _State;

    // --- Multi-fragment state (only populated when _State == SequenceState.Fragmenting) ---

    /// <summary>Thread-static scratch buffer holding the unfragmented frame.</summary>
    private byte[]? _Scratch;

    /// <summary>Thread-static offsets array reused across all fragments.</summary>
    private int[]? _ScratchOffsets;

    /// <summary>Bytes from offset 0 through the end of the fragmentable layer header (cached headers).</summary>
    private int _HeaderEndOffset;

    /// <summary>Length of the inner-of-fragmentable payload pool inside the scratch.</summary>
    private int _InnerPayloadLength;

    /// <summary>Maximum bytes of inner payload that fit into a single fragment (multiple of 8).</summary>
    private int _MaxFragmentInnerPayload;

    /// <summary>Bytes of inner payload already emitted into previous fragments.</summary>
    private int _FragmentCursor;

    /// <summary>Cons-list depth at build time.</summary>
    private int _Depth;

    /// <summary>Trailer size at build time (cached).</summary>
    private int _TrailerSize;

    /// <summary>
    /// <c>true</c> when <see cref="_Scratch"/>/<see cref="_ScratchOffsets"/> are
    /// the pooled thread-static arrays held by <see cref="FrameSequenceScratch"/>;
    /// when true the iterator must call <see cref="FrameSequenceScratch.Release"/>
    /// on its first transition to <see cref="SequenceState.Done"/>.
    /// </summary>
    private bool _OwnsScratch;

    /// <summary>Active fragmentation kind reported by the innermost fragmentable layer.</summary>
    private FragmentationKind _FragKind;

    /// <summary>Outcome of the build operation; set during <see cref="MoveNext"/>.</summary>
    public BuildStatus Status
    {
        get; private set;
    }

    /// <summary>Creates a new iterator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FrameSequence(TStack values, TTrailer trailer, TInterceptor interceptor, ReadOnlySpan<byte> payload)
    {
        _Values = values;
        _Trailer = trailer;
        _Interceptor = interceptor;
        _Payload = payload;
        _State = SequenceState.NotStarted;
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

    /// <summary>
    /// Writes the next frame into <paramref name="dst"/>.  Returns <c>true</c>
    /// if a frame was written and sets <paramref name="bytesWritten"/> to the
    /// frame length.  Returns <c>false</c> when the sequence is exhausted or
    /// <see cref="Status"/> indicates an error.
    /// </summary>
    /// <remarks>
    /// Guaranteed throw-free.  All expected runtime situations are surfaced via
    /// <see cref="Status"/>: <see cref="BuildStatus.BufferTooSmall"/>,
    /// <see cref="BuildStatus.FragmentationRequired"/>,
    /// <see cref="BuildStatus.StackTooDeep"/>,
    /// <see cref="BuildStatus.InvalidLayerState"/>.
    /// </remarks>
    /// <param name="dst">Destination buffer for the frame.</param>
    /// <param name="bytesWritten">Frame length in bytes when the call returns <c>true</c>.</param>
    public bool MoveNext(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        switch (_State)
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

    /// <summary>
    /// First-call dispatcher: validates depth, computes the unfragmented frame
    /// size, picks the single-frame or multi-fragment path, and emits the
    /// first (and possibly only) frame.
    /// </summary>
    private bool EmitFirst(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        int depth = _Values.Depth;
        if (depth > MaxSupportedDepth)
        {
            Status = BuildStatus.StackTooDeep;
            _State = SequenceState.Done;
            return false;
        }

        int totalHdr = _Values.TotalHeaderSize;
        int trailerSize = _Trailer.TrailerSize;
        int total = totalHdr + _Payload.Length + trailerSize;
        int maxFrameLen = _Values.MaxFrameLength;

        // Single-frame fast path: fits into one frame.
        if (total <= maxFrameLen)
        {
            return EmitSingleFrame(dst, depth, totalHdr, trailerSize, total, out bytesWritten);
        }

        // Multi-frame path: requires an IFragmentable layer along the stack.
        if (!_Values.HasFragmentable)
        {
            Status = BuildStatus.FragmentationRequired;
            _State = SequenceState.Done;
            return false;
        }

        return BeginFragmenting(dst, depth, totalHdr, trailerSize, total, maxFrameLen, out bytesWritten);
    }

    /// <summary>
    /// Builds the entire frame in <paramref name="dst"/> in the non-fragmenting
    /// case.  Identical to the previous (M3) behaviour.
    /// </summary>
    private bool EmitSingleFrame(Span<byte> dst, int depth, int totalHdr, int trailerSize, int total, out int bytesWritten)
    {
        bytesWritten = 0;

        if (dst.Length < total)
        {
            Status = BuildStatus.BufferTooSmall;
            _State = SequenceState.Done;
            return false;
        }

        Span<int> offsets = stackalloc int[MaxSupportedDepth];
        offsets = offsets[..depth];

        _Values.WriteHeaders(dst, 0, offsets, ref _Interceptor);
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
        _State = SequenceState.Done;
        return true;
    }

    /// <summary>
    /// Builds the unfragmented frame into the per-thread scratch buffer (so
    /// inner-of-fragmentable checksums cover the whole datagram), validates
    /// that the located <see cref="IFragmentable"/> layer permits splitting,
    /// computes the per-fragment slice geometry and emits fragment 0.
    /// </summary>
    private bool BeginFragmenting(Span<byte> dst, int depth, int totalHdr, int trailerSize, int total, int maxFrameLen, out int bytesWritten)
    {
        bytesWritten = 0;

        // Build the full unfragmented frame in a per-thread scratch buffer.
        // TryAcquire surfaces the in-use flag so reentrant interceptor calls
        // get a freshly-allocated, non-pooled buffer pair instead of
        // overwriting the outer iterator's cached headers.
        _OwnsScratch = FrameSequenceScratch.TryAcquire(total, out byte[] scratchArray, out int[] offsetsArray);
        Span<byte> scratch = scratchArray.AsSpan(0, total);

        Span<int> offsets = offsetsArray.AsSpan(0, depth);

        // Suppress per-header interceptor calls during the scratch build; the
        // public hook contract here is one OnFrameComplete per emitted fragment.
        NoInterceptor noInterceptor = default;
        _Values.WriteHeaders(scratch, 0, offsets, ref noInterceptor);
        _Payload.CopyTo(scratch.Slice(totalHdr, _Payload.Length));

        int dataLength = totalHdr + _Payload.Length;

        // Locate the innermost fragmentable layer up front to determine which
        // post-fix phases run on the scratch versus per emitted fragment.
        if (!_Values.TryGetFragmentableInfo(
            offsets, out int headerOffset, out int headerEndOffset,
            out bool canFragment, out FragmentationKind kind, out int alignment))
        {
            // HasFragmentable was true but no IFragmentable was found —
            // a structural inconsistency that should never occur in practice.
            Status = BuildStatus.InvalidLayerState;
            FinishFragmenting();
            return false;
        }
        _ = headerOffset;

        if (!canFragment)
        {
            Status = BuildStatus.FragmentationRequired;
            FinishFragmenting();
            return false;
        }

        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            Status = BuildStatus.InvalidLayerState;
            FinishFragmenting();
            return false;
        }

        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, depth, dataLength);

        // Network-layer fragmentation: pre-run pseudo-header + inner-checksum
        // ONCE on the unfragmented scratch so e.g. UDP/TCP checksums cover the
        // whole datagram (fragment 0 carries the transport header).
        // Application-layer segmentation: the per-segment payload differs, so
        // pseudo-header and inner-checksum walks are deferred to per emitted segment.
        if (kind == FragmentationKind.NetworkLayer)
        {
            _Values.ApplyPostFix(FixPhase.Length, scratch, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, scratch, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.InnerChecksum, scratch, offsets, dataLength, ref ctx);
        }
        // OuterChecksum / Trailer for the unfragmented build are intentionally
        // skipped — they are recomputed per fragment in EmitNextFragment.

        BuildStatus geoStatus = FragmentGeometryHelper.TryComputeFragmentGeometry(
            canFragment: true, alignment, headerEndOffset, dataLength, maxFrameLen, trailerSize,
            out int innerLen, out int maxFragInner);
        if (geoStatus != BuildStatus.Success)
        {
            Status = geoStatus;
            FinishFragmenting();
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

        _State = SequenceState.Fragmenting;
        return EmitNextFragment(dst, out bytesWritten);
    }

    /// <summary>
    /// Copies the cached headers and the next inner-payload slice into
    /// <paramref name="dst"/>, re-runs the per-fragment post-fix phases,
    /// patches the fragment header fields, writes the trailer and invokes
    /// <see cref="IFrameInterceptor.OnFrameComplete"/>.  Advances
    /// <see cref="_FragmentCursor"/>.
    /// </summary>
    private bool EmitNextFragment(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        if (_Scratch is null || _ScratchOffsets is null)
        {
            Status = BuildStatus.InvalidLayerState;
            FinishFragmenting();
            return false;
        }

        if (_FragmentCursor >= _InnerPayloadLength)
        {
            FinishFragmenting();
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
            FinishFragmenting();
            return false;
        }

        Span<byte> scratch = _Scratch.AsSpan();
        Span<int> offsets = _ScratchOffsets.AsSpan(0, _Depth);

        // Copy cached header bytes (frame start through end of fragmentable header).
        scratch.Slice(0, _HeaderEndOffset).CopyTo(dst);
        // Copy the per-fragment payload slice from the inner-of-fragmentable
        // payload pool inside the scratch.
        scratch.Slice(_HeaderEndOffset + _FragmentCursor, sliceLen).CopyTo(dst.Slice(_HeaderEndOffset));

        // Per-fragment post-fix passes: Length re-patches every layer's length
        // field on the smaller fragment frame; OuterChecksum recomputes
        // header-only checksums (e.g. IPv4 header checksum) over the new bytes;
        // Trailer prepares the trailer slot.  PublishPseudoHeader and
        // InnerChecksum are intentionally skipped — they belong to the
        // already-finalised unfragmented datagram.
        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, _Depth, dataLength);

        _Values.ApplyPostFixUpTo(FixPhase.Length, dst, offsets, dataLength, ref ctx, _HeaderEndOffset);

        // Patch fragment-specific fields on every IFragmentable layer that
        // matches the active fragmentation kind (this also clears the IPv4 DF
        // bit for NetworkLayer kind).  PatchFragmentable filters by kind so
        // application-segmentation layers do not patch outer IP layers.
        _Values.PatchFragmentable(dst, offsets, dataLength, _FragmentCursor, moreFragments, _FragKind);

        if (_FragKind == FragmentationKind.ApplicationSegmentation)
        {
            // Per-segment full post-fix walk: every segment is its own complete
            // network-layer datagram with a per-segment transport checksum.
            _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.InnerChecksum, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx);
        }
        else
        {
            // Outer checksum AFTER the fragment-field patch so checksums reflect
            // the final header bytes.
            _Values.ApplyPostFixUpTo(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx, _HeaderEndOffset);
        }
        _Values.ApplyPostFix(FixPhase.Trailer, dst, offsets, dataLength, ref ctx);

        if (_TrailerSize > 0)
        {
            _Trailer.WriteTrailer(dst[..total], dataLength);
        }

        _Interceptor.OnFrameComplete(dst[..total]);

        _FragmentCursor += sliceLen;
        bytesWritten = total;
        if (!moreFragments)
        {
            // Last fragment emitted — release the pooled scratch immediately
            // so the next top-level call on this thread reuses it.
            FinishFragmenting();
        }
        return true;
    }

    /// <summary>State machine for the iterator.</summary>
    private enum SequenceState : byte
    {
        /// <summary>No frame has been emitted yet.</summary>
        NotStarted = 0,
        /// <summary>The single-frame fast path or the fragment loop has finished.</summary>
        Done = 1,
        /// <summary>Multi-fragment mode; <see cref="_FragmentCursor"/> tracks progress.</summary>
        Fragmenting = 2,
    }

    /// <summary>
    /// Transitions the state machine to <see cref="SequenceState.Done"/> and
    /// releases the per-thread scratch reservation if this iterator was the
    /// pooled owner.  Idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FinishFragmenting()
    {
        if (_OwnsScratch)
        {
            FrameSequenceScratch.Release();
            _OwnsScratch = false;
        }
        _State = SequenceState.Done;
    }
}

