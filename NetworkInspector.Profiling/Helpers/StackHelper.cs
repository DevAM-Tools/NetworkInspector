// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Shared helper to build a fully-registered protocol stack for profiling scenarios.
/// Centralises stack construction so every scenario uses the same configuration.
/// </summary>
internal static class StackHelper
{
    /// <summary>
    /// Creates a protocol stack with all standard protocols (Ethernet, IPv4, IPv6, UDP, …)
    /// registered and ready to parse.
    /// </summary>
    internal static Stack CreateStack()
    {
        SettingsManager? settings = new();
        try
        {
            FrameInterfaceRegistry registry = new();
            StackBuilder builder = new(settings, registry);
            builder.RegisterStandardProtocols();
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
