// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// The file format detected from the first bytes of a capture file.
/// </summary>
internal enum FileFormat
{
    #region Enum Values

    /// <summary>PCAPNG format (one or more sections with SHBs).</summary>
    PcapNg,

    /// <summary>Legacy PCAP format (single global header + packet records).</summary>
    LegacyPcap,

    #endregion
}

/// <summary>
/// Result of format detection: identifies the file format, byte order, and timestamp resolution.
/// </summary>
/// <param name="Format">Detected file format.</param>
/// <param name="ByteSwapped">Whether the file uses swapped byte order relative to the host.</param>
/// <param name="NanosecondTimestamps">Whether the file uses nanosecond timestamps (legacy PCAP only).</param>
internal readonly record struct FormatDetectionResult(
    FileFormat Format,
    bool ByteSwapped,
    bool NanosecondTimestamps);

/// <summary>
/// Detects the capture file format from the first bytes of the file.
/// </summary>
internal static class PcapFormatDetection
{
    #region Public API

    /// <summary>Minimum number of bytes needed for format detection.</summary>
    internal const int MinDetectionBytes = 12;

    /// <summary>
    /// Detects the capture file format from the first bytes of the file.
    /// Requires at least <see cref="MinDetectionBytes"/> bytes for PCAPNG detection
    /// (SHB block type + length + byte-order magic), or 4 bytes for legacy PCAP.
    /// </summary>
    /// <param name="data">First bytes of the file (at least 12 bytes recommended).</param>
    /// <param name="result">Detection result if successful.</param>
    /// <returns>True if a known format was detected; false otherwise.</returns>
    internal static bool TryDetect(ReadOnlySpan<byte> data, out FormatDetectionResult result)
    {
        if (data.Length < 4)
        {
            result = default;
            return false;
        }

        // Read first 4 bytes as LE to identify the format
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);

        switch (magic)
        {
            // PCAPNG: SHB block type is palindromic (0x0A0D0D0A), so it reads
            // the same in both byte orders. The actual byte order is determined
            // by the byte-order magic at offset 8.
            case PcapConstants.BlockTypeSHB:
                {
                    if (data.Length < MinDetectionBytes)
                    {
                        result = default;
                        return false;
                    }

                    // Read the byte-order magic at offset 8
                    uint byteOrderMagic = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
                    result = byteOrderMagic switch
                    {
                        PcapConstants.PcapngMagic => new FormatDetectionResult
                        {
                            Format = FileFormat.PcapNg,
                            ByteSwapped = false,
                            NanosecondTimestamps = false,
                        },
                        PcapConstants.PcapngSwappedMagic => new FormatDetectionResult
                        {
                            Format = FileFormat.PcapNg,
                            ByteSwapped = true,
                            NanosecondTimestamps = false,
                        },
                        _ => default,
                    };
                    return byteOrderMagic is PcapConstants.PcapngMagic or PcapConstants.PcapngSwappedMagic;
                }

            // Legacy PCAP — microsecond timestamps, native byte order
            case PcapConstants.PcapMagicMicros:
                result = new FormatDetectionResult
                {
                    Format = FileFormat.LegacyPcap,
                    ByteSwapped = false,
                    NanosecondTimestamps = false,
                };
                return true;

            // Legacy PCAP — microsecond timestamps, swapped byte order
            case PcapConstants.PcapSwappedMagicMicros:
                result = new FormatDetectionResult
                {
                    Format = FileFormat.LegacyPcap,
                    ByteSwapped = true,
                    NanosecondTimestamps = false,
                };
                return true;

            // Legacy PCAP — nanosecond timestamps, native byte order
            case PcapConstants.PcapMagicNanos:
                result = new FormatDetectionResult
                {
                    Format = FileFormat.LegacyPcap,
                    ByteSwapped = false,
                    NanosecondTimestamps = true,
                };
                return true;

            // Legacy PCAP — nanosecond timestamps, swapped byte order
            case PcapConstants.PcapSwappedMagicNanos:
                result = new FormatDetectionResult
                {
                    Format = FileFormat.LegacyPcap,
                    ByteSwapped = true,
                    NanosecondTimestamps = true,
                };
                return true;

            default:
                result = default;
                return false;
        }
    }

    #endregion
}
