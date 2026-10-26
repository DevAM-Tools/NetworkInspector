// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Handles BLF container decompression.
/// BLF log containers store objects either uncompressed (method 0), LZ4-compressed
/// (method 1, raw LZ4 block format), or zlib-compressed (method 2).
///
/// LZ4 (method 1) was introduced by Vector's Binlog SDK for high-throughput captures.
/// It uses the LZ4 raw block format: the uncompressed size is stored in the container
/// header and the compressed payload is fed directly to <see cref="Lz4Codec.Decompress"/>.
/// </summary>
internal static class BlfContainer
{
    #region Public API

    /// <summary>
    /// Decompresses or copies a BLF container payload based on the compression method.
    /// </summary>
    /// <param name="compressedData">The raw (possibly compressed) payload data.</param>
    /// <param name="compressionMethod">Compression method: 0 = none, 1 = LZ4, 2 = zlib.</param>
    /// <param name="uncompressedSize">Expected decompressed size in bytes.</param>
    /// <param name="maxUncompressedSize">
    /// Maximum allowed uncompressed size in bytes. A value of <c>0</c> disables the check.
    /// When active and <paramref name="uncompressedSize"/> exceeds this value,
    /// <see cref="BlfDecompressionLimitExceededException"/> is thrown before any buffer
    /// is allocated.
    /// </param>
    /// <returns>Decompressed (or copied) byte array.</returns>
    /// <remarks>
    /// Exceptions other than <see cref="BlfException"/> (for example
    /// <see cref="OutOfMemoryException"/> or <see cref="IOException"/> originating from
    /// the underlying storage layer) are not caught and propagate to the caller.
    /// </remarks>
    /// <exception cref="BlfException">Thrown if decompression fails or produces unexpected output size.</exception>
    /// <exception cref="BlfDecompressionLimitExceededException">
    /// Thrown when <paramref name="maxUncompressedSize"/> is positive and
    /// <paramref name="uncompressedSize"/> exceeds it.
    /// </exception>
    internal static byte[] Decompress(
        ReadOnlySpan<byte> compressedData,
        ushort compressionMethod,
        uint uncompressedSize,
        long maxUncompressedSize = 0)
    {
        // Guard against untrusted uncompressedSize before any allocation (threat: a forged header forcing a multi-GiB
        // buffer; mitigation: reject up front with a dedicated exception that carries the limit and the requested size;
        // rejected alternative: trying the allocation and catching OutOfMemoryException, which still stresses the GC).
        // The check is skipped when maxUncompressedSize == 0 (limit inactive).
        if (maxUncompressedSize > 0 && uncompressedSize > (ulong)maxUncompressedSize)
        {
            throw new BlfDecompressionLimitExceededException(maxUncompressedSize, uncompressedSize);
        }

        // A .NET array holds at most Array.MaxLength bytes. Without this guard a size above it (possible with the limit
        // disabled) would surface as OverflowException or OutOfMemoryException instead of the format error callers catch.
        if (uncompressedSize > (uint)Array.MaxLength)
        {
            throw new BlfException(
                $"BLF container uncompressedSize {uncompressedSize} exceeds the largest array ({Array.MaxLength} bytes).");
        }

        if (compressionMethod == BlfConstants.CompressionNone)
        {
            // The copy is as large as the payload, so the limit is also applied to the payload length itself (the declared
            // size is checked above, but a forged header can declare less than it carries).
            if (maxUncompressedSize > 0 && compressedData.Length > maxUncompressedSize)
            {
                throw new BlfDecompressionLimitExceededException(maxUncompressedSize, compressedData.Length);
            }

            // The zlib and LZ4 paths reject a container whose decoded size differs from the declared one; method 0 has to
            // do the same. Otherwise a header that understates its payload would be accepted silently and the
            // declared size (which the scanner and the size limit rely on) would no longer describe the container.
            if ((uint)compressedData.Length != uncompressedSize)
            {
                throw new BlfException(
                    $"BLF uncompressed container size mismatch: expected {uncompressedSize} bytes, got {compressedData.Length}.");
            }

            // Uncompressed: copy, because the source span may point into a memory-mapped view that is released later.
            return compressedData.ToArray();
        }

        if (compressionMethod == BlfConstants.CompressionLz4)
        {
            return _DecompressLz4(compressedData, uncompressedSize);
        }

        if (compressionMethod == BlfConstants.CompressionZlib)
        {
            return _DecompressZlib(compressedData, uncompressedSize);
        }

        throw new BlfException($"Unsupported BLF compression method: {compressionMethod}");
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Decompresses LZ4-compressed data (raw LZ4 block format) into a buffer of the expected size.
    /// Uses <see cref="Lz4Codec.Decompress"/> which operates
    /// directly on spans without an intermediate allocation.
    /// </summary>
    private static byte[] _DecompressLz4(ReadOnlySpan<byte> compressedData, uint uncompressedSize)
    {
        // Every byte is overwritten: LZ4 fills exactly uncompressedSize or the size check below throws, so the zeroing
        // pass of `new byte[]` is pure cost on a buffer of up to MaxUncompressedContainerSize.
        byte[] output = GC.AllocateUninitializedArray<byte>((int)uncompressedSize);

        int decoded = Lz4Codec.Decompress(compressedData, output.AsSpan());

        if (decoded < 0)
        {
            throw new BlfException(
                $"BLF LZ4 decompression failed (Lz4Codec returned {decoded}).");
        }

        if ((uint)decoded != uncompressedSize)
        {
            throw new BlfException(
                $"BLF LZ4 decompression size mismatch: expected {uncompressedSize} bytes, got {decoded}.");
        }

        return output;
    }

    /// <summary>
    /// Decompresses zlib-compressed data into a buffer of the expected size.
    /// Inflates directly from the input span via a pinned <see cref="UnmanagedMemoryStream"/>;
    /// there is no intermediate ArrayPool copy of the compressed bytes.
    /// </summary>
    private static unsafe byte[] _DecompressZlib(ReadOnlySpan<byte> compressedData, uint uncompressedSize)
    {
        if (compressedData.IsEmpty)
        {
            throw new BlfException("BLF zlib decompression failed: compressed payload is empty.");
        }

        // Every byte is overwritten: the inflate loop fills exactly uncompressedSize or the size check below throws.
        byte[] output = GC.AllocateUninitializedArray<byte>((int)uncompressedSize);

        fixed (byte* p = compressedData)
        {
            using UnmanagedMemoryStream compressedStream = new(p, compressedData.Length);
            using ZLibStream zlibStream = new(compressedStream, CompressionMode.Decompress);

            // Read until the declared size is filled or the stream ends. Inflating stops once the buffer is full, so a
            // stream that holds more data than declared is cut at uncompressedSize and not reported; a stream that holds
            // less is caught by the size check after the loop.
            int totalRead = 0;
            try
            {
                while (totalRead < output.Length)
                {
                    int bytesRead = zlibStream.Read(output, totalRead, output.Length - totalRead);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalRead += bytesRead;
                }
            }
            catch (InvalidDataException ex)
            {
                // Corrupt zlib stream — wrap so callers only need to catch BlfException.
                throw new BlfException($"BLF zlib decompression failed: {ex.Message}", ex);
            }

            if (totalRead != (int)uncompressedSize)
            {
                throw new BlfException(
                    $"BLF zlib decompression size mismatch: expected {uncompressedSize} bytes, got {totalRead}");
            }
        }

        return output;
    }

    #endregion
}
