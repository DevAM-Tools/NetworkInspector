// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Result of parsing a BLF object into a network frame.
/// Contains the reconstructed frame data, link type, and channel information.
/// </summary>
/// <param name="FrameData">
/// Reconstructed frame data (Ethernet, SocketCAN, DLT_LIN, or DLT_FLEXRAY).
/// May alias a decompressed container array for Type 120/102 Ethernet frames.
/// </param>
/// <param name="LinkType">Link type for the reconstructed frame.</param>
/// <param name="Channel">Channel number (BLF-level, used for interface registration).</param>
/// <param name="ObjectType">Object type that produced this frame (for bus type classification).</param>
/// <param name="HardwareChannel">
/// Ethernet hardware channel when the object marked it valid. Null for every other object.
/// </param>
internal readonly record struct BlfFrameResult(
    ReadOnlyMemory<byte> FrameData,
    LinkType LinkType,
    ushort Channel,
    uint ObjectType,
    ushort? HardwareChannel = null);

/// <summary>
/// Dispatches BLF object payloads to the appropriate protocol parser
/// and returns the reconstructed frame with its link type.
/// Routes by object type to Ethernet, CAN, LIN, or FlexRay parsers.
/// </summary>
internal static class BlfFrameDispatcher
{
    #region Public API

    /// <summary>
    /// Tries to convert a parsed BLF object into a network frame.
    /// </summary>
    /// <param name="objectInfo">Parsed object metadata and payload.</param>
    /// <param name="result">Frame result with data, link type, and channel on success.</param>
    /// <param name="slabs">
    /// Frame slab allocator of the sequential reader, or null for one array per frame (random access, tests).
    /// FlexRay frames always get their own array (the frame builder is shared with the protocol layer).
    /// </param>
    /// <returns>True if a frame was produced; false if the object type is unknown or parsing failed.</returns>
    internal static bool TryDispatch(in BlfObjectInfo objectInfo, out BlfFrameResult result, FrameSlabAllocator? slabs = null)
    {
        result = default;

        switch (objectInfo.ObjectType)
        {
            #region Ethernet
            case BlfConstants.ObjTypeEthernetFrame:
                if (!EthernetParser.TryParseType71(objectInfo.Payload, slabs, out ReadOnlyMemory<byte> ethFrame71, out ushort ethCh71))
                {
                    return false;
                }
                result = new BlfFrameResult
                {
                    FrameData = ethFrame71,
                    LinkType = LinkType.Ethernet,
                    Channel = ethCh71,
                    ObjectType = objectInfo.ObjectType,
                };
                return true;

            case BlfConstants.ObjTypeEthernetFrameEx:
            case BlfConstants.ObjTypeEthernetFrameForwarded:
            case BlfConstants.ObjTypeEthernetErrorEx:
            case BlfConstants.ObjTypeEthernetErrorForwarded:
                // Types 121-123 share the Type 120 header; error frames carry the captured bytes like 120 does.
                if (!EthernetParser.TryParseType120(
                    objectInfo.Payload, objectInfo.PayloadMemory, slabs, out ReadOnlyMemory<byte> ethFrame120, out ushort ethCh120))
                {
                    return false;
                }
                result = new BlfFrameResult
                {
                    FrameData = ethFrame120,
                    LinkType = LinkType.Ethernet,
                    Channel = ethCh120,
                    ObjectType = objectInfo.ObjectType,
                    HardwareChannel = _HardwareChannelOrNull(objectInfo),
                };
                return true;

            case BlfConstants.ObjTypeEthernetRxError:
                if (!EthernetParser.TryParseType102(
                    objectInfo.Payload, objectInfo.PayloadMemory, slabs, out ReadOnlyMemory<byte> ethFrame102, out ushort ethCh102))
                {
                    return false;
                }
                result = new BlfFrameResult
                {
                    FrameData = ethFrame102,
                    LinkType = LinkType.Ethernet,
                    Channel = ethCh102,
                    ObjectType = objectInfo.ObjectType,
                    HardwareChannel = _HardwareChannelOrNull(objectInfo),
                };
                return true;

            #endregion

            #region CAN (classic)
            case BlfConstants.ObjTypeCanMessage:
                return _TryDispatchCan(CanParser.TryParseCanMessage, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeCanMessage2:
                return _TryDispatchCan(CanParser.TryParseCanMessage2, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeCanError:
                return _TryDispatchCan(CanParser.TryParseCanError, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeCanOverload:
                return _TryDispatchCan(CanParser.TryParseCanOverload, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeCanErrorExt:
                return _TryDispatchCan(CanParser.TryParseCanErrorExt, objectInfo, slabs, out result);

            #endregion

            #region CAN FD
            case BlfConstants.ObjTypeCanFdMessage:
                return _TryDispatchCan(CanParser.TryParseCanFdMessage, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeCanFdMessage64:
                return _TryDispatchCan(CanParser.TryParseCanFdMessage64, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeCanFdError64:
                return _TryDispatchCan(CanParser.TryParseCanFdError64, objectInfo, slabs, out result);

            #endregion

            #region CAN XL
            case BlfConstants.ObjTypeCanXlChannelFrame:
                return _TryDispatchCan(CanParser.TryParseCanXlChannelFrame, objectInfo, slabs, out result);

            #endregion

            #region LIN (V1)
            case BlfConstants.ObjTypeLinMessage:
                return _TryDispatchLin(LinParser.TryParseLinMessageV1, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeLinCrcError:
                return _TryDispatchLinError(BlfConstants.LinErrorCrc, objectInfo, isV2: false, slabs, out result);

            case BlfConstants.ObjTypeLinRcvError:
                return _TryDispatchLinError(BlfConstants.LinErrorRcv, objectInfo, isV2: false, slabs, out result);

            case BlfConstants.ObjTypeLinSndError:
                return _TryDispatchLinError(BlfConstants.LinErrorSnd, objectInfo, isV2: false, slabs, out result);

            #endregion

            #region LIN (V2)
            case BlfConstants.ObjTypeLinMessage2:
                return _TryDispatchLin(LinParser.TryParseLinMessageV2, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeLinCrcError2:
                return _TryDispatchLinError(BlfConstants.LinErrorCrc, objectInfo, isV2: true, slabs, out result);

            case BlfConstants.ObjTypeLinRcvError2:
                return _TryDispatchLinError(BlfConstants.LinErrorRcv, objectInfo, isV2: true, slabs, out result);

            case BlfConstants.ObjTypeLinSndError2:
                return _TryDispatchLinError(BlfConstants.LinErrorSnd, objectInfo, isV2: true, slabs, out result);

            case BlfConstants.ObjTypeLinSleep:
                return _TryDispatchLin(LinParser.TryParseLinSleep, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeLinWakeup:
                return _TryDispatchLin(LinParser.TryParseLinWakeup, objectInfo, slabs, out result);

            case BlfConstants.ObjTypeLinWakeup2:
                return _TryDispatchLin(LinParser.TryParseLinWakeup2, objectInfo, slabs, out result);

            #endregion

            #region FlexRay
            case BlfConstants.ObjTypeFlexRayData:
                return _TryDispatchFlexRay(FlexRayParser.TryParseFlexRayData, objectInfo, out result);

            case BlfConstants.ObjTypeFlexRayMessage:
                return _TryDispatchFlexRay(FlexRayParser.TryParseFlexRayMessage, objectInfo, out result);

            case BlfConstants.ObjTypeFlexRayRcvMessage:
                return _TryDispatchFlexRay(FlexRayParser.TryParseFlexRayRcvMessage, objectInfo, out result);

            case BlfConstants.ObjTypeFlexRayRcvMessageEx:
                return _TryDispatchFlexRay(FlexRayParser.TryParseFlexRayRcvMessageEx, objectInfo, out result);

            #endregion

            default:
                // TODO: CAN XL error frame (type 140) not mapped to SocketCAN yet
                return false;
        }
    }

    /// <summary>
    /// Link type for a BLF object type. Both the file reader and the stream reader use this map.
    /// </summary>
    internal static LinkType LinkTypeForObject(uint objectType) => objectType switch
    {
        BlfConstants.ObjTypeEthernetFrame or BlfConstants.ObjTypeEthernetFrameEx
            or BlfConstants.ObjTypeEthernetFrameForwarded or BlfConstants.ObjTypeEthernetErrorEx
            or BlfConstants.ObjTypeEthernetErrorForwarded or BlfConstants.ObjTypeEthernetRxError => LinkType.Ethernet,

        BlfConstants.ObjTypeCanMessage or BlfConstants.ObjTypeCanError
            or BlfConstants.ObjTypeCanOverload or BlfConstants.ObjTypeCanErrorExt
            or BlfConstants.ObjTypeCanMessage2 or BlfConstants.ObjTypeCanFdMessage
            or BlfConstants.ObjTypeCanFdMessage64 or BlfConstants.ObjTypeCanFdError64
            or BlfConstants.ObjTypeCanXlChannelFrame
            => LinkType.CanSocketcan,

        BlfConstants.ObjTypeLinMessage or BlfConstants.ObjTypeLinMessage2
            or BlfConstants.ObjTypeLinCrcError or BlfConstants.ObjTypeLinCrcError2
            or BlfConstants.ObjTypeLinRcvError or BlfConstants.ObjTypeLinRcvError2
            or BlfConstants.ObjTypeLinSndError or BlfConstants.ObjTypeLinSndError2
            or BlfConstants.ObjTypeLinSleep or BlfConstants.ObjTypeLinWakeup
            or BlfConstants.ObjTypeLinWakeup2
            => LinkType.Lin,

        BlfConstants.ObjTypeFlexRayData or BlfConstants.ObjTypeFlexRayMessage
            or BlfConstants.ObjTypeFlexRayRcvMessage or BlfConstants.ObjTypeFlexRayRcvMessageEx
            => LinkType.Flexray,

        _ => LinkType.Null,
    };

    /// <summary>
    /// Reads only the channel field for a frame-producing object type, without reconstructing
    /// frame bytes. Uses the same minimum payload sizes as the corresponding parsers.
    /// </summary>
    internal static bool TryGetChannel(uint objectType, ReadOnlySpan<byte> payload, out ushort channel)
    {
        channel = 0;
        return objectType switch
        {
            BlfConstants.ObjTypeEthernetFrame => EthernetParser.TryGetChannelType71(payload, out channel),
            BlfConstants.ObjTypeEthernetFrameEx
                or BlfConstants.ObjTypeEthernetFrameForwarded
                or BlfConstants.ObjTypeEthernetErrorEx
                or BlfConstants.ObjTypeEthernetErrorForwarded => EthernetParser.TryGetChannelType120(payload, out channel),
            BlfConstants.ObjTypeEthernetRxError => EthernetParser.TryGetChannelType102(payload, out channel),
            BlfConstants.ObjTypeCanMessage
                or BlfConstants.ObjTypeCanMessage2
                or BlfConstants.ObjTypeCanError
                or BlfConstants.ObjTypeCanOverload
                or BlfConstants.ObjTypeCanErrorExt
                or BlfConstants.ObjTypeCanFdMessage
                or BlfConstants.ObjTypeCanFdMessage64
                or BlfConstants.ObjTypeCanFdError64
                or BlfConstants.ObjTypeCanXlChannelFrame =>
                CanParser.TryGetChannel(objectType, payload, out channel),
            BlfConstants.ObjTypeLinMessage
                or BlfConstants.ObjTypeLinCrcError
                or BlfConstants.ObjTypeLinRcvError
                or BlfConstants.ObjTypeLinSndError
                or BlfConstants.ObjTypeLinMessage2
                or BlfConstants.ObjTypeLinCrcError2
                or BlfConstants.ObjTypeLinRcvError2
                or BlfConstants.ObjTypeLinSndError2
                or BlfConstants.ObjTypeLinSleep
                or BlfConstants.ObjTypeLinWakeup
                or BlfConstants.ObjTypeLinWakeup2 =>
                LinParser.TryGetChannel(objectType, payload, out channel),
            BlfConstants.ObjTypeFlexRayData
                or BlfConstants.ObjTypeFlexRayMessage
                or BlfConstants.ObjTypeFlexRayRcvMessage
                or BlfConstants.ObjTypeFlexRayRcvMessageEx =>
                FlexRayParser.TryGetChannel(objectType, payload, out channel),
            _ => false,
        };
    }

    #endregion

    #region Private Helpers

    private static ushort? _HardwareChannelOrNull(in BlfObjectInfo objectInfo)
    {
        if (!EthernetParser.TryReadHardwareChannel(objectInfo.ObjectType, objectInfo.Payload, out ushort hardwareChannel))
        {
            return null;
        }

        return hardwareChannel;
    }

    /// <summary>Parser delegate for FlexRay object types (per-frame array).</summary>
    private delegate bool TryParseDelegate(ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel);

    /// <summary>Parser delegate for CAN / LIN object types that can build into a frame slab.</summary>
    private delegate bool TryParseSlabDelegate(
        ReadOnlySpan<byte> payload, FrameSlabAllocator? slabs, out ReadOnlyMemory<byte> frame, out ushort channel);

    /// <summary>Dispatches a CAN object type to its parser, producing SocketCAN link type.</summary>
    private static bool _TryDispatchCan(
        TryParseSlabDelegate parser, in BlfObjectInfo objectInfo, FrameSlabAllocator? slabs, out BlfFrameResult result)
    {
        result = default;
        if (!parser(objectInfo.Payload, slabs, out ReadOnlyMemory<byte> canFrame, out ushort canChannel))
        {
            return false;
        }
        result = new BlfFrameResult
        {
            FrameData = canFrame,
            LinkType = LinkType.CanSocketcan,
            Channel = canChannel,
            ObjectType = objectInfo.ObjectType,
        };
        return true;
    }

    /// <summary>Dispatches a LIN object type to its parser, producing DLT_LIN link type.</summary>
    private static bool _TryDispatchLin(
        TryParseSlabDelegate parser, in BlfObjectInfo objectInfo, FrameSlabAllocator? slabs, out BlfFrameResult result)
    {
        result = default;
        if (!parser(objectInfo.Payload, slabs, out ReadOnlyMemory<byte> linFrame, out ushort linChannel))
        {
            return false;
        }
        result = new BlfFrameResult
        {
            FrameData = linFrame,
            LinkType = LinkType.Lin,
            Channel = linChannel,
            ObjectType = objectInfo.ObjectType,
        };
        return true;
    }

    /// <summary>Dispatches a LIN error object type to its parser.</summary>
    private static bool _TryDispatchLinError(
        byte errorType, in BlfObjectInfo objectInfo, bool isV2, FrameSlabAllocator? slabs, out BlfFrameResult result)
    {
        result = default;
        bool success;
        ReadOnlyMemory<byte> linFrame;
        ushort linChannel;

        if (isV2)
        {
            success = LinParser.TryParseLinErrorV2(objectInfo.Payload, errorType, slabs, out linFrame, out linChannel);
        }
        else
        {
            success = LinParser.TryParseLinErrorV1(objectInfo.Payload, errorType, slabs, out linFrame, out linChannel);
        }

        if (!success)
        {
            return false;
        }

        result = new BlfFrameResult
        {
            FrameData = linFrame,
            LinkType = LinkType.Lin,
            Channel = linChannel,
            ObjectType = objectInfo.ObjectType,
        };
        return true;
    }

    /// <summary>Dispatches a FlexRay object type to its parser, producing DLT_FLEXRAY link type.</summary>
    private static bool _TryDispatchFlexRay(
        TryParseDelegate parser, in BlfObjectInfo objectInfo, out BlfFrameResult result)
    {
        result = default;
        if (!parser(objectInfo.Payload, out byte[] frFrame, out ushort frChannel))
        {
            return false;
        }
        result = new BlfFrameResult
        {
            FrameData = frFrame,
            LinkType = LinkType.Flexray,
            Channel = frChannel,
            ObjectType = objectInfo.ObjectType,
        };
        return true;
    }

    #endregion
}
