// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Eval;

/// <summary>
/// Exercises the evaluation context directly, including the owner-scan fallbacks that only run
/// for protocols without a conventional container field.
/// </summary>
internal sealed class FilterEvalContextTests
{
    #region Helpers

    private static FilterEvalContext _Bound(Stack stack, Packet packet)
    {
        FilterEvalContext context = new(FilterTestHelper.FieldOwners(stack));
        context.Bind(packet);
        return context;
    }

    #endregion

    #region Presence

    [Test]
    public async Task HasProtocol_WithContainerField_UsesContainerLookup()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        bool present = context.HasProtocol(
            FilterTestHelper.ProtocolIdOf(stack, "udp"),
            FilterTestHelper.FieldIdOf(stack, "udp"));

        await Assert.That(present).IsTrue();
    }

    [Test]
    public async Task HasProtocol_WithoutContainerField_ScansOwnersFlat()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        bool udp = context.HasProtocol(FilterTestHelper.ProtocolIdOf(stack, "udp"), FieldId.Invalid);
        bool tcp = context.HasProtocol(FilterTestHelper.ProtocolIdOf(stack, "tcp"), FieldId.Invalid);

        await Assert.That(udp).IsTrue();
        await Assert.That(tcp).IsFalse();
    }

    [Test]
    public async Task HasProtocol_WithoutContainerField_ScansOwnersInsideScope()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);
        FieldId udpField = FilterTestHelper.FieldIdOf(stack, "udp");

        int hits = context.FindAnchors([udpField], ProtocolId.Invalid, 1, out int hitsBase);
        await Assert.That(hits).IsEqualTo(1);

        Field previous = context.PushDomain(context.HitAt(hitsBase, 0));
        bool udp = context.HasProtocol(FilterTestHelper.ProtocolIdOf(stack, "udp"), FieldId.Invalid);
        bool ip = context.HasProtocol(FilterTestHelper.ProtocolIdOf(stack, "ip"), FieldId.Invalid);
        context.PopDomain(previous);
        context.ReleaseHits(hitsBase);

        await Assert.That(udp).IsTrue();
        await Assert.That(ip).IsFalse();
    }

    [Test]
    public async Task HasProtocol_OwnerScanInsideScope_LooksPastTheDomainRoot()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        // Re-attribute one UDP child to another protocol so the scan can only succeed on a
        // descendant, never on the scope's own root node.
        ProtocolId[] owners = FilterTestHelper.FieldOwners(stack);
        ProtocolId foreign = FilterTestHelper.ProtocolIdOf(stack, "tcp");
        owners[FilterTestHelper.FieldIdOf(stack, "udp.srcport").Value] = foreign;

        FilterEvalContext context = new(owners);
        context.Bind(packet);
        FieldId udpField = FilterTestHelper.FieldIdOf(stack, "udp");
        _ = context.FindAnchors([udpField], ProtocolId.Invalid, 0, out int hitsBase);
        Field previous = context.PushDomain(context.HitAt(hitsBase, 0));

        bool found = context.HasProtocol(foreign, FieldId.Invalid);

        context.PopDomain(previous);
        context.ReleaseHits(hitsBase);
        context.Unbind();

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task HasAnyContainer_InsideScope_FindsDomainRootAndDescendants()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);
        FieldId udpField = FilterTestHelper.FieldIdOf(stack, "udp");

        int hits = context.FindAnchors([udpField], ProtocolId.Invalid, 0, out int hitsBase);
        Field previous = context.PushDomain(context.HitAt(hitsBase, 0));

        bool self = context.HasAnyContainer(udpField);
        bool child = context.HasAnyContainer(FilterTestHelper.FieldIdOf(stack, "udp.srcport"));
        bool outside = context.HasAnyContainer(FilterTestHelper.FieldIdOf(stack, "ip.ttl"));

        context.PopDomain(previous);
        context.ReleaseHits(hitsBase);

        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(self).IsTrue();
        await Assert.That(child).IsTrue();
        await Assert.That(outside).IsFalse();
    }

    [Test]
    public async Task HasAnyField_ReportsFirstPresentField()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);
        FieldId tcpPort = FilterTestHelper.FieldIdOf(stack, "tcp.srcport");
        FieldId udpPort = FilterTestHelper.FieldIdOf(stack, "udp.srcport");

        await Assert.That(context.HasAnyField([tcpPort, udpPort])).IsTrue();
        await Assert.That(context.HasAnyField([tcpPort])).IsFalse();
    }

    #endregion

    #region Anchors

    [Test]
    public async Task FindAnchors_ByProtocol_CollectsOwnedNodes()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        int hits = context.FindAnchors([], FilterTestHelper.ProtocolIdOf(stack, "ip"), 0, out int hitsBase);
        Field first = context.HitAt(hitsBase, 0);
        context.ReleaseHits(hitsBase);

        await Assert.That(hits).IsGreaterThan(0);
        await Assert.That(context.OwnerOf(first.FieldId)).IsEqualTo(FilterTestHelper.ProtocolIdOf(stack, "ip"));
    }

    [Test]
    public async Task FindAnchors_NestedClaims_GrowTheHitBuffer()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);
        ProtocolId ip = FilterTestHelper.ProtocolIdOf(stack, "ip");

        List<int> bases = [];
        int total = 0;
        for (int i = 0; i < 5; i++)
        {
            total += context.FindAnchors([], ip, 0, out int hitsBase);
            bases.Add(hitsBase);
        }

        for (int i = bases.Count - 1; i >= 0; i--)
        {
            context.ReleaseHits(bases[i]);
        }

        await Assert.That(total).IsGreaterThan(8);
    }

    [Test]
    public async Task FindAnchors_UnknownAnchor_CollectsNothing()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        int hits = context.FindAnchors([], ProtocolId.Invalid, 0, out int hitsBase);
        context.ReleaseHits(hitsBase);

        await Assert.That(hits).IsEqualTo(0);
    }

    [Test]
    public async Task OwnerOf_FieldOutsideTheOwnerTable_IsInvalid()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        await Assert.That(context.OwnerOf(FieldId.Invalid)).IsEqualTo(ProtocolId.Invalid);

        context.Unbind();
    }

    #endregion

    #region Errors

    [Test]
    public async Task SetError_KeepsTheFirstFailure()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        context.SetError(FilterError.Runtime("first"));
        context.SetError(FilterError.Runtime("second"));

        await Assert.That(context.Error!.Message).IsEqualTo("first");
        await Assert.That(context.Packet).IsSameReferenceAs(packet);

        context.Unbind();
    }

    [Test]
    public async Task Bind_ClearsPreviousError()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        FilterEvalContext context = _Bound(stack, packet);

        context.SetError(FilterError.Runtime("stale"));
        context.Bind(packet);

        await Assert.That(context.Error).IsNull();
    }

    #endregion
}
