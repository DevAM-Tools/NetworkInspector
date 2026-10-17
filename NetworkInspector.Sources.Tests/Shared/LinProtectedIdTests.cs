// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Shared;

/// <summary>
/// Tests for <see cref="LinProtectedId"/>, the one parity rule of the BLF and ASC LIN readers.
/// </summary>
internal sealed class LinProtectedIdTests
{
    [Test]
    [Arguments((byte)0x00, (byte)0x80)]
    [Arguments((byte)0x01, (byte)0xC1)]
    [Arguments((byte)0x05, (byte)0x85)]
    [Arguments((byte)0x10, (byte)0x50)]
    [Arguments((byte)0x3C, (byte)0x3C)]
    [Arguments((byte)0x3D, (byte)0x7D)]
    public async Task ComputeAddsBothParityBits(byte frameId, byte expectedPid)
    {
        await Assert.That(LinProtectedId.Compute(frameId)).IsEqualTo(expectedPid);
    }

    [Test]
    public async Task ComputeIgnoresTheUpperTwoBitsOfTheInput()
    {
        // An input that already carries parity bits (or garbage there) yields the same PID as the bare id.
        await Assert.That(LinProtectedId.Compute(0xC1)).IsEqualTo(LinProtectedId.Compute(0x01));
        await Assert.That(LinProtectedId.Compute(0xFF)).IsEqualTo(LinProtectedId.Compute(0x3F));
    }

    [Test]
    public async Task MaskRecoversEveryFrameId()
    {
        // Writers recover the 6-bit id with the mask; this must hold for all 64 ids.
        bool allRecovered = true;
        for (int id = 0; id <= LinProtectedId.FrameIdMask; id++)
        {
            allRecovered &= (LinProtectedId.Compute((byte)id) & LinProtectedId.FrameIdMask) == id;
        }

        await Assert.That(allRecovered).IsTrue();
    }
}
