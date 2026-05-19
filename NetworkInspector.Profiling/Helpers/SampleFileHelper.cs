// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Creates temporary PCAPNG and BLF files from synthetic frames for use in
/// read-profiling scenarios. Files are written to the system temp directory.
/// </summary>
internal static class SampleFileHelper
{
    /// <summary>Base directory for profiling temp files.</summary>
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "ni_profiling");

    /// <summary>
    /// Generates a temporary PCAPNG file containing the given frames.
    /// Creates the output directory if it does not exist.
    /// </summary>
    /// <param name="frames">Frames to write into the PCAPNG file.</param>
    /// <returns>The full path to the generated PCAPNG file.</returns>
    internal static string CreatePcapngFile(Frame[] frames)
    {
        Directory.CreateDirectory(TempDir);
        string path = Path.Combine(TempDir, "profiling_sample.pcapng");

        using Exporters.Pcapng.PcapngExporter exporter = Exporters.Pcapng.PcapngExporter
            .CreateBuilder()
            .ToFile(path)
            .Build();

        foreach (Frame frame in frames)
        {
            if (!exporter.OnFrame(frame))
            {
                break;
            }
        }

        exporter.OnFinish();
        return path;
    }

    /// <summary>
    /// Generates a temporary BLF file containing the given frames.
    /// Creates the output directory if it does not exist.
    /// </summary>
    /// <param name="frames">Frames to write into the BLF file.</param>
    /// <returns>The full path to the generated BLF file.</returns>
    internal static string CreateBlfFile(Frame[] frames)
    {
        Directory.CreateDirectory(TempDir);
        string path = Path.Combine(TempDir, "profiling_sample.blf");

        using Exporters.Blf.BlfExporter exporter = Exporters.Blf.BlfExporter
            .CreateBuilder()
            .ToFile(path)
            .Build();

        foreach (Frame frame in frames)
        {
            if (!exporter.OnFrame(frame))
            {
                break;
            }
        }

        exporter.OnFinish();
        return path;
    }

    /// <summary>
    /// Deletes all profiling temp files created by this helper.
    /// Best-effort — silently ignores errors if files are still in use.
    /// </summary>
    internal static void Cleanup()
    {
        try
        {
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }
    }
}
