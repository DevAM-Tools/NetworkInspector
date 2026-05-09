// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Carries the active <see cref="PacketIndex"/>, optional <see cref="ValueCacheBuilder"/>,
/// the <see cref="DispatchContext"/>, the owning <see cref="Stack"/>, and the identity of the
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
    #region Fields

    private readonly PacketIndex? _Index;
    private readonly ValueCacheBuilder? _ValueCacheBuilder;

    /// <summary>
    /// The dispatch context for the current parse invocation.
    /// Set to <see langword="default"/> (<see cref="DispatchContext.HasDispatch"/> = <see langword="false"/>)
    /// when no dispatch table was involved (e.g., root-level parse, direct <c>CallProtocol</c>).
    /// Populated by <c>MutField.TryCallNextProtocol</c> overloads before calling the child protocol.
    /// </summary>
    private readonly DispatchContext _Dispatch;

    /// <summary>
    /// The protocol stack that owns this parse invocation.
    /// Carries the stack through the parse chain so that <see cref="MutField"/> dispatch
    /// methods no longer need a separate <c>Stack</c> parameter. <see langword="null"/> only
    /// for the default/empty context — all real parse calls populate this field.
    /// </summary>
    private readonly Stack? _Stack;

    /// <summary>
    /// The <see cref="ProtocolId"/> of the protocol currently executing its
    /// <see cref="IProtocol.Parse"/> method. Set automatically by
    /// <see cref="Stack.CallProtocol"/> before every parse invocation so that protocols
    /// never need to pass their own ID explicitly.
    /// Equals <see cref="ProtocolId.Invalid"/> for the default/empty context and for
    /// lazy populators (which cannot carry a ref-struct context).
    /// </summary>
    private readonly ProtocolId _SelfProtocolId;

    #endregion

    #region Constructors

    /// <summary>An empty parse context with no attached index. All record calls are no-ops.</summary>
    public static ParseContext Empty => default;

    /// <summary>Creates a non-indexed parse context carrying only the stack (used for unindexed parses).</summary>
    internal ParseContext(Stack stack)
    {
        _Index = null;
        _ValueCacheBuilder = null;
        _Dispatch = default;
        _Stack = stack;
        _SelfProtocolId = ProtocolId.Invalid;
    }

    /// <summary>Creates a parse context with the given packet index and stack.</summary>
    internal ParseContext(PacketIndex index, Stack stack)
    {
        _Index = index;
        _ValueCacheBuilder = null;
        _Dispatch = default;
        _Stack = stack;
        _SelfProtocolId = ProtocolId.Invalid;
    }

    /// <summary>Creates a parse context with a packet index, value cache builder, and stack.</summary>
    internal ParseContext(PacketIndex index, ValueCacheBuilder valueCacheBuilder, Stack stack)
    {
        _Index = index;
        _ValueCacheBuilder = valueCacheBuilder;
        _Dispatch = default;
        _Stack = stack;
        _SelfProtocolId = ProtocolId.Invalid;
    }

    /// <summary>Full private constructor used by <see cref="WithDispatch"/> and <see cref="WithSelfProtocol"/> to produce updated copies.</summary>
    private ParseContext(PacketIndex? index, ValueCacheBuilder? valueCacheBuilder, DispatchContext dispatch, Stack? stack, ProtocolId selfProtocolId)
    {
        _Index = index;
        _ValueCacheBuilder = valueCacheBuilder;
        _Dispatch = dispatch;
        _Stack = stack;
        _SelfProtocolId = selfProtocolId;
    }

    #endregion

    #region Properties

    /// <summary>Whether a packet index is attached for presence recording.</summary>
    public bool HasIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Index is not null;
    }

    /// <summary>Whether a stack is attached. <see langword="false"/> only for the default/empty context.</summary>
    public bool HasStack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Stack is not null;
    }

    /// <summary>
    /// The protocol stack for this parse invocation.
    /// <see langword="null"/> only for the default/empty context — all real parse calls carry the stack.
    /// </summary>
    public Stack? Stack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Stack;
    }

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
    public ProtocolId SelfProtocolId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _SelfProtocolId;
    }

    /// <summary>The optional value cache builder for recording field values. May be <see langword="null"/>.</summary>
    internal ValueCacheBuilder? ValueCacheBuilder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ValueCacheBuilder;
    }

    /// <summary>
    /// The dispatch context for the protocol currently being parsed.
    /// <see cref="DispatchContext.HasDispatch"/> is <see langword="false"/> when the protocol
    /// was not invoked via a dispatch table lookup (e.g., root frame protocol, direct call).
    /// <para>
    /// Protocol implementations use this to identify which table and key triggered them —
    /// for example, Signal PDU uses it to select the correct PDU definition without
    /// inspecting individual parent-protocol fields.
    /// </para>
    /// </summary>
    public DispatchContext Dispatch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Dispatch;
    }

    #endregion

    #region Methods

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseContext WithDispatch(DispatchContext dispatch)
        => new(_Index, _ValueCacheBuilder, dispatch, _Stack, _SelfProtocolId);

    /// <summary>
    /// Returns a copy of this context with <see cref="SelfProtocolId"/> set to
    /// <paramref name="protocolId"/>. Called by <see cref="Stack.CallProtocol"/> immediately
    /// before invoking a protocol's parse delegate so that every protocol can always read
    /// its own <see cref="ProtocolId"/> from the context without storing it manually.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseContext WithSelfProtocol(ProtocolId protocolId)
        => new(_Index, _ValueCacheBuilder, _Dispatch, _Stack, protocolId);

    /// <summary>
    /// Records that the current packet contains the given index group.
    /// No-op when no index is attached (zero overhead).
    /// Called by protocols during <see cref="IProtocol.Parse"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordGroupPresence(IndexGroupId groupId)
        => _Index?.RecordGroupPresence(groupId);

    /// <summary>
    /// Records that the current packet contains the given protocol.
    /// No-op when no index is attached (zero overhead).
    /// Called by protocols during <see cref="IProtocol.Parse"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordProtocolPresence(ProtocolId protocolId)
        => _Index?.RecordProtocolPresence(protocolId);

    /// <summary>
    /// Records a field value in the active value cache builder.
    /// No-op when no builder is attached (single null-check, zero overhead).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void TryRecordValue(FieldId fieldId, in FieldValueData value)
        => _ValueCacheBuilder?.TryRecordValue(fieldId, value);

    #endregion
}
