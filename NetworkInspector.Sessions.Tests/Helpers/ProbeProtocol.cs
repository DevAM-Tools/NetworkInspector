// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>
/// Minimal Ethernet-level protocol that makes the life cycle of a <see cref="Stack"/> observable: it counts its
/// <see cref="IProtocol.OnShutdown"/> calls (which <see cref="Stack.Dispose"/> triggers exactly once), it can park a parse
/// on a test gate, it records a shutdown that overlaps a parse, and its lazy populator fails after the shutdown the way a
/// protocol that reads a released resource (for example a settings lock) fails.
///
/// <para>
/// Thread-safety: the counters are <see cref="Interlocked"/>/volatile; the parse hook is set before the parse it is meant
/// for starts and read with a volatile load.
/// </para>
/// </summary>
internal sealed class ProbeProtocol : IProtocol
{
    #region Fields

    private FieldId _EagerFieldId;
    private FieldId _ContainerFieldId;
    private FieldId _ChildFieldId;
    private volatile Action? _ParseHook;
    private volatile Packet? _LastParsedPacket;
    private int _ShutdownCount;
    private int _ActiveParses;
    private int _ParseCount;
    private int _ShutdownDuringParseCount;

    #endregion

    #region Properties

    /// <summary>Name that is unique per stack because every stack of a test has its own probe.</summary>
    public string Name => "probe";

    /// <inheritdoc/>
    public string UiName => "Probe";

    /// <summary>When true, <see cref="OnShutdown"/> throws after it counted, so the stack reports a shutdown diagnostic.</summary>
    internal bool ThrowOnShutdown { get; init; }

    /// <summary>How many times <see cref="Stack.Dispose"/> (or <see cref="Stack.Shutdown"/>) reached this protocol.</summary>
    internal int ShutdownCount => Volatile.Read(ref _ShutdownCount);

    /// <summary>How many times a shutdown ran while at least one parse of this protocol was still inside <see cref="Parse"/>.</summary>
    internal int ShutdownDuringParseCount => Volatile.Read(ref _ShutdownDuringParseCount);

    /// <summary>How many parses entered <see cref="Parse"/>.</summary>
    internal int ParseCount => Volatile.Read(ref _ParseCount);

    /// <summary>The packet of the most recent parse that entered <see cref="Parse"/>; the ingest recycle for a source frame.</summary>
    internal Packet? LastParsedPacket => _LastParsedPacket;

    /// <summary>Field id of the eagerly appended value.</summary>
    internal FieldId EagerFieldId => _EagerFieldId;

    /// <summary>Field id of the lazy container.</summary>
    internal FieldId ContainerFieldId => _ContainerFieldId;

    /// <summary>Field id of the child the lazy populator appends.</summary>
    internal FieldId ChildFieldId => _ChildFieldId;

    #endregion

    #region Public API

    /// <summary>Sets the callback every parse runs first; <see langword="null"/> clears it. Set before the parse it should catch.</summary>
    internal void SetParseHook(Action? hook) => _ParseHook = hook;

    /// <summary>Registers the fields. Called once by the stack factory while the builder is open.</summary>
    internal void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        _EagerFieldId = builder.RegisterField(protocolId, "probe.eager", "Eager", FieldType.U64);
        _ContainerFieldId = builder.RegisterField(protocolId, "probe", "Probe", FieldType.None);
        _ChildFieldId = builder.RegisterField(protocolId, "probe.child", "Child", FieldType.U64);
    }

    /// <inheritdoc/>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        _LastParsedPacket = parentField.Packet;
        _ = Interlocked.Increment(ref _ActiveParses);
        _ = Interlocked.Increment(ref _ParseCount);
        try
        {
            _ParseHook?.Invoke();
            _ = parentField.Append(_EagerFieldId, FieldValue.NewU64((ulong)data.Length));

            // The populator may run long after the parse, on any thread; it models a protocol that needs a resource the
            // shutdown released.
            FieldId childId = _ChildFieldId;
            _ = parentField.AppendLazy(_ContainerFieldId, FieldValue.None, (in MutField container) =>
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _ShutdownCount) != 0, typeof(ProbeProtocol));
                _ = container.Append(childId, FieldValue.NewU64(7));
                return 0;
            });
            return data.Length;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _ActiveParses);
        }
    }

    /// <inheritdoc/>
    public void OnShutdown(Stack stack)
    {
        if (Volatile.Read(ref _ActiveParses) != 0)
        {
            _ = Interlocked.Increment(ref _ShutdownDuringParseCount);
        }

        _ = Interlocked.Increment(ref _ShutdownCount);
        if (ThrowOnShutdown)
        {
            throw new InvalidOperationException("probe shutdown failure");
        }
    }

    #endregion
}

/// <summary>Builds stacks around a <see cref="ProbeProtocol"/>.</summary>
internal static class ProbeStack
{
    /// <summary>
    /// Builds a stack with the frame protocol and one fresh probe that handles Ethernet frames. The stack shares
    /// <paramref name="registry"/>, as every <see cref="Session.Restart"/> factory result must.
    /// </summary>
    internal static Stack Create(FrameInterfaceRegistry registry, out ProbeProtocol probe, bool throwOnShutdown = false)
    {
        ArgumentNullException.ThrowIfNull(registry);

        SettingsManager? settingsManager = new();
        try
        {
            StackBuilder builder = new(settingsManager, registry);
            FrameProtocol frame = new();
            ProtocolId frameId = builder.RegisterProtocol(frame);
            frame.RegisterFields(builder, frameId);
            ProbeProtocol created = new() { ThrowOnShutdown = throwOnShutdown };
            ProtocolId probeId = builder.RegisterProtocol(created);
            created.RegisterFields(builder, probeId);
            builder.RegisterParserInU64TableByName(FrameProtocol.LinkTypeTableName, (ulong)LinkType.Ethernet, probeId);
            Stack stack = builder.Build();

            // Ownership of the settings manager moved to the stack.
            settingsManager = null;
            probe = created;
            return stack;
        }
        finally
        {
            settingsManager?.Dispose();
        }
    }
}
