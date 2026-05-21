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
        // Guard against untrusted uncompressedSize before any allocation.
        // The check is skipped when maxUncompressedSize == 0 (default, limit inactive).
        if (maxUncompressedSize > 0 && uncompressedSize > (ulong)maxUncompressedSize)
        {
            throw new BlfDecompressionLimitExceededException(maxUncompressedSize, uncompressedSize);
        }

        if (compressionMethod == BlfConstants.CompressionNone)
        {
            // Uncompressed: just copy
            return compressedData.ToArray();
        }

        if (compressionMethod == BlfConstants.CompressionLz4)
        {
            return DecompressLz4(compressedData, uncompressedSize);
        }

        if (compressionMethod == BlfConstants.CompressionZlib)
        {
            return DecompressZlib(compressedData, uncompressedSize);
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
    private static byte[] DecompressLz4(ReadOnlySpan<byte> compressedData, uint uncompressedSize)
    {
        byte[] output = new byte[uncompressedSize];

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
    /// Uses <see cref="ZLibStream"/> with the raw compressed bytes wrapped in a MemoryStream.
    /// </summary>
    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressedData, uint uncompressedSize)
    {
        byte[] output = new byte[uncompressedSize];

        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(compressedData.Length, 1));
        try
        {
            compressedData.CopyTo(rented);
            using MemoryStream compressedStream = new(rented, 0, compressedData.Length,
                writable: false, publiclyVisible: true);
            using ZLibStream zlibStream = new(compressedStream, CompressionMode.Decompress);

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

            return output;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    #endregion
}
