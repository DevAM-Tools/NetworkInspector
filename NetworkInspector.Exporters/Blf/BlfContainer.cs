// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// The uncompressed objects of one BLF log container, filled on the caller's thread and compressed and written by the writer
/// thread (<see cref="BlfWriter"/>). Two exist per export in background mode.
/// <para>
/// <b>Why it is split from the writer:</b> zlib on a container of several MiB is the most expensive single operation of any
/// exporter; on the caller's thread it would stall the reader for every container. The container only collects objects;
/// compressing and writing it is the pipeline's job.
/// </para>
/// <para>
/// <b>Container boundary invariant:</b> an object is never split across containers. <see cref="TryAppendObject"/>
/// refuses an object that would push a non-empty container past its size; the exporter submits the container and appends the
/// object to the next one. A single object larger than the size gets a container of its own.
/// </para>
/// <para><b>Thread safety:</b> owned by one thread at a time (see <see cref="IExportBatch"/>).</para>
/// </summary>
internal sealed class BlfContainer : IExportBatch
{
    #region Configuration and state

    /// <summary>
    /// Initial capacity in bytes (256 KiB). The buffer grows toward the container size on demand, so tiny exports do not rent the
    /// full size up front.
    /// </summary>
    private const int _InitialCapacity = 256 * 1024;

    /// <summary>The objects collected so far, uncompressed and 4-byte aligned, ready to be written as one log container.</summary>
    private readonly PooledBuffer _Buffer = new(_InitialCapacity);

    /// <summary>Uncompressed size in bytes at which the container counts as full (see <see cref="TryAppendObject"/>).</summary>
    private readonly int _MaxSize;

    /// <summary>Creates an empty container.</summary>
    /// <param name="maxSize">Uncompressed size at which the container is considered full (bytes).</param>
    internal BlfContainer(int maxSize) => _MaxSize = maxSize;

    /// <summary>Frames whose objects are in this container (the unit of the lost-batch accounting).</summary>
    public int PacketCount { get; private set; }

    /// <summary>Objects in this container (a frame may add a channel-name object besides its frame object).</summary>
    internal int ObjectCount { get; private set; }

    /// <summary>Uncompressed bytes in the container.</summary>
    public long EstimatedSizeBytes => _Buffer.Length;

    /// <summary>The uncompressed objects, valid until <see cref="Reset"/> or <see cref="Dispose"/>.</summary>
    internal ReadOnlySpan<byte> Content => _Buffer.WrittenSpan;

    #endregion

    #region Appending objects

    /// <summary>Appends one object to the container, unless it does not fit and the container already holds objects.</summary>
    /// <param name="objectType">BLF object type.</param>
    /// <param name="objectVersion">Object structure version (typically 0).</param>
    /// <param name="relativeTimestamp">Timestamp relative to the file's start anchor (nanoseconds).</param>
    /// <param name="payload">Pre-built object payload bytes.</param>
    /// <returns>
    /// <see langword="true"/> when the object was appended; <see langword="false"/> when it would push a non-empty container past
    /// its size (the caller hands the container over and appends to the next one). An empty container always takes the object.
    /// </returns>
    internal bool TryAppendObject(uint objectType, ushort objectVersion, ulong relativeTimestamp, ReadOnlySpan<byte> payload)
    {
        // Calculate object sizes with 4-byte alignment.
        int rawObjectSize = BlfWriter.ObjectHeaderTotalSize + payload.Length;
        int padding = BlfConstants.AlignmentPaddingByteCount(rawObjectSize);
        int totalObjectSize = rawObjectSize + padding;

        // If adding this object would exceed the container limit, the caller hands this container over first. This guarantees no
        // object spans container boundaries; the Length test lets a single object larger than the limit take an empty container.
        if (_Buffer.Length > 0 && _Buffer.Length + totalObjectSize > _MaxSize)
        {
            return false;
        }

        // Reserve space for the entire object (headers + payload + padding); each object is written in place, so no per-object
        // array is allocated.
        Span<byte> buf = _Buffer.Reserve(totalObjectSize);

        // -- Block header (16 bytes) --
        BinaryPrimitives.WriteUInt32LittleEndian(buf, BlfConstants.ObjectMagic);                        // "LOBJ" signature
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(4), (ushort)BlfWriter.ObjectHeaderTotalSize); // header_size = 32
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(6), 1);                                      // header_type = V1
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(8), (uint)rawObjectSize);                    // object_length (unpadded)
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(12), objectType);                            // object_type

        // -- Log object header V1 (16 bytes) --
        //   uint32 flags (4) | uint16 client_index (2) | uint16 object_version (2) | uint64 object_timestamp (8)
        // Timestamp is last. A timestamp-first layout cannot be reconstructed by BLF readers.
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(16), BlfConstants.TimestampResolution1Ns);   // flags = 1 ns
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(20), 0);                                     // client_index
        BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(22), objectVersion);                         // object_version
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(24), relativeTimestamp);                     // object_timestamp (ns)

        // -- Payload --
        payload.CopyTo(buf.Slice(BlfWriter.ObjectHeaderTotalSize));

        // -- 4-byte alignment padding (zeros) --
        if (padding > 0)
        {
            buf.Slice(rawObjectSize, padding).Clear();
        }

        ObjectCount++;
        return true;
    }

    /// <summary>Counts one frame whose objects were appended (called once per accepted frame).</summary>
    internal void CountFrame() => PacketCount++;

    /// <summary>Forgets the objects so the container can be filled again; keeps the pooled capacity.</summary>
    public void Reset()
    {
        _Buffer.Reset();
        PacketCount = 0;
        ObjectCount = 0;
    }

    /// <summary>Returns the pooled buffer.</summary>
    public void Dispose() => _Buffer.Return();

    #endregion
}
