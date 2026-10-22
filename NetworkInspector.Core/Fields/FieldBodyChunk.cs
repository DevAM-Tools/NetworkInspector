// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Descriptor for one follow-up chunk of <see cref="FieldStorage"/>: a contiguous region of
/// <see cref="FieldStorage.ChunkSize"/> <see cref="FieldBody"/> slots within a slab's backing array. A recycle keeps it
/// or passes it to <see cref="FieldChunkPool"/>.
/// <para>
/// <b>Thread-safety:</b> a descriptor is written once by the thread that appends its follow-up chunk (the parse thread, or
/// after <see cref="Packet.Seal"/> the lazy materializer that reserved the chunk's first slot) and is then read-only
/// until the exclusive recycle, which may clear it after passing the chunk to the pool.
/// Cross-thread reads are safe because <see cref="FieldStorage"/> publishes a grown descriptor table as one object
/// and uses a volatile chunk-count store as the release fence for a new chunk.
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
