// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Capability: the layer owns a “next protocol” field (Ethernet EtherType,
/// IPv4 Protocol, IPv6 NextHeader, VLAN inner EtherType, …) into which the
/// value supplied by an inner <see cref="IProvidesNextProtocolValue"/> can
/// be patched once that value is known.
/// </summary>
/// <remarks>
/// <para>
/// The value flow is inner → outer: the inner layer’s
/// <see cref="IProvidesProtocolType.ProtocolType"/> is written into this
/// outer layer’s field via <see cref="PatchNextProtocol"/>. Implementations
/// must respect any user-pinned explicit value: if the field was set
/// explicitly at construction time, <see cref="PatchNextProtocol"/> is a
/// no-op (the explicit value wins).
/// </para>
/// <para>
/// This non-generic interface carries the runtime patching contract. The
/// generic <see cref="IConsumesNextProtocolValue{TKind}"/> variant adds a
/// compile-time discriminator that prevents stacking layers from disjoint
/// next-protocol namespaces (EtherType vs. IP next-protocol).
/// </para>
/// </remarks>
public interface IConsumesNextProtocolValue : IProtocolLayer
{
    /// <summary>
    /// Patches the outer’s next-protocol field with the value supplied by
    /// the inner layer. No-op when the field was pinned to an explicit value.
    /// </summary>
    /// <param name="frame">Complete frame buffer.</param>
    /// <param name="myOffset">Offset where this layer’s header starts.</param>
    /// <param name="nextProtocol">Protocol-type value supplied by the inner layer.</param>
    void PatchNextProtocol(scoped Span<byte> frame, int myOffset, ushort nextProtocol);
}

/// <summary>
/// Compile-time-typed capability: the layer’s next-protocol field accepts
/// values from the namespace identified by <typeparamref name="TKind"/>
/// (e.g. <see cref="EtherTypeKind"/> for Ethernet/VLAN, or
/// <see cref="IpNextProtocolKind"/> for IPv4 Protocol / IPv6 NextHeader).
/// </summary>
/// <typeparam name="TKind">Phantom-type discriminator for the value namespace.</typeparam>
/// <remarks>
/// Used by capability-typed <c>Then(...)</c> overloads to reject stackings
/// where the inner’s value namespace does not match this slot. A layer may
/// implement this interface for more than one <typeparamref name="TKind"/>
/// when it owns multiple disjoint next-protocol slots (none does today; the
/// design permits it).
/// </remarks>
public interface IConsumesNextProtocolValue<TKind> : IConsumesNextProtocolValue
    where TKind : struct
{
}
