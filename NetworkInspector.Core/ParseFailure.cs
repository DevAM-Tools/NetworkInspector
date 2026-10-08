// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Why <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>
/// or the recycling overload did not parse.
/// </summary>
/// <remarks>
/// <see cref="None"/> is returned only with a successful parse.
/// Expected precondition failures are codes so a recycle loop does not pay for an exception.
/// </remarks>
public enum ParseFailure : byte
{
    #region Public API

    /// <summary>The parse ran. Dissector and watch failures are stored on the packet instead.</summary>
    None = 0,

    /// <summary><see cref="Frame.IsValid"/> is false. <see cref="Frame"/> is a struct, so there is no null frame.</summary>
    InvalidFrame = 1,

    /// <summary>The recycling overload was called with a null packet.</summary>
    NullRecycle = 2,

    /// <summary>The recycle packet has not been sealed.</summary>
    NotFinalized = 3,

    /// <summary>A materializer holds the packet, or another recycle already holds the gate.</summary>
    MaterializerActive = 4,

    /// <summary>The frame registry is not the stack's frame-interface registry.</summary>
    RegistryMismatch = 5,

    /// <summary>The recycle packet belongs to a different stack instance.</summary>
    StackMismatch = 6,

    /// <summary><see cref="ParseOptions.FieldTree"/> is neither Build nor Skip.</summary>
    InvalidFieldTree = 7,

    /// <summary>The value cache belongs to a different stack.</summary>
    CacheStackMismatch = 8,

    /// <summary>The packet index belongs to a different stack.</summary>
    IndexStackMismatch = 9,

    /// <summary>The packet id jumps past the stack's next first-parse id.</summary>
    ParseIdGap = 10,

    /// <summary>
    /// The packet id is <see cref="PacketId.Invalid"/> or otherwise <see cref="PacketId.IsValid"/> is false.
    /// </summary>
    InvalidPacketId = 11,

    #endregion
}
