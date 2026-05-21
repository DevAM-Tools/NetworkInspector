// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for all strongly-typed ID types: FieldId, ProtocolId, ProtocolTableId,
/// HeuristicProtocolTableId, PostParserId, FrameId, PacketId, FrameSourceId,
/// FrameInterfaceId, IndexGroupId.
/// </summary>
internal sealed class IdTypeTests
{
    // === FieldId (int) ===

    [Test]
    public async Task FieldId_ConstructionAndValueRoundtrip()
    {
        FieldId id = new(42);
        await Assert.That(id.Value).IsEqualTo(42);
    }

    [Test]
    public async Task FieldId_InvalidSentinelHasMaxValue() => await Assert.That(FieldId.Invalid.Value).IsEqualTo(-1);

    [Test]
    public async Task FieldId_IsValid_ForValidValues()
    {
        FieldId id = new(0);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task FieldId_IsNotValid_ForInvalidSentinel() => await Assert.That(FieldId.Invalid.IsValid).IsFalse();

    [Test]
    public async Task FieldId_Equality()
    {
        FieldId a = new(10);
        FieldId b = new(10);
        FieldId c = new(20);
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a).IsNotEqualTo(c);
    }

    [Test]
    public async Task FieldId_DefaultIsValid()
    {
        FieldId id = default; // Value = 0
        await Assert.That(id.Value).IsEqualTo(0);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task FieldId_CanBeUsedAsDictionaryKey()
    {
        Dictionary<FieldId, string> dict = new()
        {
            [new FieldId(1)] = "one",
            [new FieldId(2)] = "two",
        };
        await Assert.That(dict[new FieldId(1)]).IsEqualTo("one");
        await Assert.That(dict[new FieldId(2)]).IsEqualTo("two");
    }

    [Test]
    public async Task FieldId_CompareToOrdering()
    {
        FieldId low = new(1);
        FieldId high = new(100);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
    }

    [Test]
    public async Task FieldId_ComparisonOperators()
    {
        FieldId a = new(5);
        FieldId b = new(10);
        await Assert.That(a < b).IsTrue();
        await Assert.That(b > a).IsTrue();
        await Assert.That(a <= b).IsTrue();
        await Assert.That(b >= a).IsTrue();
        FieldId a2 = new(5);
        await Assert.That(a <= a2).IsTrue();
        await Assert.That(a >= a2).IsTrue();
    }

    // === ProtocolId (int) ===

    [Test]
    public async Task ProtocolId_ConstructionAndValueRoundtrip()
    {
        ProtocolId id = new(99);
        await Assert.That(id.Value).IsEqualTo(99);
    }

    [Test]
    public async Task ProtocolId_InvalidSentinelHasMaxValue() => await Assert.That(ProtocolId.Invalid.Value).IsEqualTo(-1);

    [Test]
    public async Task ProtocolId_IsValid_ForValidValues()
    {
        ProtocolId id = new(0);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task ProtocolId_IsNotValid_ForInvalidSentinel() => await Assert.That(ProtocolId.Invalid.IsValid).IsFalse();

    [Test]
    public async Task ProtocolId_Equality()
    {
        ProtocolId a = new(7);
        ProtocolId b = new(7);
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task ProtocolId_CompareToOrdering()
    {
        ProtocolId low = new(1);
        ProtocolId high = new(1000);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
    }

    // === ProtocolTableId (int) ===

    [Test]
    public async Task ProtocolTableId_ConstructionAndValueRoundtrip()
    {
        ProtocolTableId id = new(3);
        await Assert.That(id.Value).IsEqualTo(3);
    }

    [Test]
    public async Task ProtocolTableId_InvalidSentinelHasMaxValue() => await Assert.That(ProtocolTableId.Invalid.Value).IsEqualTo(-1);

    [Test]
    public async Task ProtocolTableId_IsValid()
    {
        await Assert.That(new ProtocolTableId(0).IsValid).IsTrue();
        await Assert.That(ProtocolTableId.Invalid.IsValid).IsFalse();
    }

    [Test]
    public async Task ProtocolTableId_CanBeUsedAsDictionaryKey()
    {
        Dictionary<ProtocolTableId, int> dict = new()
        {
            [new ProtocolTableId(0)] = 100,
        };
        await Assert.That(dict[new ProtocolTableId(0)]).IsEqualTo(100);
    }

    // === HeuristicProtocolTableId (int) ===

    [Test]
    public async Task HeuristicProtocolTableId_ConstructionAndValueRoundtrip()
    {
        HeuristicProtocolTableId id = new(5);
        await Assert.That(id.Value).IsEqualTo(5);
    }

    [Test]
    public async Task HeuristicProtocolTableId_InvalidSentinel()
    {
        await Assert.That(HeuristicProtocolTableId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(HeuristicProtocolTableId.Invalid.IsValid).IsFalse();
    }

    // === PostParserId (int) ===

    [Test]
    public async Task PostParserId_ConstructionAndValueRoundtrip()
    {
        PostParserId id = new(12);
        await Assert.That(id.Value).IsEqualTo(12);
    }

    [Test]
    public async Task PostParserId_InvalidSentinel()
    {
        await Assert.That(PostParserId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(PostParserId.Invalid.IsValid).IsFalse();
    }

    // === FrameId (int) ===

    [Test]
    public async Task FrameId_ConstructionAndValueRoundtrip()
    {
        FrameId id = new(1000);
        await Assert.That(id.Value).IsEqualTo(1000);
    }

    [Test]
    public async Task FrameId_InvalidSentinel()
    {
        await Assert.That(FrameId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(FrameId.Invalid.IsValid).IsFalse();
    }

    [Test]
    public async Task FrameId_DefaultIsValid()
    {
        FrameId id = default;
        await Assert.That(id.Value).IsEqualTo(0);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task FrameId_CompareToOrdering()
    {
        FrameId low = new(10);
        FrameId high = new(20);
        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(high.CompareTo(low)).IsGreaterThan(0);
        await Assert.That(low.CompareTo(low)).IsEqualTo(0);
    }

    // === PacketId (int) ===

    [Test]
    public async Task PacketId_ConstructionAndValueRoundtrip()
    {
        PacketId id = new(500);
        await Assert.That(id.Value).IsEqualTo(500);
    }

    [Test]
    public async Task PacketId_InvalidSentinel()
    {
        await Assert.That(PacketId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(PacketId.Invalid.IsValid).IsFalse();
    }

    // === FrameSourceId (int) ===

    [Test]
    public async Task FrameSourceId_ConstructionAndValueRoundtrip()
    {
        FrameSourceId id = new(77);
        await Assert.That(id.Value).IsEqualTo(77);
    }

    [Test]
    public async Task FrameSourceId_InvalidSentinel()
    {
        await Assert.That(FrameSourceId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(FrameSourceId.Invalid.IsValid).IsFalse();
    }

    // === FrameInterfaceId (int) ===

    [Test]
    public async Task FrameInterfaceId_ConstructionAndValueRoundtrip()
    {
        FrameInterfaceId id = new(4);
        await Assert.That(id.Value).IsEqualTo(4);
    }

    [Test]
    public async Task FrameInterfaceId_InvalidSentinel()
    {
        await Assert.That(FrameInterfaceId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(FrameInterfaceId.Invalid.IsValid).IsFalse();
    }

    // === IndexGroupId (int) ===

    [Test]
    public async Task IndexGroupId_ConstructionAndValueRoundtrip()
    {
        IndexGroupId id = new(8);
        await Assert.That(id.Value).IsEqualTo(8);
    }

    [Test]
    public async Task IndexGroupId_InvalidSentinel()
    {
        await Assert.That(IndexGroupId.Invalid.Value).IsEqualTo(-1);
        await Assert.That(IndexGroupId.Invalid.IsValid).IsFalse();
    }
}
