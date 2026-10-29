// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Icmpv6;

/// <summary>
/// Groups all registered field IDs for ICMPv6 Multicast Listener Discovery (MLD) sub-fields.
/// Populated once in <see cref="Icmpv6Protocol"/> (its start hook) and read-only afterwards, so concurrent parses may share
/// it. The same reasoning as for <see cref="Icmpv6NdpFieldIds"/> applies: one struct passed by <see langword="in"/>
/// reference keeps the parser signatures short and creates no per-packet object.
/// </summary>
internal readonly struct Icmpv6MldFieldIds
{
    #region MLDv1 and MLDv2 query fields

    /// <summary>Maximum response delay of an MLDv1 message, milliseconds.</summary>
    internal FieldId MaxResponseDelay
    {
        get; init;
    }

    /// <summary>Multicast address the message is about (all-zero for a general query).</summary>
    internal FieldId MulticastAddress
    {
        get; init;
    }

    /// <summary>Maximum response code of an MLDv2 query (floating-point encoded above 32767).</summary>
    internal FieldId MaxResponseCode
    {
        get; init;
    }

    /// <summary>Container for the S / QRV flags byte of an MLDv2 query.</summary>
    internal FieldId Flags
    {
        get; init;
    }

    /// <summary>Suppress router-side processing flag (S).</summary>
    internal FieldId FlagS
    {
        get; init;
    }

    /// <summary>Querier's robustness variable (QRV), three bits.</summary>
    internal FieldId FlagQrv
    {
        get; init;
    }

    /// <summary>The four reserved bits of the flags byte.</summary>
    internal FieldId FlagReserved
    {
        get; init;
    }

    /// <summary>Querier's query interval code (floating-point encoded above 127), seconds.</summary>
    internal FieldId Qqi
    {
        get; init;
    }

    /// <summary>Number of sources the query declares.</summary>
    internal FieldId NbSources
    {
        get; init;
    }

    /// <summary>One source address of the query (repeated).</summary>
    internal FieldId SourceAddress
    {
        get; init;
    }

    #endregion

    #region MLDv2 report fields

    /// <summary>Number of multicast address records the report declares.</summary>
    internal FieldId ReportRecordCount
    {
        get; init;
    }

    /// <summary>Container of one multicast address record (repeated).</summary>
    internal FieldId Record
    {
        get; init;
    }

    /// <summary>Record type (include, exclude, change, allow, block).</summary>
    internal FieldId RecordType
    {
        get; init;
    }

    /// <summary>Length of the auxiliary data of a record in 32-bit words.</summary>
    internal FieldId RecordAuxDataLength
    {
        get; init;
    }

    /// <summary>Number of sources of a record.</summary>
    internal FieldId RecordNbSources
    {
        get; init;
    }

    /// <summary>Multicast address of a record.</summary>
    internal FieldId RecordMulticastAddress
    {
        get; init;
    }

    /// <summary>One source address of a record (repeated).</summary>
    internal FieldId RecordSourceAddress
    {
        get; init;
    }

    /// <summary>Auxiliary data of a record.</summary>
    internal FieldId RecordAuxData
    {
        get; init;
    }

    #endregion
}
