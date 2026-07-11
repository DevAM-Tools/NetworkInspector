// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>Tests for session strongly-typed identifiers.</summary>
internal sealed class IdTypeTests
{
    // === JobId ===

    [Test]
    public async Task JobId_ConstructionAndValueRoundtrip()
    {
        JobId id = new(42);
        await Assert.That(id.Value).IsEqualTo(42);
    }

    [Test]
    public async Task JobId_InvalidSentinel()
    {
        await Assert.That(JobId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(JobId.Invalid.IsValid).IsFalse();
    }

    [Test]
    public async Task JobId_DefaultIsValid()
    {
        JobId id = default;
        await Assert.That(id.Value).IsEqualTo(0);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task JobId_EqualityAndHashCode()
    {
        JobId a = new(10);
        JobId b = new(10);
        JobId c = new(20);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a).IsNotEqualTo(c);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.Equals((object)c)).IsFalse();
        await Assert.That(a.ToString()).IsEqualTo("10");
    }

    [Test]
    public async Task JobId_CompareToAndOperators()
    {
        JobId low = new(5);
        JobId high = new(10);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
        await Assert.That(low < high).IsTrue();
        await Assert.That(high > low).IsTrue();
        await Assert.That(low <= high).IsTrue();
        await Assert.That(high >= low).IsTrue();
        JobId sameAsLow = new(5);
        await Assert.That(low <= sameAsLow).IsTrue();
        await Assert.That(low >= sameAsLow).IsTrue();
        await Assert.That(low == high).IsFalse();
        await Assert.That(low != high).IsTrue();
    }

    // === ListenerId ===

    [Test]
    public async Task ListenerId_ConstructionAndValueRoundtrip()
    {
        ListenerId id = new(7);
        await Assert.That(id.Value).IsEqualTo(7);
    }

    [Test]
    public async Task ListenerId_InvalidSentinel()
    {
        await Assert.That(ListenerId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(ListenerId.Invalid.IsValid).IsFalse();
    }

    [Test]
    public async Task ListenerId_DefaultIsValid()
    {
        ListenerId id = default;
        await Assert.That(id.Value).IsEqualTo(0);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task ListenerId_EqualityAndHashCode()
    {
        ListenerId a = new(3);
        ListenerId b = new(3);
        ListenerId c = new(4);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a).IsNotEqualTo(c);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.Equals((object)c)).IsFalse();
        await Assert.That(a.ToString()).IsEqualTo("3");
    }

    [Test]
    public async Task ListenerId_CompareToAndOperators()
    {
        ListenerId low = new(1);
        ListenerId high = new(9);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low < high).IsTrue();
        await Assert.That(high > low).IsTrue();
        await Assert.That(low <= high).IsTrue();
        await Assert.That(high >= low).IsTrue();
        ListenerId sameAsLow = new(1);
        await Assert.That(low <= sameAsLow).IsTrue();
        await Assert.That(low >= sameAsLow).IsTrue();
        await Assert.That(low == high).IsFalse();
        await Assert.That(low != high).IsTrue();
    }
}
