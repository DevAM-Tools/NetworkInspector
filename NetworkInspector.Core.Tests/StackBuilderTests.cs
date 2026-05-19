// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Errors;
using NetworkInspector.Core.Protocols;
using NetworkInspector.Core.Tables;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for StackBuilder: protocol, field, and table registration, deferred callbacks, and Build().
/// </summary>
internal sealed class StackBuilderTests
{
    // === Built-in fields ===

    [Test]
    public async Task Builder_AutoRegistersRootField()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        FieldId? rootId = builder.GetFieldId("root");
        await Assert.That(rootId).IsNotNull();
        await Assert.That(rootId!.Value.IsValid).IsTrue();
    }

    [Test]
    public async Task Builder_AutoRegistersPacketErrorField()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        FieldId? errorId = builder.GetFieldId("packet.error");
        await Assert.That(errorId).IsNotNull();
        await Assert.That(errorId!.Value.IsValid).IsTrue();
    }

    // === Protocol Registration ===

    [Test]
    public async Task Builder_RegisterProtocol_ReturnsValidId()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test Protocol");
        ProtocolId id = builder.RegisterProtocol(proto);
        await Assert.That(id.IsValid).IsTrue();
    }

    [Test]
    public async Task Builder_RegisterProtocol_LookupByName()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("myproto", "My Protocol");
        ProtocolId id = builder.RegisterProtocol(proto);
        ProtocolId? lookupId = builder.GetProtocolId("myproto");
        await Assert.That(lookupId).IsNotNull();
        await Assert.That(lookupId!.Value).IsEqualTo(id);
    }

    [Test]
    public async Task Builder_RegisterProtocol_DuplicateNameThrows()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto1 = new("dup", "Dup 1");
        StubProtocol proto2 = new("dup", "Dup 2");
        builder.RegisterProtocol(proto1);

        RegistrationException ex = Assert.Throws<RegistrationException>(() => builder.RegisterProtocol(proto2));
        await Assert.That(ex).IsTypeOf<DuplicateNameRegistrationException>();
    }

    [Test]
    public async Task Builder_ProtocolCount()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        // A fresh builder always has 2 built-in protocols: "root" (link-type dispatch) and "packet" (top-level entry).
        await Assert.That(builder.ProtocolCount).IsEqualTo(2);

        builder.RegisterProtocol(new StubProtocol("a", "A"));
        await Assert.That(builder.ProtocolCount).IsEqualTo(3);

        builder.RegisterProtocol(new StubProtocol("b", "B"));
        await Assert.That(builder.ProtocolCount).IsEqualTo(4);
    }

    [Test]
    public async Task Builder_GetProtocol_ReturnsInfo()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("eth", "Ethernet");
        ProtocolId id = builder.RegisterProtocol(proto);
        ProtocolInfo? info = builder.GetProtocol(id);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Name).IsEqualTo("eth");
        await Assert.That(info.UiName).IsEqualTo("Ethernet");
    }

    [Test]
    public async Task Builder_GetProtocol_InvalidSentinelIdReturnsNull()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        await Assert.That(builder.GetProtocol(ProtocolId.Invalid)).IsNull();
    }

    // === Field Registration ===

    [Test]
    public async Task Builder_RegisterField_ReturnsValidId()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        FieldId fieldId = builder.RegisterField(protoId, "test.field", "Test Field", FieldType.U64);
        await Assert.That(fieldId.IsValid).IsTrue();
    }

    [Test]
    public async Task Builder_RegisterField_LookupByName()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        FieldId fieldId = builder.RegisterField(protoId, "test.value", "Value", FieldType.I64);
        FieldId? lookupId = builder.GetFieldId("test.value");
        await Assert.That(lookupId).IsNotNull();
        await Assert.That(lookupId!.Value).IsEqualTo(fieldId);
    }

    [Test]
    public async Task Builder_RegisterField_LookupById()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        FieldId fieldId = builder.RegisterField(protoId, "test.mac", "MAC", FieldType.MacAddress);
        FieldInfo? info = builder.GetField(fieldId);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Name).IsEqualTo("test.mac");
        await Assert.That(info.UiName).IsEqualTo("MAC");
        await Assert.That(info.FieldType).IsEqualTo(FieldType.MacAddress);
    }

    [Test]
    public async Task Builder_FieldAccess_InvalidSentinelIdsFailSoft()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        await Assert.That(builder.GetField(FieldId.Invalid)).IsNull();
        await Assert.That(builder.GetFieldIndexGroup(FieldId.Invalid)).IsEqualTo(IndexGroupId.Invalid);
    }

    [Test]
    public async Task Builder_RegisterField_DuplicateNameThrows()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        builder.RegisterField(protoId, "test.dup", "Dup", FieldType.U64);

        RegistrationException ex = Assert.Throws<RegistrationException>(
            () => builder.RegisterField(protoId, "test.dup", "Dup Again", FieldType.Bool));
        await Assert.That(ex).IsTypeOf<DuplicateNameRegistrationException>();
    }

    [Test]
    public async Task Builder_RegisterFieldInGroup()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        FieldId fieldId = builder.RegisterFieldInGroup(
            protoId, "test.grouped", "Grouped", FieldType.U64, "test.group");
        await Assert.That(fieldId.IsValid).IsTrue();
        await Assert.That(builder.IndexGroupCount).IsGreaterThan(0);
    }

    [Test]
    public async Task Builder_GetIndexGroup_InvalidSentinelIdReturnsNull()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        await Assert.That(builder.GetIndexGroup(IndexGroupId.Invalid)).IsNull();
    }

    // === Protocol Table Registration ===

    [Test]
    public async Task Builder_RegisterProtocolTable_ReturnsValidId()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "test.table", "Test Table", ProtocolTableKeyType.U64);
        await Assert.That(tableId.IsValid).IsTrue();
    }

    [Test]
    public async Task Builder_RegisterProtocolTable_LookupByName()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "my.table", "My Table", ProtocolTableKeyType.String);
        ProtocolTableId? lookupId = builder.GetProtocolTableId("my.table");
        await Assert.That(lookupId).IsNotNull();
        await Assert.That(lookupId!.Value).IsEqualTo(tableId);
    }

    [Test]
    public async Task Builder_TableInfo_InvalidSentinelIdsFailSoft()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        await Assert.That(builder.GetProtocolTableInfo(ProtocolTableId.Invalid)).IsNull();
        await Assert.That(builder.GetHeuristicProtocolTableInfo(HeuristicProtocolTableId.Invalid)).IsNull();
    }

    [Test]
    public async Task Builder_RegisterProtocolTable_DuplicateNameThrows()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.RegisterProtocolTable("dup.table", "Table 1", ProtocolTableKeyType.U64);

        RegistrationException ex = Assert.Throws<RegistrationException>(
            () => builder.RegisterProtocolTable("dup.table", "Table 2", ProtocolTableKeyType.U64));
        await Assert.That(ex).IsTypeOf<DuplicateNameRegistrationException>();
    }

    [Test]
    public async Task Builder_RegisterParserInU64Table()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("child", "Child");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "parent.type", "Type", ProtocolTableKeyType.U64);
        // Should succeed
        builder.RegisterParserInU64Table(tableId, 0x0800, protoId);
    }

    [Test]
    public async Task Builder_RegisterParserInU64TableByName()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("child", "Child");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        builder.RegisterProtocolTable("test.dispatch", "Dispatch", ProtocolTableKeyType.U64);
        // Should succeed
        builder.RegisterParserInU64TableByName("test.dispatch", 17, protoId);
    }

    [Test]
    public void Builder_RegisterParserWithInvalidTableIds_ThrowsNotFoundInsteadOfIndexErrors()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolId protoId = builder.RegisterProtocol(new StubProtocol("child", "Child"));

        _ = Assert.Throws<NotFoundRegistrationException>(
            () => builder.RegisterParserInU64Table(ProtocolTableId.Invalid, 0x0800, protoId));
        _ = Assert.Throws<NotFoundRegistrationException>(
            () => builder.RegisterParserInStringTable(ProtocolTableId.Invalid, "key", protoId));
        _ = Assert.Throws<NotFoundRegistrationException>(
            () => builder.RegisterParserInBytesTable(ProtocolTableId.Invalid, new BytesKey([0x01]), protoId));
        _ = Assert.Throws<NotFoundRegistrationException>(
            () => builder.RegisterParserInBoolTable(ProtocolTableId.Invalid, true, protoId));
        _ = Assert.Throws<NotFoundRegistrationException>(
            () => builder.RegisterParserInAnyTable(ProtocolTableId.Invalid, protoId));
        _ = Assert.Throws<NotFoundRegistrationException>(
            () => builder.RegisterHeuristicParser(HeuristicProtocolTableId.Invalid, new StubHeuristicParser()));
    }

    // === String table registration (success path) ===

    [Test]
    public async Task Builder_RegisterParserInStringTable()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("child", "Child");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "parent.name", "Name", ProtocolTableKeyType.String);
        // Should succeed without exception
        builder.RegisterParserInStringTable(tableId, "http", protoId);
        await Assert.That(tableId.IsValid).IsTrue();
    }

    // === Bytes table registration (success path) ===

    [Test]
    public async Task Builder_RegisterParserInBytesTable()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("child", "Child");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "parent.magic", "Magic", ProtocolTableKeyType.Bytes);
        builder.RegisterParserInBytesTable(tableId, new BytesKey([0xCA, 0xFE]), protoId);
        await Assert.That(tableId.IsValid).IsTrue();
    }

    // === Bool table registration (success path) ===

    [Test]
    public async Task Builder_RegisterParserInBoolTable()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("child", "Child");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "parent.flag", "Flag", ProtocolTableKeyType.Bool);
        builder.RegisterParserInBoolTable(tableId, true, protoId);
        await Assert.That(tableId.IsValid).IsTrue();
    }

    // === Any table registration (success path) ===

    [Test]
    public async Task Builder_RegisterParserInAnyTable()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("child", "Child");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "parent.any", "Any", ProtocolTableKeyType.Any);
        builder.RegisterParserInAnyTable(tableId, protoId);
        await Assert.That(tableId.IsValid).IsTrue();
    }

    // === Heuristic protocol table and parser (success path) ===

    [Test]
    public async Task Builder_RegisterHeuristicProtocolTable()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolId ownerProtoId = builder.RegisterProtocol(new StubProtocol("owner", "Owner"));
        HeuristicProtocolTableId tableId = builder.RegisterHeuristicProtocolTable(
            ownerProtoId, "heuristic.test", "Heuristic Test");
        await Assert.That(tableId.IsValid).IsTrue();
    }

    [Test]
    public async Task Builder_RegisterHeuristicParser_SuccessPath()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolId ownerProtoId = builder.RegisterProtocol(new StubProtocol("owner", "Owner"));
        HeuristicProtocolTableId tableId = builder.RegisterHeuristicProtocolTable(
            ownerProtoId, "heuristic.test", "Heuristic Test");
        builder.RegisterHeuristicParser(tableId, new StubHeuristicParser());
        await Assert.That(tableId.IsValid).IsTrue();
    }

    // === Stream reassembly config ===

    [Test]
    public async Task Builder_RegisterStreamReassemblyConfig()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("tcp", "TCP");
        ProtocolId protoId = builder.RegisterProtocol(proto);

        StreamReassemblyConfig config = new()
        {
            MaxPduSize = 4096
        };
        builder.RegisterStreamReassemblyConfig(protoId, config);

        StreamReassemblyConfig? retrieved = builder.GetStreamReassemblyConfig(protoId);
        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.MaxPduSize).IsEqualTo(4096);
    }

    [Test]
    public async Task Builder_GetStreamReassemblyConfig_NotRegistered_ReturnsNull()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("udp", "UDP");
        ProtocolId protoId = builder.RegisterProtocol(proto);

        StreamReassemblyConfig? config = builder.GetStreamReassemblyConfig(protoId);
        await Assert.That(config).IsNull();
    }

    [Test]
    public void Builder_RegisterStreamReassemblyConfig_Duplicate_Throws()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("tcp", "TCP");
        ProtocolId protoId = builder.RegisterProtocol(proto);

        builder.RegisterStreamReassemblyConfig(protoId, new StreamReassemblyConfig());
        Assert.Throws<DuplicateNameRegistrationException>(
            () => builder.RegisterStreamReassemblyConfig(protoId, new StreamReassemblyConfig()));
    }

    // === Deferred Callbacks ===

    [Test]
    public async Task Builder_WhenProtocolRegistered_CallbackFiresImmediately()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("early", "Early");
        ProtocolId id = builder.RegisterProtocol(proto);

        ProtocolId? capturedId = null;
        builder.WhenProtocolRegistered("early", pid => capturedId = pid);

        await Assert.That(capturedId).IsNotNull();
        await Assert.That(capturedId!.Value).IsEqualTo(id);
    }

    [Test]
    public async Task Builder_WhenProtocolRegistered_CallbackDeferred()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolId? capturedId = null;
        builder.WhenProtocolRegistered("later", pid => capturedId = pid);

        // Not yet registered
        await Assert.That(capturedId).IsNull();

        // Now register
        StubProtocol proto = new("later", "Later");
        ProtocolId id = builder.RegisterProtocol(proto);

        await Assert.That(capturedId).IsNotNull();
        await Assert.That(capturedId!.Value).IsEqualTo(id);
    }

    [Test]
    public async Task Builder_WhenFieldRegistered_CallbackDeferred()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        FieldId? capturedId = null;
        builder.WhenFieldRegistered("test.deferred", fid => capturedId = fid);

        await Assert.That(capturedId).IsNull();

        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        FieldId fieldId = builder.RegisterField(protoId, "test.deferred", "Deferred", FieldType.U64);

        await Assert.That(capturedId).IsNotNull();
        await Assert.That(capturedId!.Value).IsEqualTo(fieldId);
    }

    [Test]
    public async Task Builder_WhenProtocolTableRegistered_CallbackDeferred()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolTableId? capturedId = null;
        builder.WhenProtocolTableRegistered("deferred.table", tid => capturedId = tid);

        await Assert.That(capturedId).IsNull();

        ProtocolTableId tableId = builder.RegisterProtocolTable(
            "deferred.table", "Deferred Table", ProtocolTableKeyType.U64);

        await Assert.That(capturedId).IsNotNull();
        await Assert.That(capturedId!.Value).IsEqualTo(tableId);
    }

    // === Build ===

    [Test]
    public async Task Builder_Build_ProducesStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("eth", "Ethernet");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        _ = builder.RegisterField(protoId, "eth.dst", "Destination", FieldType.MacAddress);
        _ = builder.RegisterField(protoId, "eth.src", "Source", FieldType.MacAddress);

        using Stack stack = builder.Build();

        // 2 built-in protocols (root, packet) + 1 custom protocol (eth) = 3
        await Assert.That(stack.ProtocolCount).IsEqualTo(3);
        // 3 built-in fields (root, packet.error, packet.choice)
        // + 5 packet protocol fields (packet, packet.id, packet.timestamp, packet.frame_source_id, packet.info)
        // + 2 custom fields (eth.dst, eth.src)
        // = 10
        await Assert.That(stack.FieldCount).IsEqualTo(10);
    }

    [Test]
    public async Task Builder_Build_StackFieldLookupByName()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("test", "Test");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        _ = builder.RegisterField(protoId, "test.port", "Port", FieldType.U64);

        using Stack stack = builder.Build();

        FieldId? fieldId = stack.GetFieldId("test.port");
        await Assert.That(fieldId).IsNotNull();
        await Assert.That(fieldId!.Value.IsValid).IsTrue();
    }

    [Test]
    public async Task Builder_Build_StackProtocolLookupByName()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("udp", "UDP");
        _ = builder.RegisterProtocol(proto);

        using Stack stack = builder.Build();

        ProtocolId? id = stack.GetProtocolId("udp");
        await Assert.That(id).IsNotNull();
    }

    [Test]
    public async Task Builder_Build_StackInvalidSentinelIdsFailSoft()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolId protoId = builder.RegisterProtocol(new StubProtocol("udp", "UDP"));
        _ = builder.RegisterFieldInGroup(protoId, "udp.port", "Port", FieldType.U64, "udp");

        using Stack stack = builder.Build();

        await Assert.That(stack.GetProtocol(ProtocolId.Invalid)).IsNull();
        await Assert.That(stack.GetField(FieldId.Invalid)).IsNull();
        await Assert.That(stack.GetFieldIndexGroup(FieldId.Invalid)).IsEqualTo(IndexGroupId.Invalid);
        await Assert.That(stack.GetIndexGroup(IndexGroupId.Invalid)).IsNull();
        await Assert.That(stack.GetProtocolTableInfo(ProtocolTableId.Invalid)).IsNull();
        await Assert.That(stack.GetHeuristicProtocolTableInfo(HeuristicProtocolTableId.Invalid)).IsNull();
    }

    [Test]
    public async Task Builder_Build_OnStartFailure_CollectsBuildDiagnosticsOnStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        List<string> started = [];
        List<string> shutdown = [];

        _ = builder.RegisterProtocol(new LifecycleProtocol("first", "First", started, shutdown));
        _ = builder.RegisterProtocol(new LifecycleProtocol(
            "fail",
            "Fail",
            started,
            shutdown,
            throwOnStart: true));
        _ = builder.RegisterProtocol(new LifecycleProtocol("later", "Later", started, shutdown));

        using Stack stack = builder.Build();

        await Assert.That(started.Count).IsEqualTo(3);
        await Assert.That(started[0]).IsEqualTo("first");
        await Assert.That(started[1]).IsEqualTo("fail");
        await Assert.That(started[2]).IsEqualTo("later");
        await Assert.That(stack.BuildDiagnostics.Length).IsEqualTo(1);
        BuildStartupError startupError = (BuildStartupError)stack.BuildDiagnostics.Span[0];
        await Assert.That(startupError.Severity).IsEqualTo(BuildDiagnosticSeverity.Error);
        await Assert.That(startupError.ProtocolName).IsEqualTo("fail");
        await Assert.That(startupError.ProtocolUiName).IsEqualTo("Fail");
        await Assert.That(startupError.Exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(startupError.Exception.Message).IsEqualTo("Startup failed for fail.");
        await Assert.That(shutdown.Count).IsEqualTo(0);

        _ = settingsManager.AllSettings;
    }

    [Test]
    public async Task Builder_Build_NoOnStartFailures_LeavesBuildDiagnosticsEmpty()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("first", "First", [], []));

        using Stack stack = builder.Build();

        await Assert.That(stack.BuildDiagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Builder_RegisterProtocolWithCallback()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProtocol proto = new("cb", "Callback Test");
        bool callbackFired = false;

        _ = builder.RegisterProtocol(proto, (factory, id, p) =>
        {
            callbackFired = true;
            _ = factory.RegisterField(id, "cb.field", "Field", FieldType.U64);
        });

        await Assert.That(callbackFired).IsTrue();
        await Assert.That(builder.GetFieldId("cb.field")).IsNotNull();
    }

    // === Stack.Shutdown aggregate-exception contract ===

    /// <summary>
    /// Verifies that <see cref="Stack.Shutdown"/> wraps all protocol shutdown exceptions
    /// in an <see cref="AggregateException"/> and that all protocols still receive
    /// <see cref="IProtocol.OnShutdown"/> even when an earlier one throws.
    /// </summary>
    [Test]
    public async Task Stack_Shutdown_SingleThrowingProtocol_ThrowsAggregateException()
    {
        List<string> started = [];
        List<string> shutdown = [];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("bad", "Bad", started, shutdown, throwOnShutdown: true));

        Stack stack = builder.Build();

        AggregateException? ex = null;
        try
        {
            stack.Shutdown();
        }
        catch (AggregateException aex)
        {
            ex = aex;
        }
        finally
        {
            stack.Dispose(); // safe — Dispose swallows the already-fired shutdown errors
        }

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.InnerExceptions.Count).IsEqualTo(1);
        await Assert.That(ex.InnerExceptions[0]).IsTypeOf<InvalidOperationException>();
        await Assert.That(ex.InnerExceptions[0].Message).IsEqualTo("Shutdown failed for bad.");
        // Protocol was still called
        await Assert.That(shutdown).Contains("bad");
    }

    /// <summary>
    /// Verifies fault-tolerance: when multiple protocols throw on shutdown,
    /// all exceptions are collected into the single <see cref="AggregateException"/>
    /// and every protocol's <see cref="IProtocol.OnShutdown"/> is still invoked.
    /// </summary>
    [Test]
    public async Task Stack_Shutdown_MultipleThrowingProtocols_AllExceptionsCollected()
    {
        List<string> started = [];
        List<string> shutdown = [];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("a", "A", started, shutdown, throwOnShutdown: true));
        _ = builder.RegisterProtocol(new LifecycleProtocol("b", "B", started, shutdown));
        _ = builder.RegisterProtocol(new LifecycleProtocol("c", "C", started, shutdown, throwOnShutdown: true));

        Stack stack = builder.Build();

        AggregateException? ex = null;
        try
        {
            stack.Shutdown();
        }
        catch (AggregateException aex)
        {
            ex = aex;
        }
        finally
        {
            stack.Dispose();
        }

        await Assert.That(ex).IsNotNull();
        // All three protocols must have been called despite earlier failures
        await Assert.That(shutdown).Contains("a");
        await Assert.That(shutdown).Contains("b");
        await Assert.That(shutdown).Contains("c");
        // Exactly two exceptions collected (a and c)
        await Assert.That(ex!.InnerExceptions.Count).IsEqualTo(2);
    }

    /// <summary>
    /// Verifies that a stack with no throwing protocols completes <see cref="Stack.Shutdown"/>
    /// without exception.
    /// </summary>
    [Test]
    public async Task Stack_Shutdown_NoThrowingProtocols_DoesNotThrow()
    {
        List<string> started = [];
        List<string> shutdown = [];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("x", "X", started, shutdown));

        using Stack stack = builder.Build();

        await Assert.That(stack.Shutdown).ThrowsNothing();
        await Assert.That(shutdown).Contains("x");
    }

    /// <summary>
    /// Verifies that <see cref="Stack.Dispose"/> captures protocol shutdown exceptions into
    /// <see cref="Stack.ShutdownDiagnostics"/> instead of silently dropping them. This is the
    /// CA1065-compliant surface for callers who only call <see cref="IDisposable.Dispose"/>.
    /// </summary>
    [Test]
    public async Task Stack_Dispose_CapturesShutdownExceptions_InShutdownDiagnostics()
    {
        List<string> started = [];
        List<string> shutdown = [];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("a", "A", started, shutdown, throwOnShutdown: true));
        _ = builder.RegisterProtocol(new LifecycleProtocol("b", "B", started, shutdown));
        _ = builder.RegisterProtocol(new LifecycleProtocol("c", "C", started, shutdown, throwOnShutdown: true));

        Stack stack = builder.Build();
        await Assert.That(() => stack.Dispose()).ThrowsNothing();

        ReadOnlyMemory<Exception> diagnostics = stack.ShutdownDiagnostics;
        await Assert.That(diagnostics.Length).IsEqualTo(2);
        await Assert.That(diagnostics.Span[0]).IsTypeOf<InvalidOperationException>();
        await Assert.That(diagnostics.Span[1]).IsTypeOf<InvalidOperationException>();
        // Every protocol still ran
        await Assert.That(shutdown).Contains("a");
        await Assert.That(shutdown).Contains("b");
        await Assert.That(shutdown).Contains("c");
    }

    /// <summary>
    /// Verifies that <see cref="Stack.ShutdownDiagnostics"/> is empty when no protocol throws.
    /// </summary>
    [Test]
    public async Task Stack_Dispose_CleanShutdown_LeavesDiagnosticsEmpty()
    {
        List<string> started = [];
        List<string> shutdown = [];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("x", "X", started, shutdown));

        Stack stack = builder.Build();
        stack.Dispose();
        await Assert.That(stack.ShutdownDiagnostics.Length).IsEqualTo(0);
    }

    /// <summary>
    /// Verifies idempotent <see cref="Stack.Dispose"/>: a second call must not re-run shutdown
    /// or overwrite <see cref="Stack.ShutdownDiagnostics"/>.
    /// </summary>
    [Test]
    public async Task Stack_Dispose_Idempotent()
    {
        List<string> started = [];
        List<string> shutdown = [];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterProtocol(new LifecycleProtocol("once", "Once", started, shutdown));

        Stack stack = builder.Build();
        stack.Dispose();
        stack.Dispose();
        await Assert.That(shutdown.Count(name => name == "once")).IsEqualTo(1);
    }

    // === Build Callback Warnings ===

    [Test]
    public async Task Build_UnresolvedProtocolCallback_ProducesProtocolWarning()
    {
        // Arrange — register a callback for "never.exists" but never register that protocol
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenProtocolRegistered("never.exists", static _ => { });

        // Act
        using Stack stack = builder.Build();

        // Assert — exactly one warning for the unresolved protocol callback
        BuildDiagnostic[] diagnostics = stack.BuildDiagnostics.Span.ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        BuildCallbackWarning warning = (BuildCallbackWarning)diagnostics[0];
        await Assert.That(warning.Severity).IsEqualTo(BuildDiagnosticSeverity.Warning);
        await Assert.That(warning.EntityKind).IsEqualTo(BuildCallbackWarningKind.Protocol);
        await Assert.That(warning.Name).IsEqualTo("never.exists");
        await Assert.That(warning.CallbackCount).IsEqualTo(1);
    }

    [Test]
    public async Task Build_UnresolvedFieldCallback_ProducesFieldWarning()
    {
        // Arrange — register a callback for "ghost.field" but never register that field
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenFieldRegistered("ghost.field", static _ => { });

        // Act
        using Stack stack = builder.Build();

        // Assert
        BuildDiagnostic[] diagnostics = stack.BuildDiagnostics.Span.ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        BuildCallbackWarning warning = (BuildCallbackWarning)diagnostics[0];
        await Assert.That(warning.Severity).IsEqualTo(BuildDiagnosticSeverity.Warning);
        await Assert.That(warning.EntityKind).IsEqualTo(BuildCallbackWarningKind.Field);
        await Assert.That(warning.Name).IsEqualTo("ghost.field");
        await Assert.That(warning.CallbackCount).IsEqualTo(1);
    }

    [Test]
    public async Task Build_UnresolvedProtocolTableCallback_ProducesTableWarning()
    {
        // Arrange — register a callback for "missing.table" but never register that table
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenProtocolTableRegistered("missing.table", static _ => { });

        // Act
        using Stack stack = builder.Build();

        // Assert
        BuildDiagnostic[] diagnostics = stack.BuildDiagnostics.Span.ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        BuildCallbackWarning warning = (BuildCallbackWarning)diagnostics[0];
        await Assert.That(warning.Severity).IsEqualTo(BuildDiagnosticSeverity.Warning);
        await Assert.That(warning.EntityKind).IsEqualTo(BuildCallbackWarningKind.ProtocolTable);
        await Assert.That(warning.Name).IsEqualTo("missing.table");
        await Assert.That(warning.CallbackCount).IsEqualTo(1);
    }

    [Test]
    public async Task Build_OneUnresolvedCallbackPerCategory_ProducesExactlyThreeWarnings()
    {
        // Arrange — one unresolved callback per entity kind, all distinct names
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenProtocolRegistered("unresolved.proto", static _ => { });
        builder.WhenFieldRegistered("unresolved.field", static _ => { });
        builder.WhenProtocolTableRegistered("unresolved.table", static _ => { });

        // Act
        using Stack stack = builder.Build();

        // Assert
        await Assert.That(stack.BuildDiagnostics.Length).IsEqualTo(3);

        // All diagnostics must be BuildCallbackWarning
        foreach (BuildDiagnostic diag in stack.BuildDiagnostics.Span.ToArray())
        {
            await Assert.That(diag).IsAssignableTo<BuildCallbackWarning>();
            await Assert.That(diag.Severity).IsEqualTo(BuildDiagnosticSeverity.Warning);
        }
    }

    [Test]
    public async Task Build_MultipleCallbacksForSameName_CallbackCountIsCorrect()
    {
        // Arrange — three callbacks registered for the same protocol name, none ever resolved
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenProtocolRegistered("multi.proto", static _ => { });
        builder.WhenProtocolRegistered("multi.proto", static _ => { });
        builder.WhenProtocolRegistered("multi.proto", static _ => { });

        // Act
        using Stack stack = builder.Build();

        // Assert — a single warning with CallbackCount = 3
        BuildDiagnostic[] diagnostics = stack.BuildDiagnostics.Span.ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        BuildCallbackWarning warning = (BuildCallbackWarning)diagnostics[0];
        await Assert.That(warning.CallbackCount).IsEqualTo(3);
        await Assert.That(warning.Name).IsEqualTo("multi.proto");
    }

    [Test]
    public async Task Build_MixedResolvedAndUnresolved_OnlyUnresolvedProducesWarnings()
    {
        // Arrange — "resolved.proto" is registered after its callback; "unresolved.proto" is not
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenProtocolRegistered("resolved.proto", static _ => { });
        builder.WhenProtocolRegistered("unresolved.proto", static _ => { });

        // Register "resolved.proto" so its callback fires immediately
        builder.RegisterProtocol(new StubProtocol("resolved.proto", "Resolved"));

        // Act
        using Stack stack = builder.Build();

        // Assert — only the unresolved callback produces a warning
        BuildDiagnostic[] diagnostics = stack.BuildDiagnostics.Span.ToArray();
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        BuildCallbackWarning warning = (BuildCallbackWarning)diagnostics[0];
        await Assert.That(warning.EntityKind).IsEqualTo(BuildCallbackWarningKind.Protocol);
        await Assert.That(warning.Name).IsEqualTo("unresolved.proto");
    }

    [Test]
    public async Task Build_AllCallbacksResolved_NoBuildCallbackWarnings()
    {
        // Arrange — callbacks for all three entity types, each registered before Build
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        builder.WhenProtocolRegistered("ok.proto", static _ => { });
        builder.WhenFieldRegistered("ok.field", static _ => { });
        builder.WhenProtocolTableRegistered("ok.table", static _ => { });

        StubProtocol proto = new("ok.proto", "OK Proto");
        ProtocolId protoId = builder.RegisterProtocol(proto);
        builder.RegisterField(protoId, "ok.field", "OK Field", FieldType.U64);
        builder.RegisterProtocolTable("ok.table", "OK Table", ProtocolTableKeyType.U64);

        // Act
        using Stack stack = builder.Build();

        // Assert — no warnings; no startup errors either
        await Assert.That(stack.BuildDiagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BuildCallbackWarning_Message_ContainsEntityKindAndName()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.WhenProtocolRegistered("doc.proto", static _ => { });

        // Act
        using Stack stack = builder.Build();

        // Assert — validate the human-readable message format
        BuildCallbackWarning warning = (BuildCallbackWarning)stack.BuildDiagnostics.Span[0];
        await Assert.That(warning.Message).Contains("doc.proto");
        await Assert.That(warning.Message).Contains("Protocol");
        await Assert.That(warning.ToString()).Contains("[Warning]");
    }

    // === Stub protocol for test use ===

    private sealed class StubProtocol(string name, string uiName) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    private sealed class StubHeuristicParser : IHeuristicParser
    {
        public ProtocolId ProtocolId => ProtocolId.Invalid;
        public string Name => "heuristic.stub";
        public string UiName => "Heuristic Stub";

        public bool Test(ReadOnlyMemory<byte> data) => false;
    }

    private sealed class LifecycleProtocol(
        string name,
        string uiName,
        List<string> started,
        List<string> shutdown,
        bool throwOnStart = false,
        bool throwOnShutdown = false) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;

        public void OnStart(Stack stack)
        {
            started.Add(name);
            if (throwOnStart)
            {
                throw new InvalidOperationException($"Startup failed for {name}.");
            }
        }

        public void OnShutdown(Stack stack)
        {
            shutdown.Add(name);
            if (throwOnShutdown)
            {
                throw new InvalidOperationException($"Shutdown failed for {name}.");
            }
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }
}