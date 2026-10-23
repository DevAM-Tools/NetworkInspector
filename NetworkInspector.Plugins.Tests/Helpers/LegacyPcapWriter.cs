// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Writes a legacy libpcap file (not PCAPNG) from frames: 24-byte global header with the microsecond magic
/// <c>0xA1B2C3D4</c> in little-endian byte order, version 2.4, link type Ethernet, then a 16-byte record header and the bytes
/// of every frame. Test helper; the Core exporters write PCAPNG only, so legacy files need a writer of their own.
/// </summary>
internal static class LegacyPcapWriter
{
    #region Constants

    /// <summary>Link type 1: Ethernet.</summary>
    private const uint _LinkTypeEthernet = 1;

    /// <summary>Snap length written to the header.</summary>
    private const uint _SnapLength = 65535;

    #endregion

    #region Public API

    /// <summary>Writes the file.</summary>
    /// <param name="path">Output path.</param>
    /// <param name="frames">Ethernet frames; timestamps are written with microsecond precision.</param>
    internal static void Write(string path, IReadOnlyList<Frame> frames)
    {
        using FileStream stream = File.Create(path);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xA1B2C3D4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], _SnapLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], _LinkTypeEthernet);
        stream.Write(header);

        // One 16-byte record header per frame: seconds, microseconds, stored length, original length.
        Span<byte> record = stackalloc byte[16];
        foreach (Frame frame in frames)
        {
            long nanos = frame.Timestamp.AsNanos;
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(nanos / 1_000_000_000L));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)(nanos % 1_000_000_000L / 1000));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)frame.Length);
            stream.Write(record);
            stream.Write(frame.Data.Span);
        }
    }

    #endregion
}
