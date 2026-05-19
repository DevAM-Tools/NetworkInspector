// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// SOME/IP message header (16 bytes) per AUTOSAR SomeIpProtocol specification §3.1.
/// Layout: ServiceId(2) + MethodId(2) + Length(4) + ClientId(2) + SessionId(2)
/// + ProtocolVersion(1) + InterfaceVersion(1) + MessageType(1) + ReturnCode(1).
/// All multi-byte fields are big-endian.
/// </summary>
[BinaryWritable]
internal readonly partial struct SomeIpHeader
{
    /// <summary>Size of the SOME/IP header in bytes.</summary>
    internal const int Size = 16;

    /// <summary>Service identifier.</summary>
    internal U16BE ServiceId
    {
        get; init;
    }

    /// <summary>Method or event identifier (top bit distinguishes events).</summary>
    internal U16BE MethodId
    {
        get; init;
    }

    /// <summary>
    /// Length field in bytes, counted from <see cref="ClientId"/> to the end
    /// of the payload (inclusive).  Patched in post-fix.
    /// </summary>
    internal U32BE Length
    {
        get; init;
    }

    /// <summary>Client identifier.</summary>
    internal U16BE ClientId
    {
        get; init;
    }

    /// <summary>Session identifier (incremented per request).</summary>
    internal U16BE SessionId
    {
        get; init;
    }

    /// <summary>Protocol version (always 1 for current SOME/IP).</summary>
    internal byte ProtocolVersion
    {
        get; init;
    }

    /// <summary>Interface version of the called service.</summary>
    internal byte InterfaceVersion
    {
        get; init;
    }

    /// <summary>Message type (request / response / notification / TP-flagged).</summary>
    internal byte MessageType
    {
        get; init;
    }

    /// <summary>Return code (0 = OK; non-zero on error responses).</summary>
    internal byte ReturnCode
    {
        get; init;
    }
}
