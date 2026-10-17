// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Tests for the bus fields the ASC reader shares with the BLF reader and both writers: the classic CAN bus DLC 9 to 15
/// in <c>len8_dlc</c>, and the LIN protected identifier plus the checksum model in the <c>DLT_LIN</c> header.
/// </summary>
internal sealed class AscBusFieldTests
{
    #region Classic CAN DLC

    [Test]
    public async Task CanLineWithDlc15KeepsTheBusDlcInLen8Dlc()
    {
        bool ok = AscCanParser.TryParse(
            "0.100000 1 123 Rx d f 01 02 03 04 05 06 07 08"u8, 16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[BlfConstants.SocketCanLenOffset]).IsEqualTo((byte)8);
        await Assert.That(frame[BlfConstants.SocketCanLen8DlcOffset]).IsEqualTo((byte)15);
        await Assert.That(frame[8]).IsEqualTo((byte)0x01);
        await Assert.That(frame[15]).IsEqualTo((byte)0x08);
    }

    [Test]
    public async Task CanLineWithDlc8LeavesLen8DlcZero()
    {
        bool ok = AscCanParser.TryParse(
            "0.100000 1 123 Rx d 8 01 02 03 04 05 06 07 08"u8, 16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame[BlfConstants.SocketCanLen8DlcOffset]).IsEqualTo((byte)0);
    }

    #endregion

    #region LIN protected identifier and checksum model

    [Test]
    [Arguments("CSM = enhanced", (byte)2)]
    [Arguments("CSM = classic", (byte)1)]
    [Arguments("", (byte)0)]
    public async Task LinLineCarriesTheProtectedIdAndTheChecksumModel(string csm, byte expectedBits)
    {
        byte[] line = Encoding.ASCII.GetBytes($"0.100000 Li 10 Rx 2 01 02 checksum = 7A {csm}".TrimEnd());

        bool ok = AscLinParser.TryParse(line, 16, out _, out _, out byte[] frame);

        // Byte 4: data length in the high nibble, checksum model in bits 1-0. Byte 5: id 0x10 with parity = 0x50.
        await Assert.That(ok).IsTrue();
        await Assert.That(frame[4]).IsEqualTo((byte)((2 << 4) | expectedBits));
        await Assert.That(frame[5]).IsEqualTo(LinProtectedId.Compute(0x10));
        await Assert.That(frame[6]).IsEqualTo((byte)0x7A);
    }

    #endregion
}
