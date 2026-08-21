// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Tests for <see cref="Concurrency.SaturatingInterlocked"/>.</summary>
internal sealed class SaturatingInterlockedTests
{
    [Test]
    public async Task Increment_FromZero_ReturnsOne()
    {
        int value = 0;
        int result = Concurrency.SaturatingInterlocked.Increment(ref value);
        await Assert.That(result).IsEqualTo(1);
        await Assert.That(value).IsEqualTo(1);
    }

    [Test]
    public async Task Increment_AtIntMaxValue_StaysSaturated()
    {
        int value = int.MaxValue;
        int result = Concurrency.SaturatingInterlocked.Increment(ref value);
        await Assert.That(result).IsEqualTo(int.MaxValue);
        await Assert.That(value).IsEqualTo(int.MaxValue);
    }
}

/// <summary>Tests for <see cref="Concurrency.SaturatingVolatileCounter"/>.</summary>
internal sealed class SaturatingVolatileCounterTests
{
    [Test]
    public async Task Increment_FromZero_ReturnsOne()
    {
        Concurrency.SaturatingVolatileCounter counter = new();
        counter.Increment();
        await Assert.That(counter.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Increment_AtIntMaxValue_StaysSaturated()
    {
        Concurrency.SaturatingVolatileCounter counter = new(int.MaxValue);
        counter.Increment();
        await Assert.That(counter.Value).IsEqualTo(int.MaxValue);
    }
}
