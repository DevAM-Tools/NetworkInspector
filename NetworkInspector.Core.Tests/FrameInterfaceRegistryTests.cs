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

    [Test]
    public async Task Register_NullUiName_ThrowsArgumentNullException()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => registry.Register(sourceId, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Register_EmptyUiName_ThrowsArgumentException()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => registry.Register(sourceId, "")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Register_UiNameWithControlCharacters_ThrowsArgumentException()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);

        await Assert.That(() => registry.Register(sourceId, "eth\n0")).Throws<ArgumentException>();
    }

    [Test]
    public async Task RegisterSource_NullSource_ThrowsArgumentNullException()
    {
        FrameInterfaceRegistry registry = new();

        await Assert.That(() => registry.RegisterSource(null!)).Throws<ArgumentNullException>();
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

    /// <summary>
    /// Builds a private <c>FrameInterfaceRegistry.Snapshot&lt;T&gt;</c> with an empty array and the given count, by reflection
    /// (the type is private). Only for the tests that drive the capacity guard without allocating a huge array.
    /// </summary>
    private static object _SnapshotWithCount<T>(int count)
        where T : class
    {
        Type open = typeof(FrameInterfaceRegistry).GetNestedType("Snapshot`1", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Snapshot type not found.");
        return Activator.CreateInstance(open.MakeGenericType(typeof(T)), Array.Empty<T>(), count)
            ?? throw new InvalidOperationException("Snapshot could not be created.");
    }

    // === Minimal stub for IFrameSource ===

    [Test]
    public async Task Register_OversizedInterfaceCount_Throws()
    {
        FrameInterfaceRegistry registry = new();
        System.Reflection.FieldInfo? interfacesField = typeof(FrameInterfaceRegistry).GetField(
            "_Interfaces", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(interfacesField).IsNotNull();

        // A snapshot that claims one more entry than an id can hold; the count is what the capacity guard reads, so no array of
        // that size is needed.
        object oversized = _SnapshotWithCount<FrameInterfaceInfo>(
            ArrayIndexIdRange.MaxValue + 1);
        interfacesField!.SetValue(registry, oversized);

        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => registry.Register(sourceId, "eth0"));
        await Assert.That(ex.Message).Contains("Maximum frame interface count exceeded");
    }

    [Test]
    public async Task RegisterSource_OversizedSourceCount_Throws()
    {
        FrameInterfaceRegistry registry = new();
        System.Reflection.FieldInfo? sourcesField = typeof(FrameInterfaceRegistry).GetField(
            "_Sources", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(sourcesField).IsNotNull();

        // See Register_OversizedInterfaceCount_Throws: only the count of the snapshot matters.
        object oversized = _SnapshotWithCount<FrameSourceInfo>(
            ArrayIndexIdRange.MaxValue + 1);
        sourcesField!.SetValue(registry, oversized);

        using StubFrameSource source = new("S");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterSource(source));
        await Assert.That(ex.Message).Contains("Maximum frame source count exceeded");
    }

    [Test]
    public async Task ConcurrentRegistrationsGetDenseUniqueIdsAndEveryIdResolves()
    {
        const int threads = 8;
        const int perThread = 2_000;
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("S");
        FrameSourceId sourceId = registry.RegisterSource(source);
        int[][] issued = new int[threads][];

        // Eight writers race on one table; the shared array grows several times, so both the spare-capacity claim and the
        // doubling path run under contention.
        Task[] writers = [.. Enumerable.Range(0, threads).Select(thread => Task.Factory.StartNew(
            () =>
            {
                int[] ids = new int[perThread];
                for (int index = 0; index < perThread; index++)
                {
                    ids[index] = registry.Register(sourceId, "if").Value;
                }

                issued[thread] = ids;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default))];
        await Task.WhenAll(writers);

        // Ids are exactly 0..n-1, each issued once, and every one resolves to an entry that carries its own id.
        bool[] seen = new bool[threads * perThread];
        bool allUnique = true;
        foreach (int[] ids in issued)
        {
            foreach (int id in ids)
            {
                allUnique &= !seen[id];
                seen[id] = true;
            }
        }

        bool allResolve = true;
        for (int id = 0; id < seen.Length; id++)
        {
            FrameInterfaceInfo? info = registry.Get(new FrameInterfaceId(id));
            allResolve &= info is not null && info.Id.Value == id;
        }

        await Assert.That(registry.Count).IsEqualTo(threads * perThread);
        await Assert.That(allUnique).IsTrue();
        await Assert.That(allResolve).IsTrue();
        await Assert.That(registry.All.Length).IsEqualTo(threads * perThread);
    }

    private sealed class StubFrameSource(string uiName) : IFrameSource
    {
        public string UiName => uiName;
        public string? Description => null;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        public void Stop() => _ = UiName; // Intentional: prevents CA1822 since this is an interface impl
        public void Dispose()
        {
        }
    }
}
