// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Manages output path splitting by size or item count (frames or packets).
/// Generates sequentially numbered output paths for both single-file exporters and
/// directory-oriented exporters (e.g. Parquet datasets).
/// <para>
/// Size splits use live <see cref="IExportByteProgress.EstimatedOutputBytes"/> from the
/// active exporter — never filesystem probes.
/// </para>
/// <para>This type is <b>not thread-safe</b>; all members must be called from a single thread.
/// The internal file-index counter is not synchronized.</para>
/// </summary>
internal sealed class SplitOutputManager
{
    #region Fields

    /// <summary>Base path for output (without extension for file splits, or full directory base).</summary>
    private readonly string _BasePath;

    /// <summary>File extension including the dot; empty for directory-oriented outputs.</summary>
    private readonly string _Extension;

    /// <summary>Maximum bytes per output file or dataset directory (0 = no limit).</summary>
    private readonly long _MaxSize; // bytes

    /// <summary>Maximum items (frames/packets) per output (0 = no limit).</summary>
    private readonly int _MaxCount;

    /// <summary>
    /// When <see langword="true"/>, <see cref="NextPath"/> yields directory paths (Parquet datasets)
    /// instead of single files.
    /// </summary>
    private readonly bool _IsDirectoryOutput;

    /// <summary>Current output index (for split naming).</summary>
    private int _FileIndex;

    #endregion

    #region Public API

    /// <summary>Whether splitting is enabled.</summary>
    internal bool IsSplitting => _MaxSize > 0 || _MaxCount > 0;

    /// <summary>Whether size-based splitting is enabled.</summary>
    internal bool IsSizeSplitting => _MaxSize > 0;

    /// <summary>Whether outputs are directories (Parquet) rather than single files.</summary>
    internal bool IsDirectoryOutput => _IsDirectoryOutput;

    /// <summary>
    /// Creates a new split output manager.
    /// </summary>
    /// <param name="outputPath">Output file or directory path (must be a real path, not stdout).</param>
    /// <param name="maxSize">Maximum bytes per output (0 = unlimited). Callers should pass MiB * 1024 * 1024.</param>
    /// <param name="maxCount">Maximum items per output (0 = unlimited).</param>
    /// <param name="isDirectoryOutput">
    /// When <see langword="true"/>, treat <paramref name="outputPath"/> as a dataset directory base
    /// (no extension stripping).
    /// </param>
    internal SplitOutputManager(
        string outputPath,
        long maxSize,
        int maxCount,
        bool isDirectoryOutput = false)
    {
        _MaxSize = maxSize;
        _MaxCount = maxCount;
        _IsDirectoryOutput = isDirectoryOutput;

        string normalized = outputPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (_IsDirectoryOutput)
        {
            // Keep names like "dataset.parquet" intact as a directory base name.
            _Extension = "";
            _BasePath = normalized;
        }
        else
        {
            _Extension = Path.GetExtension(normalized);
            _BasePath = normalized[..^_Extension.Length];
        }
    }

    /// <summary>
    /// Returns the path for the next output file or dataset directory.
    /// For single-output mode, always returns the base path (+ extension for files).
    /// For split mode, appends a sequential number.
    /// </summary>
    internal string NextPath()
    {
        if (!IsSplitting)
        {
            return _BasePath + _Extension;
        }

        _FileIndex++;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{_BasePath}_{_FileIndex:D5}{_Extension}");
    }

    /// <summary>
    /// Determines whether a new split is needed based on live estimated size and item count.
    /// </summary>
    /// <param name="estimatedOutputBytes">
    /// <see cref="IExportByteProgress.EstimatedOutputBytes"/> from the active exporter
    /// (committed + pending buffers). Ignored when size-splitting is disabled.
    /// </param>
    /// <param name="itemCount">Number of frames or packets written to the current output.</param>
    internal bool NeedsSplit(long estimatedOutputBytes, int itemCount)
    {
        if (_MaxSize > 0 && estimatedOutputBytes >= _MaxSize)
        {
            return true;
        }

        if (_MaxCount > 0 && itemCount >= _MaxCount)
        {
            return true;
        }

        return false;
    }

    #endregion
}
