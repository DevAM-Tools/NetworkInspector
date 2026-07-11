// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="Stack"/> lifecycle: Shutdown idempotency and Dispose safety
/// (regression for HIGH-2).
/// </summary>
internal sealed class StackTests
{
    private static Stack _BuildStack(params IProtocol[] protocols)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        foreach (IProtocol protocol in protocols)
        {
            builder.RegisterProtocol(protocol);
        }
        return builder.Build();
    }

    // === Shutdown idempotency (regression for HIGH-2) ===

    [Test]
    public async Task Shutdown_CalledTwice_InvokesOnShutdownOnce()
    {
        // Regression for HIGH-2: without the _ShutdownFlag once-gate, calling Shutdown
        // twice would invoke OnShutdown twice on every registered protocol.
        CountingProto proto = new();
        using Stack stack = _BuildStack(proto);

        stack.Shutdown();
        stack.Shutdown(); // must be a no-op

        await Assert.That(proto.ShutdownCount).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_ThenDispose_InvokesOnShutdownOnce()
    {
        // Calling Shutdown explicitly and then letting Dispose run via `using` must still
        // invoke OnShutdown exactly once.
        CountingProto proto = new();
        using Stack stack = _BuildStack(proto);

        stack.Shutdown();
        // Dispose is called by `using` — it calls Shutdown again internally.

        await Assert.That(proto.ShutdownCount).IsEqualTo(1);
    }

    [Test]
    public async Task Shutdown_WithNoProtocols_DoesNotThrow()
    {
        // An empty stack should shutdown without error.
        using Stack stack = _BuildStack();
        Exception? ex = null;
        try
        {
            stack.Shutdown();
        }
        catch (Exception e)
        {
            ex = e;
        }
        await Assert.That(ex).IsNull();
    }

    // === Stack introspection and dispatch (exit-point coverage) ===

    private static (Stack Stack, ProtocolId ChildId, ProtocolTableId U64Table, ProtocolTableId StringTable,
        ProtocolTableId BytesTable, ProtocolTableId BoolTable, ProtocolTableId AnyTable,
        HeuristicProtocolTableId HeuristicTable, IndexGroupId IndexGroup, FieldAliasGroupId AliasGroup)
        _BuildFullTableStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        DispatchSpyProtocol child = new("child", "Child");
        DispatchSpyProtocol parent = new("parent", "Parent");
        ProtocolId childId = builder.RegisterProtocol(child);
        ProtocolId parentId = builder.RegisterProtocol(parent);
        child.RegisterFields(builder, childId);
        parent.RegisterFields(builder, parentId);

        ProtocolTableId u64Table = builder.RegisterProtocolTable("tbl.u64", "U64", ProtocolTableKeyType.U64);
        ProtocolTableId stringTable = builder.RegisterProtocolTable("tbl.str", "String", ProtocolTableKeyType.String);
        ProtocolTableId bytesTable = builder.RegisterProtocolTable("tbl.bytes", "Bytes", ProtocolTableKeyType.Bytes);
        ProtocolTableId boolTable = builder.RegisterProtocolTable("tbl.bool", "Bool", ProtocolTableKeyType.Bool);
        ProtocolTableId anyTable = builder.RegisterProtocolTable("tbl.any", "Any", ProtocolTableKeyType.Any);

        builder.RegisterParserInU64Table(u64Table, 42, childId);
        builder.RegisterParserInStringTable(stringTable, "key", childId);
        builder.RegisterParserInBytesTable(bytesTable, new BytesKey([0xAB]), childId);
        builder.RegisterParserInBoolTable(boolTable, true, childId);
        builder.RegisterParserInAnyTable(anyTable, childId);

        HeuristicProtocolTableId heuristicTable = builder.RegisterHeuristicProtocolTable(
            parentId, "tbl.heuristic", "Heuristic");
        builder.RegisterHeuristicParser(heuristicTable, new MatchPrefixHeuristicParser(childId, [0xBE, 0xEF]));

        FieldId fieldA = builder.RegisterFieldInGroup(parentId, "parent.a", "A", FieldType.U64, "grp.one");
        FieldId fieldB = builder.RegisterField(parentId, "parent.b", "B", FieldType.U64);
        IndexGroupId indexGroup = builder.GetFieldIndexGroup(fieldA);
        FieldAliasGroupId aliasGroup = builder.RegisterFieldAliasGroup(parentId, "parent.any", "alias", [fieldA, fieldB]);

        builder.RegisterPostParser(parentId, priority: 1, description: "post");

        Stack stack = builder.Build();
        return (stack, childId, u64Table, stringTable, bytesTable, boolTable, anyTable, heuristicTable, indexGroup, aliasGroup);
    }

    [Test]
    public async Task Stack_ProtocolAndFieldAccess_ExercisesLookupPaths()
    {
        (Stack stack, ProtocolId childId, _, _, _, _, _, _, _, _) = _BuildFullTableStack();
        using (stack)
        {
            await Assert.That(stack.GetProtocol(childId)).IsNotNull();
            await Assert.That(stack.GetProtocol(ProtocolId.Invalid)).IsNull();
            await Assert.That(stack.GetProtocolId("child")).IsEqualTo(childId);
            await Assert.That(stack.GetProtocolId("missing")).IsNull();
            await Assert.That(stack.ProtocolCount).IsGreaterThan(0);
            await Assert.That(stack.Protocols.Length).IsEqualTo(stack.ProtocolCount);
            await Assert.That(stack.FieldCount).IsGreaterThan(0);
            await Assert.That(stack.Fields.Length).IsEqualTo(stack.FieldCount);
            await Assert.That(stack.GetField(FieldId.Invalid)).IsNull();
            await Assert.That(stack.GetFieldId("parent.a")).IsNotNull();
            await Assert.That(stack.GetProtocolTableId("missing.table")).IsNull();
            await Assert.That(stack.GetHeuristicProtocolTableId("missing.heur")).IsNull();
        }
    }

    [Test]
    public async Task Stack_IndexGroupsAndAliasGroups_ExposeMetadata()
    {
        (Stack stack, _, _, _, _, _, _, _, IndexGroupId indexGroup, FieldAliasGroupId aliasGroup) = _BuildFullTableStack();
        using (stack)
        {
            await Assert.That(stack.GetIndexGroup(indexGroup)).IsNotNull();
            await Assert.That(stack.GetIndexGroup(IndexGroupId.Invalid)).IsNull();
            await Assert.That(stack.GetIndexGroupId("grp.one")).IsNotNull();
            await Assert.That(stack.GetIndexGroupId("nope")).IsNull();
            await Assert.That(stack.IndexGroupCount).IsGreaterThan(0);
            await Assert.That(stack.IndexGroups.Length).IsEqualTo(stack.IndexGroupCount);

            FieldAliasGroupInfo? aliasInfo = stack.GetFieldAliasGroup(aliasGroup);
            await Assert.That(aliasInfo).IsNotNull();
            await Assert.That(stack.GetFieldAliasGroup(FieldAliasGroupId.Invalid)).IsNull();
            await Assert.That(stack.GetFieldAliasGroupId("parent.any")).IsEqualTo(aliasGroup);
            await Assert.That(stack.FieldAliasGroupCount).IsEqualTo(1);
            await Assert.That(stack.FieldAliasGroups.Length).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Stack_ProtocolTables_AllKeyTypesResolve()
    {
        (Stack stack, ProtocolId childId, ProtocolTableId u64, ProtocolTableId str, ProtocolTableId bytes,
            ProtocolTableId boolean, ProtocolTableId any, _, _, _) = _BuildFullTableStack();
        using (stack)
        {
            await Assert.That(stack.GetProtocolTableInfo(u64)).IsNotNull();
            await Assert.That(stack.GetProtocolTableId("tbl.u64")).IsEqualTo(u64);
            await Assert.That(stack.ProtocolTableCount).IsGreaterThan(0);
            await Assert.That(stack.ProtocolTableInfos.Length).IsEqualTo(stack.ProtocolTableCount);

            ReadOnlySpan<ProtocolId> u64Hit = stack.GetProtocolsFromU64ProtocolTable(u64, 42);
            ReadOnlySpan<ProtocolId> strHit = stack.GetProtocolsFromStringProtocolTable(str, "key");
            ReadOnlySpan<ProtocolId> bytesHit = stack.GetProtocolsFromBytesProtocolTable(bytes, new BytesKey([0xAB]));
            ReadOnlySpan<ProtocolId> boolHit = stack.GetProtocolsFromBoolProtocolTable(boolean, true);
            ReadOnlySpan<ProtocolId> anyHit = stack.GetProtocolsFromAnyProtocolTable(any);
            ReadOnlySpan<ProtocolId> missStr = stack.GetProtocolsFromStringProtocolTable(ProtocolTableId.Invalid, "key");
            ReadOnlySpan<ProtocolId> missBytes = stack.GetProtocolsFromBytesProtocolTable(ProtocolTableId.Invalid, new BytesKey([0xAB]));
            ReadOnlySpan<ProtocolId> missBool = stack.GetProtocolsFromBoolProtocolTable(ProtocolTableId.Invalid, true);
            ReadOnlySpan<ProtocolId> missAny = stack.GetProtocolsFromAnyProtocolTable(ProtocolTableId.Invalid);

            int u64HitLen = u64Hit.Length;
            ProtocolId strFirst = strHit[0];
            ProtocolId bytesFirst = bytesHit[0];
            ProtocolId boolFirst = boolHit[0];
            ProtocolId anyFirst = anyHit[0];
            bool missStrEmpty = missStr.IsEmpty;
            bool missBytesEmpty = missBytes.IsEmpty;
            bool missBoolEmpty = missBool.IsEmpty;
            bool missAnyEmpty = missAny.IsEmpty;

            await Assert.That(u64HitLen).IsEqualTo(1);
            await Assert.That(strFirst).IsEqualTo(childId);
            await Assert.That(bytesFirst).IsEqualTo(childId);
            await Assert.That(boolFirst).IsEqualTo(childId);
            await Assert.That(anyFirst).IsEqualTo(childId);
            await Assert.That(missStrEmpty).IsTrue();
            await Assert.That(missBytesEmpty).IsTrue();
            await Assert.That(missBoolEmpty).IsTrue();
            await Assert.That(missAnyEmpty).IsTrue();

            bool invalidU64Empty = stack.GetProtocolsFromU64ProtocolTable(ProtocolTableId.Invalid, 0).IsEmpty;
            await Assert.That(invalidU64Empty).IsTrue();
            await Assert.That(stack.GetStringTableEntries(u64)).IsNull();

            IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? u64Entries = stack.GetU64TableEntries(u64);
            IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? strEntries = stack.GetStringTableEntries(str);
            IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? bytesEntries = stack.GetBytesTableEntries(bytes);
            IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? boolEntries = stack.GetBoolTableEntries(boolean);
            ReadOnlyMemory<ProtocolId>? anyIds = stack.GetAnyTableProtocolIds(any);

            await Assert.That(u64Entries).IsNotNull();
            await Assert.That(strEntries!.Any()).IsTrue();
            await Assert.That(bytesEntries!.Any()).IsTrue();
            await Assert.That(boolEntries!.Any()).IsTrue();
            await Assert.That(anyIds!.Value.Length).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Stack_HeuristicTables_MatchAndExposeMetadata()
    {
        (Stack stack, ProtocolId childId, _, _, _, _, _, HeuristicProtocolTableId heuristicTable, _, _) =
            _BuildFullTableStack();
        using (stack)
        {
            await Assert.That(stack.GetHeuristicProtocolTableInfo(heuristicTable)).IsNotNull();
            await Assert.That(stack.GetHeuristicProtocolTableId("tbl.heuristic")).IsEqualTo(heuristicTable);
            await Assert.That(stack.HeuristicProtocolTableCount).IsEqualTo(1);
            await Assert.That(stack.HeuristicProtocolTableInfos.Length).IsEqualTo(1);

            ProtocolId? matched = stack.TryMatchHeuristic(heuristicTable, new byte[] { 0xBE, 0xEF, 0x01 });
            await Assert.That(matched).IsEqualTo(childId);
            await Assert.That(stack.TryMatchHeuristic(HeuristicProtocolTableId.Invalid, ReadOnlyMemory<byte>.Empty)).IsNull();

            HeuristicProtocolTable? table = stack.GetHeuristicProtocolTable(heuristicTable);
            await Assert.That(table).IsNotNull();
            await Assert.That(table!.Name).IsEqualTo("tbl.heuristic");
            await Assert.That(table.Count).IsEqualTo(1);
            await Assert.That(table.IsEmpty).IsFalse();
            await Assert.That(table.Entries.Count).IsEqualTo(1);
            await Assert.That(table.FindByName("prefix")).IsNotNull();
            await Assert.That(table.TryMatchAll(new byte[] { 0xBE, 0xEF })[0]).IsEqualTo(childId);
            await Assert.That(table.TryMatchWithName(new byte[] { 0xBE, 0xEF })!.Value.Id).IsEqualTo(childId);
        }
    }

    [Test]
    public async Task Stack_DispatchHelpers_ResolveProtocolAndDelegate()
    {
        (Stack stack, ProtocolId childId, _, _, _, _, _, _, _, _) = _BuildFullTableStack();
        using (stack)
        {
            await Assert.That(stack.ResolveProtocol(childId)).IsTypeOf<DispatchSpyProtocol>();
            await Assert.That(stack.ResolveProtocol(ProtocolId.Invalid)).IsNull();
            await Assert.That(stack.ResolveParseDelegate(childId)).IsNotNull();
            await Assert.That(stack.ResolveParseDelegate(ProtocolId.Invalid)).IsNull();
            await Assert.That(stack.PostParserCount).IsEqualTo(1);
            await Assert.That(stack.PostParsers.Length).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Stack_CallProtocol_InvokesParseDelegate()
    {
        (Stack stack, ProtocolId childId, _, _, _, _, _, _, _, _) = _BuildFullTableStack();
        using (stack)
        {
            byte[] payload = [0x01];
            Frame frame = Frame.Create(
                new FrameId(1),
                Timestamp.FromSecs(0),
                payload,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            Packet packet = new(new PacketId(1), stack, frame);
            bool success;
            bool isError;
            {
                MutField root = packet.RootFieldMut();
                ParseContext ctx = new(stack);
                success = stack.CallProtocol(childId, in root, ReadOnlyMemory<byte>.Empty, in ctx).IsSuccess;
                isError = stack.CallProtocol(ProtocolId.Invalid, in root, ReadOnlyMemory<byte>.Empty, in ctx).IsError;
            }

            await Assert.That(success).IsTrue();
            await Assert.That(isError).IsTrue();
        }
    }

    [Test]
    public async Task Stack_ProtocolTable_InternalApi_ExercisesAllKeyTypes()
    {
        (Stack stack, ProtocolId childId, ProtocolTableId u64, ProtocolTableId str, ProtocolTableId bytes,
            ProtocolTableId boolean, ProtocolTableId any, _, _, _) = _BuildFullTableStack();
        using (stack)
        {
            ProtocolTable? u64Table = stack.GetProtocolTable(u64);
            ProtocolTable? strTable = stack.GetProtocolTable(str);
            ProtocolTable? bytesTableObj = stack.GetProtocolTable(bytes);
            ProtocolTable? boolTableObj = stack.GetProtocolTable(boolean);
            ProtocolTable? anyTableObj = stack.GetProtocolTable(any);

            await Assert.That(u64Table!.GetU64(42)).IsEqualTo(childId);
            await Assert.That(strTable!.GetString("key")).IsEqualTo(childId);
            await Assert.That(bytesTableObj!.GetBytes(new BytesKey([0xAB]))).IsEqualTo(childId);
            await Assert.That(boolTableObj!.GetBool(true)).IsEqualTo(childId);
            await Assert.That(anyTableObj!.GetAny()).IsEqualTo(childId);
            await Assert.That(u64Table.Count).IsGreaterThan(0);
            await Assert.That(strTable.IsEmpty).IsFalse();
            await Assert.That(u64Table.Info.Name).IsEqualTo("tbl.u64");
            await Assert.That(u64Table.KeyType).IsEqualTo(ProtocolTableKeyType.U64);

            await Assert.That(() => strTable.RegisterU64(1, childId)).Throws<InvalidOperationException>();
            await Assert.That(() => u64Table.RegisterString("x", childId)).Throws<InvalidOperationException>();
            await Assert.That(() => u64Table.RegisterBytes(new BytesKey([1]), childId)).Throws<InvalidOperationException>();
        }
    }

    // === Helpers ===

    private sealed class CountingProto : IProtocol
    {
        /// <summary>Number of times <see cref="OnShutdown"/> has been called.</summary>
        public int ShutdownCount;

        public string Name => "counting";
        public string UiName => "Counting";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;

        public void OnShutdown(Stack stack) => ShutdownCount++;
    }

    private sealed class DispatchSpyProtocol(string name, string uiName) : IProtocol
    {
        private FieldId _DataFieldId;

        public string Name => name;
        public string UiName => uiName;

        public void RegisterFields(StackBuilder builder, ProtocolId protoId)
        {
            _DataFieldId = builder.RegisterField(protoId, $"{name}.data", "Data", FieldType.Bytes);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (data.Length > 0)
            {
                parentField.Append(_DataFieldId, FieldValue.NewBytes(data));
            }
            return 0;
        }
    }

    private sealed class MatchPrefixHeuristicParser(ProtocolId protocolId, byte[] prefix) : IHeuristicParser
    {
        public ProtocolId ProtocolId => protocolId;
        public string Name => "prefix";
        public string UiName => "Prefix";

        public bool Test(ReadOnlyMemory<byte> data)
        {
            if (data.Length < prefix.Length)
            {
                return false;
            }
            ReadOnlySpan<byte> span = data.Span;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (span[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
