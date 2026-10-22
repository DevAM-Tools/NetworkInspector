// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Proves the evaluator materializes lazy fields only when a value walk needs them.</summary>
internal sealed class FilterLazyMaterializeTests
{
    #region Helpers

    private static (Stack Stack, LazyValueProtocol Protocol, ProtocolId Id) _Build()
    {
#pragma warning disable CA2000 // Ownership of the settings manager transfers to the stack.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        LazyValueProtocol protocol = new();
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        return (builder.Build(), protocol, id);
    }

    private static Packet _Parse(Stack stack, ProtocolId firstProtocol, int packetId)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(packetId),
            new byte[4],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(FieldTreeMode.Build, firstProtocol: firstProtocol);
        bool parsed = Packet.TryParse(
            new PacketId(packetId),
            stack,
            frame,
            in options,
            out Packet? packet,
            out ParseFailure failure);
        if (!parsed || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    #endregion

    #region Value walk

    [Test]
    public async Task Match_LazyField_MaterializesOnValueCompare()
    {
        (Stack stack, LazyValueProtocol protocol, ProtocolId id) = _Build();
        using (stack)
        {
            Filter filter = FilterTestHelper.CompileOrThrow("lazy.value == 53", stack);
            Packet packet = _Parse(stack, id, 0);

            await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
            await Assert.That(protocol.PopulateCount).IsEqualTo(0);
            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
            await Assert.That(protocol.PopulateCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Match_LazyField_MissStillMaterializesOnce()
    {
        (Stack stack, LazyValueProtocol protocol, ProtocolId id) = _Build();
        using (stack)
        {
            protocol.Value = 9;
            Filter filter = FilterTestHelper.CompileOrThrow("lazy.value == 53", stack);
            Packet packet = _Parse(stack, id, 0);

            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
            await Assert.That(protocol.PopulateCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Scope_LazyAnchor_MaterializesDuringBfs()
    {
        (Stack stack, LazyValueProtocol protocol, ProtocolId id) = _Build();
        using (stack)
        {
            Filter filter = FilterTestHelper.CompileOrThrow("$lazy { lazy.value == 53 }", stack);
            Packet packet = _Parse(stack, id, 0);

            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsTrue();
            await Assert.That(protocol.PopulateCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Match_UnrelatedPresence_DoesNotMaterializeLazyContainer()
    {
        (Stack stack, LazyValueProtocol protocol, ProtocolId id) = _Build();
        using (stack)
        {
            // Bare protocol presence without a container-index hit still walks owners; use a
            // field presence on a different protocol so the lazy branch is never entered.
            Filter filter = FilterTestHelper.CompileOrThrow("false", stack);
            Packet packet = _Parse(stack, id, 0);

            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
            await Assert.That(protocol.PopulateCount).IsEqualTo(0);
            await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
        }
    }

    #endregion

    #region Pre-check observer

    /// <summary>
    /// The session's matching-read pre-check rules a packet out when the observer of
    /// <see cref="Filter.CreatePrecheckObserver"/> reports no match after a parse without field tree. For a field inside
    /// a lazy container that verdict must equal the field-tree filter's, on a first parse and on a replay.
    /// </summary>
    [Test]
    [Arguments("lazy.value == 53", 53UL, true)]
    [Arguments("lazy.value == 53", 54UL, false)]
    [Arguments("!(lazy.value == 53)", 53UL, false)]
    [Arguments("!(lazy.value == 53)", 54UL, true)]
    [Arguments("lazy.value != 53", 53UL, false)]
    [Arguments("lazy.value != 53", 54UL, true)]
    public async Task PrecheckObserverLazyFieldAgreesWithTheFieldTreeFilter(string expression, ulong value, bool expected)
    {
        (Stack stack, LazyValueProtocol protocol, ProtocolId id) = _Build();
        using (stack)
        {
            protocol.Value = value;
            Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
            FilterObserver? observer = filter.CreatePrecheckObserver();
            bool treeMatched = FilterTestHelper.MatchOrThrow(filter, _Parse(stack, id, 0));

            // Id 0 is first-parsed above, so this skip parse is a replay; id 1 is a first parse in skip mode.
            FieldWatch watch = observer!.CreateOwnerExpandingWatch();
            bool replayRead = _Observe(stack, id, 0, observer, watch, out bool replayMatched);
            bool firstRead = _Observe(stack, id, 1, observer, watch, out bool firstMatched);

            await Assert.That(treeMatched).IsEqualTo(expected);
            await Assert.That(replayRead).IsTrue();
            await Assert.That(replayMatched).IsEqualTo(expected);
            await Assert.That(firstRead).IsTrue();
            await Assert.That(firstMatched).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// Why the pre-check needs the owner-expanding watch: with only the observer's own watch, a lazy container outside an
    /// index group stays closed in a parse without field tree, so the observer never sees the matching value.
    /// </summary>
    [Test]
    public async Task PrecheckObserverOwnWatchAloneMissesTheLazyField()
    {
        (Stack stack, LazyValueProtocol _, ProtocolId id) = _Build();
        using (stack)
        {
            Filter filter = FilterTestHelper.CompileOrThrow("lazy.value == 53", stack);
            FilterObserver observer = filter.CreatePrecheckObserver()!;

            bool read = _Observe(stack, id, 0, observer, observer.Watch, out bool matched);

            await Assert.That(read).IsTrue();
            await Assert.That(matched).IsFalse();
            await Assert.That(observer.CreateOwnerExpandingWatch() == observer.Watch).IsFalse();
        }
    }

    private static bool _Observe(
        Stack stack, ProtocolId firstProtocol, int packetId, FilterObserver observer, FieldWatch watch, out bool matched)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(packetId),
            new byte[4],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseObserver[] observers = [new() { Observer = observer, Watch = watch }];
        ParseOptions options = new(FieldTreeMode.Skip, firstProtocol: firstProtocol, observers: observers);
        if (!Packet.TryParse(new PacketId(packetId), stack, frame, in options, out _, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return observer.TryReadMatch(out matched, out _);
    }

    #endregion
}
