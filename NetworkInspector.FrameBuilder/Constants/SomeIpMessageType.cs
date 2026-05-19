// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Constants;

/// <summary>
/// SOME/IP message type constants per AUTOSAR SomeIpProtocol specification (Table 8).
/// </summary>
public static class SomeIpMessageType
{
    /// <summary>Request requiring response (0x00).</summary>
    public const byte Request = 0x00;

    /// <summary>Request not requiring response / fire and forget (0x01).</summary>
    public const byte RequestNoReturn = 0x01;

    /// <summary>Notification event (0x02).</summary>
    public const byte Notification = 0x02;

    /// <summary>Response to a Request (0x80).</summary>
    public const byte Response = 0x80;

    /// <summary>Error response (0x81).</summary>
    public const byte Error = 0x81;

    /// <summary>
    /// TP flag — OR'd with the underlying message type to indicate a
    /// SOME/IP-TP segmented message.
    /// </summary>
    public const byte TpFlag = 0x20;

    /// <summary>Request TP (0x20 = Request | TpFlag).</summary>
    public const byte RequestTp = Request | TpFlag;

    /// <summary>Request No Return TP (0x21).</summary>
    public const byte RequestNoReturnTp = RequestNoReturn | TpFlag;

    /// <summary>Notification TP (0x22).</summary>
    public const byte NotificationTp = Notification | TpFlag;

    /// <summary>Response TP (0xa0).</summary>
    public const byte ResponseTp = Response | TpFlag;
}
