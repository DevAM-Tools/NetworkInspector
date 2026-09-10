// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Describes why a recycling parse operation cannot proceed.
/// <para>
/// Returned by the hot-path <c>TryParseFrame(Packet recycle, …)</c> and
/// <c>TryParseFrameIndexed(Packet recycle, …)</c> factory methods instead of throwing,
/// so that tight recycling loops remain completely exception-free.
/// The corresponding throwing <c>ParseFrame(Packet recycle, …)</c> overloads translate
/// these codes into the appropriate <see cref="System.InvalidOperationException"/> or
/// <see cref="System.ArgumentException"/>.
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
    /// The <c>recycle</c> packet belongs to a different
    /// <see cref="Stack"/> than the one supplied to the factory method.
    /// The <c>stack</c> argument must be reference-equal to the recycle packet's stack.
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
