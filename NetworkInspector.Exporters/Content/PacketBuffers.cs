// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// Reads the additional buffers of a packet for the exporters that write them (<see cref="ExportContent.Buffers"/>): one place for
/// the "buffer inside the packet's own count must exist" check, so every writer (DuckDB, Parquet, and PBF Columnar through
/// <see cref="ColumnarPacketBatch"/>, PBF Standard, JSON, CSV, Text) reports a broken buffer table the same way.
/// </summary>
/// <remarks>
/// <para>
/// Buffer 0 is the frame (<see cref="ExportContent.FrameBytes"/>); the additional buffers are the indexes 1 up to
/// <see cref="Packet.BufferCount"/> - 1, at most 255 (the effect-layer key packs the index into eight bits). A writer loops
/// <c>for (int index = 1; index &lt; packet.BufferCount; index++)</c> and calls <see cref="Get"/>.
/// </para>
/// <para>
/// Thread safety: stateless; safe from any thread.
/// </para>
/// </remarks>
/// <seealso cref="Packet.Buffer"/>
internal static class PacketBuffers
{
    #region Reading

    /// <summary>
    /// Returns the bytes of the additional buffer <paramref name="index"/> of <paramref name="packet"/>.
    /// </summary>
    /// <param name="packet">The packet; its buffers are valid until it is recycled, so a caller encodes or copies the bytes before
    /// <c>OnPacket</c> returns.</param>
    /// <param name="index">A buffer index from 1 to <see cref="Packet.BufferCount"/> - 1; the caller's loop guarantees the range.</param>
    /// <returns>The buffer bytes; empty for a bound empty buffer (not a failure).</returns>
    /// <exception cref="InvalidOperationException">
    /// The packet reports no buffer at <paramref name="index"/> although the index is inside its own buffer count: a broken
    /// invariant of the packet's buffer table, not an expected failure, so it is thrown from a cold helper.
    /// </exception>
    /// <remarks>
    /// <see cref="Packet.Buffer"/> returns <see langword="null"/> only for an index outside <c>0..BufferCount-1</c>, which the
    /// caller's loop never asks for. A <see langword="null"/> is therefore reported and never skipped, because a skipped buffer would
    /// silently shift the exported buffer list. The exception surfaces through the exporter's existing packet-failure path
    /// (the batch rolls back, or the writer reports the packet as failed), so the error reaches the user through the error events.
    /// </remarks>
    internal static ReadOnlySpan<byte> Get(Packet packet, int index)
    {
        // The nullable flow analysis knows that the DoesNotReturn helper ends the null branch, so Value is safe below.
        ReadOnlyMemory<byte>? buffer = packet.Buffer(index);
        if (buffer is null)
        {
            _ThrowMissing(packet.Id, index, packet.BufferCount);
        }

        return buffer.Value.Span;
    }

    /// <summary>Reports a missing packet buffer as a broken invariant; kept out of line so the copy loops have no throw site.</summary>
    /// <param name="packetId">The packet whose buffer table is inconsistent.</param>
    /// <param name="index">The missing buffer index.</param>
    /// <param name="bufferCount">The buffer count the packet reported.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowMissing(PacketId packetId, int index, int bufferCount) =>
        throw new InvalidOperationException(Invariant(
            $"Buffer {index} of packet {packetId.Value} is missing although the packet reports {bufferCount} buffers."));

    #endregion
}
