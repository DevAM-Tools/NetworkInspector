// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// The one build state machine behind <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>
/// and <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/>.  The only
/// difference between the two is <typeparamref name="TState"/>: how the write walk
/// reaches per-flow state.
/// </summary>
/// <remarks>
/// <para>
/// Single frame: when the unfragmented frame fits the smallest MTU along the
/// cons-list, the whole frame is written into the caller's buffer and five post-fix
/// phases run on it.
/// </para>
/// <para>
/// Fragmentation ("build once, slice many"): the unfragmented frame is written once
/// into a per-thread scratch buffer, so stateful counters advance once per logical
/// packet.  For <see cref="FragmentationKind.NetworkLayer"/> the inner checksum is
/// computed once over the whole datagram and travels in fragment 0.  For
/// <see cref="FragmentationKind.ApplicationSegmentation"/> every segment is a complete
/// datagram with its own length, pseudo-header and inner checksum.
/// </para>
/// <para>
/// Failure contract: <see cref="MoveNext"/> never throws.  A failure sets
/// <see cref="Status"/>, ends the sequence and reports zero bytes.  When it happens
/// before the first frame of the packet reached the caller, the session counters are
/// restored, so a failed packet does not consume an IP Identification, a TCP
/// sequence range or a SOME/IP session id.
/// </para>
/// <para>Thread safety: single-use and not thread-safe.</para>
/// </remarks>
/// <typeparam name="TStack">Concrete cons-list type.</typeparam>
/// <typeparam name="TTrailer">Trailer type.</typeparam>
/// <typeparam name="TInterceptor">Interceptor type.</typeparam>
/// <typeparam name="TState">Per-flow state access (<see cref="NoSessionState"/> or <see cref="SessionSequenceState"/>).</typeparam>
internal ref struct FrameEngine<TStack, TTrailer, TInterceptor, TState>
    where TStack : struct, IStackNode
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
    where TState : struct, ISequenceState, allows ref struct
{
    #region Fields

    /// <summary>The fully-populated cons-list whose layers describe the frame.</summary>
    private readonly TStack _Values;

    /// <summary>Optional trailer; <see cref="NoTrailer"/> writes nothing.</summary>
    private readonly TTrailer _Trailer;

    /// <summary>Optional interceptor; <see cref="NoInterceptor"/> is a no-op.</summary>
    private TInterceptor _Interceptor;

    /// <summary>Per-flow state access.</summary>
    private TState _SessionState;

    /// <summary>Frame payload (after every layer's header).</summary>
    private readonly ReadOnlySpan<byte> _Payload;

    /// <summary>State machine for <see cref="MoveNext"/>.</summary>
    private EnginePhase _Phase;

    // --- Multi-fragment state (only meaningful while _Phase == EnginePhase.Fragmenting) ---

    /// <summary>Scratch buffer holding the unfragmented frame.</summary>
    private byte[]? _Scratch;

    /// <summary>Offsets array reused across all fragments.</summary>
    private int[]? _ScratchOffsets;

    /// <summary>Bytes from offset 0 through the end of the fragmentable layer header (cached headers).</summary>
    private int _HeaderEndOffset;

    /// <summary>Length of the inner-of-fragmentable payload pool inside the scratch.</summary>
    private int _InnerPayloadLength;

    /// <summary>Maximum bytes of inner payload per fragment (multiple of the layer alignment).</summary>
    private int _MaxFragmentInnerPayload;

    /// <summary>Bytes of inner payload already emitted into previous fragments.</summary>
    private int _FragmentCursor;

    /// <summary>Cons-list depth at build time.</summary>
    private int _Depth;

    /// <summary>Trailer size at build time.</summary>
    private int _TrailerSize;

    /// <summary>
    /// <c>true</c> while this engine holds the pooled per-thread scratch reservation;
    /// <see cref="_Finish"/> releases it exactly once.
    /// </summary>
    private bool _OwnsScratch;

    /// <summary>Active fragmentation kind reported by the innermost fragmentable layer.</summary>
    private FragmentationKind _FragKind;

    #endregion

    #region Lifecycle

    /// <summary>Creates an engine that has not emitted anything yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal FrameEngine(TStack values, TTrailer trailer, TInterceptor interceptor, TState sessionState, ReadOnlySpan<byte> payload)
    {
        _Values = values;
        _Trailer = trailer;
        _Interceptor = interceptor;
        _SessionState = sessionState;
        _Payload = payload;
        _Phase = EnginePhase.NotStarted;
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

    /// <summary>Outcome of the build; <see cref="BuildStatus.Success"/> until a failure occurs.</summary>
    internal BuildStatus Status
    {
        readonly get; private set;
    }

    /// <summary>
    /// Ends the sequence and releases the pooled per-thread scratch reservation when
    /// the caller stops before the last fragment.  Idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Dispose() => _Finish();

    #endregion

    #region MoveNext

    /// <summary>Writes the next frame; see the wrapper types for the public contract.</summary>
    internal bool MoveNext(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        if (_Phase == EnginePhase.Done)
        {
            return false;
        }

        // The owning session was disposed (and maybe re-rented) after this sequence
        // was created: its state now belongs to another flow and must not be touched.
        if (!_SessionState.IsSessionLive)
        {
            return _Fail(BuildStatus.SessionDisposed, rollback: false);
        }

        return _Phase == EnginePhase.Fragmenting
            ? _EmitNextFragment(dst, out bytesWritten)
            : _EmitFirst(dst, out bytesWritten);
    }

    /// <summary>
    /// First-call dispatcher: validates depth, computes the unfragmented frame size and
    /// picks the single-frame or the multi-fragment path.
    /// </summary>
    private bool _EmitFirst(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        int depth = _Values.Depth;
        if (depth > FrameLimits.MaxSupportedDepth)
        {
            return _Fail(BuildStatus.StackTooDeep, rollback: false);
        }

        // Sizes are bounded: every HeaderSize is a small layer constant and the
        // payload is a span (≤ int.MaxValue), so the sums below do not overflow for
        // any frame a caller can hold in memory.
        int totalHdr = _Values.TotalHeaderSize;
        int trailerSize = _Trailer.TrailerSize;
        int total = totalHdr + _Payload.Length + trailerSize;
        int maxFrameLen = _Values.MaxFrameLength;

        if (total <= maxFrameLen)
        {
            return _EmitSingleFrame(dst, depth, totalHdr, trailerSize, total, out bytesWritten);
        }

        if (!_Values.HasFragmentable)
        {
            return _Fail(BuildStatus.FragmentationRequired, rollback: false);
        }

        return _BeginFragmenting(depth, totalHdr, trailerSize, total, maxFrameLen, dst, out bytesWritten);
    }

    #endregion

    #region Single frame

    /// <summary>Builds the entire frame in <paramref name="dst"/> in the non-fragmenting case.</summary>
    private bool _EmitSingleFrame(Span<byte> dst, int depth, int totalHdr, int trailerSize, int total, out int bytesWritten)
    {
        bytesWritten = 0;

        // Reject before the write walk: nothing is written and no counter moves.
        if (dst.Length < total)
        {
            return _Fail(BuildStatus.BufferTooSmall, rollback: false);
        }

        // Write walk.  Stateful layers advance their counters here; every failure
        // below rolls them back because no byte reaches the caller.
        Span<int> offsets = stackalloc int[FrameLimits.MaxSupportedDepth];
        offsets = offsets[..depth];
        _SessionState.BeginPacket(_Payload.Length);
        _SessionState.WriteHeaders(in _Values, dst, offsets, ref _Interceptor);
        _Payload.CopyTo(dst.Slice(totalHdr, _Payload.Length));

        int dataLength = totalHdr + _Payload.Length;
        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, depth, dataLength);

        // Post-fix phases.  Layers report conformance violations (a length field that
        // cannot represent the frame, UDP over IPv6 with a pinned zero checksum)
        // through ctx.Status instead of throwing; check after each phase that can set it.
        _Values.ApplyPostFix(FixPhase.Length, dst, offsets, dataLength, ref ctx);
        if (ctx.Status != BuildStatus.Success)
        {
            return _Fail(ctx.Status, rollback: true);
        }

        _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, dst, offsets, dataLength, ref ctx);
        _Values.ApplyPostFix(FixPhase.InnerChecksum, dst, offsets, dataLength, ref ctx);
        if (ctx.Status != BuildStatus.Success)
        {
            return _Fail(ctx.Status, rollback: true);
        }

        _Values.ApplyPostFix(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx);
        _Values.ApplyPostFix(FixPhase.Trailer, dst, offsets, dataLength, ref ctx);

        // Commit.
        if (trailerSize > 0)
        {
            _Trailer.WriteTrailer(dst[..total], dataLength);
        }

        _Interceptor.OnFrameComplete(dst[..total]);
        bytesWritten = total;
        _Finish();
        return true;
    }

    #endregion

    #region Fragmentation

    /// <summary>
    /// Builds the unfragmented frame into the per-thread scratch buffer (so the session
    /// counters advance once for the whole logical packet), validates the located
    /// <see cref="IFragmentable"/> layer, computes the slice geometry and emits fragment 0.
    /// </summary>
    private bool _BeginFragmenting(int depth, int totalHdr, int trailerSize, int total, int maxFrameLen, Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        // Reserve the per-thread scratch.  A reentrant build (from an interceptor
        // callback) gets a private non-pooled pair so it cannot overwrite the cached
        // headers of the outer build.
        _OwnsScratch = FrameSequenceScratch.TryAcquire(total, out byte[] scratchArray, out int[] offsetsArray);
        Span<byte> scratch = scratchArray.AsSpan(0, total);
        Span<int> offsets = offsetsArray.AsSpan(0, depth);

        // Write walk into the scratch.  Per-header interceptor calls are suppressed:
        // the public hook contract here is one OnFrameComplete per emitted fragment.
        NoInterceptor noInterceptor = default;
        _SessionState.BeginPacket(_Payload.Length);
        _SessionState.WriteHeaders(in _Values, scratch, offsets, ref noInterceptor);
        _Payload.CopyTo(scratch.Slice(totalHdr, _Payload.Length));

        int dataLength = totalHdr + _Payload.Length;

        // Locate the innermost fragmentable layer; it decides which post-fix phases run
        // once on the scratch and which run per emitted fragment.
        if (!_Values.TryGetFragmentableInfo(
            offsets, out _, out int headerEndOffset, out bool canFragment, out FragmentationKind kind, out int alignment))
        {
            // HasFragmentable was true but no IFragmentable was found: a structural
            // inconsistency of the cons-list implementation.
            return _Fail(BuildStatus.InvalidLayerState, rollback: true);
        }

        if (!canFragment)
        {
            return _Fail(BuildStatus.FragmentationRequired, rollback: true);
        }

        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, depth, dataLength);

        // Network-layer fragmentation: length, pseudo-header and inner checksum run
        // once on the unfragmented scratch, so the transport checksum covers the
        // whole datagram.  Application segmentation defers them to every segment.
        if (kind == FragmentationKind.NetworkLayer)
        {
            _Values.ApplyPostFix(FixPhase.Length, scratch, offsets, dataLength, ref ctx);
            if (ctx.Status != BuildStatus.Success)
            {
                return _Fail(ctx.Status, rollback: true);
            }

            _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, scratch, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.InnerChecksum, scratch, offsets, dataLength, ref ctx);
            if (ctx.Status != BuildStatus.Success)
            {
                return _Fail(ctx.Status, rollback: true);
            }
        }

        // Geometry: alignment must be a power of two, and one aligned slice must fit.
        BuildStatus geoStatus = FragmentGeometryHelper.TryComputeFragmentGeometry(
            canFragment: true, alignment, headerEndOffset, dataLength, maxFrameLen, trailerSize,
            out int innerLen, out int maxFragInner);
        if (geoStatus != BuildStatus.Success)
        {
            return _Fail(geoStatus, rollback: true);
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
        _Phase = EnginePhase.Fragmenting;
        return _EmitNextFragment(dst, out bytesWritten);
    }

    /// <summary>
    /// Copies the cached headers and the next inner-payload slice into
    /// <paramref name="dst"/>, re-runs the per-fragment post-fix phases, patches the
    /// fragment fields, writes the trailer and invokes
    /// <see cref="IFrameInterceptor.OnFrameComplete"/>.
    /// </summary>
    private bool _EmitNextFragment(Span<byte> dst, out int bytesWritten)
    {
        bytesWritten = 0;

        // Counters are rolled back only while no fragment of this packet reached the
        // caller; after that the packet is partly on the wire and its id is used.
        bool nothingEmitted = _FragmentCursor == 0;
        if (_Scratch is null || _ScratchOffsets is null)
        {
            return _Fail(BuildStatus.InvalidLayerState, rollback: nothingEmitted);
        }

        if (_FragmentCursor >= _InnerPayloadLength)
        {
            _Finish();
            return false;
        }

        int remaining = _InnerPayloadLength - _FragmentCursor;
        int sliceLen = Math.Min(_MaxFragmentInnerPayload, remaining);
        bool moreFragments = sliceLen < remaining;
        int dataLength = _HeaderEndOffset + sliceLen;
        int total = dataLength + _TrailerSize;
        if (dst.Length < total)
        {
            return _Fail(BuildStatus.BufferTooSmall, rollback: nothingEmitted);
        }

        // Cached headers (frame start through the fragmentable header), then the slice.
        Span<byte> scratch = _Scratch.AsSpan();
        Span<int> offsets = _ScratchOffsets.AsSpan(0, _Depth);
        scratch[.._HeaderEndOffset].CopyTo(dst);
        scratch.Slice(_HeaderEndOffset + _FragmentCursor, sliceLen).CopyTo(dst[_HeaderEndOffset..]);

        Span<byte> psSrc = stackalloc byte[16];
        Span<byte> psDst = stackalloc byte[16];
        scoped PostFixContext ctx = default;
        FragmentGeometryHelper.InitContext(ref ctx, psSrc, psDst, offsets, _Depth, dataLength);

        // Length re-patches every header up to and including the fragmentable layer on
        // the smaller frame.  Inner headers exist only in fragment 0 and keep the
        // values computed on the unfragmented datagram.
        _Values.ApplyPostFixUpTo(FixPhase.Length, dst, offsets, dataLength, ref ctx, _HeaderEndOffset);
        if (ctx.Status != BuildStatus.Success)
        {
            return _Fail(ctx.Status, rollback: nothingEmitted);
        }

        // Fragment fields (offset, more-fragments; clears IPv4 DF) on every
        // fragmentable layer of the active kind only, so an application segmenter
        // never patches the outer IP layer.
        _Values.PatchFragmentable(dst, offsets, dataLength, _FragmentCursor, moreFragments, _FragKind);

        if (_FragKind == FragmentationKind.ApplicationSegmentation)
        {
            // Every segment is a complete datagram with its own transport checksum.
            _Values.ApplyPostFix(FixPhase.PublishPseudoHeader, dst, offsets, dataLength, ref ctx);
            _Values.ApplyPostFix(FixPhase.InnerChecksum, dst, offsets, dataLength, ref ctx);
            if (ctx.Status != BuildStatus.Success)
            {
                return _Fail(ctx.Status, rollback: nothingEmitted);
            }

            _Values.ApplyPostFix(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx);
        }
        else
        {
            // Header-only checksums after the fragment-field patch so they cover the final bytes.
            _Values.ApplyPostFixUpTo(FixPhase.OuterChecksum, dst, offsets, dataLength, ref ctx, _HeaderEndOffset);
        }

        _Values.ApplyPostFix(FixPhase.Trailer, dst, offsets, dataLength, ref ctx);

        // Commit.
        if (_TrailerSize > 0)
        {
            _Trailer.WriteTrailer(dst[..total], dataLength);
        }

        _Interceptor.OnFrameComplete(dst[..total]);
        _FragmentCursor += sliceLen;
        bytesWritten = total;
        if (!moreFragments)
        {
            // Last fragment: release the pooled scratch now so the next top-level
            // build on this thread reuses it.
            _Finish();
        }

        return true;
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Records <paramref name="status"/>, optionally restores the session counters,
    /// ends the sequence and returns <c>false</c> for the caller to forward.
    /// </summary>
    private bool _Fail(BuildStatus status, bool rollback)
    {
        if (rollback)
        {
            _SessionState.RollbackPacket();
        }

        Status = status;
        _Finish();
        return false;
    }

    /// <summary>
    /// Transitions to <see cref="EnginePhase.Done"/> and releases the pooled per-thread
    /// scratch reservation if this engine owns it.  Idempotent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _Finish()
    {
        if (_OwnsScratch)
        {
            FrameSequenceScratch.Release();
            _OwnsScratch = false;
        }

        _Phase = EnginePhase.Done;
    }

    /// <summary>State machine of the engine.</summary>
    private enum EnginePhase : byte
    {
        /// <summary>No frame has been emitted yet.</summary>
        NotStarted = 0,

        /// <summary>The single frame or the last fragment was emitted, or the build failed.</summary>
        Done = 1,

        /// <summary>Multi-fragment mode; <see cref="_FragmentCursor"/> tracks progress.</summary>
        Fragmenting = 2,
    }

    #endregion
}
