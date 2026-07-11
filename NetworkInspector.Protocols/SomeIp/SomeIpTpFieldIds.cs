// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Groups all registered field IDs for SOME/IP-TP header sub-fields.
/// Populated in <see cref="SomeIpProtocol._OnStartCustom"/> from attribute-registered field IDs.
/// </summary>
internal readonly struct SomeIpTpFieldIds
{
    /// <summary>TP container field ("SOME/IP-TP").</summary>
    internal FieldId Container
    {
        get; init;
    }

    /// <summary>TP byte offset (28-bit value).</summary>
    internal FieldId Offset
    {
        get; init;
    }

    /// <summary>More Segments flag.</summary>
    internal FieldId MoreSegments
    {
        get; init;
    }

    /// <summary>Reserved bits (should be 0).</summary>
    internal FieldId Reserved
    {
        get; init;
    }
}
