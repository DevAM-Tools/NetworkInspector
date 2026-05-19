// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Reassembly;
using NetworkInspector.Protocols.Tcp;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the TCP reassembly engine components: <see cref="SegmentBuffer"/>,
/// <see cref="TcpStreamState"/>, and PDU boundary detection.
/// The reassembly engine is not yet wired into <see cref="TcpProtocol"/> (Phase 4),
/// so these tests verify the components directly.
/// </summary>
internal sealed class TcpReassemblyTests
{
    #region Constants

    private static readonly ProtocolId _TestProtocolId = new(42);
    private const ulong _TestStreamId = 1;

    #endregion

    #region Helpers

    /// <summary>Creates a config with a length-prefix PDU boundary detector.</summary>
    private static StreamReassemblyConfig LengthPrefixConfig(
        int offset = 0,
        int size = 2,
        bool bigEndian = true,
        bool lengthIncludesHeader = false,
        int headerSize = 0) => new()
        {
            BoundaryDetector = new LengthPrefixDetector(offset, size, bigEndian, lengthIncludesHeader, headerSize),
        };

    /// <summary>Creates a config with a delimiter-based PDU boundary detector.</summary>
    private static StreamReassemblyConfig DelimiterConfig(byte[] delimiter) => new()
    {
        BoundaryDetector = new DelimiterDetector(delimiter),
    };

    /// <summary>Creates a default stream detection context.</summary>
    private static StreamDetectionContext DefaultContext() => new()
    {
        StreamId = _TestStreamId,
        ProtocolId = _TestProtocolId,
        HandshakeObserved = true,
    };

    #endregion

    #region LengthPrefixDetector

    [Test]
    public async Task LengthPrefix_Complete_WhenDataSufficient()
    {
        LengthPrefixDetector detector = new(0, 2, bigEndian: true);

        // 2-byte length prefix = 5, followed by 5 bytes of data
        byte[] data = [0x00, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        // Total = prefix(2) + payload(5) = 7
        await Assert.That(result.Length).IsEqualTo(7);
    }

    [Test]
    public async Task LengthPrefix_Incomplete_WhenNotEnoughData()
    {
        LengthPrefixDetector detector = new(0, 2, bigEndian: true);

        // Length prefix says 10 bytes, but only 3 available
        byte[] data = [0x00, 0x0A, 0x01, 0x02, 0x03];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task LengthPrefix_Incomplete_WhenNotEnoughForHeader()
    {
        LengthPrefixDetector detector = new(0, 2, bigEndian: true);

        // Only 1 byte, can't even read the 2-byte length prefix
        byte[] data = [0x00];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task LengthPrefix_LengthIncludesHeader()
    {
        // Length field includes the header (offset + length field size)
        LengthPrefixDetector detector = new(0, 2, bigEndian: true, lengthIncludesHeader: true);

        // Total PDU length = 7 (including the 2-byte length prefix)
        byte[] data = [0x00, 0x07, 0x01, 0x02, 0x03, 0x04, 0x05];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(7);
    }

    [Test]
    public async Task LengthPrefix_WithHeaderSize()
    {
        // 4-byte header before the length field, length at offset 4
        LengthPrefixDetector detector = new(4, 2, bigEndian: true, headerSize: 6);

        // Header(4) + LenField(2) + Payload(3) = [H H H H 0x00 0x03 P P P]
        byte[] data = [0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x03, 0x01, 0x02, 0x03];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(9);
    }

    [Test]
    public async Task LengthPrefix_LittleEndian()
    {
        LengthPrefixDetector detector = new(0, 2, bigEndian: false);

        // Little endian: length = 0x0005 stored as [05, 00]
        byte[] data = [0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(7);
    }

    [Test]
    public async Task LengthPrefix_SingleByteLength()
    {
        LengthPrefixDetector detector = new(0, 1, bigEndian: true);

        // 1-byte length = 3
        byte[] data = [0x03, 0xAA, 0xBB, 0xCC];
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(4);
    }

    #endregion

    #region DelimiterDetector

    [Test]
    public async Task Delimiter_Complete_WhenFound()
    {
        DelimiterDetector detector = new([0x0D, 0x0A]); // \r\n

        byte[] data = [0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0D, 0x0A]; // "Hello\r\n"
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(7);
    }

    [Test]
    public async Task Delimiter_Incomplete_WhenNotFound()
    {
        DelimiterDetector detector = new([0x0D, 0x0A]);

        byte[] data = [0x48, 0x65, 0x6C, 0x6C, 0x6F]; // "Hello" (no \r\n)
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task Delimiter_SingleByteDelimiter()
    {
        DelimiterDetector detector = new([0x00]); // null terminator

        byte[] data = [0x41, 0x42, 0x43, 0x00]; // "ABC\0"
        PduBoundaryResult result = detector.Detect(data);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(4);
    }

    #endregion

    #region PatternResyncHeuristic

    [Test]
    public async Task Resync_FindsPattern()
    {
        PatternResyncHeuristic heuristic = new([0xAA, 0xBB]);

        // Pattern at offset 3: [junk, junk, junk, 0xAA, 0xBB, data...]
        byte[] data = [0x01, 0x02, 0x03, 0xAA, 0xBB, 0xCC];
        ResyncResult result = heuristic.Resync(data);

        // Searches from offset 1, finds pattern at index 3 → skip = 3
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Resync_PatternNotFound_Failure()
    {
        PatternResyncHeuristic heuristic = new([0xAA, 0xBB]);

        byte[] data = [0x01, 0x02, 0x03, 0x04];
        ResyncResult result = heuristic.Resync(data);

        await Assert.That(result.IsSuccess).IsFalse();
    }

    #endregion

    #region SegmentBuffer

    [Test]
    public async Task SegmentBuffer_SinglePdu_Complete()
    {
        // Length-prefix config: 2-byte big-endian length at offset 0
        StreamReassemblyConfig config = LengthPrefixConfig();
        SegmentBuffer buffer = new(config);

        // Append a complete PDU: length=3, data=[A,B,C]
        byte[] segment = [0x00, 0x03, 0x41, 0x42, 0x43];
        bool appended = buffer.AppendSegment(segment);
        await Assert.That(appended).IsTrue();

        bool extracted = buffer.TryExtractPdu(DefaultContext(), out ReadOnlyMemory<byte> pdu);
        await Assert.That(extracted).IsTrue();
        await Assert.That(pdu.Length).IsEqualTo(5);
    }

    [Test]
    public async Task SegmentBuffer_MultiSegment_ReassemblesComplete()
    {
        StreamReassemblyConfig config = LengthPrefixConfig();
        SegmentBuffer buffer = new(config);

        // First segment: partial PDU (length=5 but only 2 bytes of data)
        byte[] seg1 = [0x00, 0x05, 0xAA, 0xBB];
        buffer.AppendSegment(seg1);

        // Should be incomplete — can't extract yet
        bool extracted1 = buffer.TryExtractPdu(DefaultContext(), out _);
        await Assert.That(extracted1).IsFalse();

        // Second segment: remaining 3 bytes
        byte[] seg2 = [0xCC, 0xDD, 0xEE];
        buffer.AppendSegment(seg2);

        // Now should be complete
        bool extracted2 = buffer.TryExtractPdu(DefaultContext(), out ReadOnlyMemory<byte> pdu);
        await Assert.That(extracted2).IsTrue();
        // Total = prefix(2) + payload(5) = 7
        await Assert.That(pdu.Length).IsEqualTo(7);
    }

    [Test]
    public async Task SegmentBuffer_MultiplePdus_ExtractedSequentially()
    {
        StreamReassemblyConfig config = LengthPrefixConfig();
        SegmentBuffer buffer = new(config);

        // Two complete PDUs in one segment
        // PDU1: length=2, data=[AA,BB] → 4 bytes
        // PDU2: length=1, data=[CC] → 3 bytes
        byte[] segment = [0x00, 0x02, 0xAA, 0xBB, 0x00, 0x01, 0xCC];
        buffer.AppendSegment(segment);

        // Extract first PDU
        bool extracted1 = buffer.TryExtractPdu(DefaultContext(), out ReadOnlyMemory<byte> pdu1);
        await Assert.That(extracted1).IsTrue();
        await Assert.That(pdu1.Length).IsEqualTo(4);

        // Extract second PDU
        bool extracted2 = buffer.TryExtractPdu(DefaultContext(), out ReadOnlyMemory<byte> pdu2);
        await Assert.That(extracted2).IsTrue();
        await Assert.That(pdu2.Length).IsEqualTo(3);

        // No more PDUs
        bool extracted3 = buffer.TryExtractPdu(DefaultContext(), out _);
        await Assert.That(extracted3).IsFalse();
    }

    [Test]
    public async Task SegmentBuffer_NoDetector_ReturnsFalse()
    {
        StreamReassemblyConfig config = new(); // No detector
        SegmentBuffer buffer = new(config);

        byte[] data = [0x01, 0x02, 0x03];
        bool appended = buffer.AppendSegment(data);
        await Assert.That(appended).IsFalse();
    }

    [Test]
    public async Task SegmentBuffer_Clear_ResetsState()
    {
        StreamReassemblyConfig config = LengthPrefixConfig();
        SegmentBuffer buffer = new(config);

        byte[] segment = [0x00, 0x03, 0x41, 0x42, 0x43];
        buffer.AppendSegment(segment);

        buffer.Clear();
        await Assert.That(buffer.TotalLength).IsEqualTo(0);
    }

    [Test]
    public async Task SegmentBuffer_Delimiter_ExtractsPdu()
    {
        StreamReassemblyConfig config = DelimiterConfig("\r\n"u8.ToArray());
        SegmentBuffer buffer = new(config);

        byte[] segment = "Hello\r\n"u8.ToArray();
        buffer.AppendSegment(segment);

        bool extracted = buffer.TryExtractPdu(DefaultContext(), out ReadOnlyMemory<byte> pdu);
        await Assert.That(extracted).IsTrue();
        await Assert.That(pdu.Length).IsEqualTo(7);
    }

    /// <summary>
    /// A resync heuristic that reports SkipBytes greater than the bytes currently buffered
    /// must not corrupt <see cref="SegmentBuffer.TotalLength"/>. The buffer must
    /// transition to Error state and TotalLength must remain non-negative.
    /// </summary>
    [Test]
    public async Task SegmentBuffer_ResyncHeuristicOvershoot_TransitionsToErrorWithoutCorruption()
    {
        // Always-invalid detector triggers the resync path; overshoot heuristic
        // returns SkipBytes > buffered bytes, which previously corrupted _TotalLength.
        StreamReassemblyConfig config = new()
        {
            BoundaryDetector = new AlwaysInvalidDetector(),
            ResyncHeuristic = new OvershootResyncHeuristic(),
        };
        SegmentBuffer buffer = new(config);

        // Buffer 4 bytes; heuristic will return SkipBytes = int.MaxValue (> 4).
        byte[] segment = [0xAA, 0xBB, 0xCC, 0xDD];
        buffer.AppendSegment(segment);

        // Extract triggers detection (Invalid) → resync with overshoot skip.
        buffer.TryExtractPdu(DefaultContext(), out _);

        // TotalLength must be non-negative; buffer must be in Error state (no more extraction).
        await Assert.That(buffer.TotalLength).IsGreaterThanOrEqualTo(0);
        bool extractedAfterError = buffer.TryExtractPdu(DefaultContext(), out _);
        await Assert.That(extractedAfterError).IsFalse();
    }

    #endregion

    #region TcpStreamState

    [Test]
    public async Task StreamState_ForwardAndReverse_IndependentBuffers()
    {
        StreamReassemblyConfig config = LengthPrefixConfig();
        TcpStreamState state = new(_TestStreamId, _TestProtocolId, config);

        SegmentBuffer forward = state.GetBuffer(true);
        SegmentBuffer reverse = state.GetBuffer(false);

        // They should be different objects
        await Assert.That(ReferenceEquals(forward, reverse)).IsFalse();
    }

    [Test]
    public async Task StreamState_Bidirectional_ReassemblesIndependently()
    {
        StreamReassemblyConfig config = LengthPrefixConfig();
        TcpStreamState state = new(_TestStreamId, _TestProtocolId, config);

        StreamDetectionContext ctx = DefaultContext();

        // Forward direction: complete PDU
        byte[] fwdData = [0x00, 0x02, 0xAA, 0xBB];
        state.Forward.AppendSegment(fwdData);
        bool fwdExtracted = state.Forward.TryExtractPdu(ctx, out ReadOnlyMemory<byte> fwdPdu);
        await Assert.That(fwdExtracted).IsTrue();
        await Assert.That(fwdPdu.Length).IsEqualTo(4);

        // Reverse direction: incomplete then complete
        byte[] revData1 = [0x00, 0x03, 0x11];
        state.Reverse.AppendSegment(revData1);
        bool revExtracted1 = state.Reverse.TryExtractPdu(ctx, out _);
        await Assert.That(revExtracted1).IsFalse();

        byte[] revData2 = [0x22, 0x33];
        state.Reverse.AppendSegment(revData2);
        bool revExtracted2 = state.Reverse.TryExtractPdu(ctx, out ReadOnlyMemory<byte> revPdu);
        await Assert.That(revExtracted2).IsTrue();
        await Assert.That(revPdu.Length).IsEqualTo(5);
    }

    [Test]
    public async Task StreamState_Clear_ClearsBothDirections()
    {
        StreamReassemblyConfig config = LengthPrefixConfig();
        TcpStreamState state = new(_TestStreamId, _TestProtocolId, config);

        byte[] data = [0x00, 0x01, 0xFF];
        state.Forward.AppendSegment(data);
        state.Reverse.AppendSegment(data);

        state.Clear();

        await Assert.That(state.Forward.TotalLength).IsEqualTo(0);
        await Assert.That(state.Reverse.TotalLength).IsEqualTo(0);
    }

    #endregion

    #region PduBoundaryResult

    [Test]
    public async Task PduBoundaryResult_Complete_Properties()
    {
        PduBoundaryResult result = PduBoundaryResult.Complete(42);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.IsIncomplete).IsFalse();
        await Assert.That(result.IsInvalid).IsFalse();
        await Assert.That(result.Length).IsEqualTo(42);
    }

    [Test]
    public async Task PduBoundaryResult_Incomplete_Properties()
    {
        PduBoundaryResult result = PduBoundaryResult.Incomplete;

        await Assert.That(result.IsIncomplete).IsTrue();
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.IsInvalid).IsFalse();
    }

    [Test]
    public async Task PduBoundaryResult_Invalid_Properties()
    {
        PduBoundaryResult result = PduBoundaryResult.Invalid;

        await Assert.That(result.IsInvalid).IsTrue();
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.IsIncomplete).IsFalse();
    }

    #endregion
}

/// <summary>
/// Boundary detector stub that always reports the current data as invalid,
/// triggering the resync path in <see cref="SegmentBuffer"/>.
/// </summary>
file sealed class AlwaysInvalidDetector : IPduBoundaryDetector
{
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data) => PduBoundaryResult.Invalid;
}

/// <summary>
/// Resync heuristic stub that always returns a SkipBytes value larger than any
/// realistic buffer, exercising the out-of-range guard in <see cref="SegmentBuffer"/>.
/// </summary>
file sealed class OvershootResyncHeuristic : IResyncHeuristic
{
    public ResyncResult Resync(ReadOnlySpan<byte> data) => ResyncResult.Skip(int.MaxValue);
}
