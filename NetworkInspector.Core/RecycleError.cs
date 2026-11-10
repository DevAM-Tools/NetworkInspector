// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Describes why a recycling parse operation cannot proceed.
/// <para>
/// <see cref="Packet.PrepareForReuse"/> returns <see cref="RecycleError"/>?
/// and <see cref="Packet.TryParse(Packet, PacketId, Stack, Frame, in ParseOptions, out ParseFailure)"/>
/// maps that code to <see cref="ParseFailure"/> without throwing. Every member is produced by
/// <see cref="Packet.PrepareForReuse"/> itself; the checks of the caller (tree mode, cache stack, id gap) report
/// <see cref="ParseFailure"/> codes directly.
/// </para>
/// </summary>
internal enum RecycleError
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
    /// The <see cref="Frame.Registry"/> of the new frame is not the <see cref="Stack.FrameInterfaceRegistry"/> of the stack
    /// supplied to the recycling parse. Frame and that stack must share the same <see cref="FrameInterfaceRegistry"/>
    /// instance.
    /// </summary>
    RegistryMismatch,

    /// <summary>
    /// The stack supplied to the recycling parse uses another <see cref="FrameInterfaceRegistry"/> than the stack the
    /// <c>recycle</c> packet belongs to, so its interface and source ids would not match the frame.
    /// A different stack of the same registry is accepted: the packet is rebound to it (see <see cref="Packet.Stack"/>).
    /// </summary>
    StackMismatch,
}
