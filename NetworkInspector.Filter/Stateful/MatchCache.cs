// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Stateful;

/// <summary>
/// Remembers the verdict of every packet a filter instance has already evaluated.
/// <para>
/// Two bitmaps are needed rather than one: <c>evaluated</c> records that a decision exists and
/// <c>matched</c> records what it was. A single bitmap could not distinguish "evaluated to false"
/// from "not evaluated yet", and that distinction is what lets a stateful filter answer repeated
/// queries for an already-seen packet without replaying its state machine.
/// </para>
/// <para>
/// Roaring bitmaps keep this compact even for very large captures, and lookups are O(1) for the
/// common dense case.
/// </para>
/// </summary>
internal sealed class MatchCache
{
    #region Fields

    private RoaringBitmap _Evaluated = new();
    private RoaringBitmap _Matched = new();

    #endregion

    #region Properties

    /// <summary>Number of packets with a cached verdict.</summary>
    public long EvaluatedCount => _Evaluated.Cardinality;

    /// <summary>Packets that matched so far.</summary>
    public ReadOnlyRoaringBitmap Matched => _Matched.AsReadOnly();

    /// <summary>Packets that have been evaluated so far.</summary>
    public ReadOnlyRoaringBitmap Evaluated => _Evaluated.AsReadOnly();

    #endregion

    #region Access

    /// <summary>Reads a cached verdict.</summary>
    public bool TryGet(PacketId packetId, out bool matched)
    {
        int value = packetId.Value;
        if (value < 0 || !_Evaluated.Contains((uint)value))
        {
            matched = false;
            return false;
        }

        matched = _Matched.Contains((uint)value);
        return true;
    }

    /// <summary>Stores a verdict.</summary>
    public void Store(PacketId packetId, bool matched)
    {
        int value = packetId.Value;
        if (value < 0)
        {
            return;
        }

        _Evaluated.Add((uint)value);
        if (matched)
        {
            _Matched.Add((uint)value);
        }
    }

    /// <summary>Forgets every cached verdict.</summary>
    public void Clear()
    {
        _Evaluated = new();
        _Matched = new();
    }

    #endregion
}
