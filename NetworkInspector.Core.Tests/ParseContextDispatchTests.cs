// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ParseContext.SelfProtocolId"/> and
/// <see cref="DispatchContext.CallerProtocolId"/>.
/// <para>
/// Verifies that every protocol receives its own ID in <c>context.SelfProtocolId</c>
/// and that child protocols can identify which parent protocol dispatched them via
/// <c>context.Dispatch.CallerProtocolId</c>, even when multiple parent protocols share
/// the same dispatch table (e.g., IPv4 and IPv6 both using <c>ip.proto</c>).
/// </para>
/// <para><b>Thread safety:</b> Each test creates its own <see cref="Stack"/> — no shared state.</para>
/// </summary>
internal sealed class ParseContextDispatchTests
{
    #region Helpers

    /// <summary>
    /// Builds a minimal stack with a parent protocol that dispatches via a u64 table
    /// and a child protocol that records the context it receives.
    /// </summary>
    private static (Stack Stack, SpyProtocol Parent, SpyProtocol Child, ProtocolId ParentId, ProtocolId ChildId)
        _BuildDispatchStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        SpyProtocol child = new("child", "Child");
        SpyProtocol parent = new("parent", "Parent");

        ProtocolId childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);

        ProtocolId parentId = builder.RegisterProtocol(parent);
        parent.RegisterFields(builder, parentId);

        // Register dispatch table owned by parent; child registered at key 0x99
        ProtocolTableId tableId = builder.RegisterProtocolTable("parent.type", "Parent Type", ProtocolTableKeyType.U64);
        builder.RegisterParserInU64Table(tableId, 0x99, childId);
        parent.SetDispatchTable(tableId, 0x99);

        Stack stack = builder.Build();
        return (stack, parent, child, parentId, childId);
    }

    /// <summary>
    /// Parses a minimal synthetic frame using the given protocol as the first protocol.
    /// Frame payload is a single byte 0x99 that the parent uses as the dispatch key.
    /// </summary>
    private static Packet _ParseFrame(Stack stack, ProtocolId firstProtocolId)
    {
        // 1-byte payload: dispatch key 0x99
        byte[] data = [0x99];
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(0),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(1), stack, frame, firstProtocolId);
    }

    #endregion

    #region SelfProtocolId tests

    [Test]
    public async Task SelfProtocolId_IsSetToOwnProtocolId_ForParentProtocol()
    {
        (Stack stack, SpyProtocol parent, _, ProtocolId parentId, _) = _BuildDispatchStack();
        using (stack)
        {
            _ = _ParseFrame(stack, parentId);
            await Assert.That(parent.ReceivedSelfProtocolId).IsEqualTo(parentId);
        }
    }

    [Test]
    public async Task SelfProtocolId_IsSetToOwnProtocolId_ForChildProtocol()
    {
        (Stack stack, _, SpyProtocol child, ProtocolId parentId, ProtocolId childId) = _BuildDispatchStack();
        using (stack)
        {
            _ = _ParseFrame(stack, parentId);
            await Assert.That(child.ReceivedSelfProtocolId).IsEqualTo(childId);
        }
    }

    [Test]
    public async Task SelfProtocolId_DefaultContext_HasNoStack()
    {
        // default(ParseContext) bypasses all constructors, so _SelfProtocolId is default(ProtocolId)
        // (Value=0, IsValid=true). The reliable signal for an empty context is HasStack==false.
        ParseContext ctx = default;
        // Extract before await — ParseContext is a ref struct and cannot be held across awaits
        bool hasStack = ctx.HasStack;
        ProtocolId selfId = ctx.SelfProtocolId;
        await Assert.That(hasStack).IsFalse();
        await Assert.That(selfId).IsEqualTo(default(ProtocolId));
    }

    #endregion

    #region CallerProtocolId tests

    [Test]
    public async Task CallerProtocolId_IsParentId_WhenDispatchedViaTable()
    {
        (Stack stack, _, SpyProtocol child, ProtocolId parentId, _) = _BuildDispatchStack();
        using (stack)
        {
            _ = _ParseFrame(stack, parentId);
            // The child was dispatched by the parent — CallerProtocolId must equal parentId
            await Assert.That(child.ReceivedCallerProtocolId).IsEqualTo(parentId);
        }
    }

    [Test]
    public async Task CallerProtocolId_HasDispatch_IsFalse_ForRootProtocol()
    {
        // The root protocol was invoked via Packet._ParseFrame → CallProtocol directly,
        // without a dispatch table lookup — HasDispatch must be false.
        // CallerProtocolId is meaningless when HasDispatch is false.
        (Stack stack, SpyProtocol parent, _, ProtocolId parentId, _) = _BuildDispatchStack();
        using (stack)
        {
            _ = _ParseFrame(stack, parentId);
            await Assert.That(parent.ReceivedHasDispatch).IsFalse();
        }
    }

    [Test]
    public async Task CallerProtocolId_DifferentForTwoDistinctParents_SameTable()
    {
        // Regression for the IPv4/IPv6 share-a-table scenario:
        // Two parent protocols both dispatch via the same table to the same child.
        // The child must see the correct CallerProtocolId for each invocation.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        SpyProtocol child = new("child2", "Child2");
        SpyProtocol parent1 = new("parent1", "Parent1");
        SpyProtocol parent2 = new("parent2", "Parent2");

        ProtocolId childId = builder.RegisterProtocol(child);
        child.RegisterFields(builder, childId);

        ProtocolId parent1Id = builder.RegisterProtocol(parent1);
        parent1.RegisterFields(builder, parent1Id);

        ProtocolId parent2Id = builder.RegisterProtocol(parent2);
        parent2.RegisterFields(builder, parent2Id);

        // Shared dispatch table
        ProtocolTableId tableId = builder.RegisterProtocolTable("shared.type", "Shared Type", ProtocolTableKeyType.U64);
        builder.RegisterParserInU64Table(tableId, 0x01, childId);
        parent1.SetDispatchTable(tableId, 0x01);
        parent2.SetDispatchTable(tableId, 0x01);

        using Stack stack = builder.Build();

        byte[] data = [0x01];
        Frame frame = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), data,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

        // Parse via parent1 — child must see parent1Id as caller
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, parent1Id);
        ProtocolId callerAfterParent1 = child.ReceivedCallerProtocolId;

        // Parse via parent2 — child must see parent2Id as caller
        _ = Packet.ParseFrame(new PacketId(2), stack, frame, parent2Id);
        ProtocolId callerAfterParent2 = child.ReceivedCallerProtocolId;

        await Assert.That(callerAfterParent1).IsEqualTo(parent1Id);
        await Assert.That(callerAfterParent2).IsEqualTo(parent2Id);
        await Assert.That(callerAfterParent1).IsNotEqualTo(callerAfterParent2);
    }

    #endregion

    #region SizeTests

    [Test]
    public async Task DispatchContext_SizeIsExpected()
    {
        // DispatchContext must be 32 bytes: object(8) + ulong(8) + ProtocolTableId(4) +
        // ProtocolId(4) + DispatchKeyKind(1) + 7 padding = 32.
        // This test guards against accidental struct bloat.
        int size = Marshal.SizeOf<DispatchContext>();
        await Assert.That(size).IsEqualTo(32);
    }

    #endregion

    #region Spy protocol

    /// <summary>
    /// A minimal protocol that records the <see cref="ParseContext"/> it receives.
    /// On <see cref="Parse"/>, optionally dispatches to a u64 table using its single-byte payload.
    /// </summary>
    private sealed class SpyProtocol(string name, string uiName) : IProtocol
    {
        private FieldId _ContainerFieldId;
        private ProtocolTableId _TableId;
        private ulong _DispatchKey;
        private bool _HasDispatchTable;

        /// <summary>The <see cref="ProtocolId"/> received via <c>context.SelfProtocolId</c> on last parse.</summary>
        public ProtocolId ReceivedSelfProtocolId { get; private set; } = ProtocolId.Invalid;

        /// <summary>The <see cref="ProtocolId"/> received via <c>context.Dispatch.CallerProtocolId</c> on last parse.</summary>
        public ProtocolId ReceivedCallerProtocolId { get; private set; } = ProtocolId.Invalid;

        /// <summary>Whether <c>context.Dispatch.HasDispatch</c> was true on last parse.</summary>
        public bool ReceivedHasDispatch
        {
            get; private set;
        }

        /// <inheritdoc/>
        public string Name => name;

        /// <inheritdoc/>
        public string UiName => uiName;

        /// <summary>Registers the container field with the builder.</summary>
        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
            => _ContainerFieldId = builder.RegisterField(protocolId, name, uiName, FieldType.None);

        /// <summary>Configures this protocol to dispatch to a u64 table using a fixed key.</summary>
        public void SetDispatchTable(ProtocolTableId tableId, ulong dispatchKey)
        {
            _TableId = tableId;
            _DispatchKey = dispatchKey;
            _HasDispatchTable = true;
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            ReceivedSelfProtocolId = context.SelfProtocolId;
            ReceivedCallerProtocolId = context.Dispatch.CallerProtocolId;
            ReceivedHasDispatch = context.Dispatch.HasDispatch;

            MutField container = parentField.Append(_ContainerFieldId, FieldValue.None);

            if (_HasDispatchTable && data.Length >= 1)
            {
                container.TryCallNextProtocolU64(_TableId, _DispatchKey, data[1..], in context);
            }

            if (data.Length >= 1)
            {
                return 1;
            }

            return 0;
        }
    }

    #endregion
}
