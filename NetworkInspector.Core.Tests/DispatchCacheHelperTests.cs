// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for <see cref="DispatchCacheHelper"/> cache builders.</summary>
internal sealed class DispatchCacheHelperTests
{
    [Test]
    public async Task BuildU64DelegateCache_MissingTable_ReturnsEmptyArray()
    {
        using Stack stack = _BuildStackWithU64Table();

        ParseDelegate?[] cache = stack.BuildU64DelegateCache(ProtocolTableId.Invalid, 256);

        await Assert.That(cache.Length).IsEqualTo(256);
        await Assert.That(cache[0]).IsNull();
    }

    [Test]
    public async Task BuildU64DelegateCache_NegativeDomainSize_ThrowsArgumentOutOfRangeException()
    {
        using Stack stack = _BuildStackWithU64Table();

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => stack.BuildU64DelegateCache(ProtocolTableId.Invalid, -1));

        await Assert.That(ex.ParamName).IsEqualTo("domainSize");
    }

    [Test]
    public async Task BuildU64DelegateCache_ZeroDomainSize_ThrowsArgumentOutOfRangeException()
    {
        using Stack stack = _BuildStackWithU64Table();

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => stack.BuildU64DelegateCache(ProtocolTableId.Invalid, 0));

        await Assert.That(ex.ParamName).IsEqualTo("domainSize");
    }

    [Test]
    public async Task BuildU64DelegateCache_DomainSizeAboveMax_ThrowsArgumentOutOfRangeException()
    {
        using Stack stack = _BuildStackWithU64Table();

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => stack.BuildU64DelegateCache(ProtocolTableId.Invalid, 65_537));

        await Assert.That(ex.ParamName).IsEqualTo("domainSize");
    }

    [Test]
    public async Task BuildU64DelegateCache_RegisteredKey_ResolvesDelegate()
    {
        using Stack stack = _BuildStackWithU64Table(out ProtocolTableId tableId, out ProtocolId childId);

        ParseDelegate?[] cache = stack.BuildU64DelegateCache(tableId, 256);

        await Assert.That(cache[0x42]).IsNotNull();
        await Assert.That(cache[0x43]).IsNull();
    }

    [Test]
    public async Task BuildU64SparseDelegateCache_MissingTable_ReturnsEmpty()
    {
        using Stack stack = _BuildStackWithU64Table();

        (ulong Key, ParseDelegate Parse)[] cache = stack.BuildU64SparseDelegateCache(ProtocolTableId.Invalid);

        await Assert.That(cache.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BuildU64SparseDelegateCache_RegisteredKey_ReturnsEntry()
    {
        using Stack stack = _BuildStackWithU64Table(out ProtocolTableId tableId, out _);

        (ulong Key, ParseDelegate Parse)[] cache = stack.BuildU64SparseDelegateCache(tableId);

        await Assert.That(cache.Length).IsEqualTo(1);
        await Assert.That(cache[0].Key).IsEqualTo(0x42UL);
        await Assert.That(cache[0].Parse).IsNotNull();
    }

    [Test]
    public async Task BuildU64IdCache_MissingTable_ReturnsEmptyArray()
    {
        using Stack stack = _BuildStackWithU64Table();

        ProtocolId?[] cache = stack.BuildU64IdCache(ProtocolTableId.Invalid, 256);

        await Assert.That(cache.Length).IsEqualTo(256);
        await Assert.That(cache[0]).IsNull();
    }

    [Test]
    public async Task BuildU64IdCache_NegativeDomainSize_ThrowsArgumentOutOfRangeException()
    {
        using Stack stack = _BuildStackWithU64Table();

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => stack.BuildU64IdCache(ProtocolTableId.Invalid, -1));

        await Assert.That(ex.ParamName).IsEqualTo("domainSize");
    }

    [Test]
    public async Task BuildU64IdCache_RegisteredKey_StoresProtocolId()
    {
        using Stack stack = _BuildStackWithU64Table(out ProtocolTableId tableId, out ProtocolId childId);

        ProtocolId?[] cache = stack.BuildU64IdCache(tableId, 256);

        await Assert.That(cache[0x42]).IsEqualTo(childId);
    }

    [Test]
    public async Task BuildU64SparseIdCache_MissingTable_ReturnsEmpty()
    {
        using Stack stack = _BuildStackWithU64Table();

        (ulong Key, ProtocolId Id)[] cache = stack.BuildU64SparseIdCache(ProtocolTableId.Invalid);

        await Assert.That(cache.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BuildU64SparseIdCache_RegisteredKey_ReturnsEntry()
    {
        using Stack stack = _BuildStackWithU64Table(out ProtocolTableId tableId, out ProtocolId childId);

        (ulong Key, ProtocolId Id)[] cache = stack.BuildU64SparseIdCache(tableId);

        await Assert.That(cache.Length).IsEqualTo(1);
        await Assert.That(cache[0].Key).IsEqualTo(0x42UL);
        await Assert.That(cache[0].Id).IsEqualTo(childId);
    }

    private static Stack _BuildStackWithU64Table()
        => _BuildStackWithU64Table(out _, out _);

    private static Stack _BuildStackWithU64Table(out ProtocolTableId tableId, out ProtocolId childId)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubProtocol child = new("child", "Child");
        childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);

        StubProtocol parent = new("parent", "Parent");
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        tableId = builder.RegisterProtocolTable("parent.type", "Parent Type", ProtocolTableKeyType.U64);
        builder.RegisterParserInU64Table(tableId, 0x42, childId);

        return builder.Build();
    }

    private sealed class StubProtocol(string name, string uiName) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
            => _ = builder.RegisterField(protocolId, $"{name}.field", "Field", FieldType.None);

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => data.Length;
    }
}
