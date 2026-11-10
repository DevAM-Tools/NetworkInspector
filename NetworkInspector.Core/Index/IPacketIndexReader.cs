// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Read-only view of a <see cref="PacketIndex"/>. Exposes only query and
/// lookup operations — mutation methods (<c>BeginPacket</c>, <c>EndPacket</c>,
/// <c>RecordGroupPresence</c>, <c>RecordProtocolPresence</c>) are not accessible
/// through this interface.
///
/// <para>
/// <b>Boxing:</b> <see cref="PacketIndex"/> is a class — storing it as this interface does
/// not allocate. <see cref="PacketIndexReaderView"/> is a struct that implements this
/// interface; casting the view, storing it in an <see cref="IPacketIndexReader"/> field, or
/// passing it to a non-generic parameter of this type boxes. Hot-path APIs must take
/// <c>TIndex where TIndex : IPacketIndexReader</c> (or the concrete <see cref="PacketIndex"/>)
/// instead.
/// </para>
///
/// <para>
/// <b>Thread-safety:</b> Views returned by this interface alias the live index bitmaps.
/// A reader may keep a view and query it while the index continues to grow; newly committed
/// packet IDs become visible on that same view. Materializing a copy via
/// <see cref="ReadOnlyRoaringBitmap.ToBitmap"/> or combining bitmaps with set operations
/// is appropriate only when the index is no longer growing.
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
    #region Index shape

    /// <summary>Number of index groups tracked; valid <see cref="IndexGroupId"/> values are below this count.</summary>
    int GroupCount
    {
        get;
    }

    /// <summary>Number of protocols tracked; valid <see cref="ProtocolId"/> values are below this count.</summary>
    int ProtocolCount
    {
        get;
    }

    /// <summary>The stack this index was created for; the only source of valid ids for the lookups below.</summary>
    Stack Stack
    {
        get;
    }

    /// <summary>Creates a presence query builder.</summary>
    /// <returns>A new query bound to this index; it holds no result until the caller adds terms.</returns>
    PresenceQuery Query();

    #endregion

    #region Lookups that require a valid id

    /// <summary>
    /// Gets the bitmap of packets containing a specific index group.
    /// The caller must supply an <paramref name="groupId"/> obtained from this
    /// index's own <see cref="Stack"/>; otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// Use <see cref="TryGetGroupBitmap"/> when the ID may be invalid.
    /// </summary>
    /// <param name="groupId">An index group id of this index's own <see cref="Stack"/>.</param>
    /// <returns>The live bitmap of the packets that hold the group; it grows with the index.</returns>
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
    /// <param name="protocolId">A protocol id of this index's own <see cref="Stack"/>.</param>
    /// <returns>The live bitmap of the packets that hold the protocol; it grows with the index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    ReadOnlyRoaringBitmap GetProtocolBitmap(ProtocolId protocolId);

    /// <summary>
    /// Gets the bitmap of packets containing a specific field by resolving
    /// the field's index group via the stack metadata.
    /// Returns <see cref="ReadOnlyRoaringBitmap.Empty"/> if the field is in range but has no index group.
    /// Use <see cref="TryGetFieldBitmap"/> when the ID may be invalid.
    /// </summary>
    /// <param name="fieldId">A field id of this index's own <see cref="Stack"/>.</param>
    /// <returns>
    /// The live bitmap of the packets that hold the field, or <see cref="ReadOnlyRoaringBitmap.Empty"/> when the field has no
    /// index group.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="fieldId"/> is out of range for this index's <see cref="Stack"/>.
    /// </exception>
    ReadOnlyRoaringBitmap GetFieldBitmap(FieldId fieldId);

    /// <summary>
    /// Counts packets containing a specific index group.
    /// The caller must supply an <paramref name="groupId"/> obtained from this
    /// index's own <see cref="Stack"/>; otherwise throws
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// Use <see cref="TryGroupCardinality"/> when the ID may be invalid.
    /// </summary>
    /// <param name="groupId">An index group id of this index's own <see cref="Stack"/>.</param>
    /// <returns>The number of packets committed so far that hold the group.</returns>
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
    /// <param name="protocolId">A protocol id of this index's own <see cref="Stack"/>.</param>
    /// <returns>The number of packets committed so far that hold the protocol.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="protocolId"/> is out of range for this index.
    /// </exception>
    long ProtocolCardinality(ProtocolId protocolId);

    #endregion

    #region Lookups that report an invalid id

    /// <summary>
    /// Attempts to retrieve the bitmap of packets containing the specified index group.
    /// Returns <see langword="false"/> when <paramref name="groupId"/> is out of range
    /// for this index. Never throws for invalid IDs. On failure <paramref name="bitmap"/>
    /// is set to <see cref="ReadOnlyRoaringBitmap.Empty"/>.
    /// </summary>
    /// <param name="groupId">The index group id to look up; any value, including one of another stack.</param>
    /// <param name="bitmap">The live bitmap of the group on success; <see cref="ReadOnlyRoaringBitmap.Empty"/> on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="groupId"/> is in range for this index.</returns>
    bool TryGetGroupBitmap(IndexGroupId groupId, out ReadOnlyRoaringBitmap bitmap);

    /// <summary>
    /// Attempts to retrieve the bitmap of packets containing the specified protocol.
    /// Returns <see langword="false"/> when <paramref name="protocolId"/> is out of range
    /// for this index. Never throws for invalid IDs. On failure <paramref name="bitmap"/>
    /// is set to <see cref="ReadOnlyRoaringBitmap.Empty"/>.
    /// </summary>
    /// <param name="protocolId">The protocol id to look up; any value, including one of another stack.</param>
    /// <param name="bitmap">The live bitmap of the protocol on success; <see cref="ReadOnlyRoaringBitmap.Empty"/> on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="protocolId"/> is in range for this index.</returns>
    bool TryGetProtocolBitmap(ProtocolId protocolId, out ReadOnlyRoaringBitmap bitmap);

    /// <summary>
    /// Attempts to retrieve the bitmap of packets containing the specified field, resolved
    /// via its index group.
    /// Returns <see langword="false"/> when <paramref name="fieldId"/> is out of range,
    /// or when the field has no index group (unlike <see cref="GetFieldBitmap"/>, which returns an empty bitmap with success
    /// semantics for a field without a group). Never throws for invalid IDs. On failure <paramref name="bitmap"/> is set to
    /// <see cref="ReadOnlyRoaringBitmap.Empty"/>.
    /// </summary>
    /// <param name="fieldId">The field id to look up; any value, including one of another stack.</param>
    /// <param name="bitmap">The live bitmap of the field's group on success; <see cref="ReadOnlyRoaringBitmap.Empty"/> on failure.</param>
    /// <returns><see langword="true"/> when the field is in range and has an index group.</returns>
    bool TryGetFieldBitmap(FieldId fieldId, out ReadOnlyRoaringBitmap bitmap);

    /// <summary>
    /// Attempts to count packets containing the specified index group.
    /// Returns <see langword="false"/> when <paramref name="groupId"/> is out of range.
    /// Never throws for invalid IDs. <paramref name="cardinality"/> is 0 on failure.
    /// </summary>
    /// <param name="groupId">The index group id to count; any value, including one of another stack.</param>
    /// <param name="cardinality">The number of packets that hold the group on success; 0 on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="groupId"/> is in range for this index.</returns>
    bool TryGroupCardinality(IndexGroupId groupId, out long cardinality);

    /// <summary>
    /// Attempts to count packets containing the specified protocol.
    /// Returns <see langword="false"/> when <paramref name="protocolId"/> is out of range.
    /// Never throws for invalid IDs. <paramref name="cardinality"/> is 0 on failure.
    /// </summary>
    /// <param name="protocolId">The protocol id to count; any value, including one of another stack.</param>
    /// <param name="cardinality">The number of packets that hold the protocol on success; 0 on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="protocolId"/> is in range for this index.</returns>
    bool TryProtocolCardinality(ProtocolId protocolId, out long cardinality);

    #endregion
}
