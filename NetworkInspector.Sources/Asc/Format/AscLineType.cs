// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Classifies the type of an ASC file line for efficient dispatch.
/// </summary>
internal enum AscLineType : byte
{
    /// <summary>Line type could not be determined or is not supported.</summary>
    Unknown,

    /// <summary>Comment line (starts with <c>//</c> or <c>;</c>).</summary>
    Comment,

    /// <summary>File header line (<c>date</c>, <c>base</c>, <c>internal events logged</c>).</summary>
    Header,

    /// <summary><c>Begin Triggerblock</c> marker.</summary>
    TriggerBlockBegin,

    /// <summary><c>End TriggerBlock</c> marker.</summary>
    TriggerBlockEnd,

    /// <summary><c>Start of measurement</c> event.</summary>
    StartOfMeasurement,

    /// <summary>Classical CAN message (standard or extended frame).</summary>
    CanMessage,

    /// <summary>CAN FD message (line starts with <c>CANFD</c>).</summary>
    CanFdMessage,

    /// <summary>CAN error frame (<c>ErrorFrame</c>).</summary>
    CanErrorFrame,

    /// <summary>CAN overload frame (<c>OverloadFrame</c>).</summary>
    CanOverloadFrame,

    /// <summary>CAN bus statistics event.</summary>
    CanBusStatistics,

    /// <summary>CAN status event (<c>CAN &lt;channel&gt; Status:…</c>).</summary>
    CanStatus,

    /// <summary>LIN message or event (channel starts with <c>L</c>).</summary>
    LinMessage,

    /// <summary>LIN event (sleep, wakeup, scheduler mode change, etc.).</summary>
    LinEvent,

    /// <summary>FlexRay message (line contains <c>Fr</c> prefix).</summary>
    FlexRayMessage,

    /// <summary>FlexRay start cycle event.</summary>
    FlexRayStartCycle,

    /// <summary>Ethernet packet (<c>ETH</c> or <c>AFDX</c> prefix).</summary>
    EthernetPacket,

    /// <summary>Environment variable event.</summary>
    EnvironmentVariable,

    /// <summary>System variable event (<c>SV:</c> prefix).</summary>
    SystemVariable,

    /// <summary>Log trigger event.</summary>
    LogTrigger,

    /// <summary>GPS event.</summary>
    GpsEvent,
}
