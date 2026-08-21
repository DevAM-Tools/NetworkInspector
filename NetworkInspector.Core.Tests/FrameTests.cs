// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Frame"/>: factory validation and the <c>default(Frame)</c>
/// sentinel access-guard contract.
/// </summary>
internal sealed class FrameTests
{
    [Test]
    public async Task Default_Frame_IsNotValid()
    {
        Frame def = default;
        await Assert.That(def.IsValid).IsFalse();
        await Assert.That(Frame.Invalid.IsValid).IsFalse();
    }

    [Test]
    public async Task Default_Frame_PropertyAccess_Throws()
    {
        Frame def = default;
        await Assert.That(() => _ = def.Id).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.Timestamp).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.Data).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.LinkType).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.InterfaceId).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.HasInterface).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.Registry).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.IsEmpty).Throws<InvalidOperationException>();
        await Assert.That(() => _ = def.Length).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Create_WithValidArguments_ProducesValidFrame()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        FrameInterfaceId interfaceId = registry.Register(sourceId, "eth0");
        ReadOnlyMemory<byte> data = new byte[] { 1, 2, 3 };

        ParseResult<Frame> result = Frame.Create(
            new FrameId(7), Timestamp.FromNanos(0), data, LinkType.Ethernet, interfaceId, registry);

        await Assert.That(result.IsSuccess).IsTrue();
        Frame frame = result.Value;
        await Assert.That(frame.IsValid).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(3);
        await Assert.That(ReferenceEquals(frame.Registry, registry)).IsTrue();
    }

    [Test]
    public async Task Create_WithUnregisteredInterfaceId_ReturnsError()
    {
        FrameInterfaceRegistry registry = new();
        FrameInterfaceId fake = new(42);
        ReadOnlyMemory<byte> data = new byte[] { 0 };

        ParseResult<Frame> result = Frame.Create(
            new FrameId(1), Timestamp.FromNanos(0), data, LinkType.Ethernet, fake, registry);

        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Create_WithInvalidInterfaceId_AllowsConstruction()
    {
        FrameInterfaceRegistry registry = new();
        ReadOnlyMemory<byte> data = new byte[] { 0xAA };

        ParseResult<Frame> result = Frame.Create(
            new FrameId(2), Timestamp.FromNanos(0), data, LinkType.Ethernet, FrameInterfaceId.Invalid, registry);

        await Assert.That(result.IsSuccess).IsTrue();
        Frame frame = result.Value;
        await Assert.That(frame.HasInterface).IsFalse();
    }

    [Test]
    public async Task Create_WithNullRegistry_Throws()
    {
        ReadOnlyMemory<byte> data = new byte[] { 0 };
        await Assert.That(() => _ = Frame.Create(
            new FrameId(1), Timestamp.FromNanos(0), data, LinkType.Ethernet, FrameInterfaceId.Invalid, null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Default_DoesNotEqual_CreatedFrameWithIdZero()
    {
        FrameInterfaceRegistry registry = new();
        Frame zero = _CreateFrame(registry, new FrameId(0));

        await Assert.That(default(Frame) == zero).IsFalse();
    }

    [Test]
    public async Task Default_Equals_InvalidSentinel()
    {
        await Assert.That(default(Frame) == Frame.Invalid).IsTrue();
    }

    [Test]
    public async Task Default_CompareTo_CreatedFrameWithIdZero_IsNegative()
    {
        FrameInterfaceRegistry registry = new();
        Frame zero = _CreateFrame(registry, new FrameId(0));

        await Assert.That(default(Frame).CompareTo(zero)).IsLessThan(0);
    }

    [Test]
    public async Task Default_GetHashCode_DiffersFromCreatedFrameWithIdZero()
    {
        FrameInterfaceRegistry registry = new();
        Frame zero = _CreateFrame(registry, new FrameId(0));

        await Assert.That(default(Frame).GetHashCode()).IsNotEqualTo(zero.GetHashCode());
    }

    [Test]
    public async Task ComparisonOperators_OrderByFrameId()
    {
        FrameInterfaceRegistry registry = new();
        Frame low = _CreateFrame(registry, new FrameId(1));
        Frame high = _CreateFrame(registry, new FrameId(5));
        Frame same = _CreateFrame(registry, new FrameId(1));

        await Assert.That(low.CompareTo(high)).IsLessThan(0);
        await Assert.That(low < high).IsTrue();
        await Assert.That(high > low).IsTrue();
        await Assert.That(low <= same).IsTrue();
        await Assert.That(low >= same).IsTrue();
        await Assert.That(low == same).IsTrue();
        await Assert.That(low != high).IsTrue();
        await Assert.That(low.Equals(high)).IsFalse();
        await Assert.That(low.Equals((object)same)).IsTrue();
        await Assert.That(low.GetHashCode()).IsEqualTo(same.GetHashCode());
    }

    private static Frame _CreateFrame(FrameInterfaceRegistry registry, FrameId id)
    {
        return Frame.Create(
            id,
            Timestamp.FromNanos(0),
            new byte[] { 0x01 },
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            registry).Value;
    }

    private sealed class StubFrameSource : IFrameSource
    {
        public string UiName => "stub";
        public string? Description => null;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        public void Stop() => _ = UiName;
        public void Dispose()
        {
        }
    }
}
