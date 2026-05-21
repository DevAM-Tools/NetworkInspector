// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for <see cref="Icmpv6NdpFlagsFormatter"/> covering all RA (4 entries)
/// and NA (8 entries) flag combinations.
/// </summary>
internal sealed class Icmpv6NdpFlagsFormatterTests
{
    #region Router Advertisement (2 flags: M, O)

    [Test]
    public async Task FormatRa_NoFlags() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatRa(false, false)).IsEqualTo("[None]");

    [Test]
    public async Task FormatRa_Managed() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatRa(true, false)).IsEqualTo("[M]");

    [Test]
    public async Task FormatRa_Other() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatRa(false, true)).IsEqualTo("[O]");

    [Test]
    public async Task FormatRa_ManagedAndOther() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatRa(true, true)).IsEqualTo("[M, O]");

    #endregion

    #region Neighbor Advertisement (3 flags: R, S, O)

    [Test]
    public async Task FormatNa_NoFlags() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(false, false, false)).IsEqualTo("[None]");

    [Test]
    public async Task FormatNa_Router() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(true, false, false)).IsEqualTo("[R]");

    [Test]
    public async Task FormatNa_Solicited() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(false, true, false)).IsEqualTo("[S]");

    [Test]
    public async Task FormatNa_Override() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(false, false, true)).IsEqualTo("[O]");

    [Test]
    public async Task FormatNa_RouterSolicited() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(true, true, false)).IsEqualTo("[R, S]");

    [Test]
    public async Task FormatNa_RouterOverride() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(true, false, true)).IsEqualTo("[R, O]");

    [Test]
    public async Task FormatNa_SolicitedOverride() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(false, true, true)).IsEqualTo("[S, O]");

    [Test]
    public async Task FormatNa_AllSet() =>
        await Assert.That(Icmpv6NdpFlagsFormatter.FormatNa(true, true, true)).IsEqualTo("[R, S, O]");

    #endregion
}
