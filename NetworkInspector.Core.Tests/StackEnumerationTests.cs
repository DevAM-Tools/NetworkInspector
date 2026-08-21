// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Stack"/> and <see cref="StackBuilder"/> enumeration, table dispatch,
/// and metadata accessors that are not covered by registration-focused tests.
/// </summary>
internal sealed class StackEnumerationTests
{
    private static (StackBuilder Builder, ProtocolId Child1, ProtocolId Child2, ProtocolTableId U64Table,
        ProtocolTableId StringTable, ProtocolTableId BytesTable, ProtocolTableId BoolTable,
        ProtocolTableId AnyTable, IndexGroupId IndexGroup, FieldAliasGroupId AliasGroup)
        _BuildRichBuilder()
    {
        SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        ProtocolId child1 = builder.RegisterProtocol(new StubProtocol("child1", "Child 1"));
        ProtocolId child2 = builder.RegisterProtocol(new StubProtocol("child2", "Child 2"));

        ProtocolTableId u64 = builder.RegisterProtocolTable("test.u64", "U64", ProtocolTableKeyType.U64);
        ProtocolTableId str = builder.RegisterProtocolTable("test.str", "String", ProtocolTableKeyType.String);
        ProtocolTableId bytes = builder.RegisterProtocolTable("test.bytes", "Bytes", ProtocolTableKeyType.Bytes);
        ProtocolTableId boolean = builder.RegisterProtocolTable("test.bool", "Bool", ProtocolTableKeyType.Bool);
        ProtocolTableId any = builder.RegisterProtocolTable("test.any", "Any", ProtocolTableKeyType.Any);

        builder.RegisterParserInU64Table(u64, 0x0800, child1);
        builder.RegisterParserInU64Table(u64, 0x86DD, child2);
        builder.RegisterParserInStringTable(str, "http", child1);
        builder.RegisterParserInBytesTable(bytes, new BytesKey([0xAA]), child2);
        builder.RegisterParserInBoolTable(boolean, true, child1);
        builder.RegisterParserInBoolTable(boolean, false, child2);
        builder.RegisterParserInAnyTable(any, child1);
        builder.RegisterParserInAnyTable(any, child2);

        ProtocolId owner = builder.RegisterProtocol(new StubProtocol("owner", "Owner"));
        FieldId f1 = builder.RegisterFieldInGroup(owner, "owner.f1", "F1", FieldType.U64, "idx.group");
        FieldId f2 = builder.RegisterField(owner, "owner.f2", "F2", FieldType.String);
        IndexGroupId idx = builder.GetFieldIndexGroup(f1);
        FieldAliasGroupId alias = builder.RegisterFieldAliasGroup(owner, "owner.any", "alias", [f1, f2]);

        return (builder, child1, child2, u64, str, bytes, boolean, any, idx, alias);
    }

    [Test]
    public async Task Builder_EnumerationProperties_ArePopulated()
    {
        (StackBuilder builder, ProtocolId c1, _, _, _, _, _, _, IndexGroupId idx, FieldAliasGroupId alias) =
            _BuildRichBuilder();

        await Assert.That(builder.ProtocolCount).IsGreaterThan(2);
        await Assert.That(builder.Protocols.Length).IsEqualTo(builder.ProtocolCount);
        await Assert.That(builder.FieldCount).IsGreaterThan(2);
        await Assert.That(builder.Fields.Length).IsEqualTo(builder.FieldCount);
        await Assert.That(builder.IndexGroupCount).IsGreaterThan(0);
        await Assert.That(builder.IndexGroups.Length).IsGreaterThan(0);
        await Assert.That(builder.GetIndexGroup(idx)!.Name).IsEqualTo("idx.group");
        await Assert.That(builder.GetIndexGroupId("idx.group")).IsEqualTo(idx);
        await Assert.That(builder.GetIndexGroup(IndexGroupId.Invalid)).IsNull();
        await Assert.That(builder.GetIndexGroupId("missing")).IsNull();

        await Assert.That(builder.ProtocolTableCount).IsGreaterThan(0);
        await Assert.That(builder.ProtocolTableInfos.Length).IsEqualTo(builder.ProtocolTableCount);
        await Assert.That(builder.FieldAliasGroupCount).IsEqualTo(1);
        await Assert.That(builder.FieldAliasGroups.Length).IsEqualTo(1);
        await Assert.That(builder.GetFieldAliasGroup(alias)!.Name).IsEqualTo("owner.any");
        await Assert.That(builder.PostParsers.Length).IsEqualTo(builder.PostParserCount);
        await Assert.That(builder.GetProtocolId("child1")).IsEqualTo(c1);
        await Assert.That(builder.GetProtocolId("missing")).IsNull();
    }

    [Test]
    public async Task Builder_TableDispatchAndEntries()
    {
        (StackBuilder builder, ProtocolId c1, ProtocolId c2, ProtocolTableId u64, ProtocolTableId str,
            ProtocolTableId bytes, ProtocolTableId boolean, ProtocolTableId any, _, _) = _BuildRichBuilder();

        ReadOnlySpan<ProtocolId> u64Hit = builder.GetProtocolsFromU64ProtocolTable(u64, 0x0800);
        int u64HitLength = u64Hit.Length;
        ProtocolId u64First = u64Hit[0];
        bool u64MissEmpty = builder.GetProtocolsFromU64ProtocolTable(u64, 999).IsEmpty;
        bool invalidEmpty = builder.GetProtocolsFromU64ProtocolTable(ProtocolTableId.Invalid, 1).IsEmpty;

        ProtocolId httpProto = builder.GetProtocolsFromStringProtocolTable(str, "http")[0];
        ProtocolId bytesProto = builder.GetProtocolsFromBytesProtocolTable(bytes, new BytesKey([0xAA]))[0];
        ProtocolId boolTrueProto = builder.GetProtocolsFromBoolProtocolTable(boolean, true)[0];
        ProtocolId boolFalseProto = builder.GetProtocolsFromBoolProtocolTable(boolean, false)[0];
        int anyLength = builder.GetProtocolsFromAnyProtocolTable(any).Length;

        await Assert.That(u64HitLength).IsEqualTo(1);
        await Assert.That(u64First).IsEqualTo(c1);
        await Assert.That(u64MissEmpty).IsTrue();
        await Assert.That(invalidEmpty).IsTrue();
        await Assert.That(httpProto).IsEqualTo(c1);
        await Assert.That(bytesProto).IsEqualTo(c2);
        await Assert.That(boolTrueProto).IsEqualTo(c1);
        await Assert.That(boolFalseProto).IsEqualTo(c2);
        await Assert.That(anyLength).IsEqualTo(2);

        int u64Entries = 0;
        foreach (KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>> _ in builder.GetU64TableEntries(u64)!)
        {
            u64Entries++;
        }
        await Assert.That(u64Entries).IsEqualTo(2);

        await Assert.That(builder.GetProtocolTableId("test.u64")).IsEqualTo(u64);
        await Assert.That(builder.GetProtocolTableInfo(u64)!.Name).IsEqualTo("test.u64");
        await Assert.That(builder.GetAnyTableProtocolIds(any)!.Value.Length).IsEqualTo(2);
        await Assert.That(builder.GetU64TableEntries(ProtocolTableId.Invalid)).IsNull();
    }

    [Test]
    public async Task Stack_EnumerationAndTableDispatch_MirrorBuilder()
    {
        (StackBuilder builder, ProtocolId c1, ProtocolId c2, ProtocolTableId u64, ProtocolTableId str,
            ProtocolTableId bytes, ProtocolTableId boolean, ProtocolTableId any, IndexGroupId idx,
            FieldAliasGroupId alias) = _BuildRichBuilder();

        using Stack stack = builder.Build();

        await Assert.That(stack.ProtocolCount).IsEqualTo(builder.ProtocolCount);
        await Assert.That(stack.Protocols.Length).IsEqualTo(stack.ProtocolCount);
        await Assert.That(stack.GetProtocol(c1)!.Name).IsEqualTo("child1");
        await Assert.That(stack.GetProtocolId("child2")).IsEqualTo(c2);
        await Assert.That(stack.GetProtocol(ProtocolId.Invalid)).IsNull();

        await Assert.That(stack.FieldCount).IsGreaterThan(0);
        await Assert.That(stack.Fields.Length).IsEqualTo(stack.FieldCount);
        await Assert.That(stack.GetFieldId("owner.f1")).IsNotNull();
        await Assert.That(stack.GetIndexGroup(idx)!.Name).IsEqualTo("idx.group");
        await Assert.That(stack.GetIndexGroupId("idx.group")).IsEqualTo(idx);
        await Assert.That(stack.IndexGroups.Length).IsEqualTo(stack.IndexGroupCount);

        await Assert.That(stack.FieldAliasGroupCount).IsEqualTo(1);
        await Assert.That(stack.GetFieldAliasGroup(alias)!.MemberCount).IsEqualTo(2);
        await Assert.That(stack.FieldAliasGroups.Length).IsEqualTo(1);

        await Assert.That(stack.ProtocolTableCount).IsGreaterThan(0);
        await Assert.That(stack.GetProtocolTableInfo(u64)!.KeyType).IsEqualTo(ProtocolTableKeyType.U64);
        await Assert.That(stack.GetProtocolTableId("test.str")).IsEqualTo(str);
        await Assert.That(stack.ProtocolTableInfos.Length).IsEqualTo(stack.ProtocolTableCount);
        await Assert.That(stack.PostParsers.Length).IsEqualTo(stack.PostParserCount);

        ProtocolId stackU64 = stack.GetProtocolsFromU64ProtocolTable(u64, 0x86DD)[0];
        ProtocolId stackHttp = stack.GetProtocolsFromStringProtocolTable(str, "http")[0];
        ProtocolId stackBytes = stack.GetProtocolsFromBytesProtocolTable(bytes, new BytesKey([0xAA]))[0];
        ProtocolId stackBoolTrue = stack.GetProtocolsFromBoolProtocolTable(boolean, true)[0];
        int stackAnyLength = stack.GetProtocolsFromAnyProtocolTable(any).Length;

        await Assert.That(stackU64).IsEqualTo(c2);
        await Assert.That(stackHttp).IsEqualTo(c1);
        await Assert.That(stackBytes).IsEqualTo(c2);
        await Assert.That(stackBoolTrue).IsEqualTo(c1);
        await Assert.That(stackAnyLength).IsEqualTo(2);

        int stringEntries = 0;
        foreach (KeyValuePair<string, ReadOnlyMemory<ProtocolId>> _ in stack.GetStringTableEntries(str)!)
        {
            stringEntries++;
        }
        await Assert.That(stringEntries).IsEqualTo(1);

        ReadOnlyMemory<ProtocolId>? anyIds = stack.GetAnyTableProtocolIds(any);
        await Assert.That(anyIds!.Value.Length).IsEqualTo(2);
        await Assert.That(stack.GetU64TableEntries(ProtocolTableId.Invalid)).IsNull();
        await Assert.That(stack.IncludeExceptionStackTrace).IsFalse();
        await Assert.That(stack.Settings.SettingCount).IsEqualTo(0);
    }

    private sealed class StubProtocol(string name, string uiName) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }
}
