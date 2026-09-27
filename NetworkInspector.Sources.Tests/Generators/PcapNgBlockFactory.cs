// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Generators;

/// <summary>Builds SHB/IDB prefixes and hostile EPB tails for PCAPNG reader tests.</summary>
internal static class PcapNgBlockFactory
{
    internal const uint ShbType = 0x0A0D_0D0A;
    internal const uint EpbType = 0x0000_0006;
    internal const uint DsbType = 0x0000_000A;
    internal const uint ByteOrderMagic = 0x1A2B_3C4D;

    /// <summary>SHB plus one Ethernet IDB, no packets.</summary>
    internal static byte[] ShbAndEthernetIdb(bool nanosecondResolution = true)
    {
        using PcapNgTestWriter writer = new();
        writer.AddInterface(LinkType.Ethernet, nanosecondResolution);
        return writer.Build();
    }

    /// <summary>Concatenates a prefix file and one extra block.</summary>
    internal static byte[] Append(byte[] prefix, ReadOnlySpan<byte> block)
    {
        byte[] result = new byte[prefix.Length + block.Length];
        prefix.CopyTo(result, 0);
        block.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    /// <summary>First 12 bytes of an SHB with a chosen <c>block_total_length</c>.</summary>
    internal static byte[] TruncatedShbHeader(uint blockLength)
    {
        byte[] data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, ShbType);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), blockLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), ByteOrderMagic);
        return data;
    }

    /// <summary>Minimum 12-byte unknown non-packet block (DSB type, empty body).</summary>
    internal static byte[] EmptyDsb()
    {
        byte[] block = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(block, DsbType);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 12);
        return block;
    }

    /// <summary>
    /// EPB whose on-disk payload is <paramref name="payload"/> (padded to 4).
    /// <paramref name="capturedLength"/> / <paramref name="originalLength"/> are the header fields.
    /// </summary>
    internal static byte[] EnhancedPacket(
        uint interfaceId,
        ReadOnlySpan<byte> payload,
        uint? capturedLength = null,
        uint? originalLength = null,
        ReadOnlySpan<byte> options = default,
        uint? trailingLength = null)
    {
        uint capLen = capturedLength ?? (uint)payload.Length;
        uint origLen = originalLength ?? capLen;
        int paddedPayload = (payload.Length + 3) & ~3;
        uint blockLen = (uint)(32 + paddedPayload + options.Length);

        byte[] block = new byte[blockLen];
        BinaryPrimitives.WriteUInt32LittleEndian(block, EpbType);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), blockLen);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), capLen);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), origLen);
        payload.CopyTo(block.AsSpan(28));
        if (!options.IsEmpty)
        {
            options.CopyTo(block.AsSpan(28 + paddedPayload));
        }

        uint trailing = trailingLength ?? blockLen;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan((int)blockLen - 4), trailing);
        return block;
    }

    /// <summary>EPB type and length only — used for misaligned <c>block_total_length</c>.</summary>
    internal static byte[] EpbLengthHeader(uint blockLength)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, EpbType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), blockLength);
        return header;
    }

    /// <summary>UTF-8 option TLV plus opt_endofopt.</summary>
    internal static byte[] CommentOptionAndEnd(string comment)
    {
        byte[] value = Encoding.UTF8.GetBytes(comment);
        int padded = (value.Length + 3) & ~3;
        byte[] options = new byte[4 + padded + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(options, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(2), (ushort)value.Length);
        value.CopyTo(options.AsSpan(4));
        return options;
    }
}
