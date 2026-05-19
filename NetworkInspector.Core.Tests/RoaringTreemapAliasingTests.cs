// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the new aliasing fast-paths on <see cref="RoaringTreemap"/> (F-IX-04).
/// Verifies that <c>And/Or/AndNot/Xor</c> against <c>this</c> behave correctly without
/// corrupting the operand.
/// </summary>
internal sealed class RoaringTreemapAliasingTests
{
    private static RoaringTreemap Build(params ulong[] values)
    {
        RoaringTreemap tm = new();
        foreach (ulong v in values)
        {
            tm.Add(v);
        }
        return tm;
    }

    [Test]
    public async Task SelfAnd_EqualsSelfClone_NoMutation()
    {
        RoaringTreemap a = Build(1UL, 100UL, 0x1_0000_0001UL);
        RoaringTreemap r = a.And(a);
        await Assert.That(r.Cardinality).IsEqualTo(3L);
        await Assert.That(r.Contains(1UL)).IsTrue();
        await Assert.That(r.Contains(100UL)).IsTrue();
        await Assert.That(r.Contains(0x1_0000_0001UL)).IsTrue();

        // Mutating the result must not affect the source
        r.Add(2UL);
        await Assert.That(a.Contains(2UL)).IsFalse();
    }

    [Test]
    public async Task SelfOr_EqualsSelfClone_NoMutation()
    {
        RoaringTreemap a = Build(5UL, 0x2_0000_0005UL);
        RoaringTreemap r = a.Or(a);
        await Assert.That(r.Cardinality).IsEqualTo(2L);
        r.Add(99UL);
        await Assert.That(a.Contains(99UL)).IsFalse();
    }

    [Test]
    public async Task SelfAndNot_IsEmpty()
    {
        RoaringTreemap a = Build(1UL, 2UL, 3UL);
        RoaringTreemap r = a.AndNot(a);
        await Assert.That(r.IsEmpty).IsTrue();
        await Assert.That(a.Cardinality).IsEqualTo(3L);
    }

    [Test]
    public async Task SelfXor_IsEmpty()
    {
        RoaringTreemap a = Build(7UL, 0x3_0000_0007UL);
        RoaringTreemap r = a.Xor(a);
        await Assert.That(r.IsEmpty).IsTrue();
        await Assert.That(a.Cardinality).IsEqualTo(2L);
    }

    [Test]
    public async Task Clone_ProducesIndependentCopy()
    {
        RoaringTreemap a = Build(1UL, 2UL);
        RoaringTreemap c = a.Clone();
        await Assert.That(c.Cardinality).IsEqualTo(2L);
        c.Add(99UL);
        await Assert.That(a.Contains(99UL)).IsFalse();
        await Assert.That(c.Contains(99UL)).IsTrue();
    }
}