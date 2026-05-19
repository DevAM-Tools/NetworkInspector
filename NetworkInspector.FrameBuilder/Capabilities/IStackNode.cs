// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Marker interface for any node in the typed cons-list that represents a
/// composed protocol stack.  Implemented by <see cref="StackEnd"/>,
/// <see cref="Stack{THead,TTail}"/> and <see cref="StatelessStack{THead,TTail}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cons-list orientation: <c>THead</c> is the most-recently-added layer (the
/// innermost layer added so far during the fluent build), <c>TTail</c> is the
/// remainder of the list (containing the outer layers).  When a frame is
/// serialized, the walk recurses into <c>Tail</c> first (writing outer layers)
/// and then writes <c>Head</c>.
/// </para>
/// <para>
/// The recursive operations below are implemented as instance methods on each
/// concrete cons-list struct (<see cref="StackEnd"/>,
/// <see cref="Stack{THead,TTail}"/>, <see cref="StatelessStack{THead,TTail}"/>).
/// Calls go through the <c>where T : struct, IStackNode</c> generic constraint
/// so the JIT devirtualises every step at specialisation time.
/// </para>
/// <para>Thread safety: implementations are <c>readonly struct</c>; safe to share.</para>
/// </remarks>
public interface IStackNode
{
    /// <summary>Number of layers in this cons-list.  Constant per type.</summary>
    int Depth
    {
        get;
    }

    /// <summary>Sum of every layer's <see cref="IProtocolLayer.HeaderSize"/>.</summary>
    int TotalHeaderSize
    {
        get;
    }

    /// <summary>
    /// Smallest MTU asserted by any <see cref="IProvidesMtu"/> layer along the
    /// cons-list, in bytes; <see cref="int.MaxValue"/> when no layer asserts an MTU.
    /// </summary>
    int MaxFrameLength
    {
        get;
    }

    /// <summary>
    /// <c>true</c> if any layer along the cons-list implements
    /// <see cref="IFragmentable"/> and can therefore split an oversize payload.
    /// </summary>
    bool HasFragmentable
    {
        get;
    }

    /// <summary>
    /// Walks the cons-list outer→inner and applies the given
    /// <paramref name="phase"/> to every layer.
    /// </summary>
    /// <param name="phase">Phase being processed.</param>
    /// <param name="frame">Frame buffer covering the whole frame.</param>
    /// <param name="offsets">
    /// Outer→inner header offsets recorded during the write walk; outer-most at
    /// index 0, inner-most at index <see cref="Depth"/>-1.
    /// </param>
    /// <param name="frameLength">Total bytes in the frame (headers + payload).</param>
    /// <param name="ctx">Mutable cross-layer post-fix context.</param>
    void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets, int frameLength, scoped ref PostFixContext ctx);

    /// <summary>
    /// Stateful write walk for use by <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
    /// Writes every layer's header outer→inner; for <see cref="IStatefulLayer"/>
    /// nodes it dispatches to the stateful overload (passing the shared
    /// <paramref name="state"/>); for <see cref="IStatelessLayer"/> nodes it
    /// calls the stateless <see cref="IStatelessLayer.WriteHeader"/>.
    /// </summary>
    /// <remarks>
    /// All-stateless cons-lists (used directly by
    /// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>) prefer the
    /// dedicated, slightly cheaper <see cref="IStatelessStack.WriteHeaders{TInterceptor}"/>
    /// overload that omits the <see cref="SessionState"/> parameter.
    /// </remarks>
    int WriteHeaders<TInterceptor>(
        scoped Span<byte> dst,
        int offset,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor,
        scoped ref SessionState state)
        where TInterceptor : struct, IFrameInterceptor;

    /// <summary>
    /// Initialises the per-layer state for every <see cref="IStatefulLayer"/>
    /// in the cons-list.  Called once per <see cref="Session{TStack,TTrailer,TInterceptor}"/>
    /// open.  No-op for stateless cons-lists.
    /// </summary>
    void InitializeStatefulState(ref SessionState state);

    /// <summary>
    /// Patches the cons-list <c>Head</c>'s <see cref="IConsumesNextProtocolValue"/>,
    /// if it has one, with <paramref name="innerProtocol"/>.  Used by the
    /// writing walk so the layer that just got written tells its immediate
    /// outer neighbour what its protocol type is.  No-op on <see cref="StackEnd"/>.
    /// </summary>
    /// <param name="frame">Frame buffer.</param>
    /// <param name="myHeadOffset">Offset of this node's <c>Head</c> in <paramref name="frame"/>.</param>
    /// <param name="innerProtocol">Protocol-type value to patch in.</param>
    void PatchHeadAsOuterFromInner(scoped Span<byte> frame, int myHeadOffset, ushort innerProtocol);

    /// <summary>
    /// Locates the innermost <see cref="IFragmentable"/> layer along the
    /// cons-list and reports the byte range of its header along with its
    /// <see cref="FragmentationKind"/> and <see cref="IFragmentable.FragmentAlignment"/>.
    /// </summary>
    /// <param name="offsets">Per-layer header offsets recorded during the write walk.</param>
    /// <param name="headerOffset">Offset of the fragmentable layer's header (when found).</param>
    /// <param name="headerEndOffset">Offset just past the fragmentable layer's header.</param>
    /// <param name="canFragment">
    /// <see cref="IFragmentable.CanFragment"/> of the located instance; meaningful
    /// only when the method returns <c>true</c>.
    /// </param>
    /// <param name="kind">
    /// <see cref="IFragmentable.FragmentationKind"/> of the located instance.
    /// Selects the iterator's branch (IP-style vs. application segmentation).
    /// </param>
    /// <param name="alignment">
    /// <see cref="IFragmentable.FragmentAlignment"/> of the located instance.
    /// Per-fragment payload slice size is rounded down to this multiple.
    /// </param>
    /// <returns><c>true</c> when a fragmentable layer was located; <c>false</c> otherwise.</returns>
    bool TryGetFragmentableInfo(
        scoped ReadOnlySpan<int> offsets,
        out int headerOffset, out int headerEndOffset,
        out bool canFragment, out FragmentationKind kind, out int alignment);

    /// <summary>
    /// Invokes <see cref="IFragmentable.PatchFragmentHeader"/> on every
    /// <see cref="IFragmentable"/> layer along the cons-list (outer→inner walk)
    /// whose <see cref="IFragmentable.FragmentationKind"/> matches
    /// <paramref name="activeKind"/>.  Layers with a non-matching kind keep
    /// their cached header bytes verbatim and are not rewritten.
    /// </summary>
    /// <param name="frame">Current fragment buffer.</param>
    /// <param name="offsets">Per-layer header offsets recorded during the write walk.</param>
    /// <param name="frameDataLength">
    /// Total bytes from offset 0 through the end of the fragment payload
    /// (excluding any trailer slot that follows).
    /// </param>
    /// <param name="fragmentPayloadOffset">Position of this fragment's payload in the original payload pool.</param>
    /// <param name="moreFragments"><c>true</c> when at least one further fragment will follow.</param>
    /// <param name="activeKind">Fragmentation kind reported by the innermost fragmentable layer.</param>
    void PatchFragmentable(
        scoped Span<byte> frame, scoped ReadOnlySpan<int> offsets,
        int frameDataLength, int fragmentPayloadOffset,
        bool moreFragments, FragmentationKind activeKind);

    /// <summary>
    /// Variant of <see cref="ApplyPostFix"/> that runs <paramref name="phase"/>
    /// only on layers whose header end offset is at most
    /// <paramref name="maxHeaderEndOffset"/>.  Used by the fragmentation path
    /// to keep length / outer-checksum walks from rewriting payload bytes that
    /// belong to inner-of-fragmentable layers (UDP / TCP / SOME-IP) whose
    /// headers exist only in fragment 0.
    /// </summary>
    /// <param name="phase">Phase being processed.</param>
    /// <param name="frame">Frame buffer covering the whole fragment.</param>
    /// <param name="offsets">Per-layer header offsets recorded during the write walk.</param>
    /// <param name="frameLength">Total bytes in the fragment frame (headers + payload).</param>
    /// <param name="ctx">Mutable cross-layer post-fix context.</param>
    /// <param name="maxHeaderEndOffset">
    /// Inclusive cutoff; layers whose <c>headerOffset + HeaderSize</c> exceeds
    /// this value are skipped.
    /// </param>
    void ApplyPostFixUpTo(
        FixPhase phase, scoped Span<byte> frame, scoped Span<int> offsets,
        int frameLength, scoped ref PostFixContext ctx, int maxHeaderEndOffset);
}

