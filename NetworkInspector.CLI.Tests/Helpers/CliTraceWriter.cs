// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Writes small capture files for the command line tests: a PCAPNG with Ethernet/IPv4/TCP segments of one connection whose
/// sequence numbers start high (so a relative and an absolute rendering differ). Stateless; thread-safe.
/// </summary>
internal static class CliTraceWriter
{
    #region Constants

    /// <summary>Sequence number of the first segment.</summary>
    internal const uint FirstSequence = 1_000_000;

    /// <summary>Bytes of payload per segment.</summary>
    internal const int PayloadBytes = 100;

    #endregion

    #region Public API

    /// <summary>
    /// Writes <paramref name="count"/> segments from 192.0.2.1:50000 to 192.0.2.2:5001 (PSH|ACK) with the sequence numbers
    /// <c>1,000,000 + 100 · i</c> and 100-byte payloads to a PCAPNG file.
    /// </summary>
    /// <param name="path">Output path.</param>
    /// <param name="count">Number of segments.</param>
    internal static void WriteTcpPcapng(string path, int count)
    {
        FrameInterfaceRegistry registry = new();
        using NetworkInspector.Exporters.Pcapng.PcapngExporter exporter = NetworkInspector.Exporters.Pcapng.PcapngExporter.CreateBuilder()
            .ToFile(path).Build();

        // A SYN comes first: the stream tracker numbers relative to the initial sequence number of a SYN, so without it
        // the relative and the absolute rendering would be the same. The SYN uses the number just below the data.
        exporter.OnFrame(Frame.Create(new FrameId(0), Timestamp.FromSecs(1_000), _TcpSegment(FirstSequence - 1, 0x02, 0), LinkType.Ethernet,
            FrameInterfaceId.Invalid, registry).Value);
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = _TcpSegment(FirstSequence + (uint)(PayloadBytes * i), 0x18, PayloadBytes, (byte)i);
            Frame frame = Frame.Create(new FrameId(i + 1), Timestamp.FromSecs(1_001 + i), bytes, LinkType.Ethernet, FrameInterfaceId.Invalid,
                registry).Value;
            exporter.OnFrame(frame);
        }

        exporter.OnFinish();
    }

    #endregion

    #region Private Helpers

    private static byte[] _TcpSegment(uint sequence, byte flags, int payloadLength, byte fill = 0)
    {
        byte[] frame = new byte[14 + 20 + 20 + payloadLength];
        frame[0] = 0x02;
        frame[5] = 0x02;
        frame[6] = 0x02;
        frame[11] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        Span<byte> ip = frame.AsSpan(14);
        ip[0] = 0x45;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(20 + 20 + payloadLength));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ip[6..], 0x4000);
        ip[8] = 64;
        ip[9] = 6;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ip[12..], 0xC0000201);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ip[16..], 0xC0000202);
        Span<byte> tcp = frame.AsSpan(34);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tcp, 50000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], 5001);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], 1);
        tcp[12] = 0x50;
        tcp[13] = flags;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 65535);
        tcp[20..].Fill(fill);
        return frame;
    }

    #endregion
}
