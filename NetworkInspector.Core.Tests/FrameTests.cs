// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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

    private sealed class StubFrameSource : IFrameSource
    {
        public string UiName => "stub";
        public string? Description => null;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }
        public Frame? NextFrame() => null;
        public void Stop() => _ = UiName;
        public void Dispose()
        {
        }
    }
}