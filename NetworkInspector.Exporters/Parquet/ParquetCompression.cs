// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Parquet;

/// <summary>
/// The compression codec of every column chunk of a <see cref="ParquetExporter"/> dataset (all files: <c>packets</c>,
/// <c>packet_buffers</c>, <c>topology</c>, <c>catalog</c>, <c>protocols</c>, <c>export_info</c>, and every field file). The codec
/// is always written explicitly, so the dataset never changes when Parquet.Net changes its own default. The default is
/// <see cref="ParquetExporter.DefaultCompression"/>.
/// <para>
/// The set is closed: it lists the codecs that Parquet.Net writes reliably and that the Parquet format keeps current. LZO (rarely
/// read) and the framed LZ4 codec (deprecated by the Parquet specification in favor of <see cref="Lz4"/>) are not offered.
/// Enum, no thread-safety concerns. Sync: the enum setting pairs in <c>ParquetExporterRegistration</c> (plugin
/// <c>NetworkInspector.BuiltInPlugins</c>) and the README § Compression table.
/// </para>
/// </summary>
/// <seealso cref="ParquetCompressionLevel"/>
/// <seealso cref="ParquetExporter.Builder.WithCompression"/>
public enum ParquetCompression
{
    /// <summary>Setting text <c>none</c>: no compression. The largest files, the fastest write, readable by every Parquet reader.</summary>
    None,

    /// <summary>
    /// Setting text <c>snappy</c>: fast, light compression that the common Parquet readers support. Typically smaller than
    /// <see cref="None"/> and larger than <see cref="Zstd"/>; it has no levels, so <see cref="ParquetCompressionLevel"/> does not apply.
    /// </summary>
    Snappy,

    /// <summary>
    /// Setting text <c>gzip</c>: Deflate compression that the common Parquet readers support. Usually smaller than
    /// <see cref="Snappy"/> and slower to write.
    /// </summary>
    Gzip,

    /// <summary>
    /// Setting text <c>brotli</c>: small files, slow to write; not every Parquet reader can read it, so check the reader first.
    /// </summary>
    Brotli,

    /// <summary>
    /// Setting text <c>lz4</c>: written as the <c>LZ4_RAW</c> codec of the Parquet specification (the framed <c>LZ4</c> codec is
    /// deprecated there); DuckDB and other current readers report it as <c>LZ4_RAW</c>. Fast, light compression in the class of
    /// <see cref="Snappy"/>; older readers may not know it.
    /// </summary>
    Lz4,

    /// <summary>
    /// Setting text <c>zstd</c> (the default): a good balance of file size and write time for most data, supported by current
    /// readers (DuckDB, Arrow, Spark); a very old reader that predates Zstandard support needs <see cref="Snappy"/> or
    /// <see cref="Gzip"/>.
    /// </summary>
    Zstd,
}

/// <summary>
/// How hard the codec of <see cref="ParquetCompression"/> works: a trade of write time against file size. It applies to the codecs
/// that have levels (for example <see cref="ParquetCompression.Gzip"/>, <see cref="ParquetCompression.Brotli"/>,
/// <see cref="ParquetCompression.Zstd"/>); <see cref="ParquetCompression.None"/> and <see cref="ParquetCompression.Snappy"/> have none
/// and ignore it. The level is always set explicitly next to the codec,
/// so the dataset does not depend on the Parquet.Net default. The default is <see cref="ParquetExporter.DefaultCompressionLevel"/>.
/// Enum, no thread-safety concerns.
/// Sync: the enum setting pairs in <c>ParquetExporterRegistration</c> and the README § Compression table.
/// </summary>
/// <seealso cref="ParquetCompression"/>
public enum ParquetCompressionLevel
{
    /// <summary>Setting text <c>fastest</c>: the quickest write, the largest files of the codec.</summary>
    Fastest,

    /// <summary>Setting text <c>optimal</c> (the default): the codec's balance of write time and size.</summary>
    Optimal,

    /// <summary>Setting text <c>smallest</c>: the smallest files of the codec, the slowest write.</summary>
    Smallest,
}

/// <summary>
/// The mapping of <see cref="ParquetCompression"/> and <see cref="ParquetCompressionLevel"/> to the Parquet.Net settings, and the
/// texts of the refusal messages. Two Try mappings, so an undefined value is an expected failure for the caller to report and no
/// throwing switch arm sits in the mapping. Stateless; thread-safe.
/// </summary>
/// <remarks>
/// Sync: the enum setting pairs in <c>ParquetExporterRegistration</c> and the README § Compression table list the same texts;
/// <see cref="AllowedCompressionText"/> and <see cref="AllowedLevelText"/> repeat them for the refusal messages, and a test compares
/// them against the enums.
/// </remarks>
internal static class ParquetCompressions
{
    #region Defaults and texts

    /// <summary>
    /// The Parquet.Net codec of <see cref="ParquetExporter.DefaultCompression"/>. A constant so the builder can initialize its field
    /// without calling the Try mapping; a test pins it to the mapping of the default.
    /// </summary>
    internal const CompressionMethod DefaultMethod = CompressionMethod.Zstd;

    /// <summary>The level of <see cref="ParquetExporter.DefaultCompressionLevel"/>; a test pins it to the mapping of the default.</summary>
    internal const CompressionLevel DefaultLevel = CompressionLevel.Optimal;

    /// <summary>The allowed codec texts as one list, for the refusal messages that name the allowed values.</summary>
    internal const string AllowedCompressionText = "none, snappy, gzip, brotli, lz4, zstd";

    /// <summary>The allowed level texts as one list, for the refusal messages that name the allowed values.</summary>
    internal const string AllowedLevelText = "fastest, optimal, smallest";

    #endregion

    #region Mapping

    /// <summary>Maps a codec of the exporter to the Parquet.Net codec that is written.</summary>
    /// <param name="compression">The codec; any value, also an undefined one.</param>
    /// <param name="method">
    /// The Parquet.Net codec; <see cref="CompressionMethod.None"/> when the method returns <see langword="false"/> (not a valid answer there).
    /// </param>
    /// <returns>
    /// <see langword="false"/> for an undefined value; the caller decides how to report it (an undefined value is a bug of the caller).
    /// </returns>
    /// <remarks>
    /// <see cref="ParquetCompression.Lz4"/> maps to <see cref="CompressionMethod.Lz4Raw"/>, the codec the Parquet specification keeps; the
    /// framed <see cref="CompressionMethod.LZ4"/> is deprecated there. Cost: a switch over six constants, allocation-free.
    /// </remarks>
    internal static bool TryGetMethod(this ParquetCompression compression, out CompressionMethod method)
    {
        switch (compression)
        {
            case ParquetCompression.None:
                method = CompressionMethod.None;
                return true;
            case ParquetCompression.Snappy:
                method = CompressionMethod.Snappy;
                return true;
            case ParquetCompression.Gzip:
                method = CompressionMethod.Gzip;
                return true;
            case ParquetCompression.Brotli:
                method = CompressionMethod.Brotli;
                return true;
            case ParquetCompression.Lz4:
                method = CompressionMethod.Lz4Raw;
                return true;
            case ParquetCompression.Zstd:
                method = CompressionMethod.Zstd;
                return true;
            default:
                method = CompressionMethod.None;
                return false;
        }
    }

    /// <summary>Maps a level of the exporter to the .NET compression level that Parquet.Net takes.</summary>
    /// <param name="level">The level; any value, also an undefined one.</param>
    /// <param name="result">
    /// The .NET level; <see cref="CompressionLevel.Optimal"/> when the method returns <see langword="false"/> (not a valid answer there).
    /// </param>
    /// <returns>
    /// <see langword="false"/> for an undefined value; the caller decides how to report it (an undefined value is a bug of the caller).
    /// </returns>
    /// <remarks><see cref="ParquetCompressionLevel.Smallest"/> maps to <see cref="CompressionLevel.SmallestSize"/>. Cost: allocation-free.</remarks>
    internal static bool TryGetLevel(this ParquetCompressionLevel level, out CompressionLevel result)
    {
        switch (level)
        {
            case ParquetCompressionLevel.Fastest:
                result = CompressionLevel.Fastest;
                return true;
            case ParquetCompressionLevel.Optimal:
                result = CompressionLevel.Optimal;
                return true;
            case ParquetCompressionLevel.Smallest:
                result = CompressionLevel.SmallestSize;
                return true;
            default:
                result = CompressionLevel.Optimal;
                return false;
        }
    }

    #endregion
}
