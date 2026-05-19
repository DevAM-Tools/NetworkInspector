// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Blf.Format.Objects;

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Result of parsing a BLF object into a network frame.
/// Contains the reconstructed frame data, link type, and channel information.
/// </summary>
internal readonly struct BlfFrameResult
{
    /// <summary>Reconstructed frame data (Ethernet, SocketCAN, DLT_LIN, or DLT_FLEXRAY).</summary>
    internal byte[] FrameData
    {
        get; init;
    }

    /// <summary>Link type for the reconstructed frame.</summary>
    internal LinkType LinkType
    {
        get; init;
    }

    /// <summary>Channel number (BLF-level, used for interface registration).</summary>
    internal ushort Channel
    {
        get; init;
    }

    /// <summary>Object type that produced this frame (for bus type classification).</summary>
    internal uint ObjectType
    {
        get; init;
    }
}

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
    /// <returns>True if a frame was produced; false if the object type is unknown or parsing failed.</returns>
    internal static bool TryDispatch(in BlfObjectInfo objectInfo, out BlfFrameResult result)
    {
        result = default;

        switch (objectInfo.ObjectType)
        {
            #region Ethernet
            case BlfConstants.ObjTypeEthernetFrame:
                if (!EthernetParser.TryParseType71(objectInfo.Payload, out byte[] ethFrame71, out ushort ethCh71))
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
                if (!EthernetParser.TryParseType120(objectInfo.Payload, out byte[] ethFrame120, out ushort ethCh120))
                {
                    return false;
                }
                result = new BlfFrameResult
                {
                    FrameData = ethFrame120,
                    LinkType = LinkType.Ethernet,
                    Channel = ethCh120,
                    ObjectType = objectInfo.ObjectType,
                };
                return true;

            case BlfConstants.ObjTypeEthernetRxError:
                if (!EthernetParser.TryParseType102(objectInfo.Payload, out byte[] ethFrame102, out ushort ethCh102))
                {
                    return false;
                }
                result = new BlfFrameResult
                {
                    FrameData = ethFrame102,
                    LinkType = LinkType.Ethernet,
                    Channel = ethCh102,
                    ObjectType = objectInfo.ObjectType,
                };
                return true;

            #endregion

            #region CAN (classic)
            case BlfConstants.ObjTypeCanMessage:
                return TryDispatchCan(CanParser.TryParseCanMessage, objectInfo, out result);

            case BlfConstants.ObjTypeCanMessage2:
                return TryDispatchCan(CanParser.TryParseCanMessage2, objectInfo, out result);

            case BlfConstants.ObjTypeCanError:
                return TryDispatchCan(CanParser.TryParseCanError, objectInfo, out result);

            case BlfConstants.ObjTypeCanOverload:
                return TryDispatchCan(CanParser.TryParseCanOverload, objectInfo, out result);

            case BlfConstants.ObjTypeCanErrorExt:
                return TryDispatchCan(CanParser.TryParseCanErrorExt, objectInfo, out result);

            #endregion

            #region CAN FD
            case BlfConstants.ObjTypeCanFdMessage:
                return TryDispatchCan(CanParser.TryParseCanFdMessage, objectInfo, out result);

            case BlfConstants.ObjTypeCanFdMessage64:
                return TryDispatchCan(CanParser.TryParseCanFdMessage64, objectInfo, out result);

            case BlfConstants.ObjTypeCanFdError64:
                return TryDispatchCan(CanParser.TryParseCanFdError64, objectInfo, out result);

            #endregion

            #region LIN (V1)
            case BlfConstants.ObjTypeLinMessage:
                return TryDispatchLin(
                    (ReadOnlySpan<byte> p, out byte[] f, out ushort c) =>
                        LinParser.TryParseLinMessageV1(p, out f, out c),
                    objectInfo, out result);

            case BlfConstants.ObjTypeLinCrcError:
                return TryDispatchLinError(BlfConstants.LinErrorCrc, objectInfo, isV2: false, out result);

            case BlfConstants.ObjTypeLinRcvError:
                return TryDispatchLinError(BlfConstants.LinErrorRcv, objectInfo, isV2: false, out result);

            case BlfConstants.ObjTypeLinSndError:
                return TryDispatchLinError(BlfConstants.LinErrorSnd, objectInfo, isV2: false, out result);

            #endregion

            #region LIN (V2)
            case BlfConstants.ObjTypeLinMessage2:
                return TryDispatchLin(
                    (ReadOnlySpan<byte> p, out byte[] f, out ushort c) =>
                        LinParser.TryParseLinMessageV2(p, out f, out c),
                    objectInfo, out result);

            case BlfConstants.ObjTypeLinCrcError2:
                return TryDispatchLinError(BlfConstants.LinErrorCrc, objectInfo, isV2: true, out result);

            case BlfConstants.ObjTypeLinRcvError2:
                return TryDispatchLinError(BlfConstants.LinErrorRcv, objectInfo, isV2: true, out result);

            case BlfConstants.ObjTypeLinSndError2:
                return TryDispatchLinError(BlfConstants.LinErrorSnd, objectInfo, isV2: true, out result);

            // LIN sleep/wakeup — produce empty LIN frames with just a PID
            case BlfConstants.ObjTypeLinSleep:
            case BlfConstants.ObjTypeLinWakeup:
            case BlfConstants.ObjTypeLinWakeup2:
                return TryDispatchLinSleepWake(objectInfo, out result);

            #endregion

            #region FlexRay
            case BlfConstants.ObjTypeFlexRayData:
                return TryDispatchFlexRay(FlexRayParser.TryParseFlexRayData, objectInfo, out result);

            case BlfConstants.ObjTypeFlexRayMessage:
                return TryDispatchFlexRay(FlexRayParser.TryParseFlexRayMessage, objectInfo, out result);

            case BlfConstants.ObjTypeFlexRayRcvMessage:
                return TryDispatchFlexRay(FlexRayParser.TryParseFlexRayRcvMessage, objectInfo, out result);

            case BlfConstants.ObjTypeFlexRayRcvMessageEx:
                return TryDispatchFlexRay(FlexRayParser.TryParseFlexRayRcvMessageEx, objectInfo, out result);

            #endregion

            default:
                return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Parser delegate for CAN/LIN/FlexRay object types.</summary>
    private delegate bool TryParseDelegate(ReadOnlySpan<byte> payload, out byte[] frame, out ushort channel);

    /// <summary>Dispatches a CAN object type to its parser, producing SocketCAN link type.</summary>
    private static bool TryDispatchCan(
        TryParseDelegate parser, in BlfObjectInfo objectInfo, out BlfFrameResult result)
    {
        result = default;
        if (!parser(objectInfo.Payload, out byte[] canFrame, out ushort canChannel))
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
    private static bool TryDispatchLin(
        TryParseDelegate parser, in BlfObjectInfo objectInfo, out BlfFrameResult result)
    {
        result = default;
        if (!parser(objectInfo.Payload, out byte[] linFrame, out ushort linChannel))
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
    private static bool TryDispatchLinError(
        byte errorType, in BlfObjectInfo objectInfo, bool isV2, out BlfFrameResult result)
    {
        result = default;
        bool success;
        byte[] linFrame;
        ushort linChannel;

        if (isV2)
        {
            success = LinParser.TryParseLinErrorV2(objectInfo.Payload, errorType, out linFrame, out linChannel);
        }
        else
        {
            success = LinParser.TryParseLinErrorV1(objectInfo.Payload, errorType, out linFrame, out linChannel);
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

    /// <summary>Dispatches LIN sleep/wakeup objects — produces minimal LIN frames.</summary>
    private static bool TryDispatchLinSleepWake(in BlfObjectInfo objectInfo, out BlfFrameResult result)
    {
        result = default;

        if (objectInfo.Payload.Length < 2)
        {
            return false;
        }

        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(objectInfo.Payload);

        // Produce a minimal 4-byte DLT_LIN frame: [pid=0xFF|len=0|checksum=0|errors=0]
        // 0xFF is used as a special "sleep/wakeup" indicator PID
        byte[] frame = [0xFF, 0, 0, 0];

        result = new BlfFrameResult
        {
            FrameData = frame,
            LinkType = LinkType.Lin,
            Channel = channel,
            ObjectType = objectInfo.ObjectType,
        };
        return true;
    }

    /// <summary>Dispatches a FlexRay object type to its parser, producing DLT_FLEXRAY link type.</summary>
    private static bool TryDispatchFlexRay(
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
