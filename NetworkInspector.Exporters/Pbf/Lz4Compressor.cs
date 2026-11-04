// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Thin wrapper around <see cref="Lz4Codec"/> scoped to the PBF exporter.
/// All logic lives in <see cref="Lz4Codec"/> (NetworkInspector.Core); these methods only forward.
/// PBF blocks use the raw LZ4 block format (no frame header), so the reader needs the uncompressed size, which the
/// block header stores next to the stored size.
/// <para>
/// <b>Thread safety:</b> All methods delegate to <see cref="Lz4Codec"/>, which is
/// fully thread-safe.
/// </para>
/// </summary>
internal static class Lz4Compressor
{
    #region Codec forwarding

    /// <summary>
    /// Returns the maximum possible compressed size for <paramref name="inputSize"/> bytes.
    /// </summary>
    /// <param name="inputSize">Uncompressed input size in bytes.</param>
    /// <returns>
    /// Upper bound on compressed size in bytes, or <c>-1</c> when <paramref name="inputSize"/> exceeds the LZ4
    /// per-block input limit (see <see cref="Lz4Codec.MaxCompressedSize"/>).
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MaxCompressedSize(int inputSize) =>
        Lz4Codec.MaxCompressedSize(inputSize);

    /// <summary>
    /// Compresses <paramref name="input"/> using the LZ4 block format.
    /// Returns the compressed length, or <c>-1</c> if compression did not reduce size.
    /// </summary>
    /// <param name="input">Source data to compress.</param>
    /// <param name="output">Destination buffer. Must be at least <see cref="MaxCompressedSize"/> bytes.</param>
    /// <returns>Compressed length, or <c>-1</c> if compression was not beneficial.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compress(ReadOnlySpan<byte> input, Span<byte> output) =>
        Lz4Codec.Compress(input, output);

    #endregion
}
