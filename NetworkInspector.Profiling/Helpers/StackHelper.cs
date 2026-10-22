// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared helper to build protocol stacks for profiling scenarios.
/// <see cref="CreateStack()"/> registers the lean protocol set of the parse scenarios.
/// <see cref="CreateFullStack"/> registers every standard protocol for export/read paths.
/// <see cref="CreateStorageStack"/> adds CAN and a synthetic fanout protocol for the field-storage scenarios.
/// </summary>
internal static class StackHelper
{
    /// <summary>
    /// Creates the lean profiling stack: Frame, Ethernet, IPv4, IPv6, UDP, and TCP
    /// (<see cref="ProtocolRegistration.RegisterFairProfilingProtocols"/>). Parse scenarios measure this fixed set, so
    /// their results do not shift when protocols outside it are added to the standard registration.
    /// </summary>
    internal static Stack CreateStack() => _Build(static b => b.RegisterFairProfilingProtocols(), new());

    /// <summary>
    /// Creates the lean profiling stack on an existing frame-interface registry, so frames created once for that
    /// registry can be parsed by a fresh stack. A stack remembers which packet ids it parsed first (later parses of
    /// those ids are replays that record no index or effects), so scenarios whose ids restart at 0 per iteration need a
    /// fresh stack per iteration to measure first parses. The stack does not own the registry.
    /// </summary>
    internal static Stack CreateStack(FrameInterfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return _Build(static b => b.RegisterFairProfilingProtocols(), registry);
    }

    /// <summary>
    /// Creates a stack with all standard protocols. Use for export/read of mixed captures.
    /// </summary>
    internal static Stack CreateFullStack() => _Build(static b => b.RegisterStandardProtocols(), new());

    /// <summary>
    /// Fair profiling set plus CAN (SocketCAN, link type 227) and <see cref="FanoutProfilingProtocol"/>
    /// (<see cref="LinkType.User0"/>). Used by the field-storage scenarios only, so the fair parse scenarios keep their
    /// fixed protocol set.
    /// </summary>
    internal static Stack CreateStorageStack() => _Build(static b =>
    {
        _ = b.RegisterFairProfilingProtocols();
        CanProtocol can = new();
        ProtocolId canId = b.RegisterProtocol(can);
        can.RegisterFields(b, canId);
        FanoutProfilingProtocol.Register(b);
    }, new());

    private static Stack _Build(Action<StackBuilder> register, FrameInterfaceRegistry registry)
    {
        SettingsManager? settings = new();
        try
        {
            StackBuilder builder = new(settings, registry);
            register(builder);
            Stack stack = builder.Build();
            settings = null; // ownership transferred to stack
            return stack;
        }
        finally
        {
            settings?.Dispose();
        }
    }
}
