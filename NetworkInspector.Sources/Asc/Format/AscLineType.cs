// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Classifies the type of an ASC file line for efficient dispatch. Produced by <see cref="AscLineClassifier"/> from the
/// line prefix only (no full parse). Only the frame-producing members (CAN, CAN FD, CAN XL, error and overload frames,
/// LIN message, FlexRay message, Ethernet packet) become frames; every other member is skipped by the sources, although a
/// timestamped one still advances a relative clock.
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

    /// <summary>CAN FD message. The token after the timestamp is <c>CANFD</c>.</summary>
    CanFdMessage,

    /// <summary>CAN XL message. The token after the timestamp is <c>CANXL</c>.</summary>
    CanXlMessage,

    /// <summary>CAN error frame: <c>&lt;channel&gt; ErrorFrame</c> after the timestamp (the keyword is the second token).</summary>
    CanErrorFrame,

    /// <summary>CAN overload frame: <c>&lt;channel&gt; OverloadFrame</c> after the timestamp (the keyword is the second token).</summary>
    CanOverloadFrame,

    /// <summary>CAN bus statistics event.</summary>
    CanBusStatistics,

    /// <summary>CAN status event (<c>CAN &lt;channel&gt; Status:…</c>).</summary>
    CanStatus,

    /// <summary>
    /// LIN message: the token after the timestamp is a LIN channel (<c>Li</c>, or <c>L</c> plus digits) and the line is
    /// not a sleep or wakeup event.
    /// </summary>
    LinMessage,

    /// <summary>
    /// LIN sleep or wakeup event: a LIN channel line where the token one or two places after the channel token starts with <c>sleep</c> or <c>wakeup</c>.
    /// Not a frame.
    /// </summary>
    LinEvent,

    /// <summary>FlexRay message: <c>Fr</c> prefix followed by <c>RMSG</c>, <c>PDU</c>, or the numeric channel of the old format.</summary>
    FlexRayMessage,

    /// <summary>
    /// FlexRay start cycle event. Declared, but <see cref="AscLineClassifier"/> never returns it (such lines are
    /// <see cref="Unknown"/>).
    /// </summary>
    FlexRayStartCycle,

    /// <summary>
    /// Ethernet packet. <c>ETH</c> or <c>AFDX</c> plus channel and direction <c>Rx</c>, <c>Tx</c>, <c>TxRq</c>, or <c>TxFwd</c>.
    /// Status lines are not packets.
    /// </summary>
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
