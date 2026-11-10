// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Carries the active <see cref="PacketIndex"/>, the <see cref="DispatchContext"/>,
/// the owning <see cref="Stack"/>, and the identity of the
/// currently-executing protocol (<see cref="SelfProtocolId"/>) through the protocol parse chain.
/// <para>
/// Declared as a <see langword="readonly ref struct"/> so it is stack-only and
/// cannot be captured inside closures (e.g., lazy field populators). This enforces
/// the invariant that index presence is recorded only during direct, synchronous
/// parsing — never inside deferred lazy populators.
/// </para>
/// <para>
/// The empty context (<see cref="Empty"/> / <see langword="default"/>) represents
/// a non-indexed parse with no dispatch context. All record calls and
/// <see cref="Dispatch"/>.<see cref="DispatchContext.HasDispatch"/> are no-ops / false
/// in that case (zero overhead).
/// </para>
/// <para>
/// <b>Thread-safety:</b> <c>readonly ref struct</c> — stack-only; no thread-safety concerns.
/// </para>
/// </summary>
public readonly ref struct ParseContext
{
    #region Configuration and state

    /// <summary>The packet index that records group and protocol presence; null for a parse without an index.</summary>
    private readonly PacketIndex? _Index;

    /// <summary>Whether the parse skips the field tree (<see cref="HasFieldTree"/> is the inverse).</summary>
    private readonly bool _SkipFieldTree;

    /// <summary>The stack's verdict for this parse; see <see cref="IsReplay"/>.</summary>
    private readonly bool _IsReplay;

    /// <summary>An empty parse context with no attached index. All record calls are no-ops.</summary>
    public static ParseContext Empty => default;

    /// <summary>Creates a non-indexed parse context carrying only the stack (used for unindexed parses).</summary>
    /// <param name="stack">The stack that runs the parse.</param>
    /// <param name="skipFieldTree">Whether the parse stores no field tree (see <see cref="HasFieldTree"/>).</param>
    /// <param name="isReplay">The stack's verdict that this packet id completed its first parse earlier (see <see cref="IsReplay"/>).</param>
    internal ParseContext(Stack stack, bool skipFieldTree = false, bool isReplay = false)
    {
        _Index = null;
        Dispatch = default;
        Stack = stack;
        SelfProtocolId = ProtocolId.Invalid;
        _SkipFieldTree = skipFieldTree;
        _IsReplay = isReplay;
    }

    /// <summary>Creates a parse context with the given packet index and stack.</summary>
    /// <param name="index">The index that records presence during this parse.</param>
    /// <param name="stack">The stack that runs the parse.</param>
    /// <param name="skipFieldTree">Whether the parse stores no field tree (see <see cref="HasFieldTree"/>).</param>
    /// <param name="isReplay">The stack's verdict that this packet id completed its first parse earlier (see <see cref="IsReplay"/>).</param>
    internal ParseContext(PacketIndex index, Stack stack, bool skipFieldTree = false, bool isReplay = false)
    {
        _Index = index;
        Dispatch = default;
        Stack = stack;
        SelfProtocolId = ProtocolId.Invalid;
        _SkipFieldTree = skipFieldTree;
        _IsReplay = isReplay;
    }

    /// <summary>Full private constructor used by <see cref="WithDispatch"/> and <see cref="WithSelfProtocol"/> to produce updated copies.</summary>
    /// <param name="index">The packet index, or null.</param>
    /// <param name="dispatch">The dispatch context to carry.</param>
    /// <param name="stack">The stack, or null for the empty context.</param>
    /// <param name="selfProtocolId">The protocol that is parsing.</param>
    /// <param name="skipFieldTree">Whether the parse stores no field tree.</param>
    /// <param name="isReplay">The stack's replay verdict.</param>
    private ParseContext(
        PacketIndex? index, DispatchContext dispatch, Stack? stack, ProtocolId selfProtocolId, bool skipFieldTree, bool isReplay)
    {
        _Index = index;
        Dispatch = dispatch;
        Stack = stack;
        SelfProtocolId = selfProtocolId;
        _SkipFieldTree = skipFieldTree;
        _IsReplay = isReplay;
    }

    #endregion

    #region Parse state queries

    /// <summary>Whether a packet index is attached for presence recording.</summary>
    public bool HasIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Index is not null;
    }

    /// <summary>
    /// Whether this parse writes FieldBodies. Default and empty contexts report
    /// <see langword="true"/> so protocols that forget to check keep the Build-tree contract.
    /// </summary>
    public bool HasFieldTree
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_SkipFieldTree;
    }

    /// <summary>
    /// Whether the stack classified this parse as a replay: the packet id completed its first parse earlier, on this or another
    /// thread. A stateful protocol must not mutate cross-packet state in a replay, also when it never saw the packet before.
    /// Always <see langword="false"/> for the default/empty context.
    /// </summary>
    /// <remarks>Related: <see cref="IngestGate.IsReplay(in ParseContext, PacketId)"/> (the decision a protocol asks for).</remarks>
    public bool IsReplay
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _IsReplay;
    }

    /// <summary>Whether a stack is attached. <see langword="false"/> only for the default/empty context.</summary>
    public bool HasStack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Stack is not null;
    }

    /// <summary>
    /// The protocol stack for this parse invocation.
    /// <see langword="null"/> only for the default/empty context — all real parse calls carry the stack.
    /// </summary>
    public Stack? Stack { get; }

    /// <summary>
    /// The <see cref="ProtocolId"/> of the protocol whose <see cref="IProtocol.Parse"/> is
    /// currently executing. Set automatically by <see cref="Stack.CallProtocol"/> before each
    /// parse invocation — protocols never need to pass their own ID explicitly.
    /// Equals <see cref="ProtocolId.Invalid"/> for the default/empty context and inside lazy
    /// populators (which cannot carry a ref-struct context).
    /// <para>
    /// Consumers that dispatch child protocols can forward this value as
    /// <see cref="DispatchContext.CallerProtocolId"/> — this happens automatically via
    /// <c>TryCallNextProtocol*</c>.
    /// </para>
    /// </summary>
    public ProtocolId SelfProtocolId { get; }

    /// <summary>
    /// The dispatch context for the protocol currently being parsed.
    /// <see cref="DispatchContext.HasDispatch"/> is <see langword="false"/> when the protocol
    /// was not invoked via a dispatch table lookup (e.g., root frame protocol, direct call).
    /// <para>
    /// Protocol implementations use this to identify which table and key triggered them —
    /// for example, Signal Message uses it to select the correct PDU definition without
    /// inspecting individual parent-protocol fields.
    /// </para>
    /// </summary>
    public DispatchContext Dispatch { get; }

    #endregion

    #region Derived contexts and presence recording

    /// <summary>
    /// Returns a copy of this context with the given <see cref="DispatchContext"/> applied.
    /// Called by <c>MutField.TryCallNextProtocol*</c> immediately before dispatching to a
    /// child protocol — the child's <c>parentField</c> will carry this updated context.
    /// The current <see cref="SelfProtocolId"/> is preserved so that child protocols can
    /// read <see cref="DispatchContext.CallerProtocolId"/> to identify their parent.
    /// <para>
    /// <b>Accessibility:</b> <c>internal</c> — only <c>NetworkInspector.Core</c> can set the
    /// dispatch context. Protocol implementations in other assemblies may only read it via
    /// <see cref="Dispatch"/>.
    /// </para>
    /// </summary>
    /// <param name="dispatch">The dispatch that invokes the child protocol; only Core can create one.</param>
    /// <returns>The updated copy; this context is unchanged.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseContext WithDispatch(DispatchContext dispatch)
        => new(_Index, dispatch, Stack, SelfProtocolId, _SkipFieldTree, _IsReplay);

    /// <summary>
    /// Returns a copy of this context with <see cref="SelfProtocolId"/> set to
    /// <paramref name="protocolId"/>. Called by <see cref="Stack.CallProtocol"/> immediately
    /// before invoking a protocol's parse delegate so that every protocol can always read
    /// its own <see cref="ProtocolId"/> from the context without storing it manually.
    /// </summary>
    /// <param name="protocolId">The protocol that is about to parse.</param>
    /// <returns>The updated copy; this context is unchanged.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseContext WithSelfProtocol(ProtocolId protocolId)
        => new(_Index, Dispatch, Stack, protocolId, _SkipFieldTree, _IsReplay);

    /// <summary>
    /// Records that the current packet contains the given index group.
    /// No-op when no index is attached (zero overhead).
    /// Called by protocols during <see cref="IProtocol.Parse"/>.
    /// </summary>
    /// <param name="groupId">The index group that is present in the packet.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordGroupPresence(IndexGroupId groupId)
        => _Index?.RecordGroupPresence(groupId);

    /// <summary>
    /// Records that the current packet contains the given protocol.
    /// No-op when no index is attached (zero overhead).
    /// Called by protocols during <see cref="IProtocol.Parse"/>.
    /// </summary>
    /// <param name="protocolId">The protocol that is present in the packet.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordProtocolPresence(ProtocolId protocolId)
        => _Index?.RecordProtocolPresence(protocolId);

    #endregion
}
