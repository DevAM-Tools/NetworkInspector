// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Describes why a recycling parse operation cannot proceed.
/// <para>
/// <see cref="Packet.PrepareForReuse"/> returns <see cref="RecycleError"/>?
/// and <see cref="Packet.TryParse(Packet, PacketId, Stack, Frame, in ParseOptions, out ParseFailure)"/>
/// maps that code to <see cref="ParseFailure"/> without throwing.
/// </para>
/// <para>
/// Only <see cref="NotFinalized"/>, <see cref="MaterializerActive"/>, <see cref="RegistryMismatch"/>, and
/// <see cref="StackMismatch"/> are produced by
/// <see cref="Packet.PrepareForReuse"/> itself. The other members mirror conditions that
/// <c>Packet.TryParse</c> checks before it calls <see cref="Packet.PrepareForReuse"/>; they exist so the mapping to
/// <see cref="ParseFailure"/> stays total if an internal caller skips those checks.
/// </para>
/// </summary>
public enum RecycleError
{
    /// <summary>
    /// The packet has not been finalized yet (<see cref="Packet.IsFinalized"/> is
    /// <see langword="false"/>). Recycling an unsealed packet would corrupt an in-progress
    /// parse on the same thread.
    /// </summary>
    NotFinalized,

    /// <summary>
    /// A concurrent lazy materializer is active, or another recycle already holds the packet.
    /// Recycling while materialization is in progress would cause data corruption.
    /// </summary>
    MaterializerActive,

    /// <summary>
    /// The <see cref="Frame.Registry"/> of the new frame does not match the
    /// <see cref="Stack.FrameInterfaceRegistry"/> of the packet's owning stack.
    /// Frame and stack must share the same <see cref="FrameInterfaceRegistry"/> instance.
    /// </summary>
    RegistryMismatch,

    /// <summary>
    /// The stack supplied to the recycling parse uses another <see cref="FrameInterfaceRegistry"/> than the stack the
    /// <c>recycle</c> packet belongs to, so its interface and source ids would not match the frame.
    /// A different stack of the same registry is accepted: the packet is rebound to it (see <see cref="Packet.Stack"/>).
    /// </summary>
    StackMismatch,

    /// <summary>
    /// <see cref="FieldTreeMode"/> is neither <see cref="FieldTreeMode.Build"/> nor
    /// <see cref="FieldTreeMode.Skip"/>. The recycle packet is left unchanged.
    /// </summary>
    InvalidFieldTree,

    /// <summary>
    /// The supplied <see cref="ValueCache"/> belongs to a different <see cref="Stack"/> than
    /// the recycle packet. The recycle packet is left unchanged.
    /// </summary>
    CacheStackMismatch,

    /// <summary>
    /// The packet id would jump past the stack's next first-parse watermark.
    /// The recycle packet is left unchanged.
    /// </summary>
    ParseIdGap,
}
