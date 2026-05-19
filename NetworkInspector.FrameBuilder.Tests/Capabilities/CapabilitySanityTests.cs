// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

// CA2007: helpers below await TUnit assertion tasks via small Task-returning
// methods; ConfigureAwait is irrelevant inside an in-process test runner and
// would clutter every line.  Disabled file-wide.
#pragma warning disable CA2007

namespace NetworkInspector.FrameBuilder.Tests.Capabilities;

/// <summary>
/// Reflection-based sanity checks for the Phase V0 capability migration:
/// every layer struct must implement exactly the new typed-kind capability
/// markers and must NOT implement any of the removed slot interfaces
/// (<c>ILinkLayer</c>, <c>INetworkLayer</c>, <c>ITransportLayer</c>,
/// <c>IApplicationLayer</c>).
/// </summary>
/// <remarks>
/// Slot-interface presence is detected by name (the types are deleted, so
/// <c>typeof(ILinkLayer)</c> would not even compile).  Any reappearance of
/// such a name on a layer would fail the corresponding test.
/// </remarks>
internal sealed class CapabilitySanityTests
{
    /// <summary>Names of the four removed slot interfaces.</summary>
    private static readonly string[] _RemovedSlotInterfaces =
        ["ILinkLayer", "INetworkLayer", "ITransportLayer", "IApplicationLayer"];

    private static async Task AssertNoSlotInterfaces(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)] Type layer)
    {
        Type[] all = layer.GetInterfaces();
        foreach (string slot in _RemovedSlotInterfaces)
        {
            string slotLocal = slot;
            bool present = System.Linq.Enumerable.Any(all, i => i.Name == slotLocal);
            await Assert.That(present)
                .IsFalse()
                .Because($"{layer.Name} must not implement removed slot interface {slot}");
        }
    }

    private static async Task AssertImplements<TIface>(Type layer)
    {
        await Assert.That(typeof(TIface).IsAssignableFrom(layer))
            .IsTrue()
            .Because($"{layer.Name} must implement {typeof(TIface).Name}");
    }

    [Test]
    public async Task EthernetLayer_HasRootAndEtherTypeProvider()
    {
        Type t = typeof(EthernetLayer);
        await AssertImplements<IRootLayer>(t);
        await AssertImplements<IConsumesNextProtocolValue<EtherTypeKind>>(t);
        await AssertImplements<IProvidesMtu>(t);
        await AssertImplements<IStatelessLayer>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task VlanLayer_BridgesEtherTypeNamespace()
    {
        Type t = typeof(VlanLayer);
        await AssertImplements<IProvidesNextProtocolValue<EtherTypeKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<EtherTypeKind>>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv4Layer_IsRootAndCrossesIntoIpNextProtocol()
    {
        Type t = typeof(IPv4Layer);
        await AssertImplements<IRootLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<EtherTypeKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IProvidesPseudoHeader>(t);
        await AssertImplements<IFragmentable>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv6Layer_IsRootAndCrossesIntoIpNextProtocol()
    {
        Type t = typeof(IPv6Layer);
        await AssertImplements<IRootLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<EtherTypeKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IProvidesPseudoHeader>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task ArpLayer_TerminatesEtherTypeChainWithoutPseudoHeader()
    {
        Type t = typeof(ArpLayer);
        await AssertImplements<IProvidesNextProtocolValue<EtherTypeKind>>(t);
        await Assert.That(typeof(IProvidesPseudoHeader).IsAssignableFrom(t)).IsFalse();
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task TcpLayer_RequiresIpNextProtocolAndPseudoHeader()
    {
        Type t = typeof(TcpLayer);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IRequiresPseudoHeader>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task UdpLayer_RequiresIpNextProtocolAndPseudoHeader()
    {
        Type t = typeof(UdpLayer);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IRequiresPseudoHeader>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IcmpV4EchoLayer_RequiresIpNextProtocolButNoPseudoHeader()
    {
        Type t = typeof(IcmpV4EchoLayer);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await Assert.That(typeof(IRequiresPseudoHeader).IsAssignableFrom(t)).IsFalse();
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IcmpV6EchoLayer_RequiresIpNextProtocolAndPseudoHeader()
    {
        Type t = typeof(IcmpV6EchoLayer);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IRequiresPseudoHeader>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv6HopByHopLayer_IsExtensionLayerInIpNextProtocolNamespace()
    {
        Type t = typeof(IPv6HopByHopLayer);
        await AssertImplements<IIPv6ExtensionLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IProvidesPseudoHeader>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv6RoutingLayer_IsExtensionLayerInIpNextProtocolNamespace()
    {
        Type t = typeof(IPv6RoutingLayer);
        await AssertImplements<IIPv6ExtensionLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv6DestinationOptionsLayer_IsExtensionLayerInIpNextProtocolNamespace()
    {
        Type t = typeof(IPv6DestinationOptionsLayer);
        await AssertImplements<IIPv6ExtensionLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv6FragmentExtensionLayer_IsFragmentableExtension()
    {
        Type t = typeof(IPv6FragmentExtensionLayer);
        await AssertImplements<IIPv6ExtensionLayer>(t);
        await AssertImplements<IFragmentable>(t);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task SomeIpLayer_IsPayloadLayer()
    {
        Type t = typeof(SomeIpLayer);
        await AssertImplements<IPayloadLayer>(t);
        await Assert.That(typeof(IProvidesNextProtocolValue).IsAssignableFrom(t)).IsFalse();
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task SomeIpTpLayer_IsFragmentablePayloadLayer()
    {
        Type t = typeof(SomeIpTpLayer);
        await AssertImplements<IPayloadLayer>(t);
        await AssertImplements<IFragmentable>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task SocketCanLayer_IsRoot()
    {
        Type t = typeof(SocketCanLayer);
        await AssertImplements<IRootLayer>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task SocketCanFdLayer_IsRoot()
    {
        Type t = typeof(SocketCanFdLayer);
        await AssertImplements<IRootLayer>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task SocketCanXlLayer_IsRoot()
    {
        Type t = typeof(SocketCanXlLayer);
        await AssertImplements<IRootLayer>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv4LayerWithAutoIpId_IsStatefulRootInIpNextProtocolNamespace()
    {
        Type t = typeof(IPv4LayerWithAutoIpId);
        await AssertImplements<IRootLayer>(t);
        await AssertImplements<IStatefulLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<EtherTypeKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IProvidesPseudoHeader>(t);
        await AssertImplements<IFragmentable>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task TcpLayerWithAutoSequence_IsStatefulIpNextProtocolConsumer()
    {
        Type t = typeof(TcpLayerWithAutoSequence);
        await AssertImplements<IStatefulLayer>(t);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IRequiresPseudoHeader>(t);
        await AssertNoSlotInterfaces(t);
    }

    [Test]
    public async Task IPv6FragmentExtensionLayerWithAutoId_IsStatefulFragmentableExtension()
    {
        Type t = typeof(IPv6FragmentExtensionLayerWithAutoId);
        await AssertImplements<IIPv6ExtensionLayer>(t);
        await AssertImplements<IStatefulLayer>(t);
        await AssertImplements<IFragmentable>(t);
        await AssertImplements<IProvidesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertImplements<IConsumesNextProtocolValue<IpNextProtocolKind>>(t);
        await AssertNoSlotInterfaces(t);
    }
}
