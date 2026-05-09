// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Carries the dispatch table, key, and caller identity that caused the current protocol to be invoked.
/// Embedded directly inside <see cref="ParseContext"/> and propagated through the parse chain.
/// <para>
/// <b>Schreibschutz:</b> The <c>internal static</c> factory methods
/// (<see cref="ForU64"/>, <see cref="ForString"/>, <see cref="ForBytes"/>,
/// <see cref="ForBool"/>, <see cref="ForAny"/>) are accessible only within
/// <c>NetworkInspector.Core</c>. Protocol implementations in other assemblies can only
/// read the context via the public properties and <c>TryGet*</c> methods.
/// </para>
/// <para>
/// Layout: ~32 bytes. Embedded as a value type in <see cref="ParseContext"/> (a
/// <see langword="readonly ref struct"/>), so no heap allocation occurs. The
/// <c>default</c> instance has <see cref="Kind"/> = <see cref="DispatchKeyKind.None"/>
/// and <see cref="HasDispatch"/> = <see langword="false"/>.
/// </para>
/// <para>
/// <b>Thread safety:</b> Immutable value type; all fields are set exactly once
/// at construction and never modified.
/// </para>
/// </summary>
public readonly struct DispatchContext
{
    #region Fields

    // Layout chosen to minimise padding on a 64-bit runtime:
    //   offset 0  : object? _RefKey       (8 bytes, reference — placed first to avoid gap after ulong)
    //   offset 8  : ulong _NumericKey     (8 bytes)
    //   offset 16 : ProtocolTableId _TableId (int, 4 bytes)
    //   offset 20 : ProtocolId _CallerProtocolId (int, 4 bytes)
    //   offset 24 : DispatchKeyKind _Kind (byte, 1 byte)
    //   offset 25 : 7 bytes padding → total 32 bytes
    // The runtime may reorder fields (LayoutKind.Auto for managed structs), but the
    // total size remains 32 bytes regardless of ordering.

    private readonly object? _RefKey;
    private readonly ulong _NumericKey;
    private readonly ProtocolTableId _TableId;
    private readonly ProtocolId _CallerProtocolId;
    private readonly DispatchKeyKind _Kind;

    #endregion

    #region Constructors

    /// <summary>
    /// Full private constructor. All factory methods funnel through here so that
    /// the invariant <c>_Kind != None ⇔ _TableId.IsValid</c> is enforced in one place.
    /// </summary>
    private DispatchContext(DispatchKeyKind kind, ProtocolTableId tableId, ulong numericKey, object? refKey, ProtocolId callerProtocolId)
    {
        _Kind = kind;
        _TableId = tableId;
        _NumericKey = numericKey;
        _RefKey = refKey;
        _CallerProtocolId = callerProtocolId;
    }

    #endregion

    #region Internal Factories — write access restricted to NetworkInspector.Core

    /// <summary>
    /// Creates a dispatch context for a <see cref="ulong"/> key dispatch
    /// (e.g., CAN ID, FlexRay frame ID + channel, LIN frame ID).
    /// </summary>
    /// <param name="tableId">The dispatch table used for this lookup.</param>
    /// <param name="key">The numeric key used for this lookup.</param>
    /// <param name="callerProtocolId">The <see cref="ProtocolId"/> of the protocol that triggered the dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DispatchContext ForU64(ProtocolTableId tableId, ulong key, ProtocolId callerProtocolId)
        => new(DispatchKeyKind.U64, tableId, key, null, callerProtocolId);

    /// <summary>Creates a dispatch context for a <see cref="string"/> key dispatch.</summary>
    /// <param name="tableId">The dispatch table used for this lookup.</param>
    /// <param name="key">The string key used for this lookup.</param>
    /// <param name="callerProtocolId">The <see cref="ProtocolId"/> of the protocol that triggered the dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DispatchContext ForString(ProtocolTableId tableId, string key, ProtocolId callerProtocolId)
        => new(DispatchKeyKind.String, tableId, 0, key, callerProtocolId);

    /// <summary>Creates a dispatch context for a byte-sequence key dispatch.</summary>
    /// <param name="tableId">The dispatch table used for this lookup.</param>
    /// <param name="key">The byte-sequence key used for this lookup.</param>
    /// <param name="callerProtocolId">The <see cref="ProtocolId"/> of the protocol that triggered the dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DispatchContext ForBytes(ProtocolTableId tableId, BytesKey key, ProtocolId callerProtocolId)
        => new(DispatchKeyKind.Bytes, tableId, 0, key, callerProtocolId);  // BytesKey is a struct → boxed once

    /// <summary>Creates a dispatch context for a <see cref="bool"/> key dispatch.</summary>
    /// <param name="tableId">The dispatch table used for this lookup.</param>
    /// <param name="key">The bool key used for this lookup.</param>
    /// <param name="callerProtocolId">The <see cref="ProtocolId"/> of the protocol that triggered the dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DispatchContext ForBool(ProtocolTableId tableId, bool key, ProtocolId callerProtocolId)
        => new(DispatchKeyKind.Bool, tableId, key ? 1UL : 0UL, null, callerProtocolId);

    /// <summary>Creates a dispatch context for a wildcard (any-key) dispatch.</summary>
    /// <param name="tableId">The dispatch table used for this lookup.</param>
    /// <param name="callerProtocolId">The <see cref="ProtocolId"/> of the protocol that triggered the dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DispatchContext ForAny(ProtocolTableId tableId, ProtocolId callerProtocolId)
        => new(DispatchKeyKind.Any, tableId, 0, null, callerProtocolId);

    #endregion

    #region Public Properties — read access for protocol implementations

    /// <summary>
    /// Whether a dispatch context is present.
    /// <see langword="false"/> for <see langword="default"/>(<see cref="DispatchContext"/>) and
    /// when the protocol was invoked directly rather than via a dispatch table.
    /// </summary>
    public bool HasDispatch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Kind != DispatchKeyKind.None;
    }

    /// <summary>The key type used for the dispatch that invoked this protocol.</summary>
    public DispatchKeyKind Kind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Kind;
    }

    /// <summary>
    /// The dispatch table that invoked this protocol.
    /// Only meaningful when <see cref="HasDispatch"/> is <see langword="true"/>.
    /// </summary>
    public ProtocolTableId TableId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _TableId;
    }

    /// <summary>
    /// The protocol that triggered this dispatch (i.e., the parent protocol that called
    /// <c>TryCallNextProtocol*</c>). Equals <see cref="ProtocolId.Invalid"/> when the dispatch
    /// context is absent (<see cref="HasDispatch"/> = <see langword="false"/>), or when the
    /// parent call was a direct <c>CallProtocol</c> or heuristic match rather than a table lookup.
    /// <para>
    /// This allows child protocols to distinguish which parent dispatched them even when
    /// multiple parent protocols share the same dispatch table (e.g., IPv4 and IPv6 both
    /// register on <c>ip.proto</c>).
    /// </para>
    /// </summary>
    public ProtocolId CallerProtocolId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _CallerProtocolId;
    }

    #endregion

    #region Public TryGet Methods — typed key access

    /// <summary>
    /// Retrieves the dispatch key as a <see cref="ulong"/>.
    /// Returns <see langword="true"/> when <see cref="Kind"/> is <see cref="DispatchKeyKind.U64"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetU64(out ulong key)
    {
        key = _NumericKey;
        return _Kind == DispatchKeyKind.U64;
    }

    /// <summary>
    /// Retrieves the dispatch key as a <see cref="string"/>.
    /// Returns <see langword="true"/> when <see cref="Kind"/> is <see cref="DispatchKeyKind.String"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetString(out string? key)
    {
        key = _RefKey as string;
        return _Kind == DispatchKeyKind.String;
    }

    /// <summary>
    /// Retrieves the dispatch key as a <see cref="BytesKey"/>.
    /// Returns <see langword="true"/> when <see cref="Kind"/> is <see cref="DispatchKeyKind.Bytes"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetBytes(out BytesKey key)
    {
        if (_Kind == DispatchKeyKind.Bytes && _RefKey is BytesKey bk)
        {
            key = bk;
            return true;
        }
        key = default;
        return false;
    }

    /// <summary>
    /// Retrieves the dispatch key as a <see cref="bool"/>.
    /// Returns <see langword="true"/> when <see cref="Kind"/> is <see cref="DispatchKeyKind.Bool"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetBool(out bool key)
    {
        key = _NumericKey != 0;
        return _Kind == DispatchKeyKind.Bool;
    }

    #endregion
}
