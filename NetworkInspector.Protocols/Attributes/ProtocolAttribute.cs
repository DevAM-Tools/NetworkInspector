// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Attributes;

/// <summary>
/// Marks a class as a protocol implementation. The source generator produces
/// <c>RegisterFields(IStackBuilder, ProtocolId)</c> and related boilerplate.
/// </summary>
/// <remarks>Initializes a new protocol marker attribute.</remarks>
/// <param name="name">Machine-readable protocol name.</param>
/// <param name="uiName">Human-readable UI name.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProtocolAttribute(string name, string uiName) : Attribute
{
    /// <summary>Machine-readable protocol name (e.g., "eth", "ip", "udp").</summary>
    public string Name { get; } = name;

    /// <summary>Human-readable UI name (e.g., "Ethernet", "IPv4", "UDP").</summary>
    public string UiName { get; } = uiName;

    /// <summary>Optional protocol description.</summary>
    public string? Description
    {
        get; set;
    }
}
