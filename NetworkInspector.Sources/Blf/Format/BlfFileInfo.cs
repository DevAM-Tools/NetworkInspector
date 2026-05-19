// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Blf.Format.Headers;

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Parsed BLF file header information.
/// Extracts and stores the critical fields from the variable-length file header:
/// header size (determines where objects start), measurement start time
/// (anchor for absolute timestamps), and API version.
/// </summary>
internal sealed class BlfFileInfo
{
    #region Properties

    /// <summary>Total file header size in bytes. BLF objects start at this offset.</summary>
    internal uint HeaderSize
    {
        get; private set;
    }

    /// <summary>
    /// Measurement start time as nanoseconds since Unix epoch.
    /// Used as the base offset for computing absolute timestamps.
    /// </summary>
    internal long StartOffsetNanos
    {
        get; private set;
    }

    #endregion

    #region Factory Methods

    /// <summary>
    /// Attempts to parse the BLF file header from the given data.
    /// Validates the "LOGG" magic, reads the header prefix and embedded BlfDate,
    /// and computes the start offset in nanoseconds.
    /// </summary>
    /// <param name="data">The raw file data starting at offset 0.</param>
    /// <param name="dateTimeZone">
    /// Timezone in which the embedded SYSTEMTIME date fields are interpreted.
    /// Pass <see cref="TimeZoneInfo.Utc"/> for cross-machine reproducibility, or
    /// <see cref="TimeZoneInfo.Local"/> for Vector / Wireshark compatibility.
    /// </param>
    /// <param name="info">Parsed file info on success.</param>
    /// <returns>True if parsing succeeded; false if the data is too short or the magic is invalid.</returns>
    internal static bool TryParse(ReadOnlySpan<byte> data, TimeZoneInfo dateTimeZone, [NotNullWhen(true)] out BlfFileInfo? info)
    {
        info = null;

        // We need at least the prefix (20B) + measurement_start_time BlfDate (16B) at offset 20
        // Total minimum: 36 bytes. But spec says 144B minimum — check that.
        if (data.Length < BlfConstants.FileHeaderMinSize)
        {
            return false;
        }

        // Parse the fixed prefix (signature, header_size, api_version, platform, creation_flags)
        if (!BlfFileHeaderPrefix.TryParse(data, out BlfFileHeaderPrefix prefix, out _))
        {
            return false;
        }

        // Validate "LOGG" magic
        if (prefix.Signature.Value != BlfConstants.FileMagic)
        {
            return false;
        }

        uint headerSize = prefix.HeaderSize.Value;

        // Header size must be at least the minimum and not exceed data length
        if (headerSize < BlfConstants.FileHeaderMinSize)
        {
            return false;
        }

        // Parse measurement_start_time (BlfDate at offset 40 per Vector blf_fileheader_t).
        // Layout from wiretap/blf.h:
        //   magic[4] header_length(4) api_version(4) application(1) compression_level(1)
        //   application_major(1) application_minor(1) len_compressed(8) len_uncompressed(8)
        //   obj_count(4) application_build(4) start_date(16) end_date(16) ...
        // → start_date begins at byte 40, end_date at byte 56.
        ReadOnlySpan<byte> dateSpan = data.Slice(40);
        if (!BlfDate.TryParse(dateSpan, out BlfDate startDate, out _))
        {
            return false;
        }

        // Convert start date to nanoseconds since Unix epoch using the caller's timezone
        long startNanos = BlfTimestamp.DateToUnixNanoseconds(in startDate, dateTimeZone);

        info = new BlfFileInfo
        {
            HeaderSize = headerSize,
            StartOffsetNanos = startNanos,
        };
        return true;
    }

    #endregion
}
