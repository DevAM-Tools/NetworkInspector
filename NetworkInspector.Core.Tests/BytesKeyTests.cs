// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Tables;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="BytesKey"/>: value equality, hashing,
/// span access, and operator overloads.
/// </summary>
internal sealed class BytesKeyTests
{
    // === Construction ===

    [Test]
    public async Task Ctor_ByteArray_StoresData()
    {
        byte[] data = [0x01, 0x02, 0x03];
        BytesKey key = new(data);

        await Assert.That(key.Length).IsEqualTo(3);
        await Assert.That(key.Span[0]).IsEqualTo((byte)0x01);
        await Assert.That(key.Span[1]).IsEqualTo((byte)0x02);
        await Assert.That(key.Span[2]).IsEqualTo((byte)0x03);
    }

    [Test]
    public async Task Ctor_ReadOnlySpan_CopiesData()
    {
        byte[] original = [0xAA, 0xBB];
        ReadOnlySpan<byte> span = original;
        BytesKey key = new(span);

        // Modifying original should not affect key (it was copied)
        original[0] = 0x00;
        await Assert.That(key.Span[0]).IsEqualTo((byte)0xAA);
        await Assert.That(key.Length).IsEqualTo(2);
    }

    // === Default (null backing array) ===

    [Test]
    public async Task Default_HasZeroLength()
    {
        BytesKey key = default;
        await Assert.That(key.Length).IsEqualTo(0);
        await Assert.That(key.Span.Length).IsEqualTo(0);
    }

    // === Equality ===

    [Test]
    public async Task Equals_SameContent_ReturnsTrue()
    {
        BytesKey a = new([0x01, 0x02, 0x03]);
        BytesKey b = new([0x01, 0x02, 0x03]);
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equals_DifferentContent_ReturnsFalse()
    {
        BytesKey a = new([0x01, 0x02, 0x03]);
        BytesKey b = new([0x01, 0x02, 0x04]);
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task Equals_DifferentLength_ReturnsFalse()
    {
        BytesKey a = new([0x01, 0x02]);
        BytesKey b = new([0x01, 0x02, 0x03]);
        await Assert.That(a.Equals(b)).IsFalse();
    }

    [Test]
    public async Task Equals_BothDefault_ReturnsTrue()
    {
        BytesKey a = default;
        BytesKey b = default;
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equals_DefaultAndEmpty_ReturnsTrue()
    {
        BytesKey a = default;
        BytesKey b = new([]);
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equals_Object_SameContent()
    {
        BytesKey a = new([0x01]);
        object b = new BytesKey([0x01]);
        await Assert.That(a.Equals(b)).IsTrue();
    }

    [Test]
    public async Task Equals_Object_WrongType()
    {
        BytesKey a = new([0x01]);
        await Assert.That(a.Equals("not a BytesKey")).IsFalse();
    }

    [Test]
    public async Task Equals_Object_Null()
    {
        BytesKey a = new([0x01]);
        await Assert.That(a.Equals(null)).IsFalse();
    }

    // === Operators ===

    [Test]
    public async Task Op_Equality()
    {
        BytesKey a = new([0xDE, 0xAD]);
        BytesKey b = new([0xDE, 0xAD]);
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task Op_Inequality()
    {
        BytesKey a = new([0xDE, 0xAD]);
        BytesKey b = new([0xBE, 0xEF]);
        await Assert.That(a != b).IsTrue();
    }

    // === GetHashCode ===

    [Test]
    public async Task GetHashCode_SameContent_SameHash()
    {
        BytesKey a = new([0x01, 0x02, 0x03, 0x04]);
        BytesKey b = new([0x01, 0x02, 0x03, 0x04]);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task GetHashCode_DifferentContent_DifferentHash()
    {
        BytesKey a = new([0x01]);
        BytesKey b = new([0x02]);
        // Not strictly guaranteed, but very likely for BytesKey
        await Assert.That(a.GetHashCode()).IsNotEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task GetHashCode_Default()
    {
        BytesKey a = default;
        BytesKey b = default;
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    // === Dictionary key usage ===

    [Test]
    public async Task BytesKey_WorksAsDictionaryKey()
    {
        Dictionary<BytesKey, string> dict = new()
        {
            { new BytesKey([0x01, 0x02]), "first" },
            { new BytesKey([0x03, 0x04]), "second" },
        };

        BytesKey lookup = new([0x01, 0x02]);
        await Assert.That(dict.ContainsKey(lookup)).IsTrue();
        await Assert.That(dict[lookup]).IsEqualTo("first");

        BytesKey missing = new([0xFF]);
        await Assert.That(dict.ContainsKey(missing)).IsFalse();
    }
}