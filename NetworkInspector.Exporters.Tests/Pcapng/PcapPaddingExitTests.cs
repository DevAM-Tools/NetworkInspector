// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Direct calls into the Exporters-linked <see cref="PcapPadding.TryRoundBlockLength"/> so both
/// returns are hit from this test assembly. The reader inlines the method, which leaves the
/// exits uncounted when only exporter tests exercise PCAPNG writes. Sources is not made
/// InternalsVisibleTo this assembly: that would also expose the link-compiled
/// <c>BlfConstants</c> and <c>PcapConstants</c> copies and make those names ambiguous.
/// </summary>
internal sealed class PcapPaddingExitTests
{
    [Test]
    public async Task TryRoundBlockLength_RoundsUpToMultipleOfFour()
    {
        bool rounded = PcapPadding.TryRoundBlockLength(33, out uint length);

        await Assert.That(rounded).IsTrue();
        await Assert.That(length).IsEqualTo(36u);
    }

    [Test]
    public async Task TryRoundBlockLength_RejectsLengthNearUInt32Max()
    {
        bool rounded = PcapPadding.TryRoundBlockLength(uint.MaxValue - 2, out uint length);

        await Assert.That(rounded).IsFalse();
        await Assert.That(length).IsEqualTo(0u);
    }
}
