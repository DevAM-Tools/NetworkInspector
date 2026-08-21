// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Helpers;

/// <summary>
/// Wraps a real <see cref="PacketIndex"/> but reports one protocol and one field as untracked.
/// <see cref="IPacketIndexReader"/> allows an implementation to cover only part of a stack, and
/// candidate pruning must fall back to a full scan whenever a required bitmap is unavailable.
/// </summary>
internal sealed class PartialPacketIndexReader(
    PacketIndex inner,
    ProtocolId hiddenProtocol,
    FieldId hiddenField) : IPacketIndexReader
{
    #region Fields

    private readonly PacketIndex _Inner = inner;
    private readonly ProtocolId _HiddenProtocol = hiddenProtocol;
    private readonly FieldId _HiddenField = hiddenField;

    #endregion

    #region Properties

    /// <inheritdoc />
    public int GroupCount => _Inner.GroupCount;

    /// <inheritdoc />
    public int ProtocolCount => _Inner.ProtocolCount;

    /// <inheritdoc />
    public Stack Stack => _Inner.Stack;

    #endregion

    #region Methods

    /// <inheritdoc />
    public ReadOnlyRoaringBitmap GetGroupBitmap(IndexGroupId groupId) => _Inner.GetGroupBitmap(groupId);

    /// <inheritdoc />
    public ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId) => _Inner.GetProtocolBitmap(protocolId);

    /// <inheritdoc />
    public ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId) => _Inner.GetFieldBitmap(fieldId);

    /// <inheritdoc />
    public long GroupCardinality(IndexGroupId groupId) => _Inner.GroupCardinality(groupId);

    /// <inheritdoc />
    public long ProtocolCardinality(ProtocolId protocolId) => _Inner.ProtocolCardinality(protocolId);

    /// <inheritdoc />
    public PresenceQuery Query() => _Inner.Query();

    /// <inheritdoc />
    public bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap) =>
        _Inner.TryGetGroupBitmap(groupId, out bitmap);

    /// <inheritdoc />
    public bool TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap)
    {
        if (protocolId == _HiddenProtocol)
        {
            bitmap = ReadOnlyRoaringBitmap.Empty;
            return false;
        }
        return _Inner.TryGetProtocolBitmap(protocolId, out bitmap);
    }

    /// <inheritdoc />
    public bool TryGetFieldBitmap(FieldId fieldId, out ReadOnlyRoaringBitmap bitmap)
    {
        if (fieldId == _HiddenField)
        {
            bitmap = ReadOnlyRoaringBitmap.Empty;
            return false;
        }
        return _Inner.TryGetFieldBitmap(fieldId, out bitmap);
    }

    /// <inheritdoc />
    public bool TryGroupCardinality(IndexGroupId groupId, out long cardinality) =>
        _Inner.TryGroupCardinality(groupId, out cardinality);

    /// <inheritdoc />
    public bool TryProtocolCardinality(ProtocolId protocolId, out long cardinality) =>
        _Inner.TryProtocolCardinality(protocolId, out cardinality);

    #endregion
}
