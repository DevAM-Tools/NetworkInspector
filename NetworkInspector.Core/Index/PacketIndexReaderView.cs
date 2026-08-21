// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Zero-allocation read-only view over a <see cref="PacketIndex"/>.
/// <para>
/// When the compile-time type is this struct, bitmap lookups can inline to the owner.
/// Consume it through generic methods constrained to <see cref="IPacketIndexReader"/>
/// (for example <c>where TIndex : IPacketIndexReader</c>) so the JIT emits
/// <c>constrained.callvirt</c> and does not box.
/// </para>
/// <para>
/// Warning: do not cast this struct to <see cref="IPacketIndexReader"/>, store it in an
/// <see cref="IPacketIndexReader"/> local/field, or pass it to a parameter of that
/// interface type. Those conversions box. Pass <see cref="PacketIndex"/> or this
/// struct as the generic type argument instead. <see cref="PacketIndex"/> is a class
/// and can be used as the interface without boxing.
/// </para>
/// </summary>
public readonly struct PacketIndexReaderView : IPacketIndexReader
{
    #region Fields

    private readonly PacketIndex _Index;

    #endregion

    #region Lifecycle

    /// <summary>Creates a view over <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is <see langword="null"/>.</exception>
    public PacketIndexReaderView(PacketIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _Index = index;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The live index this view aliases, or <see langword="null"/> when the view is
    /// <c>default</c> (not constructed via <see cref="PacketIndexReaderView(PacketIndex)"/>).
    /// </summary>
    public PacketIndex? Source => _Index;

    /// <inheritdoc/>
    public int GroupCount => _Index.GroupCount;

    /// <inheritdoc/>
    public int ProtocolCount => _Index.ProtocolCount;

    /// <inheritdoc/>
    public Stack Stack => _Index.Stack;

    #endregion

    #region Methods

    /// <inheritdoc/>
    public ReadOnlyRoaringBitmap GetGroupBitmap(IndexGroupId groupId) =>
        _Index.GetGroupBitmap(groupId);

    /// <inheritdoc/>
    public ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId) =>
        _Index.GetProtocolBitmap(protocolId);

    /// <inheritdoc/>
    public ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId) =>
        _Index.GetFieldBitmap(fieldId);

    /// <inheritdoc/>
    public long GroupCardinality(IndexGroupId groupId) =>
        _Index.GroupCardinality(groupId);

    /// <inheritdoc/>
    public long ProtocolCardinality(ProtocolId protocolId) =>
        _Index.ProtocolCardinality(protocolId);

    /// <inheritdoc/>
    public PresenceQuery Query() => _Index.Query();

    /// <inheritdoc/>
    public bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap) =>
        _Index.TryGetGroupBitmap(groupId, out bitmap);

    /// <inheritdoc/>
    public bool TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap) =>
        _Index.TryGetProtocolBitmap(protocolId, out bitmap);

    /// <inheritdoc/>
    public bool TryGetFieldBitmap(FieldId fieldId, out ReadOnlyRoaringBitmap bitmap) =>
        _Index.TryGetFieldBitmap(fieldId, out bitmap);

    /// <inheritdoc/>
    public bool TryGroupCardinality(IndexGroupId groupId, out long cardinality) =>
        _Index.TryGroupCardinality(groupId, out cardinality);

    /// <inheritdoc/>
    public bool TryProtocolCardinality(ProtocolId protocolId, out long cardinality) =>
        _Index.TryProtocolCardinality(protocolId, out cardinality);

    #endregion
}
