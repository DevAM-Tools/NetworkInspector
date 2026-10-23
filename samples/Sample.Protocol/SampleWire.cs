// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Constants of the sample wire format, shared by the protocol, the heuristic parser, the post-parser, and the frame writer
/// so that they cannot drift apart. The 14-byte payload is: magic <c>NIS1</c> (4), counter (8, big endian), gear (1),
/// speed (1).
/// </summary>
internal static class SampleWire
{
    #region Constants

    /// <summary>Name of the sample protocol and of its container field.</summary>
    internal const string ProtocolName = "sample";

    /// <summary>Length of the payload in bytes: magic 4, counter 8, gear 1, speed 1.</summary>
    internal const int PayloadLength = 14;

    /// <summary>Default UDP destination port on which the protocol is registered.</summary>
    internal const ushort DefaultPort = 40000;

    /// <summary>Offset of the counter inside the payload, in bytes.</summary>
    internal const int CounterOffset = 4;

    /// <summary>Offset of the gear byte inside the payload, in bytes.</summary>
    internal const int GearOffset = 12;

    /// <summary>Offset of the speed byte inside the payload, in bytes.</summary>
    internal const int SpeedOffset = 13;

    #endregion

    #region Properties

    /// <summary>The magic bytes <c>NIS1</c>; a property over a UTF-8 literal, so no array is allocated.</summary>
    internal static ReadOnlySpan<byte> MagicBytes => "NIS1"u8;

    #endregion
}
