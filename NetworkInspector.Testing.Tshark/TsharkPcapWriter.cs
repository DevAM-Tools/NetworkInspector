// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Writes a minimal classic PCAP file containing a single frame so tshark can dissect
/// in-memory test frames the same way it dissects on-disk captures.
/// </summary>
/// <remarks>
/// Classic PCAP (not PCAPNG) is used for maximum tshark compatibility with arbitrary
/// link-layer types (<c>dlt</c>). The DLT value is the standard libpcap
/// link-type identifier (<c>1</c> = Ethernet, <c>113</c> = SLL, <c>227</c> =
/// SocketCAN, …).
/// </remarks>
internal static class TsharkPcapWriter
{
    /// <summary>
    /// Writes a single-frame classic PCAP file at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Target file path; overwritten if it exists.</param>
    /// <param name="frameData">Raw frame bytes for the link-layer type indicated by <paramref name="dlt"/>.</param>
    /// <param name="dlt">libpcap link-type identifier (DLT).</param>
    internal static void Write(string path, byte[] frameData, int dlt)
    {
        using FileStream fs = File.Create(path);
        // PCAP global header (24 bytes, little-endian magic).
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xA1B2C3D4);          // magic
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);              // version major
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);              // version minor
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 0);               // tz offset
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);             // sigfigs
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 65535);         // snaplen
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)dlt);     // link type
        fs.Write(header);

        // Per-packet record header (16 bytes) + payload.
        Span<byte> record = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0);                                // ts_sec
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 0);                           // ts_usec
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)frameData.Length);      // incl_len
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)frameData.Length);     // orig_len
        fs.Write(record);
        fs.Write(frameData);
    }

    /// <summary>
    /// Writes a multi-frame classic PCAP file at <paramref name="path"/>.
    /// All frames share the same link-layer type <paramref name="dlt"/> and are
    /// written with monotonically increasing microsecond timestamps starting at
    /// epoch zero, so tshark can apply its conversation/stream-reassembly logic
    /// (TCP-stream tracking, HTTP reassembly, …) over the sequence.
    /// </summary>
    /// <param name="path">Target file path; overwritten if it exists.</param>
    /// <param name="frames">Raw frame bytes per record, in wire order.</param>
    /// <param name="dlt">libpcap link-type identifier (DLT).</param>
    internal static void Write(string path, IReadOnlyList<byte[]> frames, int dlt) =>
        Write(path, frames, timestampsMicros: null, dlt);

    /// <summary>
    /// Writes a multi-frame classic PCAP file with explicit capture times.
    /// </summary>
    /// <remarks>
    /// Why explicit times: the TCP analysis rules depend on the time between segments (a late segment within the reordering
    /// window is reordering, the same segment 200 ms later is a retransmission, a retransmission within 20 ms of a duplicate
    /// ACK is a fast retransmission). A fixed 1 ms spacing cannot express those scenarios.
    /// </remarks>
    /// <param name="path">Target file path; overwritten if it exists.</param>
    /// <param name="frames">Raw frame bytes per record, in wire order.</param>
    /// <param name="timestampsMicros">
    /// Capture time of each frame in microseconds since the epoch; must have one entry per frame, or be
    /// <see langword="null"/> for the default 1 ms spacing.
    /// </param>
    /// <param name="dlt">libpcap link-type identifier (DLT).</param>
    internal static void Write(string path, IReadOnlyList<byte[]> frames, IReadOnlyList<long>? timestampsMicros, int dlt)
    {
        if (timestampsMicros is not null && timestampsMicros.Count != frames.Count)
        {
            throw new ArgumentException("One timestamp per frame is required.", nameof(timestampsMicros));
        }

        using FileStream fs = File.Create(path);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xA1B2C3D4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 65535);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)dlt);
        fs.Write(header);

        Span<byte> record = stackalloc byte[16];
        for (int i = 0; i < frames.Count; i++)
        {
            byte[] frame = frames[i];
            // Synthetic timestamps: 1ms apart so tshark sees them in order, unless the caller gave explicit times.
            long micros = timestampsMicros is null
                ? (long)i * 1000
                : timestampsMicros[i];
            uint tsSec = (uint)(micros / 1_000_000);
            uint tsUsec = (uint)(micros % 1_000_000);
            BinaryPrimitives.WriteUInt32LittleEndian(record, tsSec);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], tsUsec);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)frame.Length);
            fs.Write(record);
            fs.Write(frame);
        }
    }
}
