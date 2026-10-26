// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the limits of <see cref="StreamReassemblyConfig"/>: defaults, accepted values, and rejected values.
/// </summary>
internal sealed class StreamReassemblyConfigTests
{
    [Test]
    public async Task DefaultsAreSixtyFourKibiBytePduAndOneMebiByteBuffer()
    {
        StreamReassemblyConfig config = new();

        await Assert.That(config.MaxPduSize).IsEqualTo(65536);
        await Assert.That(config.MaxBufferSize).IsEqualTo(1048576);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4096)]
    [Arguments(int.MaxValue)]
    public async Task PositiveLimitsAreAccepted(int value)
    {
        StreamReassemblyConfig config = new() { MaxPduSize = value, MaxBufferSize = value };

        await Assert.That(config.MaxPduSize).IsEqualTo(value);
        await Assert.That(config.MaxBufferSize).IsEqualTo(value);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task NonPositiveMaxPduSizeIsRejected(int value)
    {
        await Assert.That(() => new StreamReassemblyConfig { MaxPduSize = value }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task NonPositiveMaxBufferSizeIsRejected(int value)
    {
        await Assert.That(() => new StreamReassemblyConfig { MaxBufferSize = value }).Throws<ArgumentOutOfRangeException>();
    }
}
