// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Helpers;

/// <summary>
/// A class reader that answers from <paramref name="first"/> or <paramref name="second"/> depending on
/// <see cref="UseSecond"/>. Lets a test change a reader's answers between two queries, which a cache for foreign reader
/// types would hide.
/// </summary>
internal sealed class SwitchingPacketIndexReader(IPacketIndexReader first, IPacketIndexReader second) : IPacketIndexReader
{
    #region Properties

    /// <summary>When true, every member answers from the second reader.</summary>
    public bool UseSecond { get; set; }

    private IPacketIndexReader _Current => UseSecond
        ? second
        : first;

    /// <inheritdoc />
    public int GroupCount => _Current.GroupCount;

    /// <inheritdoc />
    public int ProtocolCount => _Current.ProtocolCount;

    /// <inheritdoc />
    public Stack Stack => _Current.Stack;

    #endregion

    #region Methods

    /// <inheritdoc />
    public ReadOnlyRoaringBitmap GetGroupBitmap(IndexGroupId groupId) => _Current.GetGroupBitmap(groupId);

    /// <inheritdoc />
    public ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId) => _Current.GetProtocolBitmap(protocolId);

    /// <inheritdoc />
    public ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId) => _Current.GetFieldBitmap(fieldId);

    /// <inheritdoc />
    public long GroupCardinality(IndexGroupId groupId) => _Current.GroupCardinality(groupId);

    /// <inheritdoc />
    public long ProtocolCardinality(ProtocolId protocolId) => _Current.ProtocolCardinality(protocolId);

    /// <inheritdoc />
    public PresenceQuery Query() => _Current.Query();

    /// <inheritdoc />
    public bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap) =>
        _Current.TryGetGroupBitmap(groupId, out bitmap);

    /// <inheritdoc />
    public bool TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap) =>
        _Current.TryGetProtocolBitmap(protocolId, out bitmap);

    /// <inheritdoc />
    public bool TryGetFieldBitmap(FieldId fieldId, out ReadOnlyRoaringBitmap bitmap) =>
        _Current.TryGetFieldBitmap(fieldId, out bitmap);

    /// <inheritdoc />
    public bool TryGroupCardinality(IndexGroupId groupId, out long cardinality) =>
        _Current.TryGroupCardinality(groupId, out cardinality);

    /// <inheritdoc />
    public bool TryProtocolCardinality(ProtocolId protocolId, out long cardinality) =>
        _Current.TryProtocolCardinality(protocolId, out cardinality);

    #endregion
}
