// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for <see cref="CanFlagsFormatter"/> covering all flag combinations
/// for CAN classic, CAN FD, and CAN XL frame types.
/// </summary>
internal sealed class CanFlagsFormatterTests
{
    #region CAN Classic (ClassicFlagsTable — 8 entries)

    [Test]
    public async Task FormatClassic_NoFlags() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(false, false, false)).IsEqualTo("[None]");

    [Test]
    public async Task FormatClassic_Xtd() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(true, false, false)).IsEqualTo("[XTD]");

    [Test]
    public async Task FormatClassic_Rtr() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(false, true, false)).IsEqualTo("[RTR]");

    [Test]
    public async Task FormatClassic_Err() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(false, false, true)).IsEqualTo("[ERR]");

    [Test]
    public async Task FormatClassic_XtdRtr() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(true, true, false)).IsEqualTo("[XTD, RTR]");

    [Test]
    public async Task FormatClassic_XtdErr() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(true, false, true)).IsEqualTo("[XTD, ERR]");

    [Test]
    public async Task FormatClassic_RtrErr() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(false, true, true)).IsEqualTo("[RTR, ERR]");

    [Test]
    public async Task FormatClassic_AllSet() =>
        await Assert.That(CanFlagsFormatter.FormatClassic(true, true, true)).IsEqualTo("[XTD, RTR, ERR]");

    #endregion

    #region CAN FD (FdFlagsTable — 32 entries)

    [Test]
    public async Task FormatFd_NoFlags_HasFdPrefix() =>
        // "FD" is always present for FD frames.
        await Assert.That(CanFlagsFormatter.FormatFd(false, false, false, false, false)).IsEqualTo("[FD]");

    [Test]
    public async Task FormatFd_Xtd() =>
        await Assert.That(CanFlagsFormatter.FormatFd(true, false, false, false, false)).IsEqualTo("[FD, XTD]");

    [Test]
    public async Task FormatFd_BrsOnly() =>
        await Assert.That(CanFlagsFormatter.FormatFd(false, false, false, true, false)).IsEqualTo("[FD, BRS]");

    [Test]
    public async Task FormatFd_Esi() =>
        await Assert.That(CanFlagsFormatter.FormatFd(false, false, false, false, true)).IsEqualTo("[FD, ESI]");

    [Test]
    public async Task FormatFd_BrsEsi() =>
        await Assert.That(CanFlagsFormatter.FormatFd(false, false, false, true, true)).IsEqualTo("[FD, BRS, ESI]");

    [Test]
    public async Task FormatFd_AllSet() =>
        await Assert.That(CanFlagsFormatter.FormatFd(true, true, true, true, true)).IsEqualTo("[FD, XTD, RTR, ERR, BRS, ESI]");

    #endregion

    #region CAN XL (XlFlagsTable — 4 entries)

    [Test]
    public async Task FormatXl_NoFlags_HasXlfPrefix() =>
        // "XLF" is always present for XL frames.
        await Assert.That(CanFlagsFormatter.FormatXl(false, false)).IsEqualTo("[XLF]");

    [Test]
    public async Task FormatXl_Sec() =>
        await Assert.That(CanFlagsFormatter.FormatXl(true, false)).IsEqualTo("[XLF, SEC]");

    [Test]
    public async Task FormatXl_Rrs() =>
        await Assert.That(CanFlagsFormatter.FormatXl(false, true)).IsEqualTo("[XLF, RRS]");

    [Test]
    public async Task FormatXl_AllSet() =>
        await Assert.That(CanFlagsFormatter.FormatXl(true, true)).IsEqualTo("[XLF, SEC, RRS]");

    #endregion
}
