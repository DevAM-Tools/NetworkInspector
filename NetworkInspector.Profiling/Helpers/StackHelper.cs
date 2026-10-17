// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared helper to build protocol stacks for profiling scenarios.
/// <see cref="CreateStack"/> registers the lean protocol set of the parse scenarios.
/// <see cref="CreateFullStack"/> registers every standard protocol for export/read paths.
/// </summary>
internal static class StackHelper
{
    /// <summary>
    /// Creates the lean profiling stack: Frame, Ethernet, IPv4, IPv6, UDP, and TCP
    /// (<see cref="ProtocolRegistration.RegisterFairProfilingProtocols"/>). Parse scenarios measure this fixed set, so
    /// their results do not shift when protocols outside it are added to the standard registration.
    /// </summary>
    internal static Stack CreateStack() => _Build(static b => b.RegisterFairProfilingProtocols());

    /// <summary>
    /// Creates a stack with all standard protocols. Use for export/read of mixed captures.
    /// </summary>
    internal static Stack CreateFullStack() => _Build(static b => b.RegisterStandardProtocols());

    private static Stack _Build(Action<StackBuilder> register)
    {
        SettingsManager? settings = new();
        try
        {
            FrameInterfaceRegistry registry = new();
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
