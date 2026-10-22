// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Creates temporary PCAPNG, BLF, and ASC files from synthetic frames for use in read, access, and footprint
/// profiling scenarios. Files are written to the system temp directory.
/// </summary>
internal static class SampleCaptureFiles
{
    #region Fields

    /// <summary>Base directory for profiling temp files.</summary>
    private static readonly string _TempDir = Path.Combine(Path.GetTempPath(), "ni_profiling");

    /// <summary>Fixed capture start of the relative ASC workload (2023-11-14 22:13:20 UTC).</summary>
    private static readonly DateTimeOffset _AscRelativeStart = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    #endregion

    #region Public API

    /// <summary>
    /// Generates a temporary PCAPNG file containing the given frames.
    /// Creates the output directory if it does not exist.
    /// </summary>
    /// <param name="frames">Frames to write into the PCAPNG file.</param>
    /// <param name="fileName">File name inside the profiling temp directory.</param>
    /// <returns>The full path to the generated PCAPNG file.</returns>
    internal static string CreatePcapngFile(Frame[] frames, string fileName = "profiling_sample.pcapng")
    {
        Directory.CreateDirectory(_TempDir);
        string path = Path.Combine(_TempDir, fileName);

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
    /// <param name="fileName">File name inside the profiling temp directory.</param>
    /// <returns>The full path to the generated BLF file.</returns>
    internal static string CreateBlfFile(Frame[] frames, string fileName = "profiling_sample.blf")
    {
        Directory.CreateDirectory(_TempDir);
        string path = Path.Combine(_TempDir, fileName);

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
    /// Generates a temporary ASC file with absolute timestamps through <see cref="Exporters.Asc.AscExporter"/>
    /// (nanosecond decimals). Creates the output directory if it does not exist.
    /// </summary>
    /// <param name="frames">Frames to write into the ASC file.</param>
    /// <param name="fileName">File name inside the profiling temp directory.</param>
    /// <returns>The full path to the generated ASC file.</returns>
    internal static string CreateAscAbsoluteFile(Frame[] frames, string fileName)
    {
        Directory.CreateDirectory(_TempDir);
        string path = Path.Combine(_TempDir, fileName);

        using Exporters.Asc.AscExporter exporter = Exporters.Asc.AscExporter
            .CreateBuilder()
            .ToFile(path)
            .WithTimestampDecimals(Exporters.Asc.AscExporter.MaxTimestampDecimals)
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
    /// Generates a temporary ASC file with <c>timestamps relative</c> and <paramref name="count"/> classic CAN lines:
    /// line i carries the delta to the previous line (<see cref="SampleFrames.TimestampNanos"/> spacing, nanosecond
    /// decimals), channel 1, id <see cref="SampleFrames.CanId"/>, DLC 8, and data = little-endian i. Written directly
    /// because the exporter writes absolute timestamps only.
    /// </summary>
    /// <param name="count">Number of CAN lines.</param>
    /// <param name="fileName">File name inside the profiling temp directory.</param>
    /// <returns>The full path to the generated ASC file.</returns>
    internal static string CreateAscRelativeFile(int count, string fileName)
    {
        Directory.CreateDirectory(_TempDir);
        string path = Path.Combine(_TempDir, fileName);

        // Latin-1 and LF only: ASC is a byte-oriented text format; the reader accepts LF and CRLF.
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        using StreamWriter writer = new(stream, System.Text.Encoding.Latin1) { NewLine = "\n" };
        string date = _AscRelativeStart.ToString("ddd MMM dd HH:mm:ss.fff yyyy", CultureInfo.InvariantCulture);
        writer.WriteLine(Invariant($"date {date}"));
        writer.WriteLine("base hex  timestamps relative");
        writer.WriteLine("no internal events logged");
        writer.WriteLine(Invariant($"Begin Triggerblock {date}"));

        // The first delta is the distance to the capture start, the others to the previous line.
        Span<byte> data = stackalloc byte[8];
        long previous = SampleFrames.TimestampNanos(0) - 731_000L;
        for (int i = 0; i < count; i++)
        {
            long timestamp = SampleFrames.TimestampNanos(i);
            long delta = timestamp - previous;
            previous = timestamp;

            BinaryPrimitives.WriteInt64LittleEndian(data, i);
            writer.Write(Invariant($"{delta / 1_000_000_000L}.{delta % 1_000_000_000L:D9} 1  {SampleFrames.CanId(i):X3}  Rx   d 8"));
            foreach (byte b in data)
            {
                writer.Write(Invariant($" {b:X2}"));
            }

            writer.WriteLine();
        }

        writer.WriteLine("End TriggerBlock");
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
            if (Directory.Exists(_TempDir))
            {
                Directory.Delete(_TempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a file still held by another process (scanner, viewer) surfaces as either type on
            // Windows. The OS reclaims the temp directory later; a locked file does not affect the measurement.
        }
    }

    #endregion
}
