// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using TpKey = NetworkInspector.Protocols.SomeIp.SomeIpTpReassemblyKey;
using TpOutcome = NetworkInspector.Protocols.SomeIp.SomeIpTpOutcome;
using TpReassembler = NetworkInspector.Protocols.SomeIp.SomeIpTpReassembler;
using TpResult = NetworkInspector.Protocols.SomeIp.SomeIpTpReassemblyResult;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for SOME/IP-TP reassembly: segment alignment, the handling of segments that contradict the message size, and the
/// normal completion paths.
/// </summary>
internal sealed class SomeIpTpReassemblerTests
{
    private static readonly TpKey _Key = new(0x1234, 0x0042, 1, 1);

    private static byte[] _Bytes(int length, byte fill)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    #region Segment alignment

    [Test]
    public async Task ZeroLengthSegmentWithMoreSegmentsIsDropped()
    {
        TpReassembler reassembler = new();

        TpResult result = reassembler.AddSegment(in _Key, 0, ReadOnlySpan<byte>.Empty, moreSegments: true);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Dropped);
        await Assert.That(reassembler.ActiveSessionCount).IsEqualTo(0);
    }

    [Test]
    public async Task ManyZeroLengthSegmentsNeverGrowASession()
    {
        // A session that kept an entry per empty segment would grow without bound (and re-sort on every segment).
        TpReassembler reassembler = new();
        TpResult first = reassembler.AddSegment(in _Key, 0, _Bytes(16, 1), moreSegments: true);

        TpOutcome lastOutcome = TpOutcome.InProgress;
        for (int index = 0; index < 1000; index++)
        {
            lastOutcome = reassembler.AddSegment(in _Key, 16, ReadOnlySpan<byte>.Empty, moreSegments: true).Outcome;
        }

        await Assert.That(first.Outcome).IsEqualTo(TpOutcome.InProgress);
        await Assert.That(lastOutcome).IsEqualTo(TpOutcome.Dropped);
        await Assert.That(reassembler.ActiveSessionCount).IsLessThanOrEqualTo(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(15)]
    [Arguments(17)]
    [Arguments(31)]
    public async Task NonLastSegmentThatIsNotAMultipleOf16BytesIsDropped(int length)
    {
        TpReassembler reassembler = new();

        TpResult result = reassembler.AddSegment(in _Key, 0, _Bytes(length, 1), moreSegments: true);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Dropped);
        await Assert.That(reassembler.ActiveSessionCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(16)]
    [Arguments(32)]
    public async Task NonLastSegmentOfAMultipleOf16BytesIsAccepted(int length)
    {
        TpReassembler reassembler = new();

        TpResult result = reassembler.AddSegment(in _Key, 0, _Bytes(length, 1), moreSegments: true);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.InProgress);
        await Assert.That(reassembler.ActiveSessionCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(5)]
    [Arguments(16)]
    public async Task LastSegmentMayHaveAnyLength(int lastLength)
    {
        TpReassembler reassembler = new();
        _ = reassembler.AddSegment(in _Key, 0, _Bytes(16, 1), moreSegments: true);

        TpResult result = reassembler.AddSegment(in _Key, 16, _Bytes(lastLength, 2), moreSegments: false);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Complete);
        await Assert.That(result.Payload!.Length).IsEqualTo(16 + lastLength);
        await Assert.That(reassembler.ActiveSessionCount).IsEqualTo(0);
    }

    [Test]
    public async Task SingleLastSegmentAtOffsetZeroCompletesImmediately()
    {
        TpReassembler reassembler = new();

        TpResult result = reassembler.AddSegment(in _Key, 0, _Bytes(7, 9), moreSegments: false);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Complete);
        await Assert.That(result.Payload!.AsSpan().SequenceEqual(_Bytes(7, 9))).IsTrue();
    }

    #endregion

    #region Segments beyond the message end

    [Test]
    public async Task SegmentBeyondTheDeclaredEndDropsTheSessionInsteadOfBlockingItForever()
    {
        // Segments: [0,16) more, a stray at 64 (more), then the last segment [16,32) fixes the total at 32.
        TpReassembler reassembler = new();
        _ = reassembler.AddSegment(in _Key, 0, _Bytes(16, 1), moreSegments: true);
        _ = reassembler.AddSegment(in _Key, 64, _Bytes(16, 3), moreSegments: true);

        TpResult result = reassembler.AddSegment(in _Key, 16, _Bytes(16, 2), moreSegments: false);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Dropped);
        await Assert.That(reassembler.ActiveSessionCount).IsEqualTo(0);
    }

    [Test]
    public async Task SegmentBeyondAnAlreadyFixedEndDropsTheSession()
    {
        // The last segment arrives first (total 32), then a segment that starts at 64.
        TpReassembler reassembler = new();
        _ = reassembler.AddSegment(in _Key, 16, _Bytes(16, 2), moreSegments: false);

        TpResult result = reassembler.AddSegment(in _Key, 64, _Bytes(16, 3), moreSegments: true);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Dropped);
        await Assert.That(reassembler.ActiveSessionCount).IsEqualTo(0);
    }

    [Test]
    public async Task SegmentThatEndsExactlyAtTheDeclaredEndIsNotBeyondIt()
    {
        TpReassembler reassembler = new();
        _ = reassembler.AddSegment(in _Key, 16, _Bytes(16, 2), moreSegments: false);

        TpResult result = reassembler.AddSegment(in _Key, 0, _Bytes(16, 1), moreSegments: true);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Complete);
        await Assert.That(result.Payload!.Length).IsEqualTo(32);
        await Assert.That(result.Payload[0]).IsEqualTo((byte)1);
        await Assert.That(result.Payload[31]).IsEqualTo((byte)2);
    }

    [Test]
    public async Task OutOfOrderSegmentsAreAssembledInOffsetOrder()
    {
        TpReassembler reassembler = new();
        _ = reassembler.AddSegment(in _Key, 32, _Bytes(3, 3), moreSegments: false);
        _ = reassembler.AddSegment(in _Key, 0, _Bytes(16, 1), moreSegments: true);

        TpResult result = reassembler.AddSegment(in _Key, 16, _Bytes(16, 2), moreSegments: true);

        await Assert.That(result.Outcome).IsEqualTo(TpOutcome.Complete);
        await Assert.That(result.Payload!.Length).IsEqualTo(35);
        await Assert.That(result.Payload[0]).IsEqualTo((byte)1);
        await Assert.That(result.Payload[16]).IsEqualTo((byte)2);
        await Assert.That(result.Payload[34]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task SegmentWithAnOffsetAboveTheSignedRangeNeverReachesAssembly()
    {
        // 0xFFFFFFF0 does not fit a signed int; a stored copy of it must not turn into a negative array index.
        TpReassembler reassembler = new();
        _ = reassembler.AddSegment(in _Key, 0xFFFFFFF0u, _Bytes(16, 7), moreSegments: false);
        _ = reassembler.AddSegment(in _Key, 0, _Bytes(16, 1), moreSegments: true);

        TpResult result = reassembler.AddSegment(in _Key, 16, _Bytes(16, 2), moreSegments: true);

        await Assert.That(result.Outcome).IsNotEqualTo(TpOutcome.Complete);
    }

    #endregion
}
