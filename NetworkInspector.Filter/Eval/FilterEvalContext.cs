// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

/// <summary>
/// Per-filter evaluation state, reused for every packet.
/// <para>
/// The context owns everything the emitted delegate needs at runtime: the current packet, the
/// optional presence index, the field-to-protocol owner table, the active scope domain and the
/// scratch buffers used by breadth-first scope search. One instance belongs to exactly one
/// <see cref="Filter"/>, which is why filters are documented as single-threaded.
/// </para>
/// <para>
/// <b>Domain.</b> Outside a scope the domain is the whole packet and field lookups use the
/// packet's flat field index, which is O(1) amortised per occurrence. Inside
/// <c>$Name { … }</c> the domain narrows to one subtree and lookups switch to a depth-first walk
/// of that subtree; there is no per-subtree index to exploit, and subtrees are small.
/// </para>
/// <para>
/// <b>Lazy fields.</b> Every walk runs first without materialization. Only when that finds no
/// match and the packet still has unpopulated lazy fields does it repeat with materialization
/// enabled, so filters that reject early never pay for building deep subtrees.
/// </para>
/// </summary>
internal sealed class FilterEvalContext
{
    #region Fields

    private readonly ProtocolId[] _FieldOwners;

    private Packet? _Packet;
    private PacketIndex? _ConcreteIndex;
    private IPacketIndexReader? _Index;
    private bool _IndexUsable;

    public FilterError? Error { get; private set; }

    private Field _Domain;
    private int _DomainDepth;

    private Field[] _BfsQueue;
    private int _BfsTop;
    private Field[] _ScopeHits;
    private int _HitsTop;

    #endregion

    #region Construction

    /// <summary>Creates a context bound to a stack's field-owner table.</summary>
    public FilterEvalContext(ProtocolId[] fieldOwners)
    {
        _FieldOwners = fieldOwners;
        _BfsQueue = new Field[32];
        _ScopeHits = new Field[8];
    }

    #endregion

    #region Properties

    /// <summary>The packet currently under evaluation.</summary>
    public Packet Packet => _Packet!;

    #endregion

    #region Lifecycle

    /// <summary>Binds a packet with no presence index.</summary>
    public void Bind(Packet packet) => Bind<PacketIndex>(packet, null);

    /// <summary>
    /// Binds a packet for evaluation.
    /// <paramref name="index"/> is only used for whole-packet protocol presence and only when it
    /// was built for the same stack the filter was compiled against; otherwise identifier values
    /// would refer to a different registry.
    /// <para>
    /// Pass <see cref="PacketIndex"/> or <see cref="PacketIndexReaderView"/> as
    /// <typeparamref name="TIndex"/> so the view is not boxed. Storing a struct reader in
    /// <see cref="IPacketIndexReader"/> would allocate; this unwraps known view types to the
    /// live <see cref="PacketIndex"/> class instead.
    /// </para>
    /// </summary>
    public void Bind<TIndex>(Packet packet, TIndex? index)
        where TIndex : IPacketIndexReader
    {
        _Packet = packet;
        Error = null;
        _Domain = packet.RootField();
        _DomainDepth = 0;
        _BfsTop = 0;
        _HitsTop = 0;

        if (index is PacketIndex packetIndex)
        {
            _ConcreteIndex = packetIndex;
            _Index = packetIndex;
            _IndexUsable = ReferenceEquals(packetIndex.Stack, packet.Stack);
            return;
        }

        if (index is PacketIndexReaderView view)
        {
            PacketIndex? source = view.Source;
            _ConcreteIndex = source;
            _Index = source;
            _IndexUsable = source is not null && ReferenceEquals(source.Stack, packet.Stack);
            return;
        }

        if (index is not null)
        {
            // Class mocks (and an already-boxed interface). Unknown struct readers would box here;
            // the only production struct reader is PacketIndexReaderView, handled above.
            _ConcreteIndex = null;
            _Index = index;
            _IndexUsable = ReferenceEquals(index.Stack, packet.Stack);
            return;
        }

        _ConcreteIndex = null;
        _Index = null;
        _IndexUsable = false;
    }

    /// <summary>Clears the packet reference so a finished evaluation does not pin the packet.</summary>
    public void Unbind()
    {
        _Packet = null;
        _ConcreteIndex = null;
        _Index = null;
        _IndexUsable = false;
    }

    /// <summary>Records the first runtime error of the current evaluation.</summary>
    public void SetError(FilterError error)
    {
        if (Error is null)
        {
            Error = error;
        }
    }

    #endregion

    #region Presence

    /// <summary>
    /// Whether the current domain contains any of <paramref name="fields"/>.
    /// Presence is about the field existing, not about it carrying a value, so container fields
    /// with <see cref="FieldType.None"/> count as present.
    /// </summary>
    public bool HasAnyField(FieldId[] fields)
    {
        foreach (FieldId fieldId in fields)
        {
            if (HasAnyContainer(fieldId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the current domain contains the given protocol.
    /// <para>
    /// Whole-packet lookups prefer the presence index, then the protocol's container field, and
    /// only fall back to an owner scan when the protocol registers no conventional container.
    /// Scoped lookups never use the index because it is packet-granular.
    /// </para>
    /// </summary>
    public bool HasProtocol(ProtocolId protocolId, FieldId containerField)
    {
        Packet packet = _Packet!;

        if (_DomainDepth == 0
            && _IndexUsable
            && _TryGetProtocolBitmap(protocolId, out ReadOnlyRoaringBitmap bitmap))
        {
            return bitmap.Contains((uint)packet.Id.Value);
        }

        if (containerField.IsValid)
        {
            return HasAnyContainer(containerField);
        }

        return _ScanOwners(protocolId);
    }

    /// <summary>
    /// Whether the domain contains a field with the given id, regardless of whether it carries a
    /// value. Container fields hold no value, so the value walk cannot see them.
    /// </summary>
    public bool HasAnyContainer(FieldId fieldId)
    {
        if (_DomainDepth == 0)
        {
            return _Packet!.TryGetFieldValue(fieldId, out _, materialize: true);
        }

        return _ScanSubtreeForField(fieldId, materialize: false)
            || (_Packet!.HasUnpopulatedLazyFields && _ScanSubtreeForField(fieldId, materialize: true));
    }

    private bool _ScanSubtreeForField(FieldId fieldId, bool materialize)
    {
        if (_Domain.FieldId == fieldId)
        {
            return true;
        }
        foreach (Field field in _Domain.Descendants(materialize))
        {
            if (field.FieldId == fieldId)
            {
                return true;
            }
        }
        return false;
    }

    private bool _ScanOwners(ProtocolId protocolId)
    {
        if (_DomainDepth == 0)
        {
            return _ScanOwnersFlat(protocolId, materialize: false)
                || (_Packet!.HasUnpopulatedLazyFields && _ScanOwnersFlat(protocolId, materialize: true));
        }

        return _ScanOwnersSubtree(protocolId, materialize: false)
            || (_Packet!.HasUnpopulatedLazyFields && _ScanOwnersSubtree(protocolId, materialize: true));
    }

    private bool _ScanOwnersFlat(ProtocolId protocolId, bool materialize)
    {
        foreach (Field field in _Packet!.IterFieldsFlat(materialize))
        {
            if (OwnerOf(field.FieldId) == protocolId)
            {
                return true;
            }
        }
        return false;
    }

    private bool _ScanOwnersSubtree(ProtocolId protocolId, bool materialize)
    {
        if (OwnerOf(_Domain.FieldId) == protocolId)
        {
            return true;
        }
        foreach (Field field in _Domain.Descendants(materialize))
        {
            if (OwnerOf(field.FieldId) == protocolId)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Owning protocol of a field, or <see cref="ProtocolId.Invalid"/> when unknown.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolId OwnerOf(FieldId fieldId)
    {
        int index = fieldId.Value;
        return (uint)index < (uint)_FieldOwners.Length
            ? _FieldOwners[index]
            : ProtocolId.Invalid;
    }

    #endregion

    #region Value walk

    /// <summary>
    /// Tests every value the accessor produces in the current domain and reports whether any of
    /// them satisfies <paramref name="predicate"/>.
    /// </summary>
    public bool AnyValueMatches<TPredicate>(ValueAccessor accessor, ref TPredicate predicate)
        where TPredicate : struct, IValuePredicate
    {
        if (_DomainDepth == 0)
        {
            return _AnyValueFlat(accessor, ref predicate);
        }

        if (_AnyValueSubtree(accessor, ref predicate, materialize: false))
        {
            return true;
        }

        return _Packet!.HasUnpopulatedLazyFields
            && _AnyValueSubtree(accessor, ref predicate, materialize: true);
    }

    private bool _AnyValueFlat<TPredicate>(ValueAccessor accessor, ref TPredicate predicate)
        where TPredicate : struct, IValuePredicate
    {
        Packet packet = _Packet!;
        foreach (FieldId fieldId in accessor.Fields)
        {
            FieldLookupCookie cookie = FieldLookupCookie.Start;
            while (packet.TryGetNextFieldValue(fieldId, ref cookie, out FieldValue value, materialize: true))
            {
                if (accessor.TryTransform(value.Data, out FieldValueData transformed)
                    && predicate.Test(transformed))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool _AnyValueSubtree<TPredicate>(ValueAccessor accessor, ref TPredicate predicate, bool materialize)
        where TPredicate : struct, IValuePredicate
    {
        if (_TestNode(_Domain, accessor, ref predicate))
        {
            return true;
        }

        foreach (Field field in _Domain.Descendants(materialize))
        {
            if (_TestNode(field, accessor, ref predicate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool _TestNode<TPredicate>(in Field field, ValueAccessor accessor, ref TPredicate predicate)
        where TPredicate : struct, IValuePredicate
    {
        FieldId id = field.FieldId;
        bool wanted = false;
        foreach (FieldId candidate in accessor.Fields)
        {
            if (candidate == id)
            {
                wanted = true;
                break;
            }
        }

        if (!wanted)
        {
            return false;
        }

        FieldValue value = field.Value;
        return accessor.TryTransform(value.Data, out FieldValueData transformed) && predicate.Test(transformed);
    }

    #endregion

    #region Scope search

    /// <summary>
    /// Collects the breadth-first hits of a scope anchor inside the current domain.
    /// <para>
    /// Breadth-first order is what makes <c>$udp[0]</c> mean "the outermost UDP layer": a packet's
    /// field tree nests deeper protocols below shallower ones, so ordering by depth matches the
    /// user's mental model of protocol layers far better than document order would.
    /// </para>
    /// <para>
    /// Hits are appended to a shared stack-like buffer. The returned span is only valid until the
    /// matching <see cref="ReleaseHits"/> call; nested scopes claim the region above it.
    /// </para>
    /// </summary>
    /// <param name="anchorFields">Field ids the anchor may resolve to; empty for a protocol anchor.</param>
    /// <param name="anchorProtocol">Protocol the anchor resolves to, or <see cref="ProtocolId.Invalid"/>.</param>
    /// <param name="limit">Stop after this many hits; <c>0</c> means collect all.</param>
    /// <param name="hitsBase">Receives the base index to pass to <see cref="ReleaseHits"/>.</param>
    /// <returns>The number of hits collected.</returns>
    public int FindAnchors(FieldId[] anchorFields, ProtocolId anchorProtocol, int limit, out int hitsBase)
    {
        hitsBase = _HitsTop;

        // Whole-packet scope: if the presence index proves the protocol is absent, skip BFS.
        if (_DomainDepth == 0
            && anchorProtocol.IsValid
            && _IndexUsable
            && _TryGetProtocolBitmap(anchorProtocol, out ReadOnlyRoaringBitmap bitmap)
            && !bitmap.Contains((uint)_Packet!.Id.Value))
        {
            return 0;
        }

        // Prefer a non-materializing walk; only expand lazy nodes when that finds nothing.
        int collected = _Bfs(anchorFields, anchorProtocol, limit, materialize: false);
        if (collected == 0 && _Packet!.HasUnpopulatedLazyFields)
        {
            collected = _Bfs(anchorFields, anchorProtocol, limit, materialize: true);
        }

        return collected;
    }

    /// <summary>Reads a hit collected by <see cref="FindAnchors"/>.</summary>
    public Field HitAt(int hitsBase, int offset) => _ScopeHits[hitsBase + offset];

    /// <summary>Releases the hit region claimed by <see cref="FindAnchors"/>.</summary>
    public void ReleaseHits(int hitsBase) => _HitsTop = hitsBase;

    private int _Bfs(FieldId[] anchorFields, ProtocolId anchorProtocol, int limit, bool materialize)
    {
        int queueBase = _BfsTop;
        int hitsBase = _HitsTop;
        _PushQueue(_Domain);

        int head = queueBase;
        int collected = 0;
        bool stopAfterHit = limit > 0;
        while (head < _BfsTop)
        {
            Field node = _BfsQueue[head];
            head++;

            if (_IsAnchor(node, anchorFields, anchorProtocol))
            {
                _PushHit(node);
                collected++;
                if (stopAfterHit && collected >= limit)
                {
                    // Do not expand this hit (or later queue entries) — [i] only needs i+1 hits.
                    break;
                }
            }

            foreach (Field child in node.Children(materialize))
            {
                _PushQueue(child);
            }
        }

        _BfsTop = queueBase;
        if (collected == 0)
        {
            _HitsTop = hitsBase;
        }
        return collected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _IsAnchor(in Field node, FieldId[] anchorFields, ProtocolId anchorProtocol)
    {
        FieldId id = node.FieldId;

        if (anchorFields.Length == 1)
        {
            return anchorFields[0] == id;
        }

        if (anchorFields.Length != 0)
        {
            foreach (FieldId candidate in anchorFields)
            {
                if (candidate == id)
                {
                    return true;
                }
            }
            return false;
        }

        return anchorProtocol.IsValid && OwnerOf(id) == anchorProtocol;
    }

    private void _PushQueue(in Field field)
    {
        if (_BfsTop == _BfsQueue.Length)
        {
            Array.Resize(ref _BfsQueue, _BfsQueue.Length * 2);
        }
        _BfsQueue[_BfsTop++] = field;
    }

    private void _PushHit(in Field field)
    {
        if (_HitsTop == _ScopeHits.Length)
        {
            Array.Resize(ref _ScopeHits, _ScopeHits.Length * 2);
        }
        _ScopeHits[_HitsTop++] = field;
    }

    #endregion

    #region Index helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap)
    {
        if (_ConcreteIndex is not null)
        {
            return _ConcreteIndex.TryGetProtocolBitmap(protocolId, out bitmap);
        }

        return _Index!.TryGetProtocolBitmap(protocolId, out bitmap);
    }

    #endregion

    #region Domain

    /// <summary>Narrows the domain to a subtree and returns the previous domain for restoration.</summary>
    public Field PushDomain(in Field domain)
    {
        Field previous = _Domain;
        _Domain = domain;
        _DomainDepth++;
        return previous;
    }

    /// <summary>Restores a domain saved by <see cref="PushDomain"/>.</summary>
    public void PopDomain(in Field previous)
    {
        _Domain = previous;
        _DomainDepth--;
    }

    #endregion
}
