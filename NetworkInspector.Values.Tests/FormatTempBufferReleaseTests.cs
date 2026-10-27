// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values.Tests;

/// <summary>
/// <c>FormatTemp()</c> of every value type hands out the ZeroAlloc thread-static char buffer; disposing the returned
/// <see cref="TempString"/> must release it. A <see cref="TempString"/> built without the acquire epoch never matches on
/// <c>Dispose</c>, so the buffer stays locked on that thread for good: every later <c>TempString</c> on the thread then falls
/// back to a heap allocation and any test that checks the buffer on a reused pool thread fails depending on scheduling.
/// </summary>
internal sealed class FormatTempBufferReleaseTests
{
    [Test]
    public async Task EveryFormatTempReleasesTheThreadStaticBufferOnDispose()
    {
        // No await between the calls: the thread-static buffer belongs to the executing thread. Each value is formatted, disposed
        // and checked in turn, so the first leaking type is the one that fails.
        bool[] availableAfter = new bool[6];
        using (new IPv4Address(0xC0A80101u).FormatTemp())
        {
        }

        availableAfter[0] = ZeroAllocHelper.IsCharBufferAvailable();
        using (new IPv6Address(0x20010DB800000000UL, 1UL).FormatTemp())
        {
        }

        availableAfter[1] = ZeroAllocHelper.IsCharBufferAvailable();
        using (new MacAddress(0x001122334455UL).FormatTemp())
        {
        }

        availableAfter[2] = ZeroAllocHelper.IsCharBufferAvailable();
        using (new Eui64(0x0011223344556677UL).FormatTemp())
        {
        }

        availableAfter[3] = ZeroAllocHelper.IsCharBufferAvailable();
        using (new Uuid(0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL).FormatTemp())
        {
        }

        availableAfter[4] = ZeroAllocHelper.IsCharBufferAvailable();
        using (new Timestamp(1_700_000_000_123_456_789L).FormatTemp())
        {
        }

        availableAfter[5] = ZeroAllocHelper.IsCharBufferAvailable();

        await Assert.That(availableAfter[0]).IsTrue();
        await Assert.That(availableAfter[1]).IsTrue();
        await Assert.That(availableAfter[2]).IsTrue();
        await Assert.That(availableAfter[3]).IsTrue();
        await Assert.That(availableAfter[4]).IsTrue();
        await Assert.That(availableAfter[5]).IsTrue();
    }
}
