// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="Icmpv6NdpFlagsFormatter"/>: the precomputed display strings of the Router Advertisement flags
/// byte and the Neighbor Advertisement flag bits.
/// </summary>
internal sealed class Icmpv6NdpFlagsFormatterTests
{
    #region Router Advertisement flags (one byte)

    [Test]
    [Arguments((byte)0x00, "[None]")]
    [Arguments((byte)0x80, "[M]")]
    [Arguments((byte)0x40, "[O]")]
    [Arguments((byte)0xC0, "[M, O]")]
    [Arguments((byte)0x20, "[H]")]
    [Arguments((byte)0x08, "[Prf=High]")]
    [Arguments((byte)0x10, "[Prf=Reserved]")]
    [Arguments((byte)0x18, "[Prf=Low]")]
    [Arguments((byte)0xA8, "[M, H, Prf=High]")]
    [Arguments((byte)0x04, "[P]")]
    [Arguments((byte)0x02, "[S]")]
    [Arguments((byte)0xFE, "[M, O, H, Prf=Low, P, S]")]
    public async Task FormatRaNamesEverySetFlag(byte flags, string expected) =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatRa(flags)).IsEqualTo(expected);

    [Test]
    public async Task FormatRaIgnoresTheReservedBit() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatRa(0x01)).IsEqualTo("[None]");

    [Test]
    public async Task FormatRaSharesOneStringPerFlagByte() =>
        // The table hands out the same reference every time, which is what makes the call allocation-free.
        await Assert.That(ReferenceEquals(Icmpv6NdpFlagsFormatter.FormatRa(0xC0), Icmpv6NdpFlagsFormatter.FormatRa(0xC0))).IsTrue();

    [Test]
    [Arguments((byte)0, "Medium (0)")]
    [Arguments((byte)1, "High (1)")]
    [Arguments((byte)2, "Reserved (2)")]
    [Arguments((byte)3, "Low (3)")]
    public async Task RouterPreferenceTextNamesTheTwoBits(byte preference, string expected) =>
        await Assert.That(Icmpv6NdpFlagsFormatter.RouterPreferenceText(preference)).IsEqualTo(expected);

    #endregion

    #region Neighbor Advertisement flags (3 flags)

    [Test]
    [Arguments(false, false, false, "[None]")]
    [Arguments(true, false, false, "[R]")]
    [Arguments(false, true, false, "[S]")]
    [Arguments(false, false, true, "[O]")]
    [Arguments(true, true, false, "[R, S]")]
    [Arguments(true, false, true, "[R, O]")]
    [Arguments(false, true, true, "[S, O]")]
    [Arguments(true, true, true, "[R, S, O]")]
    public async Task FormatNaNamesEverySetFlag(bool router, bool solicited, bool overrideFlag, string expected) =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(router, solicited, overrideFlag)).IsEqualTo(expected);

    [Test]
    [Arguments(false, false, false, "none")]
    [Arguments(true, false, false, "rtr")]
    [Arguments(false, true, false, "sol")]
    [Arguments(false, false, true, "ovr")]
    [Arguments(true, true, true, "rtr, sol, ovr")]
    public async Task FormatNaInfoUsesTheShortWords(bool router, bool solicited, bool overrideFlag, string expected) =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNaInfo(router, solicited, overrideFlag)).IsEqualTo(expected);

    #endregion
}
