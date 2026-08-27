// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Descriptor for a contiguous region of <see cref="FieldBody"/> slots within a slab's backing array.
/// Each chunk represents 16 <see cref="FieldBody"/> slots (16 × ~64 bytes = 1024 bytes).
/// Chunk descriptors themselves are stored in a slab-backed array managed by <see cref="SlabAllocator{T}"/>.
/// <para>
/// <b>Thread-safety:</b> Chunk descriptors are written by the single thread that parses the
/// owning packet, and may also be appended during post-<see cref="Packet.Seal"/> lazy
/// materialization from any thread.
/// Cross-thread reads are safe because <see cref="Packet"/> publishes the descriptor table
/// as one object and uses a volatile chunk-count store as the release fence for a new chunk.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FieldBodyChunk
{
    /// <summary>The slab's backing array containing the field body slots.</summary>
    internal FieldBody[] Buffer;

    /// <summary>The starting offset within <see cref="Buffer"/> where this chunk's slots begin.</summary>
    internal int Offset;
}
