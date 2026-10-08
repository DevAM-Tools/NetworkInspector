// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// One observer and the fields it receives for a single parse.
/// </summary>
/// <remarks>
/// <para>
/// The default watch is <see cref="FieldWatch.None"/>, which accepts nothing.
/// A null <see cref="Observer"/> is rejected by <c>Packet.TryParse</c> before the packet is mutated.
/// </para>
/// <para><b>Thread-safety:</b> a readonly struct. The observer it points at is not thread-safe.</para>
/// </remarks>
public readonly struct ParseObserver
{
    #region Properties

    /// <summary>The observer called for this parse. Null is rejected before parse.</summary>
    public IFieldObserver? Observer { get; init; }

    /// <summary>Which fields <see cref="Observer"/> receives. Default is <see cref="FieldWatch.None"/>.</summary>
    public FieldWatch Watch { get; init; }

    #endregion
}

/// <summary>
/// Choices for one parse that are not the frame, the packet id, or the recycle packet.
/// </summary>
/// <remarks>
/// <para>
/// A readonly struct passed <c>in</c>, so the parse does not allocate an options object and does
/// not copy this value when the caller passes it by reference. <c>with</c> copies the struct and
/// does not copy the observer memory or a watch bitset.
/// </para>
/// <para>
/// The default value keeps a field tree because <see cref="FieldTreeMode.Build"/> is 0.
/// A null <see cref="FirstProtocol"/> means the stack's packet protocol.
/// <see cref="ProtocolId"/> default is a real protocol id and is not the absent sentinel.
/// A null <see cref="Index"/> does not record presence. A null <see cref="Cache"/> does not record
/// values. An empty <see cref="Observers"/> memory does not call a watch.
/// </para>
/// <para><b>Thread-safety:</b> a readonly struct. The cache, index, and observers it references have their own contracts.</para>
/// </remarks>
/// <remarks>Creates parse choices. Omitted arguments match a build parse with no cache, index, or watch.</remarks>
public readonly struct ParseOptions(
    FieldTreeMode fieldTree = FieldTreeMode.Build,
    ValueCache? cache = null,
    bool recordOnReplay = false,
    PacketIndex? index = null,
    ProtocolId? firstProtocol = null,
    ReadOnlyMemory<ParseObserver> observers = default)
{
    #region Properties

    /// <summary>Whether this parse writes the field-tree slab. Default is <see cref="FieldTreeMode.Build"/>.</summary>
    public FieldTreeMode FieldTree { get; init; } = fieldTree;

    /// <summary>Value cache filled by this parse, or null to record nothing.</summary>
    public ValueCache? Cache { get; init; } = cache;

    /// <summary>When true, a replay of an already first-parsed id still writes <see cref="Cache"/>.</summary>
    public bool RecordOnReplay { get; init; } = recordOnReplay;

    /// <summary>Packet index filled by this parse, or null to record no presence.</summary>
    public PacketIndex? Index { get; init; } = index;

    /// <summary>Protocol dispatched after the packet protocol, or null for the stack default.</summary>
    public ProtocolId? FirstProtocol { get; init; } = firstProtocol;

    /// <summary>
    /// Observers for this parse. The caller keeps the memory alive for the call and may reuse it.
    /// Empty means no watch calls.
    /// </summary>
    public ReadOnlyMemory<ParseObserver> Observers { get; init; } = observers;

    #endregion
}
