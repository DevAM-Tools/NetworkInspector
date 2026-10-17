// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Bus names, AppText bus types, interface names, and interface property bags shared by the BLF file reader
/// (<c>BlfSource</c>) and the BLF stream reader (<c>BlfStreamSource</c>), so both register identical interfaces.
/// </summary>
/// <remarks>
/// Stateless; all members are thread-safe. Link-compiled into the exporter assembly, where the BLF exporter uses the
/// same names to decide which interfaces need an AppText channel-name object.
/// </remarks>
internal static class BlfInterfaceNaming
{
    #region Public API

    /// <summary>Bus name used in the default interface name (<c>"{bus} {channel}"</c>).</summary>
    internal static string BusName(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetFrameForwarded or BlfConstants.ObjTypeEthernetErrorEx
            or BlfConstants.ObjTypeEthernetErrorForwarded or BlfConstants.ObjTypeEthernetRxError => "Ethernet",

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 => "CAN",

        BlfConstants.ObjTypeCanFdMessage or BlfConstants.ObjTypeCanFdMessage64
            or BlfConstants.ObjTypeCanFdError64 => "CAN FD",

        BlfConstants.ObjTypeCanXlChannelFrame => "CAN XL",

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2 => "LIN",

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => "FlexRay",

        _ => "Unknown",
    };

    /// <summary>
    /// AppText bus type (<see cref="BlfConstants.BusTypeCan"/>, …) of an object type, for channel-name lookup and the
    /// <see cref="BlfInterfacePropertyKeys.BusType"/> property. 0 when the object type has no bus type.
    /// </summary>
    internal static byte BusType(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetFrameForwarded or BlfConstants.ObjTypeEthernetErrorEx
            or BlfConstants.ObjTypeEthernetErrorForwarded or BlfConstants.ObjTypeEthernetRxError => BlfConstants.BusTypeEthernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
            or BlfConstants.ObjTypeCanXlChannelFrame
            => BlfConstants.BusTypeCan,

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2 => BlfConstants.BusTypeLin,

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => BlfConstants.BusTypeFlexRay,

        _ => 0,
    };

    /// <summary>
    /// Interface name: the AppText channel name when one is known for the bus type and channel, otherwise
    /// <c>"{bus} {channel}"</c>.
    /// </summary>
    /// <param name="objectType">BLF object type of the frame.</param>
    /// <param name="channel">Object channel field.</param>
    /// <param name="channelNames">AppText channel names (bus type, channel) → name, or null.</param>
    internal static string InterfaceName(
        uint objectType, ushort channel, IReadOnlyDictionary<(byte BusType, byte Channel), string>? channelNames)
    {
        byte busType = BusType(objectType);

        // AppText (reserved bits 8–15) and the object channel field are both 1-based (CANoe "CAN 1" = 1); compare
        // unchanged. Channels above 255 cannot be named by AppText.
        if (busType != 0
            && channel <= byte.MaxValue
            && channelNames is { Count: > 0 }
            && channelNames.TryGetValue((busType, (byte)channel), out string? name))
        {
            return name;
        }

        return $"{BusName(objectType)} {channel}";
    }

    /// <summary>Interface properties: channel, object type, bus type, and the Ethernet hardware channel when present.</summary>
    internal static Dictionary<string, object> BuildProperties(uint objectType, ushort channel, ushort? hardwareChannel)
    {
        Dictionary<string, object> properties = new(4)
        {
            [BlfInterfacePropertyKeys.Channel] = (long)channel,
            [BlfInterfacePropertyKeys.ObjectType] = objectType,
            [BlfInterfacePropertyKeys.BusType] = BusType(objectType),
        };
        if (hardwareChannel is { } hardware)
        {
            properties[BlfInterfacePropertyKeys.HardwareChannel] = hardware;
        }

        return properties;
    }

    #endregion
}
