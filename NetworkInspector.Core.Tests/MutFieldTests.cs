// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for <see cref="MutField"/> field-building and accessor APIs.</summary>
internal sealed class MutFieldTests
{
    private static (Stack Stack, MutFieldExerciseProtocol Proto, ProtocolId ProtoId) _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        MutFieldExerciseProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        Stack stack = builder.Build();
        return (stack, proto, protoId);
    }

    private static Packet _ParseFrame(Stack stack, ProtocolId firstProtocolId)
    {
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            new byte[14],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(0), stack, frame, firstProtocolId);
    }

    private static bool _IsProtocolTableMissing(ParseResult result)
    {
        if (!result.IsError)
        {
            return false;
        }

        if (!result.TryGetError(out ParseError error))
        {
            return false;
        }

        return error.Kind == ParseErrorKind.ProtocolTableMissing;
    }

    /// <summary>Valid array-index id that is not registered on a typical test stack.</summary>
    private static readonly ProtocolTableId _DanglingTableId = new(50_000);

    /// <summary>Valid array-index heuristic id that is not registered on a typical test stack.</summary>
    private static readonly HeuristicProtocolTableId _DanglingHeuristicTableId = new(50_000);

    [Test]
    public async Task MutField_Parse_ExercisesBuilderApis()
    {
        (Stack? stack, MutFieldExerciseProtocol proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            MutField root = packet.RootFieldMut();

            FieldId rootFieldId = root.FieldId;
            Infos.FieldInfo? fieldInfo = root.FieldInfo;
            Packet rootPacket = root.Packet;
            FieldType valueType = root.Value.Type;
            bool customIsNull = root.CustomText.IsNull;
            string info = packet.Info;
            bool exerciseCompleted = proto.ExerciseCompleted;

            FieldId expectedRootFieldId = packet.GetFieldRef(0).FieldId;

            await Assert.That(exerciseCompleted).IsTrue();
            await Assert.That(rootFieldId).IsEqualTo(expectedRootFieldId);
            await Assert.That(fieldInfo).IsNotNull();
            await Assert.That(rootPacket).IsSameReferenceAs(packet);
            await Assert.That(valueType).IsEqualTo(FieldType.U64);
            await Assert.That(customIsNull).IsTrue();
            await Assert.That(info).Contains("start ");
            await Assert.That(info).Contains(" more");
        }
    }

    [Test]
    public async Task MutField_TryGetFieldMutAt_ReturnsMutableHandle()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);

            bool found = packet.TryGetFieldMutAt(0, out MutField root);
            bool rootValid = root.IsValid;
            FieldId rootFieldId = root.FieldId;
            bool missing = packet.TryGetFieldMutAt(999, out MutField missingField);
            bool missingValid = missingField.IsValid;

            await Assert.That(found).IsTrue();
            await Assert.That(rootValid).IsTrue();
            await Assert.That(rootFieldId).IsEqualTo(packet.RootField().FieldId);
            await Assert.That(missing).IsFalse();
            await Assert.That(missingValid).IsFalse();
        }
    }

    [Test]
    public async Task MutField_IsValidAndEquality_MatchFieldSemantics()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            MutField rootMut = packet.RootFieldMut();
            Field rootField = packet.RootField();

            bool mutValid = rootMut.IsValid;
            bool fieldValid = rootField.IsValid;
            bool defaultInvalid = default(MutField).IsValid;

            bool sameEquals = rootMut.Equals(packet.RootFieldMut());
            rootMut.TryGetFirstChild(out MutField child, materialize: true);
            bool differentEquals = rootMut.Equals(child);
            Field asField = rootMut.AsField();

            await Assert.That(mutValid).IsTrue();
            await Assert.That(fieldValid).IsTrue();
            await Assert.That(defaultInvalid).IsFalse();
            await Assert.That(sameEquals).IsTrue();
            await Assert.That(differentEquals).IsFalse();
            await Assert.That(asField.StorageIndex).IsEqualTo(rootField.StorageIndex);
            await Assert.That(asField.FieldId).IsEqualTo(rootField.FieldId);
        }
    }

    [Test]
    public async Task MutField_MaterializeIfLazy_ReturnsExpected()
    {
        (Stack? stack, MutFieldExerciseProtocol proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            bool found = packet.TryGetFieldMutAt(proto.LazyContainerIndex, out MutField lazy);

            bool first = lazy.MaterializeIfLazy();
            bool second = lazy.MaterializeIfLazy();

            await Assert.That(found).IsTrue();
            await Assert.That(first).IsTrue();
            await Assert.That(second).IsFalse();
        }
    }

    [Test]
    public async Task MutField_TreeNavigation_MatchesFieldRoundtrip()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            MutField rootMut = packet.RootFieldMut();
            Field rootField = rootMut.AsField();

            bool isRoot = rootMut.IsRoot;
            bool hasParent = rootMut.TryGetParent(out _);
            bool hasPrev = rootMut.TryGetPrev(out _);

            bool mutHasFirst = rootMut.TryGetFirstChild(out MutField mutChild, materialize: true); // materialize: true — navigate/populate children including lazy
            bool fieldHasFirst = rootField.TryGetFirstChild(out Field fieldChild, materialize: true); // materialize: true — navigate/populate children including lazy
            FieldId mutChildId = mutChild.FieldId;
            ushort mutChildIndex = mutChild.StorageIndex;
            FieldId fieldChildId = fieldChild.FieldId;
            ushort fieldChildIndex = fieldChild.StorageIndex;

            bool mutHasChildren = rootMut.HasChildren(materialize: true); // materialize: true — navigate/populate children including lazy
            bool fieldHasChildren = rootField.HasChildren(materialize: true); // materialize: true — navigate/populate children including lazy
            ushort mutChildCount = rootMut.ChildCount(materialize: true); // materialize: true — navigate/populate children including lazy
            ushort fieldChildCount = rootField.ChildCount(materialize: true); // materialize: true — navigate/populate children including lazy

            bool mutHasLast = rootMut.TryGetLastChild(out MutField mutLast, materialize: true); // materialize: true — navigate/populate children including lazy
            bool fieldHasLast = rootField.TryGetLastChild(out Field fieldLast, materialize: true); // materialize: true — navigate/populate children including lazy
            FieldId mutLastId = mutLast.FieldId;
            FieldId fieldLastId = fieldLast.FieldId;
            ushort mutLastIndex = mutLast.StorageIndex;

            FieldId rootFieldId = rootMut.FieldId;
            bool parentOk = false;
            FieldId parentId = default;
            FieldId mutNextId = default;
            FieldId fieldNextId = default;
            bool prevOk = false;
            FieldId mutPrevId = default;
            FieldId fieldPrevId = default;
            bool comparePrev = false;

            if (mutHasFirst)
            {
                parentOk = mutChild.TryGetParent(out MutField parent);
                parentId = parent.FieldId;

                mutChild.TryGetNext(out MutField mutNext);
                fieldChild.TryGetNext(out Field fieldNext);
                mutNextId = mutNext.FieldId;
                fieldNextId = fieldNext.FieldId;

                if (mutHasLast && mutLastIndex != mutChildIndex)
                {
                    comparePrev = true;
                    prevOk = mutLast.TryGetPrev(out MutField mutPrev);
                    fieldLast.TryGetPrev(out Field fieldPrev);
                    mutPrevId = mutPrev.FieldId;
                    fieldPrevId = fieldPrev.FieldId;
                }
            }

            await Assert.That(isRoot).IsTrue();
            await Assert.That(hasParent).IsFalse();
            await Assert.That(hasPrev).IsFalse();
            await Assert.That(mutHasFirst).IsEqualTo(fieldHasFirst);
            await Assert.That(mutChildId).IsEqualTo(fieldChildId);
            await Assert.That(mutChildIndex).IsEqualTo(fieldChildIndex);
            await Assert.That(mutHasChildren).IsEqualTo(fieldHasChildren);
            await Assert.That(mutChildCount).IsEqualTo(fieldChildCount);
            await Assert.That(mutHasLast).IsEqualTo(fieldHasLast);
            await Assert.That(mutLastId).IsEqualTo(fieldLastId);

            if (mutHasFirst)
            {
                await Assert.That(parentOk).IsTrue();
                await Assert.That(parentId).IsEqualTo(rootFieldId);
                await Assert.That(mutNextId).IsEqualTo(fieldNextId);

                if (comparePrev)
                {
                    await Assert.That(prevOk).IsTrue();
                    await Assert.That(mutPrevId).IsEqualTo(fieldPrevId);
                }
            }
        }
    }

    [Test]
    public async Task MutField_LeafNavigation_ReturnsFalseExits()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            MutField root = packet.RootFieldMut();
            root.TryGetLastChild(out MutField cursor, materialize: true); // materialize: true — navigate/populate children including lazy
            while (cursor.TryGetLastChild(out MutField child, materialize: true)) // materialize: true — navigate/populate children including lazy
            {
                cursor = child;
            }

            bool hasFirst = cursor.TryGetFirstChild(out _, materialize: true); // materialize: true — navigate/populate children including lazy
            bool hasLast = cursor.TryGetLastChild(out _, materialize: true); // materialize: true — navigate/populate children including lazy
            bool hasChildren = cursor.HasChildren(materialize: true); // materialize: true — navigate/populate children including lazy
            ushort childCount = cursor.ChildCount(materialize: true); // materialize: true — navigate/populate children including lazy
            bool hasNext = cursor.TryGetNext(out _);

            await Assert.That(hasFirst).IsFalse();
            await Assert.That(hasLast).IsFalse();
            await Assert.That(hasChildren).IsFalse();
            await Assert.That(childCount).IsEqualTo((ushort)0);
            await Assert.That(hasNext).IsFalse();
        }
    }

    [Test]
    public async Task MutField_LazyContainer_MaterializeFalse_ReportsNoChildren()
    {
        (Stack? stack, MutFieldExerciseProtocol proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            bool found = packet.TryGetFieldMutAt(proto.LazyContainerIndex, out MutField lazy);

            bool hasChildrenFalse = lazy.HasChildren(materialize: false); // materialize: false — eager-only; do not populate lazy containers
            ushort childCountFalse = lazy.ChildCount(materialize: false); // materialize: false — eager-only; do not populate lazy containers
            bool firstFalse = lazy.TryGetFirstChild(out _, materialize: false); // materialize: false — eager-only; do not populate lazy containers
            bool lastFalse = lazy.TryGetLastChild(out _, materialize: false); // materialize: false — eager-only; do not populate lazy containers

            bool hasChildrenTrue = lazy.HasChildren(materialize: true); // materialize: true — navigate/populate children including lazy
            ushort childCountTrue = lazy.ChildCount(materialize: true); // materialize: true — navigate/populate children including lazy

            await Assert.That(found).IsTrue();
            await Assert.That(hasChildrenFalse).IsFalse();
            await Assert.That(childCountFalse).IsEqualTo((ushort)0);
            await Assert.That(firstFalse).IsFalse();
            await Assert.That(lastFalse).IsFalse();
            await Assert.That(hasChildrenTrue).IsTrue();
            await Assert.That(childCountTrue).IsGreaterThan((ushort)0);
        }
    }

    [Test]
    public async Task MutField_AsField_AndIteration_Roundtrip()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, protoId);
            MutField rootMut = packet.RootFieldMut();
            Field rootField = rootMut.AsField();

            int mutChildCount = 0;
            foreach (MutField _ in rootMut.Children(materialize: true)) // materialize: true — navigate/populate children including lazy
            {
                mutChildCount++;
            }

            int fieldChildCount = 0;
            foreach (Field _ in rootField.Children(materialize: true)) // materialize: true — navigate/populate children including lazy
            {
                fieldChildCount++;
            }

            int mutDescendantCount = 0;
            foreach (MutField _ in rootMut.Descendants(materialize: true)) // materialize: true — navigate/populate children including lazy
            {
                mutDescendantCount++;
            }

            int fieldDescendantCount = 0;
            foreach (Field _ in rootField.Descendants(materialize: true)) // materialize: true — navigate/populate children including lazy
            {
                fieldDescendantCount++;
            }

            await Assert.That(rootField.FieldId).IsEqualTo(rootMut.FieldId);
            await Assert.That(rootField.Packet).IsSameReferenceAs(packet);
            await Assert.That(mutChildCount).IsEqualTo(fieldChildCount);
            await Assert.That(mutDescendantCount).IsEqualTo(fieldDescendantCount);
            await Assert.That(mutChildCount).IsGreaterThan(0);
            await Assert.That(mutDescendantCount).IsGreaterThan(mutChildCount);
        }
    }

    [Test]
    public async Task MutField_CallProtocol_MissingStack_ReturnsInternalError()
    {
        (Stack? stack, MutFieldExerciseProtocol proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = [0x01];
            Frame frame = Frame.Create(
                new FrameId(1), Timestamp.FromSecs(0), data, LinkType.Ethernet,
                FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
            Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId);
            MutField root = packet.RootFieldMut();

            ParseResult result = root.CallProtocol(protoId, data, default);

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.TryGetError(out ParseError error)).IsTrue();
            await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.InternalError);
            await Assert.That(error.Message).Contains("Stack");
        }
    }

    [Test]
    public async Task MutField_TryCallNextProtocolU64_NullStack_ReturnsInternalError()
    {
        (Stack? stack, MutFieldExerciseProtocol proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = [0x01];
            Frame frame = Frame.Create(
                new FrameId(1), Timestamp.FromSecs(0), data, LinkType.Ethernet,
                FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
            Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId);
            MutField root = packet.RootFieldMut();

            ParseResult u64 = root.TryCallNextProtocolU64(ProtocolTableId.Invalid, 7, data, default);
            ParseResult str = root.TryCallNextProtocolString(ProtocolTableId.Invalid, "hit", data, default);
            ParseResult bytes = root.TryCallNextProtocolBytes(ProtocolTableId.Invalid, new BytesKey([0x00]), data, default);
            ParseResult boolean = root.TryCallNextProtocolBool(ProtocolTableId.Invalid, true, data, default);
            ParseResult any = root.TryCallNextProtocolAny(ProtocolTableId.Invalid, data, default);
            ParseResult heuristic = root.TryCallHeuristicProtocol(HeuristicProtocolTableId.Invalid, data, default);

            await Assert.That(u64.IsError && str.IsError && bytes.IsError && boolean.IsError && any.IsError && heuristic.IsError).IsTrue();
            await Assert.That(u64.TryGetError(out ParseError error)).IsTrue();
            await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.InternalError);
            await Assert.That(error.Message).Contains("Stack");
        }
    }

    [Test]
    public async Task MutField_TryCallNextProtocol_AllTables_ExerciseFastPaths()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        DispatchCounterProtocol child = new("child");
        ProtocolId childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);

        ProtocolTableId u64Table = builder.RegisterProtocolTable("t.u64", "U64", ProtocolTableKeyType.U64);
        ProtocolTableId strTable = builder.RegisterProtocolTable("t.str", "Str", ProtocolTableKeyType.String);
        ProtocolTableId bytesTable = builder.RegisterProtocolTable("t.bytes", "Bytes", ProtocolTableKeyType.Bytes);
        ProtocolTableId boolTable = builder.RegisterProtocolTable("t.bool", "Bool", ProtocolTableKeyType.Bool);
        ProtocolTableId anyTable = builder.RegisterProtocolTable("t.any", "Any", ProtocolTableKeyType.Any);
        ProtocolTableId emptyAnyTable = builder.RegisterProtocolTable("t.any.empty", "Any Empty", ProtocolTableKeyType.Any);

        builder.RegisterParserInU64Table(u64Table, 7, childId);
        builder.RegisterParserInStringTable(strTable, "hit", childId);
        builder.RegisterParserInBytesTable(bytesTable, new BytesKey([0xAB]), childId);
        builder.RegisterParserInBoolTable(boolTable, true, childId);
        builder.RegisterParserInAnyTable(anyTable, childId);

        DispatchExerciseProtocol parent = new(u64Table, strTable, bytesTable, boolTable, anyTable, emptyAnyTable);
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        Stack stack = builder.Build();
        using (stack)
        {
            byte[] data = [0x01];
            Frame frame = Frame.Create(
                new FrameId(1), Timestamp.FromSecs(0), data, LinkType.Ethernet,
                FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
            Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, parentId);

            await Assert.That(parent.NullTableHits).IsEqualTo(10);
            await Assert.That(parent.EmptyMatchHits).IsEqualTo(5);
            await Assert.That(parent.SingleMatchHits).IsEqualTo(5);
            await Assert.That(child.CallCount).IsEqualTo(5);
            await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThan(1); // materialize: false — current materialized count only
        }
    }

    [Test]
    public async Task MutField_TryCallNextProtocol_MultiMatch_DispatchesAll()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        DispatchCounterProtocol childA = new("child.a");
        DispatchCounterProtocol childB = new("child.b");
        ProtocolId childAId = builder.RegisterProtocol(childA);
        ProtocolId childBId = builder.RegisterProtocol(childB);
        childA.RegisterFields(builder, childAId);
        childB.RegisterFields(builder, childBId);

        ProtocolTableId u64Table = builder.RegisterProtocolTable("multi.u64", "Multi U64", ProtocolTableKeyType.U64);
        builder.RegisterParserInU64Table(u64Table, 9, childAId);
        builder.RegisterParserInU64Table(u64Table, 9, childBId);

        MultiDispatchProtocol parent = new(u64Table);
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        Stack stack = builder.Build();
        using (stack)
        {
            byte[] data = [0x01];
            Frame frame = Frame.Create(
                new FrameId(1), Timestamp.FromSecs(0), data, LinkType.Ethernet,
                FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
            _ = Packet.ParseFrame(new PacketId(0), stack, frame, parentId);

            await Assert.That(parent.MultiMatchHits).IsEqualTo(1);
            await Assert.That(childA.CallCount + childB.CallCount).IsEqualTo(2);
        }
    }

    [Test]
    public async Task MutField_TryCallHeuristicProtocol_ExercisePaths()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        DispatchCounterProtocol child = new("heur.child");
        ProtocolId childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);

        HeuristicExerciseProtocol parent = new();
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        HeuristicProtocolTableId tableId = builder.RegisterHeuristicProtocolTable(
            parentId, "heur.table", "Heuristic Table");
        builder.RegisterHeuristicParser(tableId, new MatchingHeuristicParser(childId));
        parent.SetHeuristicTable(tableId);

        Stack stack = builder.Build();
        using (stack)
        {
            byte[] data = [0xFE];
            Frame frame = Frame.Create(
                new FrameId(1), Timestamp.FromSecs(0), data, LinkType.Ethernet,
                FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
            _ = Packet.ParseFrame(new PacketId(0), stack, frame, parentId);

            await Assert.That(parent.NullTableHit).IsTrue();
            await Assert.That(parent.NoMatchHit).IsTrue();
            await Assert.That(parent.MatchHit).IsTrue();
            await Assert.That(child.CallCount).IsEqualTo(1);
        }
    }

    private sealed class MutFieldExerciseProtocol : IProtocol
    {
        private FieldId _ChainFieldId;
        private FieldId _SiblingFieldId;
        private FieldId _LazyFieldId;
        private FieldId _LazyChildFieldId;
        private ushort _LazyContainerIndex;

        public string Name => "mut.test";
        public string UiName => "MutField Exercise";
        public bool ExerciseCompleted { get; private set; }
        public ushort LazyContainerIndex => _LazyContainerIndex;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ChainFieldId = builder.RegisterField(protocolId, "mut.chain", "Chain", FieldType.None);
            _SiblingFieldId = builder.RegisterField(protocolId, "mut.sib", "Sibling", FieldType.String);
            _LazyFieldId = builder.RegisterField(protocolId, "mut.lazy", "Lazy", FieldType.None);
            _LazyChildFieldId = builder.RegisterField(protocolId, "mut.lazy.child", "Lazy Child", FieldType.String);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            parentField.SetValue(FieldValue.NewU64(7));
            parentField.SetCustomText(new LazyString("custom"));
            _ = parentField.CustomText.AsString;
            parentField.AppendCustomText(new LazyString(" suffix"));
            parentField.ClearCustomText();

            MutField chain = parentField.Append(_ChainFieldId, FieldValue.None);
            MutField current = chain;
            for (int i = 0; i < 17; i++)
            {
                current = current.Append(_ChainFieldId, FieldValue.NewU64((ulong)i));
            }
            current.InsertAfter(_SiblingFieldId, FieldValue.NewString("leaf"));

            FieldId lazyChildFieldId = _LazyChildFieldId;
            MutField lazy = parentField.PrependLazy(
                _LazyFieldId,
                FieldValue.None,
                (in MutField container) =>
                {
                    container.Append(lazyChildFieldId, FieldValue.NewString("lazy-child"));
                    return 0;
                });
            _LazyContainerIndex = lazy.StorageIndex;
            _ = lazy.ChildMut(lazy.StorageIndex);

            parentField.PrependLazyWithCustomText(
                _LazyFieldId,
                FieldValue.None,
                new LazyString("prepend-lazy"),
                (in MutField container) =>
                {
                    container.Append(lazyChildFieldId, FieldValue.NewString("prepend-lazy-child"));
                    return 0;
                });

            parentField.AppendLazyWithCustomText(
                _LazyFieldId,
                FieldValue.None,
                new LazyString("lazy-label"),
                (in MutField container) =>
                {
                    container.Append(lazyChildFieldId, FieldValue.NewString("append-child"));
                    return 0;
                });

            // InsertAfter* cannot target the root field — use a non-root anchor.
            chain.InsertAfterLazy(
                _LazyFieldId,
                FieldValue.None,
                (in MutField container) =>
                {
                    container.Append(lazyChildFieldId, FieldValue.NewString("insert-lazy-child"));
                    return 0;
                });

            chain.InsertAfterLazyWithCustomText(
                _LazyFieldId,
                FieldValue.None,
                new LazyString("insert-lazy"),
                (in MutField container) =>
                {
                    container.Append(lazyChildFieldId, FieldValue.NewString("insert-lazy-custom"));
                    return 0;
                });

            parentField.Prepend(_SiblingFieldId, FieldValue.NewString("prep-eager"));
            parentField.PrependWithCustomText(_SiblingFieldId, "prep", new LazyString("prep-text"));
            chain.InsertAfterWithCustomText(_SiblingFieldId, "after", new LazyString("after-text"));

            parentField.SetPacketInfo(new LazyString("info"));
            parentField.AppendToPacketInfo(new LazyString(" more"));
            parentField.PrependToPacketInfo(new LazyString("start "));
            _ = parentField.PacketInfo;

            ExerciseCompleted = true;
            if (data.Length >= 14)
            {
                return 14;
            }

            return data.Length;
        }
    }

    private sealed class DispatchCounterProtocol(string name) : IProtocol
    {
        public string Name => name;
        public string UiName => name;
        public int CallCount { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            builder.RegisterField(protocolId, $"{name}.field", "Field", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            CallCount++;
            return 0;
        }
    }

    private sealed class DispatchExerciseProtocol(
        ProtocolTableId u64Table,
        ProtocolTableId strTable,
        ProtocolTableId bytesTable,
        ProtocolTableId boolTable,
        ProtocolTableId anyTable,
        ProtocolTableId emptyAnyTable) : IProtocol
    {
        public string Name => "dispatch.parent";
        public string UiName => "Dispatch Parent";
        public int NullTableHits { get; private set; }
        public int EmptyMatchHits { get; private set; }
        public int SingleMatchHits { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            builder.RegisterField(protocolId, $"{Name}.root", "Root", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            ReadOnlyMemory<byte> payload = data;

            // Invalid table id is ProtocolTableMissing (Error), not a key miss.
            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolU64(ProtocolTableId.Invalid, 7, payload, in context)))
            {
                NullTableHits++;
            }
            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolU64(_DanglingTableId, 7, payload, in context)))
            {
                NullTableHits++;
            }
            if (parentField.TryCallNextProtocolU64(u64Table, 99, payload, in context).IsNotDispatched)
            {
                EmptyMatchHits++;
            }
            if (parentField.TryCallNextProtocolU64(u64Table, 7, payload, in context).TryGetConsumed(out _))
            {
                SingleMatchHits++;
            }

            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolString(ProtocolTableId.Invalid, "hit", payload, in context)))
            {
                NullTableHits++;
            }
            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolString(_DanglingTableId, "hit", payload, in context)))
            {
                NullTableHits++;
            }
            if (parentField.TryCallNextProtocolString(strTable, "miss", payload, in context).IsNotDispatched)
            {
                EmptyMatchHits++;
            }
            if (parentField.TryCallNextProtocolString(strTable, "hit", payload, in context).TryGetConsumed(out _))
            {
                SingleMatchHits++;
            }

            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolBytes(ProtocolTableId.Invalid, new BytesKey([0xAB]), payload, in context)))
            {
                NullTableHits++;
            }
            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolBytes(_DanglingTableId, new BytesKey([0xAB]), payload, in context)))
            {
                NullTableHits++;
            }
            if (parentField.TryCallNextProtocolBytes(bytesTable, new BytesKey([0x00]), payload, in context).IsNotDispatched)
            {
                EmptyMatchHits++;
            }
            if (parentField.TryCallNextProtocolBytes(bytesTable, new BytesKey([0xAB]), payload, in context).TryGetConsumed(out _))
            {
                SingleMatchHits++;
            }

            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolBool(ProtocolTableId.Invalid, true, payload, in context)))
            {
                NullTableHits++;
            }
            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolBool(_DanglingTableId, true, payload, in context)))
            {
                NullTableHits++;
            }
            if (parentField.TryCallNextProtocolBool(boolTable, false, payload, in context).IsNotDispatched)
            {
                EmptyMatchHits++;
            }
            if (parentField.TryCallNextProtocolBool(boolTable, true, payload, in context).TryGetConsumed(out _))
            {
                SingleMatchHits++;
            }

            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolAny(ProtocolTableId.Invalid, payload, in context)))
            {
                NullTableHits++;
            }
            if (_IsProtocolTableMissing(parentField.TryCallNextProtocolAny(_DanglingTableId, payload, in context)))
            {
                NullTableHits++;
            }
            if (parentField.TryCallNextProtocolAny(emptyAnyTable, payload, in context).IsNotDispatched)
            {
                EmptyMatchHits++;
            }
            if (parentField.TryCallNextProtocolAny(anyTable, payload, in context).TryGetConsumed(out _))
            {
                SingleMatchHits++;
            }

            return 1;
        }
    }

    private sealed class MultiDispatchProtocol(ProtocolTableId tableId) : IProtocol
    {
        public string Name => "multi.parent";
        public string UiName => "Multi Parent";
        public int MultiMatchHits { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            builder.RegisterField(protocolId, $"{Name}.root", "Root", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (parentField.TryCallNextProtocolU64(tableId, 9, data, in context).TryGetConsumed(out _))
            {
                MultiMatchHits++;
            }
            return 1;
        }
    }

    private sealed class HeuristicExerciseProtocol : IProtocol
    {
        private HeuristicProtocolTableId _TableId;

        public string Name => "heur.parent";
        public string UiName => "Heuristic Parent";
        public bool NullTableHit { get; private set; }
        public bool NoMatchHit { get; private set; }
        public bool MatchHit { get; private set; }

        public void SetHeuristicTable(HeuristicProtocolTableId tableId) => _TableId = tableId;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            builder.RegisterField(protocolId, $"{Name}.root", "Root", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            // Invalid heuristic id is ProtocolTableMissing; TryMatch miss is NotDispatched.
            int missingTableHits = 0;
            if (_IsProtocolTableMissing(parentField.TryCallHeuristicProtocol(HeuristicProtocolTableId.Invalid, data, in context)))
            {
                missingTableHits++;
            }
            if (_IsProtocolTableMissing(parentField.TryCallHeuristicProtocol(_DanglingHeuristicTableId, data, in context)))
            {
                missingTableHits++;
            }

            NullTableHit = missingTableHits == 2;

            if (parentField.TryCallHeuristicProtocol(_TableId, data[..0], in context).IsNotDispatched)
            {
                NoMatchHit = true;
            }

            if (parentField.TryCallHeuristicProtocol(_TableId, data, in context).TryGetConsumed(out _))
            {
                MatchHit = true;
            }

            return 1;
        }
    }

    [Test]
    public async Task MutField_TryCallNextProtocol_AllTables_MultiMatch_DispatchesAll()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        DispatchCounterProtocol childA = new("ma");
        DispatchCounterProtocol childB = new("mb");
        ProtocolId childAId = builder.RegisterProtocol(childA);
        ProtocolId childBId = builder.RegisterProtocol(childB);
        childA.RegisterFields(builder, childAId);
        childB.RegisterFields(builder, childBId);

        ProtocolTableId strTable = builder.RegisterProtocolTable("multi.str", "Multi Str", ProtocolTableKeyType.String);
        ProtocolTableId bytesTable = builder.RegisterProtocolTable("multi.bytes", "Multi Bytes", ProtocolTableKeyType.Bytes);
        ProtocolTableId boolTable = builder.RegisterProtocolTable("multi.bool", "Multi Bool", ProtocolTableKeyType.Bool);
        ProtocolTableId anyTable = builder.RegisterProtocolTable("multi.any", "Multi Any", ProtocolTableKeyType.Any);

        builder.RegisterParserInStringTable(strTable, "x", childAId);
        builder.RegisterParserInStringTable(strTable, "x", childBId);
        builder.RegisterParserInBytesTable(bytesTable, new BytesKey([0x01]), childAId);
        builder.RegisterParserInBytesTable(bytesTable, new BytesKey([0x01]), childBId);
        builder.RegisterParserInBoolTable(boolTable, true, childAId);
        builder.RegisterParserInBoolTable(boolTable, true, childBId);
        builder.RegisterParserInAnyTable(anyTable, childAId);
        builder.RegisterParserInAnyTable(anyTable, childBId);

        MultiDispatchAllTypesProtocol parent = new(strTable, bytesTable, boolTable, anyTable);
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        Stack stack = builder.Build();
        using (stack)
        {
            byte[] data = [0x01];
            Frame frame = Frame.Create(
                new FrameId(1), Timestamp.FromSecs(0), data, LinkType.Ethernet,
                FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
            _ = Packet.ParseFrame(new PacketId(0), stack, frame, parentId);

            await Assert.That(parent.MultiMatchHits).IsEqualTo(4);
            await Assert.That(childA.CallCount + childB.CallCount).IsEqualTo(8);
        }
    }

    private sealed class MultiDispatchAllTypesProtocol(
        ProtocolTableId strTable,
        ProtocolTableId bytesTable,
        ProtocolTableId boolTable,
        ProtocolTableId anyTable) : IProtocol
    {
        public string Name => "multi.all";
        public string UiName => "Multi All";
        public int MultiMatchHits { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ = strTable;
            builder.RegisterField(protocolId, "multi.all.root", "Root", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            ReadOnlyMemory<byte> payload = data;

            if (parentField.TryCallNextProtocolString(strTable, "x", payload, in context).TryGetConsumed(out _))
            {
                MultiMatchHits++;
            }
            if (parentField.TryCallNextProtocolBytes(bytesTable, new BytesKey([0x01]), payload, in context).TryGetConsumed(out _))
            {
                MultiMatchHits++;
            }
            if (parentField.TryCallNextProtocolBool(boolTable, true, payload, in context).TryGetConsumed(out _))
            {
                MultiMatchHits++;
            }
            if (parentField.TryCallNextProtocolAny(anyTable, payload, in context).TryGetConsumed(out _))
            {
                MultiMatchHits++;
            }

            return 1;
        }
    }

    private sealed class MatchingHeuristicParser(ProtocolId protocolId) : IHeuristicParser
    {
        public ProtocolId ProtocolId => protocolId;
        public string Name => "heur.match";
        public string UiName => "Heuristic Match";

        public bool Test(ReadOnlyMemory<byte> data) => data.Length > 0 && data.Span[0] == 0xFE;
    }
}
