// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>Capture workloads of the footprint, access, and read scenarios of the compact frame index.</summary>
internal enum CaptureWorkload
{
    /// <summary>PCAPNG of Ethernet/IPv6/UDP frames of 64 bytes each.</summary>
    PcapngUniform,

    /// <summary>PCAPNG of IMIX 7:4:1 Ethernet frames (64, 594, 1518 bytes).</summary>
    PcapngImix,

    /// <summary>PCAPNG of Ethernet/IPv6/UDP frames of 512 bytes each (the existing read workload).</summary>
    Pcapng512,

    /// <summary>BLF of classic CAN messages written by the BLF exporter.</summary>
    BlfCan,

    /// <summary>ASC of classic CAN lines with absolute timestamps written by the ASC exporter.</summary>
    AscAbsolute,

    /// <summary>ASC of classic CAN lines with relative timestamps.</summary>
    AscRelative,

    /// <summary>ASC of IMIX Ethernet lines (64, 594, 1518 bytes, one physical line each) written by the ASC exporter.</summary>
    AscEthernet,

    /// <summary>ASC of classic CAN lines where every 16th frame is an IMIX Ethernet line (a CAN log with an Ethernet bus).</summary>
    AscMixed,
}

/// <summary>
/// Creates, opens, and checks the capture files of <see cref="CaptureWorkload"/>. Every file is deterministic: the same
/// workload and count produce the same bytes on every run, so A and B measure identical inputs.
/// </summary>
/// <remarks><b>Thread-safety:</b> stateless; the scenarios call it from one thread.</remarks>
internal static class CaptureWorkloads
{
    #region Fields

    /// <summary>The BLF container cache budget of default <see cref="BlfSourceOptions"/>.</summary>
    internal static readonly int DefaultBlfCacheBudget = new BlfSourceOptions().CacheBudget;

    #endregion

    #region Public API

    /// <summary>Writes the capture file of <paramref name="workload"/> with <paramref name="count"/> frames.</summary>
    /// <param name="workload">Workload to write.</param>
    /// <param name="count">Number of frames.</param>
    /// <param name="stack">Stack whose registry the synthetic frames use.</param>
    /// <param name="fileName">File name inside the profiling temp directory.</param>
    /// <returns>The full path of the written file.</returns>
    internal static string CreateFile(CaptureWorkload workload, int count, Stack stack, string fileName) => workload switch
    {
        CaptureWorkload.PcapngUniform => SampleCaptureFiles.CreatePcapngFile(SampleFrames.CreateUniformEthernet(count, stack, 64), fileName),
        CaptureWorkload.PcapngImix => SampleCaptureFiles.CreatePcapngFile(SampleFrames.CreateImixEthernet(count, stack), fileName),
        CaptureWorkload.Pcapng512 => SampleCaptureFiles.CreatePcapngFile(SampleFrames.CreateSharedFrames(count, stack), fileName),
        CaptureWorkload.BlfCan => SampleCaptureFiles.CreateBlfFile(SampleFrames.CreateCanFrames(count, stack), fileName),
        CaptureWorkload.AscAbsolute => SampleCaptureFiles.CreateAscAbsoluteFile(SampleFrames.CreateCanFrames(count, stack), fileName),
        CaptureWorkload.AscRelative => SampleCaptureFiles.CreateAscRelativeFile(count, fileName),
        CaptureWorkload.AscEthernet => SampleCaptureFiles.CreateAscAbsoluteFile(SampleFrames.CreateImixEthernet(count, stack), fileName),
        CaptureWorkload.AscMixed => SampleCaptureFiles.CreateAscAbsoluteFile(_CreateMixedFrames(count, stack), fileName),
        _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unknown capture workload."),
    };

    /// <summary>
    /// Opens the file of <paramref name="workload"/> without preloading (memory-mapped PCAPNG and BLF with a full scan,
    /// disk-backed ASC), so the managed heap holds the index and not the file.
    /// </summary>
    /// <param name="workload">Workload whose format decides the source type.</param>
    /// <param name="path">File written by <see cref="CreateFile"/>.</param>
    /// <param name="blfCacheBudget">BLF container cache budget in bytes; 0 disables the cache.</param>
    internal static IRandomAccessFrameSource Open(CaptureWorkload workload, string path, int blfCacheBudget) => workload switch
    {
        CaptureWorkload.PcapngUniform or CaptureWorkload.PcapngImix or CaptureWorkload.Pcapng512 =>
            PcapngSource.Open(path, new PcapngSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Full }),
        CaptureWorkload.BlfCan =>
            BlfSource.Open(path, new BlfSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Full, CacheBudget = blfCacheBudget }),
        CaptureWorkload.AscAbsolute or CaptureWorkload.AscRelative or CaptureWorkload.AscEthernet or CaptureWorkload.AscMixed =>
            AscSource.Open(path, new AscSourceOptions { PreloadBudget = null }),
        _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unknown capture workload."),
    };

    /// <summary>Starts <paramref name="source"/> with a fresh registry and reads every frame; returns the frame count.</summary>
    internal static int StartAndDrain(IFrameSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Opens the file once, reads every frame, and throws when the count differs from <paramref name="expected"/>: a
    /// broken workload must never produce numbers.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file holds a different number of frames.</exception>
    internal static void Verify(string scenario, CaptureWorkload workload, string path, int expected)
    {
        // Default BLF cache: a sequential read after a full scan re-inflates containers that the cache does not hold.
        using IRandomAccessFrameSource source = Open(workload, path, DefaultBlfCacheBudget);
        int read = StartAndDrain(source);
        if (read != expected)
        {
            throw new InvalidOperationException(
                Invariant($"Scenario {scenario}: workload {workload} has {read} frames, expected {expected}."));
        }
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// CAN frames where every 16th frame is the IMIX Ethernet frame of the same index: four Ethernet lines per group of
    /// 64 index entries, so every group mixes short and long records.
    /// </summary>
    private static Frame[] _CreateMixedFrames(int count, Stack stack)
    {
        Frame[] can = SampleFrames.CreateCanFrames(count, stack);
        Frame[] ethernet = SampleFrames.CreateImixEthernet(count, stack);
        for (int i = 15; i < count; i += 16)
        {
            can[i] = ethernet[i];
        }

        return can;
    }

    #endregion
}
