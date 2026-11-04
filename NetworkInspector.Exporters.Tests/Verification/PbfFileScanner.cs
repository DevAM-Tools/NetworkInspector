// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Verification;

/// <summary>One decoded protobuf field: its number and either its numeric value or its length-delimited payload.</summary>
/// <param name="Number">Field number.</param>
/// <param name="Varint">
/// The value of a varint, fixed32, or fixed64 field (the fixed values are read little-endian, as protobuf fixes them); 0 for a
/// length-delimited field.
/// </param>
/// <param name="Payload">The bytes of a length-delimited field, or <see langword="null"/>.</param>
/// <param name="WireType">The protobuf wire type of the field: 0 varint, 1 fixed64, 2 length-delimited, 5 fixed32.</param>
internal readonly record struct PbfField(int Number, ulong Varint, byte[]? Payload, int WireType = 0);

/// <summary>
/// A minimal protobuf reader for PBF files written without compression: the file header message and the payload messages of all
/// blocks. It decodes the wire format only (field numbers, wire types, varints, fixed-width values, byte payloads); what a field
/// means is the test's business, so the tests state the layout they expect instead of sharing the exporter's constants blindly.
/// </summary>
internal static class PbfFileScanner
{
    #region File layout literals

    /// <summary>Length of the magic at both ends of the file (PBF README, file layout).</summary>
    private const int _MagicLength = 44;

    /// <summary>Field number of the payload message in a block envelope (PBF README, block envelope); a literal, not the exporter's constant.</summary>
    private const int _BlockPayload = 2;

    /// <summary>Sub-field of a text column entry that holds one row with a text (PBF README, Texts); a literal, not the exporter's constant.</summary>
    private const int _TextColumnPresent = 1;

    /// <summary>Sub-field of a text column entry that holds a run of rows without a text (PBF README, Texts); a literal.</summary>
    private const int _TextColumnAbsentRun = 2;

    #endregion

    #region Reading PBF files

    /// <summary>Splits a message into its fields, in order.</summary>
    /// <param name="message">One protobuf message.</param>
    /// <returns>The fields.</returns>
    /// <exception cref="InvalidDataException">A field has a wire type that the PBF format does not use (3, 4, 6, or 7).</exception>
    internal static List<PbfField> Fields(ReadOnlySpan<byte> message)
    {
        List<PbfField> fields = [];
        int pos = 0;
        while (pos < message.Length)
        {
            ulong tag = _Varint(message, ref pos);
            int number = checked((int)(tag >> 3));
            switch (checked((int)(tag & 7)))
            {
                case 0:
                    fields.Add(new PbfField(number, _Varint(message, ref pos), null));
                    break;
                case 1:
                    fields.Add(new PbfField(number, BinaryPrimitives.ReadUInt64LittleEndian(message[pos..]), null, WireType: 1));
                    pos += 8;
                    break;
                case 5:
                    fields.Add(new PbfField(number, BinaryPrimitives.ReadUInt32LittleEndian(message[pos..]), null, WireType: 5));
                    pos += 4;
                    break;
                case 2:
                    int length = checked((int)_Varint(message, ref pos));
                    fields.Add(new PbfField(number, 0, message.Slice(pos, length).ToArray(), WireType: 2));
                    pos += length;
                    break;
                default:
                    throw new InvalidDataException(Invariant($"Field {number} has the wire type {tag & 7}, which the PBF format does not use."));
            }
        }

        return fields;
    }

    /// <summary>Reads the header message and the payload messages of all blocks of an uncompressed file.</summary>
    /// <param name="path">The PBF file; it must have been written with compression off.</param>
    /// <returns>The header fields and, per block, the fields of its payload message.</returns>
    internal static (List<PbfField> Header, List<List<PbfField>> Blocks) Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(_MagicLength, 4));
        List<PbfField> header = Fields(file.AsSpan(_MagicLength + 4, headerLength));

        // The trailer is found from the end: [trailer][4-byte trailer size][magic]; the blocks lie between header and trailer.
        int trailerSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - _MagicLength - 4, 4));
        int end = file.Length - _MagicLength - 4 - trailerSize;
        int pos = _MagicLength + 4 + headerLength;
        List<List<PbfField>> blocks = [];
        while (pos < end)
        {
            // Block frame: [flags 1][uncompressed size 4][stored size 4][stored bytes]; the envelope holds type and payload.
            int storedSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos + 5, 4));
            foreach (PbfField field in Fields(file.AsSpan(pos + 9, storedSize)))
            {
                if (field.Number == _BlockPayload)
                {
                    blocks.Add(Fields(field.Payload!));
                }
            }

            pos += 9 + storedSize;
        }

        return (header, blocks);
    }

    /// <summary>
    /// Decodes a presence-encoded text column (the custom-text and custom-representation columns of a columnar field column): each
    /// present entry is one row with its text, each absent-run entry is that many rows without a text. This is the reader side of
    /// the encoding the exporter documents; it states the entries itself so a change of the writer fails a test.
    /// </summary>
    /// <remarks>
    /// Sync: NetworkInspector.Exporters/Pbf/README.md § Texts, <c>PbfFieldNumbers.TextColumnPresent</c> and <c>TextColumnAbsentRun</c>, and
    /// <c>ColumnarBlockBuilder.WriteTextColumn</c>; the entry numbers are literals here so that a changed constant fails a test.
    /// </remarks>
    /// <param name="column">The payload of the text column message (the bytes after its length prefix).</param>
    /// <returns>One entry per row: the text, or <see langword="null"/> for a row without a text.</returns>
    /// <exception cref="InvalidDataException">An entry has a sub-field number that the encoding does not define.</exception>
    internal static string?[] DecodeTextColumn(byte[] column)
    {
        List<string?> rows = [];
        foreach (PbfField entry in Fields(column))
        {
            if (entry.Number == _TextColumnPresent)
            {
                rows.Add(Encoding.UTF8.GetString(entry.Payload!));
            }
            else if (entry.Number == _TextColumnAbsentRun)
            {
                for (ulong i = 0; i < entry.Varint; i++)
                {
                    rows.Add(null);
                }
            }
            else
            {
                throw new InvalidDataException(Invariant($"A text column entry has the unknown sub-field {entry.Number}."));
            }
        }

        return [.. rows];
    }

    /// <summary>The varint value of the first field with the given number, or <see cref="ulong.MaxValue"/> when absent.</summary>
    /// <param name="fields">The fields of one message.</param>
    /// <param name="number">Field number.</param>
    /// <returns>The value.</returns>
    internal static ulong Value(List<PbfField> fields, int number)
    {
        foreach (PbfField field in fields)
        {
            if (field.Number == number)
            {
                return field.Varint;
            }
        }

        return ulong.MaxValue;
    }

    /// <summary>Reads one protobuf varint; advances <paramref name="pos"/>.</summary>
    /// <param name="data">The message bytes.</param>
    /// <param name="pos">The offset of the first varint byte; on return, the offset after the last one.</param>
    /// <returns>The decoded value.</returns>
    private static ulong _Varint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[pos++];
            result |= checked((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    #endregion
}
