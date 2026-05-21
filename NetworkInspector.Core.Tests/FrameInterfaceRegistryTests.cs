// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="FrameInterfaceRegistry"/>: source registration, interface registration,
/// lookup by ID, count, snapshot immutability.
/// </summary>
internal sealed class FrameInterfaceRegistryTests
{
    // === Empty registry ===

    [Test]
    public async Task Empty_Registry_HasZeroCounts()
    {
        FrameInterfaceRegistry registry = new();
        await Assert.That(registry.Count).IsEqualTo(0);
        await Assert.That(registry.SourceCount).IsEqualTo(0);
        await Assert.That(registry.All.Length).IsEqualTo(0);
        await Assert.That(registry.AllSources.Length).IsEqualTo(0);
    }

    // === Source registration ===

    [Test]
    public async Task RegisterSource_ReturnsValidId()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("Test");
        FrameSourceId id = registry.RegisterSource(source);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task RegisterSource_IncrementingIds()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource sa = new("A");
        using StubFrameSource sb = new("B");
        FrameSourceId id0 = registry.RegisterSource(sa);
        FrameSourceId id1 = registry.RegisterSource(sb);
        await Assert.That(id0.Value).IsEqualTo(0);
        await Assert.That(id1.Value).IsEqualTo(1);
        await Assert.That(registry.SourceCount).IsEqualTo(2);
    }

    // === Source lookup ===

    [Test]
    public async Task GetSource_ValidId_ReturnsInfo()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("MySource");
        FrameSourceId id = registry.RegisterSource(source);

        FrameSourceInfo? info = registry.GetSource(id);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Id).IsEqualTo(id);
        await Assert.That(info.Source).IsEqualTo(source);
    }

    [Test]
    public async Task GetSource_InvalidId_ReturnsNull()
    {
        FrameInterfaceRegistry registry = new();
        await Assert.That(registry.GetSource(new FrameSourceId(999))).IsNull();
    }

    [Test]
    public async Task TryGetSource_ValidId_ReturnsTrue()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("X");
        FrameSourceId id = registry.RegisterSource(source);
        bool found = registry.TryGetSource(id, out FrameSourceInfo? info);
        await Assert.That(found).IsTrue();
        await Assert.That(info).IsNotNull();
    }

    [Test]
    public async Task TryGetSource_InvalidId_ReturnsFalse()
    {
        FrameInterfaceRegistry registry = new();
        bool found = registry.TryGetSource(new FrameSourceId(0), out FrameSourceInfo? info);
        await Assert.That(found).IsFalse();
        await Assert.That(info).IsNull();
    }

    // === Interface registration ===

    [Test]
    public async Task Register_Interface_ReturnsValidId()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);
        FrameInterfaceId ifaceId = registry.Register(sourceId, "eth0");
        await Assert.That(ifaceId.IsValid).IsTrue();
        await Assert.That(registry.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Register_Interface_MultipleInterfaces()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);
        FrameInterfaceId id0 = registry.Register(sourceId, "eth0");
        FrameInterfaceId id1 = registry.Register(sourceId, "eth1");
        await Assert.That(id0.Value).IsEqualTo(0);
        await Assert.That(id1.Value).IsEqualTo(1);
        await Assert.That(registry.Count).IsEqualTo(2);
    }

    [Test]
    public void Register_Interface_InvalidSourceId_Throws()
    {
        FrameInterfaceRegistry registry = new();
        Assert.Throws<ArgumentException>(() =>
            registry.Register(FrameSourceId.Invalid, "eth0"));
    }

    [Test]
    public void Register_Interface_UnregisteredSourceId_Throws()
    {
        FrameInterfaceRegistry registry = new();
        // Source 0 not registered
        Assert.Throws<ArgumentException>(() =>
            registry.Register(new FrameSourceId(0), "eth0"));
    }

    // === Interface lookup ===

    [Test]
    public async Task Get_Interface_ValidId()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);
        FrameInterfaceId ifaceId = registry.Register(sourceId, "eth0", "Main interface", LinkType.Ethernet);

        FrameInterfaceInfo? info = registry.Get(ifaceId);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.UiName).IsEqualTo("eth0");
        await Assert.That(info.Description).IsEqualTo("Main interface");
        await Assert.That(info.LinkType).IsEqualTo(LinkType.Ethernet);
    }

    [Test]
    public async Task Get_Interface_InvalidId_ReturnsNull()
    {
        FrameInterfaceRegistry registry = new();
        await Assert.That(registry.Get(new FrameInterfaceId(999))).IsNull();
    }

    [Test]
    public async Task TryGet_Interface_ValidId()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);
        FrameInterfaceId ifaceId = registry.Register(sourceId, "eth0");

        bool found = registry.TryGet(ifaceId, out FrameInterfaceInfo? info);
        await Assert.That(found).IsTrue();
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.UiName).IsEqualTo("eth0");
    }

    [Test]
    public async Task TryGet_Interface_InvalidId_ReturnsFalse()
    {
        FrameInterfaceRegistry registry = new();
        bool found = registry.TryGet(new FrameInterfaceId(0), out FrameInterfaceInfo? info);
        await Assert.That(found).IsFalse();
        await Assert.That(info).IsNull();
    }

    // === AllSources ===

    [Test]
    public async Task AllSources_ReturnsSnapshot()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource sa = new("A");
        using StubFrameSource sb = new("B");
        registry.RegisterSource(sa);
        registry.RegisterSource(sb);

        ReadOnlySpan<FrameSourceInfo> all = registry.AllSources;
        await Assert.That(all.Length).IsEqualTo(2);
    }

    // === All (interfaces) ===

    [Test]
    public async Task All_ReturnsSnapshot()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);
        registry.Register(sourceId, "eth0");
        registry.Register(sourceId, "eth1");

        ReadOnlySpan<FrameInterfaceInfo> all = registry.All;
        await Assert.That(all.Length).IsEqualTo(2);
    }

    // === Minimal stub for IFrameSource ===

    private sealed class StubFrameSource(string uiName) : IFrameSource
    {
        public string UiName => uiName;
        public string? Description => null;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }
        public Frame? NextFrame() => null;
        public void Stop() => _ = UiName; // Intentional: prevents CA1822 since this is an interface impl
        public void Dispose()
        {
        }
    }
}
