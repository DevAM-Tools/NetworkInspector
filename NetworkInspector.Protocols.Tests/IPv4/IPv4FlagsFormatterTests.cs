// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for <see cref="IPv4FlagsFormatter"/> covering all 8 flag combinations.
/// </summary>
internal sealed class IPv4FlagsFormatterTests
{
    [Test]
    public async Task Format_NoFlags() =>
        await Assert.That(IPv4FlagsFormatter.Format(false, false, false)).IsEqualTo("[None]");

    [Test]
    public async Task Format_ReservedBitOnly() =>
        await Assert.That(IPv4FlagsFormatter.Format(true, false, false)).IsEqualTo("[RB]");

    [Test]
    public async Task Format_DontFragmentOnly() =>
        await Assert.That(IPv4FlagsFormatter.Format(false, true, false)).IsEqualTo("[DF]");

    [Test]
    public async Task Format_MoreFragmentsOnly() =>
        await Assert.That(IPv4FlagsFormatter.Format(false, false, true)).IsEqualTo("[MF]");

    [Test]
    public async Task Format_RbDf() =>
        await Assert.That(IPv4FlagsFormatter.Format(true, true, false)).IsEqualTo("[RB, DF]");

    [Test]
    public async Task Format_RbMf() =>
        await Assert.That(IPv4FlagsFormatter.Format(true, false, true)).IsEqualTo("[RB, MF]");

    [Test]
    public async Task Format_DfMf() =>
        await Assert.That(IPv4FlagsFormatter.Format(false, true, true)).IsEqualTo("[DF, MF]");

    [Test]
    public async Task Format_AllSet() =>
        await Assert.That(IPv4FlagsFormatter.Format(true, true, true)).IsEqualTo("[RB, DF, MF]");
}
