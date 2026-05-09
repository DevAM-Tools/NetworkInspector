// Copyright (c) DevAM and Network Inspector contributors
// Licensed under the MIT license.

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
    /// A concurrent lazy-field materializer is active on the packet
    /// (<c>_MaterializingFlag != 0</c>). Recycling while materialization is in progress
    /// would cause data corruption.
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
}
