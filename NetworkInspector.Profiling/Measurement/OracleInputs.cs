// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Shared input handling of the oracle writers: reading a reference capture and parsing into a recycled packet.
/// <para><b>Thread-safety:</b> stateless; the source and packets passed in are single-threaded.</para>
/// </summary>
internal static class OracleInputs
{
    #region Public API

    /// <summary>
    /// Starts <paramref name="source"/> on <paramref name="stack"/>'s registry and reads every frame. The frames own
    /// their bytes (the source copies them), so they stay valid while the caller keeps the source alive.
    /// </summary>
    internal static Frame[] ReadFrames(PcapngSource source, Stack stack)
    {
        source.Start(stack.FrameInterfaceRegistry.RegisterSource(source), stack.FrameInterfaceRegistry);
        List<Frame> frames = [];
        while (source.NextFrame() is Frame frame)
        {
            frames.Add(frame);
        }

        return [.. frames];
    }

    /// <summary>
    /// Parses <paramref name="frame"/> as <paramref name="id"/> into <paramref name="recycle"/>, or into a new packet
    /// that becomes the recycle instance. False when the parse was refused (for example a parse-id gap).
    /// </summary>
    internal static bool TryParse(Stack stack, PacketId id, Frame frame, in ParseOptions options, ref Packet? recycle)
    {
        if (recycle is not null && Packet.TryParse(recycle, id, stack, frame, in options, out _))
        {
            return true;
        }

        if (!Packet.TryParse(id, stack, frame, in options, out Packet? created, out _))
        {
            return false;
        }

        recycle = created;
        return true;
    }

    #endregion
}
