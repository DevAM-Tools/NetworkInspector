// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Shared PCAPNG block-length and packet-payload bounds checks used by the file scanner and the stream reader.
/// </summary>
internal static class PcapngBlockLayout
{
    #region Constants

    /// <summary>
    /// Bytes before EPB packet data: type(4) + length(4) + interface(4) + ts_hi(4) + ts_lo(4) + cap_len(4) + orig_len(4).
    /// </summary>
    internal const int EpbBytesBeforePacketData = 28;

    /// <summary>Bytes before SPB packet data: type(4) + length(4) + orig_len(4).</summary>
    internal const int SpbBytesBeforePacketData = 12;

    /// <summary>
    /// Bytes before obsolete PB packet data: type(4) + length(4) + interface(2) + drops(2)
    /// + ts_hi(4) + ts_lo(4) + cap_len(4) + orig_len(4).
    /// </summary>
    internal const int PbBytesBeforePacketData = 28;

    #endregion

    #region Public API

    /// <summary>
    /// Returns whether <paramref name="blockLength"/> is a valid PCAPNG <c>block_total_length</c>
    /// (at least 12 bytes, multiple of 4) and the trailing length in
    /// <paramref name="trailingFourBytes"/> equals it, or rounds up to it.
    /// Callers round the leading length with <see cref="PcapPadding.TryRoundBlockLength"/> first.
    /// This method still rejects a leading length that is not a multiple of 4.
    /// </summary>
    /// <param name="blockLength">Leading <c>block_total_length</c>.</param>
    /// <param name="trailingFourBytes">Last four bytes of the block.</param>
    /// <param name="swap">True when the section uses swapped byte order.</param>
    /// <param name="trailing">Raw trailing length field when this method returns true; 0 otherwise.</param>
    internal static bool TryValidatePcapngBlockLength(
        uint blockLength,
        ReadOnlySpan<byte> trailingFourBytes,
        bool swap,
        out uint trailing)
    {
        trailing = 0;
        if (blockLength < PcapConstants.MinBlockSize || (blockLength & 3) != 0)
        {
            return false;
        }

        if (trailingFourBytes.Length < 4)
        {
            return false;
        }

        // Equality is the normal case. The out value stays the raw trailer, not a rounded substitute.
        EndianReader reader = new(swap);
        trailing = reader.ReadU32(trailingFourBytes);
        if (trailing == blockLength)
        {
            return true;
        }

        // A short trailer is accepted when rounding it up to a multiple of 4 equals the
        // leading length. The leading length is already required to be a multiple of 4,
        // so a trailer of 33 against a leading length of 36 matches. A trailer above the
        // leading length, or one that rounds to a different multiple, stays a failure.
        // Do not round the leading length: an unaligned header cannot match a rounded trailer.
        // PaddedLength adds 3. Values above int.MaxValue - 3 cannot be rounded without wrap,
        // and they cannot be a short trailer of a block this reader accepts, so they fail closed.
        if (trailing > blockLength || trailing > (uint)int.MaxValue - 3u)
        {
            return false;
        }

        return PcapPadding.PaddedLength((int)trailing) == (int)blockLength;
    }

    /// <summary>
    /// Validates a legacy PCAP record <c>incl_len</c> against the declared snaplen (0 = not declared, then
    /// <see cref="PcapConstants.DefaultSnapLength"/> applies) and the shared <see cref="PcapConstants.MaxBlockSize"/> cap.
    /// The file reader and the stream reader both call this, so they reject the same records with the same text.
    /// </summary>
    /// <param name="inclLen">Captured length from the record header.</param>
    /// <param name="declaredSnapLength">Snaplen exactly as the global header declares it.</param>
    /// <param name="reason">Why the record is rejected; <see langword="null"/> when the method returns true.</param>
    /// <returns>True when the record may be read; false when it is a malformed header.</returns>
    internal static bool TryValidateLegacyInclLen(uint inclLen, uint declaredSnapLength, out string? reason)
    {
        reason = null;
        if (inclLen > PcapConstants.MaxBlockSize)
        {
            reason = $"Legacy PCAP incl_len {inclLen} exceeds the {PcapConstants.MaxBlockSize / (1024 * 1024)} MiB safety cap; malformed header.";
            return false;
        }

        // A file without a declared snaplen gets the format default as implicit cap, so a hostile header
        // cannot request a huge allocation. Captures with larger packets must declare their snaplen.
        uint effectiveSnapLength = declaredSnapLength > 0 ? declaredSnapLength : PcapConstants.DefaultSnapLength;
        if (inclLen > effectiveSnapLength)
        {
            reason = declaredSnapLength > 0
                ? $"Legacy PCAP incl_len {inclLen} exceeds snaplen {declaredSnapLength}; malformed header."
                : $"Legacy PCAP incl_len {inclLen} exceeds the default cap {PcapConstants.DefaultSnapLength};"
                    + " set an explicit snaplen in the global header to allow larger packets.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns whether captured packet bytes plus 4-byte padding fit in the block before the trailing length.
    /// On success, <paramref name="captured"/> is <paramref name="capturedLength"/> (not truncated into pad or options).
    /// </summary>
    /// <param name="headerBytesBeforeData">Fixed header bytes before packet data (28 for EPB/PB, 12 for SPB).</param>
    /// <param name="blockLength">Total block length including trailing length.</param>
    /// <param name="capturedLength">Declared captured length from the packet block.</param>
    /// <param name="captured">Captured byte count to slice when this method returns true.</param>
    /// <param name="pad">Padding bytes after the payload (0–3).</param>
    internal static bool TryPacketDataBounds(
        int headerBytesBeforeData,
        uint blockLength,
        uint capturedLength,
        out int captured,
        out int pad)
    {
        captured = 0;
        pad = 0;
        if (headerBytesBeforeData < 0 || capturedLength > (uint)int.MaxValue)
        {
            return false;
        }

        int capturedInt = (int)capturedLength;
        pad = PcapPadding.PaddingFor(capturedInt);
        long need = (long)headerBytesBeforeData + capturedInt + pad + 4;
        if (need > blockLength)
        {
            return false;
        }

        captured = capturedInt;
        return true;
    }

    #endregion
}
