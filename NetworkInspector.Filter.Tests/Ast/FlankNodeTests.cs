// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Ast;

/// <summary>Covers the value semantics of the flank window and endpoint structs.</summary>
internal sealed class FlankNodeTests
{
    #region Window

    [Test]
    public async Task Window_FromTime_CarriesNanoseconds()
    {
        FlankWindow window = FlankWindow.FromNanoseconds(1500);

        await Assert.That(window.IsPacketCount).IsFalse();
        await Assert.That(window.Nanoseconds).IsEqualTo(1500L);
        await Assert.That(window.PacketCount).IsEqualTo(0);
    }

    [Test]
    public async Task Window_FromPackets_CarriesCount()
    {
        FlankWindow window = FlankWindow.FromPackets(4);

        await Assert.That(window.IsPacketCount).IsTrue();
        await Assert.That(window.PacketCount).IsEqualTo(4);
    }

    [Test]
    public async Task Window_Equality_ComparesAllComponents()
    {
        FlankWindow first = FlankWindow.FromNanoseconds(10);
        FlankWindow same = FlankWindow.FromNanoseconds(10);
        FlankWindow other = FlankWindow.FromPackets(10);

        await Assert.That(first.Equals(same)).IsTrue();
        await Assert.That(first == same).IsTrue();
        await Assert.That(first != other).IsTrue();
        await Assert.That(first.Equals((object)same)).IsTrue();
        await Assert.That(first.Equals("not a window")).IsFalse();
        await Assert.That(first.GetHashCode()).IsEqualTo(same.GetHashCode());
    }

    #endregion

    #region Endpoint

    [Test]
    public async Task Endpoint_CarriesOperatorAndValue()
    {
        FlankEndpoint endpoint = new(CompareOp.LessThan, FieldValueData.NewU64(64));

        await Assert.That(endpoint.Op).IsEqualTo(CompareOp.LessThan);
        await Assert.That(endpoint.Value.TryGetAsU64(out ulong value)).IsTrue();
        await Assert.That(value).IsEqualTo(64UL);
    }

    [Test]
    public async Task Endpoint_Equality_ComparesOperatorAndValue()
    {
        FlankEndpoint first = new(CompareOp.Equal, FieldValueData.NewU64(1));
        FlankEndpoint same = new(CompareOp.Equal, FieldValueData.NewU64(1));
        FlankEndpoint differentOp = new(CompareOp.NotEqual, FieldValueData.NewU64(1));
        FlankEndpoint differentValue = new(CompareOp.Equal, FieldValueData.NewU64(2));

        await Assert.That(first.Equals(same)).IsTrue();
        await Assert.That(first == same).IsTrue();
        await Assert.That(first != differentOp).IsTrue();
        await Assert.That(first != differentValue).IsTrue();
        await Assert.That(first.Equals((object)same)).IsTrue();
        await Assert.That(first.Equals(42)).IsFalse();
        await Assert.That(first.GetHashCode()).IsEqualTo(same.GetHashCode());
    }

    #endregion

    #region Delta

    [Test]
    public async Task Delta_CarriesOperatorAndValue()
    {
        FlankDelta delta = new(CompareOp.GreaterEqual, FieldValueData.NewI64(-3));

        await Assert.That(delta.Op).IsEqualTo(CompareOp.GreaterEqual);
        _ = delta.Value.TryGetAsI64(out long value);
        await Assert.That(value).IsEqualTo(-3L);
    }

    [Test]
    public async Task Delta_Equality_ComparesOperatorAndValue()
    {
        FlankDelta first = new(CompareOp.Equal, FieldValueData.NewU64(2));
        FlankDelta same = new(CompareOp.Equal, FieldValueData.NewU64(2));
        FlankDelta differentOp = new(CompareOp.NotEqual, FieldValueData.NewU64(2));
        FlankDelta differentValue = new(CompareOp.Equal, FieldValueData.NewU64(3));

        await Assert.That(first.Equals(same)).IsTrue();
        await Assert.That(first == same).IsTrue();
        await Assert.That(first != differentOp).IsTrue();
        await Assert.That(first != differentValue).IsTrue();
        await Assert.That(first.GetHashCode()).IsEqualTo(same.GetHashCode());
    }

    [Test]
    public async Task Node_FromAndTo_IsArmed()
    {
        FlankNode node = new(
            "ip.ttl",
            new FlankEndpoint(CompareOp.Equal, FieldValueData.NewU64(1)),
            new FlankEndpoint(CompareOp.Equal, FieldValueData.NewU64(2)),
            by: null,
            isAnyChange: false,
            FlankWindow.FromPackets(10),
            when: null,
            position: 0,
            length: 1);

        await Assert.That(node.IsArmedMode).IsTrue();
    }

    [Test]
    public async Task Node_FromOnly_IsNotArmed()
    {
        FlankNode node = new(
            "ip.ttl",
            new FlankEndpoint(CompareOp.Equal, FieldValueData.NewU64(1)),
            to: null,
            by: null,
            isAnyChange: false,
            FlankWindow.FromPackets(10),
            when: null,
            position: 0,
            length: 1);

        await Assert.That(node.IsArmedMode).IsFalse();
    }

    #endregion
}
