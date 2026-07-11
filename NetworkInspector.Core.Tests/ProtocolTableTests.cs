// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Direct tests for <see cref="ProtocolTable"/> registration, lookup, iteration, and metadata.
/// </summary>
internal sealed class ProtocolTableTests
{
    private static ProtocolTable _CreateTable(
        ProtocolTableKeyType keyType, string name = "test.table", string? description = "desc")
    {
        ProtocolTableId id = new(0);
        ProtocolTableInfo info = new(id, name, "Test Table", keyType, description);
        return new ProtocolTable(info);
    }

    private static ProtocolId _Proto(int value) => new(value);

    // === Metadata ===

    [Test]
    public async Task Properties_ExposeInfo()
    {
        ProtocolTableId id = new(3);
        ProtocolTableInfo info = new(id, "eth.type", "Ethernet Type", ProtocolTableKeyType.U64, "EtherType");
        ProtocolTable table = new(info);

        await Assert.That(table.Id).IsEqualTo(id);
        await Assert.That(table.Name).IsEqualTo("eth.type");
        await Assert.That(table.UiName).IsEqualTo("Ethernet Type");
        await Assert.That(table.KeyType).IsEqualTo(ProtocolTableKeyType.U64);
        await Assert.That(table.Description).IsEqualTo("EtherType");
        await Assert.That(table.Info.Name).IsEqualTo("eth.type");
        await Assert.That(ReferenceEquals(table.Info, info)).IsTrue();
    }

    // === U64 table ===

    [Test]
    public async Task U64_RegisterLookupAndGetAll()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.U64);
        ProtocolId p1 = _Proto(1);
        ProtocolId p2 = _Proto(2);

        table.RegisterU64(0x0800, p1);
        table.RegisterU64(0x0800, p2);
        table.RegisterU64(0x86DD, p1);

        await Assert.That(table.GetU64(0x0800)).IsEqualTo(p1);
        await Assert.That(table.GetU64(0x86DD)).IsEqualTo(p1);
        await Assert.That(table.GetU64(0xFFFF)).IsNull();

        ReadOnlySpan<ProtocolId> all = table.GetAllU64(0x0800);
        int allLength = all.Length;
        ProtocolId first = all[0];
        ProtocolId second = all[1];
        bool missingEmpty = table.GetAllU64(99).IsEmpty;

        await Assert.That(allLength).IsEqualTo(2);
        await Assert.That(first).IsEqualTo(p1);
        await Assert.That(second).IsEqualTo(p2);
        await Assert.That(missingEmpty).IsTrue();

        await Assert.That(table.Count).IsEqualTo(2);
        await Assert.That(table.IsEmpty).IsFalse();
    }

    [Test]
    public async Task U64_IterEntries()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.U64);
        ProtocolId p = _Proto(5);
        table.RegisterU64(1, p);
        table.RegisterU64(2, p);

        List<ulong> keys = [];
        foreach (KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>> entry in table.IterU64Entries()!)
        {
            keys.Add(entry.Key);
            await Assert.That(entry.Value.Length).IsEqualTo(1);
            await Assert.That(entry.Value.Span[0]).IsEqualTo(p);
        }
        keys.Sort();
        await Assert.That(keys.Count).IsEqualTo(2);
        await Assert.That(keys[0]).IsEqualTo(1UL);
        await Assert.That(keys[1]).IsEqualTo(2UL);
    }

    [Test]
    public void U64_WrongKeyType_ThrowsOnRegister()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.String);
        _ = Assert.Throws<InvalidOperationException>(() => table.RegisterU64(1, _Proto(1)));
    }

    // === String table ===

    [Test]
    public async Task String_RegisterLookupAndGetAll()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.String);
        ProtocolId p = _Proto(7);

        table.RegisterString("http", p);
        table.RegisterString("https", p);

        await Assert.That(table.GetString("http")).IsEqualTo(p);
        await Assert.That(table.GetString("missing")).IsNull();

        ReadOnlySpan<ProtocolId> all = table.GetAllString("http");
        int allLength = all.Length;
        bool nopeEmpty = table.GetAllString("nope").IsEmpty;

        await Assert.That(allLength).IsEqualTo(1);
        await Assert.That(nopeEmpty).IsTrue();
        await Assert.That(table.Count).IsEqualTo(2);
    }

    [Test]
    public async Task String_IterEntries()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.String);
        ProtocolId p = _Proto(1);
        table.RegisterString("a", p);

        int count = 0;
        foreach (KeyValuePair<string, ReadOnlyMemory<ProtocolId>> entry in table.IterStringEntries()!)
        {
            count++;
            await Assert.That(entry.Key).IsEqualTo("a");
        }
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public void String_WrongKeyType_ThrowsOnRegister()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.U64);
        _ = Assert.Throws<InvalidOperationException>(() => table.RegisterString("x", _Proto(1)));
    }

    // === Bytes table ===

    [Test]
    public async Task Bytes_RegisterLookupAndGetAll()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Bytes);
        BytesKey key = new([0xDE, 0xAD]);
        ProtocolId p = _Proto(3);

        table.RegisterBytes(key, p);

        await Assert.That(table.GetBytes(key)).IsEqualTo(p);
        await Assert.That(table.GetBytes(new BytesKey([0x00]))).IsNull();
        await Assert.That(table.GetAllBytes(key).Length).IsEqualTo(1);
        await Assert.That(table.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Bytes_IterEntries()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Bytes);
        BytesKey key = new([1, 2]);
        table.RegisterBytes(key, _Proto(1));

        int count = 0;
        foreach (KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>> entry in table.IterBytesEntries()!)
        {
            count++;
            await Assert.That(entry.Key).IsEqualTo(key);
        }
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public void Bytes_WrongKeyType_ThrowsOnRegister()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Bool);
        _ = Assert.Throws<InvalidOperationException>(() => table.RegisterBytes(new BytesKey([1]), _Proto(1)));
    }

    // === Bool table ===

    [Test]
    public async Task Bool_RegisterLookupGetAllAndIter()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Bool);
        ProtocolId t = _Proto(10);
        ProtocolId f = _Proto(11);

        table.RegisterBool(true, t);
        table.RegisterBool(false, f);

        await Assert.That(table.GetBool(true)).IsEqualTo(t);
        await Assert.That(table.GetBool(false)).IsEqualTo(f);
        await Assert.That(table.GetAllBool(true).Length).IsEqualTo(1);
        await Assert.That(table.GetAllBool(false).Length).IsEqualTo(1);
        await Assert.That(table.Count).IsEqualTo(2);

        List<bool> keys = [];
        foreach (KeyValuePair<bool, ReadOnlyMemory<ProtocolId>> entry in table.IterBoolEntries()!)
        {
            keys.Add(entry.Key);
        }
        keys.Sort();
        await Assert.That(keys.Count).IsEqualTo(2);
        await Assert.That(keys.Contains(false)).IsTrue();
        await Assert.That(keys.Contains(true)).IsTrue();
    }

    [Test]
    public async Task Bool_EmptyTable()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Bool);
        await Assert.That(table.IsEmpty).IsTrue();
        await Assert.That(table.GetBool(true)).IsNull();
        await Assert.That(table.GetAllBool(true).IsEmpty).IsTrue();

        int count = 0;
        foreach (KeyValuePair<bool, ReadOnlyMemory<ProtocolId>> _ in table.IterBoolEntries()!)
        {
            count++;
        }
        await Assert.That(count).IsEqualTo(0);
    }

    // === Any table ===

    [Test]
    public async Task Any_RegisterLookupGetAllAndProtocolIds()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Any);
        ProtocolId p1 = _Proto(1);
        ProtocolId p2 = _Proto(2);

        table.RegisterAny(p1);
        table.RegisterAny(p2);

        await Assert.That(table.GetAny()).IsEqualTo(p1);
        ReadOnlySpan<ProtocolId> all = table.GetAllAny();
        int allLength = all.Length;
        await Assert.That(allLength).IsEqualTo(2);
        await Assert.That(table.Count).IsEqualTo(2);

        ReadOnlyMemory<ProtocolId>? ids = table.GetAnyProtocolIds();
        await Assert.That(ids).IsNotNull();
        await Assert.That(ids!.Value.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Any_EmptyTable()
    {
        ProtocolTable table = _CreateTable(ProtocolTableKeyType.Any);
        await Assert.That(table.IsEmpty).IsTrue();
        await Assert.That(table.GetAny()).IsNull();
        await Assert.That(table.GetAllAny().IsEmpty).IsTrue();
        ReadOnlyMemory<ProtocolId>? ids = table.GetAnyProtocolIds();
        await Assert.That(ids).IsNotNull();
        await Assert.That(ids!.Value.IsEmpty).IsTrue();
    }

    // === Wrong-type iterators return null ===

    [Test]
    public async Task Iterators_ReturnNullForWrongKeyType()
    {
        ProtocolTable u64 = _CreateTable(ProtocolTableKeyType.U64);
        await Assert.That(u64.IterStringEntries()).IsNull();
        await Assert.That(u64.IterBytesEntries()).IsNull();
        await Assert.That(u64.IterBoolEntries()).IsNull();
        await Assert.That(u64.GetAnyProtocolIds()).IsNull();

        ProtocolTable any = _CreateTable(ProtocolTableKeyType.Any);
        await Assert.That(any.IterU64Entries()).IsNull();
    }
}
