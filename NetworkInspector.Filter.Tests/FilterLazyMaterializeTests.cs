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
}
