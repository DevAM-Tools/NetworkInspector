// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Exporters.Pbf;

namespace NetworkInspector.Exporters.Tests.Verification;

/// <summary>
/// Verifies PBF (Packet Binary Format) files by checking magic headers/footers,
/// block structure, and trailer integrity.
/// </summary>
internal sealed class PbfVerifier
{
    /// <summary>Expected magic header/footer (44 bytes).</summary>
    private static readonly byte[] ExpectedMagic =
        "NETWORK-INSPECTOR-PBF-FORMAT-v1\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray();

    /// <summary>Magic size in bytes.</summary>
    private static readonly int MagicSize = ExpectedMagic.Length;

    /// <summary>Whether the file header magic is valid.</summary>
    internal bool HasValidHeaderMagic
    {
        get; private set;
    }

    /// <summary>Whether the file footer magic is valid.</summary>
    internal bool HasValidFooterMagic
    {
        get; private set;
    }

    /// <summary>File size in bytes.</summary>
    internal long FileSize
    {
        get; private set;
    }

    /// <summary>Number of blocks found between header and trailer.</summary>
    internal int BlockCount
    {
        get; private set;
    }

    /// <summary>
    /// Opens and validates a PBF file.
    /// </summary>
    internal static PbfVerifier Open(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        PbfVerifier verifier = new();
        verifier.Parse(data);
        return verifier;
    }

    /// <summary>
    /// Parses and validates PBF data from a byte array.
    /// </summary>
    private void Parse(byte[] data)
    {
        FileSize = data.Length;

        // Minimum valid PBF: header magic + footer trailer size (4B) + footer magic
        int minSize = MagicSize * 2 + 4;
        if (data.Length < minSize)
        {
            return;
        }

        // Check header magic
        HasValidHeaderMagic = data.AsSpan(0, MagicSize).SequenceEqual(ExpectedMagic);

        // Check footer magic (last MagicSize bytes)
        HasValidFooterMagic = data.AsSpan(data.Length - MagicSize, MagicSize)
            .SequenceEqual(ExpectedMagic);

        if (!HasValidHeaderMagic || !HasValidFooterMagic)
        {
            return;
        }

        // Read trailer size (4 bytes before footer magic, little-endian)
        int trailerSizeOffset = data.Length - MagicSize - 4;
        uint trailerSize = BinaryPrimitives.ReadUInt32LittleEndian(
            data.AsSpan(trailerSizeOffset));

        // Count blocks between header region and trailer region
        // Header region: magic + 4-byte header-proto length prefix + header proto
        int offset = MagicSize;

        int trailerStart = trailerSizeOffset - (int)trailerSize;
        if (trailerStart < offset)
        {
            return;
        }

        CountBlocks(data, offset, trailerStart);
    }

    /// <summary>
    /// Counts block structures deterministically between the given offsets.
    /// <para>
    /// Skips the length-prefixed header protobuf
    /// (<c>[int32 LE length][proto data]</c>), then iterates blocks with exact
    /// <c>9 + storedSize</c> strides until <paramref name="end"/> is reached.
    /// </para>
    /// Block format: <c>[flags(1B)][originalSize(4B LE)][storedSize(4B LE)][data(storedSize bytes)]</c>
    /// </summary>
    private void CountBlocks(byte[] data, int start, int end)
    {
        int offset = start;

        // Skip the header protobuf: [int32 LE length][proto data]
        if (offset + 4 > end)
        {
            return;
        }
        int headerProtoLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        offset += 4 + headerProtoLen;

        // Parse blocks with an exact stride — no heuristic fallback
        while (offset + 9 <= end)
        {
            uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 5));
            int blockTotalSize = 9 + (int)storedSize;
            if (offset + blockTotalSize > end)
            {
                // Malformed block framing — stop rather than silently miscount
                break;
            }
            BlockCount++;
            offset += blockTotalSize;
        }
    }
}
