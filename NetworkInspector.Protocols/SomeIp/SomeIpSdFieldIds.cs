// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Groups all registered field IDs for SOME/IP-SD sub-fields.
/// Populated in <see cref="SomeIpProtocol.OnStartCustom"/> from attribute-registered field IDs.
/// </summary>
internal readonly struct SomeIpSdFieldIds
{
    #region Top-level SD fields

    /// <summary>SD container field.</summary>
    internal FieldId Container
    {
        get; init;
    }

    /// <summary>Flags byte.</summary>
    internal FieldId Flags
    {
        get; init;
    }

    /// <summary>Reboot flag (bit 7).</summary>
    internal FieldId FlagsReboot
    {
        get; init;
    }

    /// <summary>Unicast flag (bit 6).</summary>
    internal FieldId FlagsUnicast
    {
        get; init;
    }

    /// <summary>Explicit initial data events request flag (bit 5).</summary>
    internal FieldId FlagsInitialEvents
    {
        get; init;
    }

    #endregion

    #region Entries array

    /// <summary>Entries array container.</summary>
    internal FieldId EntriesContainer
    {
        get; init;
    }

    /// <summary>Single entry container.</summary>
    internal FieldId EntryContainer
    {
        get; init;
    }

    /// <summary>Entry type.</summary>
    internal FieldId EntryType
    {
        get; init;
    }

    /// <summary>Index 1st options run.</summary>
    internal FieldId EntryIndex1
    {
        get; init;
    }

    /// <summary>Index 2nd options run.</summary>
    internal FieldId EntryIndex2
    {
        get; init;
    }

    /// <summary>Number of options run 1.</summary>
    internal FieldId EntryNumOpt1
    {
        get; init;
    }

    /// <summary>Number of options run 2.</summary>
    internal FieldId EntryNumOpt2
    {
        get; init;
    }

    /// <summary>Service ID.</summary>
    internal FieldId EntryServiceId
    {
        get; init;
    }

    /// <summary>Instance ID.</summary>
    internal FieldId EntryInstanceId
    {
        get; init;
    }

    /// <summary>Major version.</summary>
    internal FieldId EntryMajorVer
    {
        get; init;
    }

    /// <summary>TTL (24-bit).</summary>
    internal FieldId EntryTtl
    {
        get; init;
    }

    /// <summary>Minor version (service entries type &lt; 0x04).</summary>
    internal FieldId EntryMinorVer
    {
        get; init;
    }

    /// <summary>Eventgroup ID (eventgroup entries type &gt;= 0x04).</summary>
    internal FieldId EntryEventgroupId
    {
        get; init;
    }

    #endregion

    #region Options array

    /// <summary>Options array container.</summary>
    internal FieldId OptionsContainer
    {
        get; init;
    }

    /// <summary>Single option container.</summary>
    internal FieldId OptionContainer
    {
        get; init;
    }

    /// <summary>Option length.</summary>
    internal FieldId OptionLength
    {
        get; init;
    }

    /// <summary>Option type.</summary>
    internal FieldId OptionType
    {
        get; init;
    }

    /// <summary>IPv4 address (endpoint/multicast options).</summary>
    internal FieldId OptionIpv4
    {
        get; init;
    }

    /// <summary>IPv6 address (endpoint/multicast options).</summary>
    internal FieldId OptionIpv6
    {
        get; init;
    }

    /// <summary>L4 protocol (TCP=6, UDP=17).</summary>
    internal FieldId OptionProto
    {
        get; init;
    }

    /// <summary>Port number.</summary>
    internal FieldId OptionPort
    {
        get; init;
    }

    /// <summary>Configuration string (configuration option 0x01).</summary>
    internal FieldId OptionConfigString
    {
        get; init;
    }

    /// <summary>Load balancing priority.</summary>
    internal FieldId OptionLbPriority
    {
        get; init;
    }

    /// <summary>Load balancing weight.</summary>
    internal FieldId OptionLbWeight
    {
        get; init;
    }

    #endregion
}
