// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Descriptor for a contiguous region of <see cref="FieldBody"/> slots within a slab's backing array.
/// Each chunk represents 16 <see cref="FieldBody"/> slots (16 × ~64 bytes = 1024 bytes).
/// Chunk descriptors themselves are stored in a slab-backed array managed by <see cref="SlabAllocator{T}"/>.
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. Chunks are created during single-threaded parsing
/// and become immutable after <see cref="Packet.Seal"/>. Cross-thread reads are safe
/// because the chunk references are published via volatile fences in the <see cref="Packet"/>.
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
