// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System.Collections.Generic;

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Key for identifying a SOME/IP-TP reassembly session.
/// A session is uniquely identified by the tuple (ServiceId, MethodId, ClientId, SessionId).
/// </summary>
internal readonly struct SomeIpTpReassemblyKey : IEquatable<SomeIpTpReassemblyKey>
{
    /// <summary>SOME/IP Service ID.</summary>
    internal ushort ServiceId
    {
        get;
    }

    /// <summary>SOME/IP Method ID.</summary>
    internal ushort MethodId
    {
        get;
    }

    /// <summary>SOME/IP Client ID.</summary>
    internal ushort ClientId
    {
        get;
    }

    /// <summary>SOME/IP Session ID.</summary>
    internal ushort SessionId
    {
        get;
    }

    /// <summary>Creates a new reassembly key.</summary>
    internal SomeIpTpReassemblyKey(ushort serviceId, ushort methodId, ushort clientId, ushort sessionId)
    {
        ServiceId = serviceId;
        MethodId = methodId;
        ClientId = clientId;
        SessionId = sessionId;
    }

    /// <inheritdoc/>
    public bool Equals(SomeIpTpReassemblyKey other) =>
        ServiceId == other.ServiceId && MethodId == other.MethodId &&
        ClientId == other.ClientId && SessionId == other.SessionId;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SomeIpTpReassemblyKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ServiceId, MethodId, ClientId, SessionId);
}

/// <summary>
/// A single fragment in a SOME/IP-TP reassembly session.
/// </summary>
internal readonly struct SomeIpTpFragment
{
    /// <summary>Byte offset where this fragment starts in the reassembled message.</summary>
    internal uint Offset
    {
        get;
    } // bytes

    /// <summary>Fragment payload data.</summary>
    internal byte[] Data
    {
        get;
    }

    /// <summary>Creates a new fragment.</summary>
    internal SomeIpTpFragment(uint offset, byte[] data)
    {
        Offset = offset;
        Data = data;
    }
}

/// <summary>
/// Tracks SOME/IP-TP fragment reassembly sessions.
/// Segments are identified by (ServiceID, MethodID, ClientID, SessionID) and
/// reassembled based on byte offsets from the TP header.
/// </summary>
internal sealed class SomeIpTpReassembler
{
    /// <summary>Maximum number of concurrent reassembly sessions.</summary>
    private const int MaxSessions = 1024;

    /// <summary>Maximum total reassembled message size (1 MiB).</summary>
    private const int MaxReassembledSize = 1024 * 1024; // bytes

    /// <summary>Active reassembly sessions.</summary>
    private readonly Dictionary<SomeIpTpReassemblyKey, ReassemblyState> _Sessions = new();

    /// <summary>
    /// Adds a TP segment for reassembly.
    /// Returns the fully reassembled payload when all fragments have been received,
    /// or null if more fragments are still needed.
    /// </summary>
    /// <param name="key">Reassembly session identifier.</param>
    /// <param name="byteOffset">Byte offset from the TP header.</param>
    /// <param name="data">Fragment payload data.</param>
    /// <param name="moreSegments">True if more segments follow.</param>
    /// <returns>Reassembled payload or null if incomplete.</returns>
    internal byte[]? AddSegment(in SomeIpTpReassemblyKey key, uint byteOffset, ReadOnlySpan<byte> data, bool moreSegments)
    {
        // Enforce session limit — reject new sessions when at capacity
        if (!_Sessions.ContainsKey(key) && _Sessions.Count >= MaxSessions)
        {
            return null;
        }

        if (!_Sessions.TryGetValue(key, out ReassemblyState? state))
        {
            state = new ReassemblyState();
            _Sessions[key] = state;
        }

        if (state.AddFragment(byteOffset, data, moreSegments))
        {
            // Reassembly complete — extract the assembled payload and remove session
            byte[] assembled = state.Assemble();
            _Sessions.Remove(key);
            return assembled;
        }

        return null;
    }

    /// <summary>Returns the number of active reassembly sessions.</summary>
    internal int ActiveSessionCount => _Sessions.Count;

    /// <summary>Clears all reassembly sessions.</summary>
    internal void Clear() => _Sessions.Clear();

    /// <summary>
    /// Internal state for a single reassembly session. Collects fragments,
    /// detects gaps, and assembles the complete message.
    /// </summary>
    private sealed class ReassemblyState
    {
        /// <summary>Collected fragments, sorted by offset after each addition.</summary>
        private readonly List<SomeIpTpFragment> _Fragments = [];

        /// <summary>Total expected size (known once the last segment arrives).</summary>
        private uint? _TotalSize; // bytes

        /// <summary>Running count of total data bytes received.</summary>
        private uint _ReceivedBytes; // bytes

        /// <summary>
        /// Adds a fragment. Returns true if the message is now complete.
        /// </summary>
        internal bool AddFragment(uint offset, ReadOnlySpan<byte> data, bool moreSegments)
        {
            uint fragLen = (uint)data.Length;

            // Record total size when we see the last segment
            if (!moreSegments)
            {
                _TotalSize = offset + fragLen;
            }

            // Overflow protection — reject if would exceed size limit
            if (_ReceivedBytes + fragLen > MaxReassembledSize)
            {
                return false;
            }

            _ReceivedBytes += fragLen;
            _Fragments.Add(new SomeIpTpFragment(offset, data.ToArray()));

            // Sort fragments by offset for gap detection
            _Fragments.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            return IsComplete();
        }

        /// <summary>
        /// Checks if all fragments cover the total size without gaps.
        /// </summary>
        private bool IsComplete()
        {
            if (_TotalSize is not uint total)
            {
                return false;
            }

            // Walk through sorted fragments checking for gaps
            uint covered = 0; // bytes
            foreach (SomeIpTpFragment frag in _Fragments)
            {
                if (frag.Offset > covered)
                {
                    return false; // Gap detected
                }

                // Handle overlapping fragments
                uint fragEnd = frag.Offset + (uint)frag.Data.Length;
                if (fragEnd > covered)
                {
                    covered = fragEnd;
                }
            }

            return covered >= total;
        }

        /// <summary>
        /// Assembles all fragments into a single contiguous buffer.
        /// Must only be called when IsComplete() returns true.
        /// </summary>
        internal byte[] Assemble()
        {
            int total = (int)(_TotalSize ?? 0);
            byte[] buffer = new byte[total];

            foreach (SomeIpTpFragment frag in _Fragments)
            {
                int start = (int)frag.Offset;
                int copyLen = Math.Min(frag.Data.Length, total - start);
                if (copyLen > 0)
                {
                    frag.Data.AsSpan(0, copyLen).CopyTo(buffer.AsSpan(start, copyLen));
                }
            }

            return buffer;
        }
    }
}
