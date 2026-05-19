// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Manages output file splitting by size or frame count.
/// Generates sequentially numbered output paths.
/// </summary>
internal sealed class SplitOutputManager
{
    /// <summary>Base path for output files (without extension for splits, or full path for single).</summary>
    private readonly string _BasePath;

    /// <summary>File extension including the dot.</summary>
    private readonly string _Extension;

    /// <summary>Maximum bytes per output file (0 = no limit).</summary>
    private readonly long _MaxSize; // bytes

    /// <summary>Maximum frames per output file (0 = no limit).</summary>
    private readonly long _MaxCount;

    /// <summary>Current output file index (for split naming).</summary>
    private int _FileIndex;

    /// <summary>Whether splitting is enabled.</summary>
    internal bool IsSplitting => _MaxSize > 0 || _MaxCount > 0;

    /// <summary>
    /// Creates a new split output manager.
    /// </summary>
    /// <param name="outputPath">Output file path.</param>
    /// <param name="maxSize">Maximum bytes per file (0 = unlimited). Callers should pass MiB * 1024 * 1024.</param>
    /// <param name="maxCount">Maximum frames per file (0 = unlimited).</param>
    internal SplitOutputManager(string outputPath, long maxSize, long maxCount)
    {
        _MaxSize = maxSize;
        _MaxCount = maxCount;
        _Extension = System.IO.Path.GetExtension(outputPath);
        _BasePath = outputPath[..^_Extension.Length];
    }

    /// <summary>
    /// Returns the path for the next output file.
    /// For single-file mode, always returns the base path + extension.
    /// For split mode, appends a sequential number.
    /// </summary>
    internal string NextPath()
    {
        if (!IsSplitting)
        {
            return _BasePath + _Extension;
        }

        _FileIndex++;
        return $"{_BasePath}_{_FileIndex:D5}{_Extension}";
    }

    /// <summary>
    /// Determines whether a new split file is needed based on current file size and frame count.
    /// </summary>
    /// <param name="currentSize">Approximate size of the current output file in bytes.</param>
    /// <param name="frameCount">Number of frames written to the current file.</param>
    internal bool NeedsSplit(long currentSize, long frameCount)
    {
        if (_MaxSize > 0 && currentSize >= _MaxSize)
        {
            return true;
        }

        if (_MaxCount > 0 && frameCount >= _MaxCount)
        {
            return true;
        }

        return false;
    }
}
