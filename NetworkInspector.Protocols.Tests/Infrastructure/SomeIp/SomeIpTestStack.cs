// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// A protocol stack for SOME/IP tests: all standard protocols, the SOME/IP settings preloaded before registration, and an optional
/// service description written to a temporary file that the stack reads through <c>someip.config_file</c>.
/// <para>
/// Why a file and not the object overload: the file route is the one a user takes, so every test exercises the settings, the
/// loader and the compiler as well as the parser. The temporary directory is unique per instance and removed on dispose, so tests
/// share no file and leave none behind. Packet ids are handed out in order by <see cref="Parse"/>, which is what the ordered
/// first parse of a capture does. <b>Thread-safety:</b> not thread-safe, one instance per test.
/// </para>
/// </summary>
internal sealed class SomeIpTestStack : IDisposable
{
    private const string _ConfigFileName = "someip.json";

    private readonly string _Directory;
    private int _NextPacketId;

    private SomeIpTestStack(Stack stack, string directory, IReadOnlyList<SettingsLoadWarning> warnings)
    {
        Stack = stack;
        _Directory = directory;
        Warnings = warnings;
    }

    /// <summary>The stack under test.</summary>
    internal Stack Stack { get; }

    /// <summary>The warnings that the registration of the standard protocols returned.</summary>
    internal IReadOnlyList<SettingsLoadWarning> Warnings { get; }

    /// <summary>
    /// Builds a stack with the standard protocols.
    /// </summary>
    /// <param name="configJson">The service description as JSON text, or <see langword="null"/> for a stack without one.</param>
    /// <param name="settings">Settings preloaded before the protocols register (name and value).</param>
    internal static SomeIpTestStack Create(string? configJson = null, params (string Name, SettingValue Value)[] settings) =>
        Create(configJson, settings, customize: null);

    /// <summary>
    /// Builds a stack with the standard protocols and lets the caller add protocols before the stack is built.
    /// </summary>
    /// <param name="configJson">The service description as JSON text, or <see langword="null"/> for a stack without one.</param>
    /// <param name="settings">Settings preloaded before the protocols register (name and value).</param>
    /// <param name="customize">Called after the standard protocols are registered and before the stack is built.</param>
    /// <param name="prepare">Called before the standard protocols are registered (a table a protocol must find at its registration).</param>
    internal static SomeIpTestStack Create(
        string? configJson,
        (string Name, SettingValue Value)[] settings,
        Action<StackBuilder>? customize,
        Action<StackBuilder>? prepare = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "folder"));

#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        SettingsManager manager = new(directory);
#pragma warning restore CA2000
        if (configJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, _ConfigFileName), configJson);
            manager.PreloadValue(SomeIpRegistration.ConfigFileSetting, _ConfigFileName);
        }

        foreach ((string name, SettingValue value) in settings)
        {
            manager.PreloadValue(name, value);
        }

        StackBuilder builder = new(manager, new FrameInterfaceRegistry());
        prepare?.Invoke(builder);
        IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterStandardProtocols();
        customize?.Invoke(builder);
        return new SomeIpTestStack(builder.Build(), directory, warnings);
    }

    /// <summary>Parses a frame as the next packet of the capture (ids 0, 1, 2, ...; one millisecond apart).</summary>
    /// <param name="frame">The frame bytes (Ethernet).</param>
    internal Packet Parse(byte[] frame)
    {
        int id = _NextPacketId++;
        return ProtocolTestHelper.ParseFrame(Stack, frame, id, Timestamp.FromMillis(id));
    }

    /// <summary>
    /// Parses a frame again under the id it was first parsed with, which is the re-parse of a view or a filter.
    /// </summary>
    /// <param name="frame">The frame bytes.</param>
    /// <param name="packetId">The id of the first parse.</param>
    internal Packet Reparse(byte[] frame, int packetId) =>
        ProtocolTestHelper.ParseFrame(Stack, frame, packetId, Timestamp.FromMillis(packetId));

    /// <summary>
    /// Parses a frame as the next packet of the capture with an explicit capture time and, optionally, a capture interface. Timing
    /// tests need both: the delay of a response is the difference of two capture times, and a request pairs only on its own
    /// interface by default.
    /// </summary>
    /// <param name="frame">The frame bytes (Ethernet).</param>
    /// <param name="nanos">The capture time in nanoseconds.</param>
    /// <param name="interfaceId">The capture interface, or <see langword="null"/> for none.</param>
    internal Packet ParseAt(byte[] frame, long nanos, FrameInterfaceId? interfaceId = null)
    {
        int id = _NextPacketId++;
        return ProtocolTestHelper.ParseFrame(Stack, frame, id, Timestamp.FromNanos(nanos), interfaceId: interfaceId);
    }

    /// <summary>Parses a frame again under the id of its first parse, with the capture time and interface of that first parse.</summary>
    /// <param name="frame">The frame bytes.</param>
    /// <param name="packetId">The id of the first parse.</param>
    /// <param name="nanos">The capture time of the first parse.</param>
    /// <param name="interfaceId">The capture interface of the first parse.</param>
    internal Packet ReparseAt(byte[] frame, int packetId, long nanos, FrameInterfaceId? interfaceId = null) =>
        ProtocolTestHelper.ParseFrame(Stack, frame, packetId, Timestamp.FromNanos(nanos), interfaceId: interfaceId);

    /// <summary>Registers a capture interface on the stack, so that a frame can carry an interface id.</summary>
    /// <param name="name">The display name of the interface.</param>
    internal FrameInterfaceId RegisterInterface(string name)
    {
        FrameSourceId source = Stack.FrameInterfaceRegistry.RegisterSource(new _TestSource());
        return Stack.FrameInterfaceRegistry.Register(source, name);
    }

    private sealed class _TestSource : IFrameSource
    {
        public string UiName => "someip-test";

        public string? Description => null;

        public int? EstimatedFrameCount => null;

        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public void Stop() => _ = UiName;

        public void Dispose()
        {
        }
    }

    /// <summary>How many fields with the given name the packet holds (lazy children included).</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    internal int Count(Packet packet, string fieldName)
    {
        FieldId? id = Stack.GetFieldId(fieldName);
        if (id is null)
        {
            return 0;
        }

        int count = 0;
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id.Value)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The U64 values of every field with the given name, in tree order.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    internal List<ulong> U64s(Packet packet, string fieldName)
    {
        List<ulong> values = [];
        FieldId? id = Stack.GetFieldId(fieldName);
        if (id is null)
        {
            return values;
        }

        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id.Value && field.Value.Data.TryGetAsU64(out ulong value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>The String values of every field with the given name, in tree order.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    internal List<string> Strings(Packet packet, string fieldName)
    {
        List<string> values = [];
        FieldId? id = Stack.GetFieldId(fieldName);
        if (id is null)
        {
            return values;
        }

        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id.Value && field.Value.Data.TryGetAsString(out string? value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>The lengths of the Bytes values of every field with the given name, in tree order.</summary>
    /// <param name="packet">The packet.</param>
    /// <param name="fieldName">The registered field name.</param>
    internal List<int> ByteLengths(Packet packet, string fieldName)
    {
        List<int> lengths = [];
        FieldId? id = Stack.GetFieldId(fieldName);
        if (id is null)
        {
            return lengths;
        }

        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id.Value && field.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> value))
            {
                lengths.Add(value.Length);
            }
        }

        return lengths;
    }

    /// <summary>
    /// Parses a frame as the next packet of the capture with a presence index attached, so a test can check which index groups
    /// the eager part of the parse recorded (before anything is materialized).
    /// </summary>
    /// <param name="frame">The frame bytes (Ethernet).</param>
    /// <param name="index">The index the parse recorded into; its bitmaps are keyed by the packet id.</param>
    internal Packet ParseIndexed(byte[] frame, out PacketIndex index)
    {
        int id = _NextPacketId++;
        index = new PacketIndex(Stack);
        Frame parsed = Frame.Create(
            new FrameId(id),
            Timestamp.FromMillis(id),
            frame,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            Stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(index: index);
        if (!Packet.TryParse(new PacketId(id), Stack, parsed, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    /// <summary>Whether the index recorded the group; the group name is the one of a field's <c>IndexGroup</c>, for example <c>someip.name</c>.</summary>
    /// <param name="index">The index of <see cref="ParseIndexed"/>.</param>
    /// <param name="packet">The packet that was parsed into the index.</param>
    /// <param name="groupName">The index group name.</param>
    internal bool HasGroup(PacketIndex index, Packet packet, string groupName)
    {
        IndexGroupId? group = Stack.GetIndexGroupId(groupName);
        return group is not null && index.GetGroupBitmap(group.Value).Contains((uint)packet.Id.Value);
    }

    /// <summary>
    /// Asserts that the groups the parse recorded eagerly are exactly the groups of the fields the fully materialized tree holds:
    /// no field without its group (a filter would miss it), no group without a field (a filter would waste the packet).
    /// </summary>
    /// <param name="index">The index of <see cref="ParseIndexed"/>.</param>
    /// <param name="packet">The packet that was parsed into the index.</param>
    internal async Task AssertIndexExact(PacketIndex index, Packet packet)
    {
        HashSet<int> eager = [];
        for (int gid = 0; gid < Stack.IndexGroupCount; gid++)
        {
            if (index.GetGroupBitmap(new IndexGroupId(gid)).Contains((uint)packet.Id.Value))
            {
                _ = eager.Add(gid);
            }
        }

        packet.MaterializeAll();
        HashSet<int> materialized = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: false))
        {
            IndexGroupId group = Stack.GetFieldIndexGroup(field.FieldId);
            if (group.IsValid)
            {
                _ = materialized.Add(group.Value);
            }
        }

        foreach (int gid in eager)
        {
            await Assert.That(materialized.Contains(gid)).IsTrue()
                .Because($"group '{Stack.GetIndexGroup(new IndexGroupId(gid))?.Name}' was recorded but no field carries it");
        }

        foreach (int gid in materialized)
        {
            await Assert.That(eager.Contains(gid)).IsTrue()
                .Because($"a field of group '{Stack.GetIndexGroup(new IndexGroupId(gid))?.Name}' exists but the group was not recorded");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stack.Dispose();
        try
        {
            Directory.Delete(_Directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that cannot be removed (an antivirus scan holds it) is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the next run uses a new unique directory.
        }
    }
}
