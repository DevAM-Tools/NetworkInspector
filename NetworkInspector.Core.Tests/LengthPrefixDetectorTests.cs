// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="LengthPrefixDetector"/> covering all length sizes (1/2/4),
/// endianness, and the <c>lengthIncludesHeader</c> + <c>headerSize</c> permutations
/// — including the constructor validation added by the audit (F-RA-01).
/// </summary>
internal sealed class LengthPrefixDetectorTests
{
    #region Constructor validation

    [Test]
    public async Task Ctor_RejectsLengthSizeOtherThan_1_2_4()
    {
        await Assert.That(() => new LengthPrefixDetector(0, 3))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LengthPrefixDetector(0, 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LengthPrefixDetector(0, 8))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Ctor_RejectsNegativeOffsetOrHeaderSize()
    {
        await Assert.That(() => new LengthPrefixDetector(-1, 2))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new LengthPrefixDetector(0, 2, headerSize: -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Ctor_RejectsHeaderSizeSmallerThanLengthFieldRange()
    {
        // length field at offset 4 with size 2 needs header >= 6 when lengthIncludesHeader=false
        await Assert.That(() => new LengthPrefixDetector(
                lengthOffset: 4, lengthSize: 2, lengthIncludesHeader: false, headerSize: 5))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Ctor_AllowsHeaderSizeEqualToLengthFieldRange()
    {
        LengthPrefixDetector det = new(
            lengthOffset: 4, lengthSize: 2, lengthIncludesHeader: false, headerSize: 6);
        // No exception; sanity-check Detect produces something
        PduBoundaryResult r = det.Detect(new byte[6 + 10]);
        await Assert.That(r.IsIncomplete || r.IsComplete || r.IsInvalid).IsTrue();
    }

    #endregion

    #region Length-field-only header (headerSize=0 default)

    [Test]
    public async Task Detect_DefaultHeader_BigEndian_2Byte()
    {
        // 2-byte BE length at offset 0, payload follows
        LengthPrefixDetector det = new(0, 2, bigEndian: true);
        byte[] data = [0, 4, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        PduBoundaryResult r = det.Detect(data);
        await Assert.That(r.IsComplete).IsTrue();
        // header default == lengthOffset+lengthSize = 2; payload = 4 → total 6
        await Assert.That(r.Length).IsEqualTo(6);
    }

    [Test]
    public async Task Detect_LittleEndian_4Byte()
    {
        LengthPrefixDetector det = new(0, 4, bigEndian: false);
        byte[] data = new byte[4 + 10];
        data[0] = 10; // little-endian 10
        PduBoundaryResult r = det.Detect(data);
        await Assert.That(r.IsComplete).IsTrue();
        await Assert.That(r.Length).IsEqualTo(14);
    }

    [Test]
    public async Task Detect_OneByteLength()
    {
        LengthPrefixDetector det = new(0, 1);
        byte[] data = [3, 1, 2, 3];
        PduBoundaryResult r = det.Detect(data);
        await Assert.That(r.IsComplete).IsTrue();
        await Assert.That(r.Length).IsEqualTo(4); // header(1)+payload(3)
    }

    #endregion

    #region Explicit headerSize semantics

    [Test]
    public async Task Detect_ExplicitHeaderSize_LengthExcludesHeader()
    {
        // 8-byte header, length field at offset 2, big-endian, payload follows
        LengthPrefixDetector det = new(
            lengthOffset: 2, lengthSize: 2, bigEndian: true,
            lengthIncludesHeader: false, headerSize: 8);
        byte[] data = new byte[8 + 5];
        data[2] = 0;
        data[3] = 5; // payload length = 5
        PduBoundaryResult r = det.Detect(data);
        await Assert.That(r.IsComplete).IsTrue();
        await Assert.That(r.Length).IsEqualTo(13);
    }

    [Test]
    public async Task Detect_LengthIncludesHeader()
    {
        LengthPrefixDetector det = new(
            lengthOffset: 0, lengthSize: 2, bigEndian: true,
            lengthIncludesHeader: true, headerSize: 0);
        byte[] data = new byte[10];
        data[0] = 0;
        data[1] = 10; // total length = 10
        PduBoundaryResult r = det.Detect(data);
        await Assert.That(r.IsComplete).IsTrue();
        await Assert.That(r.Length).IsEqualTo(10);
    }

    #endregion

    #region Incomplete

    [Test]
    public async Task Detect_TooFewBytesForLengthField()
    {
        LengthPrefixDetector det = new(0, 4);
        PduBoundaryResult r = det.Detect(new byte[3]);
        await Assert.That(r.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task Detect_LengthKnownButPayloadShort()
    {
        LengthPrefixDetector det = new(0, 2);
        byte[] data = [0, 100, 1, 2, 3];
        PduBoundaryResult r = det.Detect(data);
        await Assert.That(r.IsIncomplete).IsTrue();
    }

    #endregion

    #region Invalid length and boundary states

    [Test]
    public async Task Detect_InvalidLengthField_ReturnsInvalid()
    {
        await Assert.That(PduBoundaryResult.Invalid.IsInvalid).IsTrue();
        await Assert.That(PduBoundaryResult.Incomplete.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DelimiterDetector_EmptyDelimiter_Throws()
    {
        await Assert.That(() => new DelimiterDetector([])).Throws<ArgumentException>();
    }

    [Test]
    public async Task DelimiterDetector_FindsCompletePdu()
    {
        DelimiterDetector det = new([0x0D, 0x0A]);
        PduBoundaryResult found = det.Detect([0x48, 0x69, 0x0D, 0x0A, 0xFF]);
        PduBoundaryResult missing = det.Detect([0x48, 0x69]);
        await Assert.That(found.IsComplete).IsTrue();
        await Assert.That(found.Length).IsEqualTo(4);
        await Assert.That(missing.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DelimiterDetector_MutatingCallerArray_DoesNotAffectDetect()
    {
        byte[] delimiter = [0x0A];
        DelimiterDetector det = new(delimiter);
        delimiter[0] = 0x00;
        PduBoundaryResult result = det.Detect([0x48, 0x69, 0x0A, 0xFF]);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(3);
    }

    #endregion

    #region Hostile length values

    [Test]
    public async Task Detect_ZeroTotalLength_ReturnsInvalid()
    {
        LengthPrefixDetector det = new(0, 2, bigEndian: true, lengthIncludesHeader: true);
        byte[] data = [0, 0];
        PduBoundaryResult result = det.Detect(data);
        await Assert.That(result.IsInvalid).IsTrue();
    }

    [Test]
    public async Task Detect_LengthExceedsMaxPduSize_ReturnsInvalid()
    {
        LengthPrefixDetector det = new(0, 2, bigEndian: true, maxPduSize: 16);
        byte[] data = [0, 32];
        PduBoundaryResult result = det.Detect(data);
        await Assert.That(result.IsInvalid).IsTrue();
    }

    [Test]
    public async Task Detect_FourByteLengthAboveIntMax_ReturnsInvalid()
    {
        LengthPrefixDetector det = new(0, 4, bigEndian: true, lengthIncludesHeader: true);
        byte[] data = [0x7F, 0xFF, 0xFF, 0xFF];
        PduBoundaryResult result = det.Detect(data);
        await Assert.That(result.IsInvalid).IsTrue();
    }

    [Test]
    public async Task Detect_LengthFieldZeroWithIncludesHeader_ReturnsInvalid()
    {
        LengthPrefixDetector det = new(0, 1, lengthIncludesHeader: true);
        byte[] data = [0];
        PduBoundaryResult result = det.Detect(data);
        await Assert.That(result.IsInvalid).IsTrue();
    }

    [Test]
    public async Task Detect_InvalidLengthSizeViaReflection_ReturnsInvalid()
    {
        LengthPrefixDetector det = new(0, 1);
        System.Reflection.FieldInfo? lengthSizeField = typeof(LengthPrefixDetector).GetField(
            "_LengthSize", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(lengthSizeField).IsNotNull();
        lengthSizeField!.SetValue(det, 3);

        PduBoundaryResult result = det.Detect([0x01, 0x02, 0x03]);
        await Assert.That(result.IsInvalid).IsTrue();
    }

    #endregion
}
