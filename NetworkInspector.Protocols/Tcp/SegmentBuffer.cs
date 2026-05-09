// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

using System.Buffers;

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// State machine for <see cref="SegmentBuffer"/>.
/// </summary>
internal enum SegmentBufferState
{
    /// <summary>No PDU boundary detector assigned yet.</summary>
    Initial,

    /// <summary>Synchronized — actively detecting and extracting PDUs.</summary>
    Synchronized,

    /// <summary>Last detection returned Invalid — attempting resync.</summary>
    Resyncing,

    /// <summary>Unrecoverable error — no more PDUs can be extracted.</summary>
    Error
}

/// <summary>
/// Per-direction segment buffer for TCP stream reassembly.
/// Stores received segments and extracts complete PDUs using boundary detection.
/// <para>
/// <b>Zero-copy design:</b> Segments are stored as <see cref="ReadOnlyMemory{T}"/> slices
/// referencing the original packet data. Data is only copied when extracting PDUs that
/// span multiple segments or when <see cref="StreamReassemblyConfig.CopySegments"/> is set.
/// </para>
/// <para>
/// <b>State machine:</b> Initial → Synchronized (on first append) → Resyncing (on invalid data)
/// → Error (on unrecoverable failure or buffer overflow).
/// </para>
/// </summary>
internal sealed class SegmentBuffer
{
    // Segments stored as ReadOnlyMemory<byte> slices (zero-copy from packet data)
    private readonly List<ReadOnlyMemory<byte>> _Segments = [];
    private int _TotalLength;       // sum of segment lengths in bytes
    private int _TotalConsumed;     // bytes successfully extracted as PDUs
    private int _TotalDiscarded;    // bytes skipped during resync

    private SegmentBufferState _State = SegmentBufferState.Initial;

    // Detector and heuristic from configuration
    private readonly IPduBoundaryDetector? _Detector;
    private readonly IResyncHeuristic? _ResyncHeuristic;
    private readonly int _MaxBufferSize;
    private readonly int _MaxPduSize;
    private readonly bool _CopySegments;

    /// <summary>Current buffer state.</summary>
    internal SegmentBufferState State => _State;

    /// <summary>Total bytes currently buffered across all segments.</summary>
    internal int TotalLength => _TotalLength;

    /// <summary>Total bytes successfully consumed as complete PDUs.</summary>
    internal int TotalConsumed => _TotalConsumed;

    /// <summary>Total bytes discarded during resynchronization.</summary>
    internal int TotalDiscarded => _TotalDiscarded;

    /// <summary>Creates a new segment buffer from a reassembly configuration.</summary>
    internal SegmentBuffer(StreamReassemblyConfig config)
    {
        _Detector = config.BoundaryDetector;
        _ResyncHeuristic = config.ResyncHeuristic;
        _MaxBufferSize = config.MaxBufferSize;
        _MaxPduSize = config.MaxPduSize;
        _CopySegments = config.CopySegments;
    }

    /// <summary>Appends a new segment to the buffer (zero-copy unless CopySegments is true).</summary>
    /// <returns><see langword="true"/> if the segment was accepted; <see langword="false"/> if buffer overflow occurred.</returns>
    internal bool AppendSegment(ReadOnlyMemory<byte> segment)
    {
        if (_State == SegmentBufferState.Error)
        {
            return false;
        }

        // Transition from Initial to Synchronized on first data
        if (_State == SegmentBufferState.Initial)
        {
            _State = _Detector != null ? SegmentBufferState.Synchronized : SegmentBufferState.Error;
            if (_State == SegmentBufferState.Error)
            {
                return false;
            }
        }

        // Check buffer overflow
        if (_TotalLength + segment.Length > _MaxBufferSize)
        {
            _State = SegmentBufferState.Error;
            return false;
        }

        // Copy segment data if configured (live-capture with recyclable buffers)
        if (_CopySegments)
        {
            byte[] copy = new byte[segment.Length];
            segment.Span.CopyTo(copy);
            segment = copy;
        }

        _Segments.Add(segment);
        _TotalLength += segment.Length;
        return true;
    }

    /// <summary>
    /// Tries to extract the next complete PDU from the buffered data.
    /// </summary>
    /// <param name="context">Stream detection context for context-aware detectors.</param>
    /// <param name="pdu">The extracted PDU data on success.</param>
    /// <returns><see langword="true"/> if a complete PDU was extracted.</returns>
    internal bool TryExtractPdu(in StreamDetectionContext context, out ReadOnlyMemory<byte> pdu)
    {
        pdu = default;

        if (_State != SegmentBufferState.Synchronized || _Detector == null || _TotalLength == 0)
        {
            return false;
        }

        // Materialize a contiguous view of the buffered data
        PduBoundaryResult result = DetectWithMaterializedView(context);

        if (result.IsComplete)
        {
            int pduLength = result.Length;
            if (pduLength > _MaxPduSize)
            {
                // PDU too large — enter error state
                _State = SegmentBufferState.Error;
                return false;
            }

            pdu = ExtractBytes(pduLength);
            _TotalConsumed += pduLength;
            return true;
        }

        if (result.IsInvalid)
        {
            _State = SegmentBufferState.Resyncing;
            TryResync(context);
        }

        // IsIncomplete or failed resync — wait for more data
        return false;
    }

    /// <summary>Clears all buffered segments and resets counters.</summary>
    internal void Clear()
    {
        _Segments.Clear();
        _TotalLength = 0;
        _TotalConsumed = 0;
        _TotalDiscarded = 0;
        _State = _Detector != null ? SegmentBufferState.Initial : SegmentBufferState.Error;
    }

    /// <summary>
    /// Invokes the boundary detector on a materialized contiguous view of all segments.
    /// Uses direct span access for single segments (zero-copy) and ArrayPool for multi-segment.
    /// </summary>
    private PduBoundaryResult DetectWithMaterializedView(in StreamDetectionContext context)
    {
        if (_Segments.Count == 1)
        {
            // Single segment — use span directly (zero-copy)
            ReadOnlySpan<byte> span = _Segments[0].Span;
            return _Detector is IStreamPduBoundaryDetector streamDetector
                ? streamDetector.Detect(span, in context)
                : _Detector!.Detect(span);
        }

        // Multiple segments — materialize into temporary buffer from ArrayPool
        byte[] rented = ArrayPool<byte>.Shared.Rent(_TotalLength);
        try
        {
            int offset = 0;
            for (int i = 0; i < _Segments.Count; i++)
            {
                ReadOnlySpan<byte> seg = _Segments[i].Span;
                seg.CopyTo(rented.AsSpan(offset));
                offset += seg.Length;
            }

            ReadOnlySpan<byte> view = rented.AsSpan(0, _TotalLength);
            return _Detector is IStreamPduBoundaryDetector streamDetector
                ? streamDetector.Detect(view, in context)
                : _Detector!.Detect(view);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Extracts <paramref name="length"/> bytes from the front of the segment list.</summary>
    private ReadOnlyMemory<byte> ExtractBytes(int length)
    {
        if (_Segments.Count == 1 && _Segments[0].Length >= length)
        {
            // Fast path: PDU fits entirely in first segment (zero-copy)
            ReadOnlyMemory<byte> pdu = _Segments[0].Slice(0, length);
            if (_Segments[0].Length == length)
            {
                _Segments.RemoveAt(0);
            }
            else
            {
                _Segments[0] = _Segments[0].Slice(length);
            }
            _TotalLength -= length;
            return pdu;
        }

        // Slow path: PDU spans multiple segments — copy into new array
        byte[] pduBytes = new byte[length];
        int remaining = length;
        int destOffset = 0;

        while (remaining > 0 && _Segments.Count > 0)
        {
            ReadOnlyMemory<byte> seg = _Segments[0];
            int take = Math.Min(seg.Length, remaining);

            seg.Span.Slice(0, take).CopyTo(pduBytes.AsSpan(destOffset));
            destOffset += take;
            remaining -= take;

            if (take == seg.Length)
            {
                _Segments.RemoveAt(0);
            }
            else
            {
                _Segments[0] = seg.Slice(take);
            }
        }

        _TotalLength -= length;
        return pduBytes;
    }

    /// <summary>Attempts resynchronization using the configured heuristic.</summary>
    private void TryResync(in StreamDetectionContext context)
    {
        if (_ResyncHeuristic == null || _TotalLength == 0)
        {
            _State = SegmentBufferState.Error;
            return;
        }

        // Materialize view for resync scan
        if (_Segments.Count == 1)
        {
            ResyncResult result = _ResyncHeuristic.Resync(_Segments[0].Span);
            if (result.IsSuccess)
            {
                DiscardBytes(result.SkipBytes);
                _State = SegmentBufferState.Synchronized;
            }
            else
            {
                _State = SegmentBufferState.Error;
            }
            return;
        }

        // Multi-segment: materialize into temp buffer
        byte[] rented = ArrayPool<byte>.Shared.Rent(_TotalLength);
        try
        {
            int offset = 0;
            for (int i = 0; i < _Segments.Count; i++)
            {
                _Segments[i].Span.CopyTo(rented.AsSpan(offset));
                offset += _Segments[i].Length;
            }

            ResyncResult result = _ResyncHeuristic.Resync(rented.AsSpan(0, _TotalLength));
            if (result.IsSuccess)
            {
                DiscardBytes(result.SkipBytes);
                _State = SegmentBufferState.Synchronized;
            }
            else
            {
                _State = SegmentBufferState.Error;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Discards <paramref name="count"/> bytes from the front of the segment list.</summary>
    private void DiscardBytes(int count)
    {
        int remaining = count;
        while (remaining > 0 && _Segments.Count > 0)
        {
            ReadOnlyMemory<byte> seg = _Segments[0];
            int take = Math.Min(seg.Length, remaining);
            remaining -= take;

            if (take == seg.Length)
            {
                _Segments.RemoveAt(0);
            }
            else
            {
                _Segments[0] = seg.Slice(take);
            }
        }

        _TotalLength -= count;
        _TotalDiscarded += count;
    }
}
