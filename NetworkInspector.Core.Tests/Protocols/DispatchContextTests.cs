// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests.Protocols;

/// <summary>
/// Exit-point coverage for <see cref="DispatchContext"/> typed key accessors.
/// </summary>
internal sealed class DispatchContextTests
{
    [Test]
    public async Task DispatchContext_BytesKey_TryGetBytes_ReturnsTrue()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        BytesDispatchSpyProtocol child = new();
        BytesDispatchParentProtocol parent = new();
        ProtocolId childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        ProtocolTableId bytesTable = builder.RegisterProtocolTable("t.bytes", "Bytes", ProtocolTableKeyType.Bytes);
        BytesKey key = new([0xDE, 0xAD]);
        builder.RegisterParserInBytesTable(bytesTable, key, childId);
        parent.SetDispatchTable(bytesTable, key);

        using Stack stack = builder.Build();
        byte[] data = [0x01];
        Frame frame = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), data,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, parentId);

        await Assert.That(child.ReceivedKind).IsEqualTo(DispatchKeyKind.Bytes);
        await Assert.That(child.TryGetBytesResult).IsTrue();
        await Assert.That(child.ReceivedBytesKey).IsEqualTo(key);
    }

    [Test]
    public async Task DispatchContext_AllKeyKinds_AreReadableFromChild()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        DispatchSpyProtocol child = new();
        DispatchParentProtocol parent = new();
        ProtocolId childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);
        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        ProtocolTableId u64Table = builder.RegisterProtocolTable("t.u64", "U64", ProtocolTableKeyType.U64);
        builder.RegisterParserInU64Table(u64Table, 0x42, childId);
        parent.SetDispatchTable(u64Table, 0x42);

        using Stack stack = builder.Build();
        byte[] data = [0x99];
        Frame frame = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), data,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, parentId);

        await Assert.That(child.ReceivedKind).IsEqualTo(DispatchKeyKind.U64);
        await Assert.That(child.ReceivedU64).IsEqualTo(0x42UL);
        await Assert.That(child.TryGetStringResult).IsFalse();
        await Assert.That(child.TryGetBytesResult).IsFalse();
        await Assert.That(child.TryGetBoolResult).IsFalse();
    }

    private sealed class BytesDispatchSpyProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;

        public string Name => "dispatch.bytes.child";
        public string UiName => "Bytes Dispatch Child";

        public DispatchKeyKind ReceivedKind { get; private set; }
        public bool TryGetBytesResult { get; private set; }
        public BytesKey ReceivedBytesKey { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, Name, UiName, FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            _ = parentField.Append(_ContainerFieldId, FieldValue.None);
            DispatchContext dispatch = context.Dispatch;
            ReceivedKind = dispatch.Kind;
            TryGetBytesResult = dispatch.TryGetBytes(out BytesKey key);
            if (TryGetBytesResult)
            {
                ReceivedBytesKey = key;
            }
            return 0;
        }
    }

    private sealed class BytesDispatchParentProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;
        private ProtocolTableId _TableId;
        private BytesKey _DispatchKey;

        public string Name => "dispatch.bytes.parent";
        public string UiName => "Bytes Dispatch Parent";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, Name, UiName, FieldType.None);
        }

        public void SetDispatchTable(ProtocolTableId tableId, BytesKey dispatchKey)
        {
            _TableId = tableId;
            _DispatchKey = dispatchKey;
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            MutField container = parentField.Append(_ContainerFieldId, FieldValue.None);
            container.TryCallNextProtocolBytes(_TableId, _DispatchKey, data, in context);
            return 1;
        }
    }

    private sealed class DispatchSpyProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;

        public string Name => "dispatch.child";
        public string UiName => "Dispatch Child";

        public DispatchKeyKind ReceivedKind { get; private set; }
        public ulong ReceivedU64 { get; private set; }
        public bool TryGetStringResult { get; private set; }
        public bool TryGetBytesResult { get; private set; }
        public bool TryGetBoolResult { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, Name, UiName, FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            _ = parentField.Append(_ContainerFieldId, FieldValue.None);
            DispatchContext dispatch = context.Dispatch;
            ReceivedKind = dispatch.Kind;
            TryGetStringResult = dispatch.TryGetString(out _);
            TryGetBytesResult = dispatch.TryGetBytes(out _);
            TryGetBoolResult = dispatch.TryGetBool(out _);
            if (dispatch.TryGetU64(out ulong key))
            {
                ReceivedU64 = key;
            }
            return 0;
        }
    }

    private sealed class DispatchParentProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;
        private ProtocolTableId _TableId;
        private ulong _DispatchKey;

        public string Name => "dispatch.parent";
        public string UiName => "Dispatch Parent";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, Name, UiName, FieldType.None);
        }

        public void SetDispatchTable(ProtocolTableId tableId, ulong dispatchKey)
        {
            _TableId = tableId;
            _DispatchKey = dispatchKey;
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            MutField container = parentField.Append(_ContainerFieldId, FieldValue.None);
            container.TryCallNextProtocolU64(_TableId, _DispatchKey, data, in context);
            return 1;
        }
    }
}
