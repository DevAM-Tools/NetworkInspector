// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Read-only view of a <see cref="PacketIndex"/>. Exposes only query and
/// lookup operations — mutation methods (<c>BeginPacket</c>, <c>EndPacket</c>,
/// <c>RecordGroupPresence</c>, <c>RecordProtocolPresence</c>) are not accessible
/// through this interface.
///
/// <para>
/// Intended for external consumers (listeners, tools, UI) that need to query
/// protocol/field presence across packets without risking accidental modification
/// of the index state.
/// </para>
///
/// <para>
/// <b>Error handling:</b>
/// Methods without the <c>Try</c> prefix require a valid ID that was obtained
/// from this index's own <see cref="Stack"/> (i.e. the value is within bounds).
/// Passing an ID from a different stack or an out-of-range value will throw
/// <see cref="ArgumentOutOfRangeException"/>. Use the <c>Try</c> variants when
/// the ID may be invalid or from an external source.
/// </para>
/// </summary>
public interface IPacketIndexReader
{
    #region Properties

    /// <summary>Number of index groups tracked.</summary>
    int GroupCount
    {
        get;
    }

    /// <summary>Number of protocols tracked.</summary>
    int ProtocolCount
    {
        get;
    }

    /// <summary>The stack this index was created for.</summary>
    Stack Stack
    {
        get;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Gets the bitmap of packets containing a specific index group.
    /// The caller must supply an <paramref name="groupId"/> obtained from this
    /// index's own <see cref="Stack"/>; otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// Use <see cref="TryGetGroupBitmap"/> when the ID may be invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="groupId"/> is out of range for this index.
    /// </exception>
    ReadOnlyRoaringBitmap GetGroupBitmap(IndexGroupId groupId);

    /// <summary>
    /// Gets the bitmap of packets containing a specific protocol.
    /// The caller must supply a <paramref name="protocolId"/> obtained from this
    /// index's own <see cref="Stack"/>; otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// Use <see cref="TryGetProtocolBitmap"/> when the ID may be invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId);

    /// <summary>
    /// Gets the bitmap of packets containing a specific field by resolving
    /// the field's index group via the stack metadata.
    /// Returns <see cref="ReadOnlyRoaringBitmap.Empty"/> if the field has no index group.
    /// Use <see cref="TryGetFieldBitmap"/> when the ID may be invalid.
    /// </summary>
    ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId);

    /// <summary>
    /// Counts packets containing a specific index group.
    /// The caller must supply an <paramref name="groupId"/> obtained from this
    /// index's own <see cref="Stack"/>; otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// Use <see cref="TryGroupCardinality"/> when the ID may be invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="groupId"/> is out of range for this index.
    /// </exception>
    long GroupCardinality(IndexGroupId groupId);

    /// <summary>
    /// Counts packets containing a specific protocol.
    /// The caller must supply a <paramref name="protocolId"/> obtained from this
    /// index's own <see cref="Stack"/>; otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// Use <see cref="TryProtocolCardinality"/> when the ID may be invalid.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    long ProtocolCardinality(ProtocolId protocolId);

    /// <summary>Creates a presence query builder.</summary>
    PresenceQuery Query();

    /// <summary>
    /// Attempts to retrieve the bitmap of packets containing the specified index group.
    /// Returns <see langword="false"/> when <paramref name="groupId"/> is out of range
    /// for this index. Never throws for invalid IDs. On failure <paramref name="bitmap"/>
    /// is set to <see cref="ReadOnlyRoaringBitmap.Empty"/>.
    /// </summary>
    bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap);

    /// <summary>
    /// Attempts to retrieve the bitmap of packets containing the specified protocol.
    /// Returns <see langword="false"/> when <paramref name="protocolId"/> is out of range
    /// for this index. Never throws for invalid IDs. On failure <paramref name="bitmap"/>
    /// is set to <see cref="ReadOnlyRoaringBitmap.Empty"/>.
    /// </summary>
    bool TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap);

    /// <summary>
    /// Attempts to retrieve the bitmap of packets containing the specified field, resolved
    /// via its index group.
    /// Returns <see langword="false"/> when <paramref name="fieldId"/> is out of range,
    /// or when the field has no index group. Never throws for invalid IDs.
    /// </summary>
    bool TryGetFieldBitmap(FieldId fieldId, out ReadOnlyRoaringBitmap bitmap);

    /// <summary>
    /// Attempts to count packets containing the specified index group.
    /// Returns <see langword="false"/> when <paramref name="groupId"/> is out of range.
    /// Never throws for invalid IDs.
    /// </summary>
    bool TryGroupCardinality(IndexGroupId groupId, out long cardinality);

    /// <summary>
    /// Attempts to count packets containing the specified protocol.
    /// Returns <see langword="false"/> when <paramref name="protocolId"/> is out of range.
    /// Never throws for invalid IDs.
    /// </summary>
    bool TryProtocolCardinality(ProtocolId protocolId, out long cardinality);

    /// <summary>
    /// Read-only view of cached field values. Returns <see langword="null"/>
    /// if no value cache has been configured or built.
    /// </summary>
    IValueCacheReader? ValueCache
    {
        get;
    }

    #endregion
}
