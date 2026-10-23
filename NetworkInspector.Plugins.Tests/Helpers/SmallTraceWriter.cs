// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Generates small reproducible traces for the built-in plugin tests: Random frames written to BLF, PCAPNG, ASC, and legacy
/// PCAP files with the Core exporters themselves (not through the registrations under test, so a defect in a registration
/// cannot hide behind its own output). Test helper; thread-safe because it keeps no state.
/// </summary>
internal static class SmallTraceWriter
{
    #region Types

    /// <summary>The paths of one generated trace in four formats.</summary>
    /// <param name="Blf">BLF file.</param>
    /// <param name="Pcapng">PCAPNG file.</param>
    /// <param name="Asc">ASC file.</param>
    /// <param name="Pcap">Legacy PCAP file.</param>
    /// <param name="Frames">The generated frames, for comparisons.</param>
    internal sealed record Trace(string Blf, string Pcapng, string Asc, string Pcap, IReadOnlyList<Frame> Frames);

    #endregion

    #region Public API

    /// <summary>Reads every frame of a source that is not started yet; the source is started with a new registry.</summary>
    /// <param name="source">The source; it is disposed by the caller.</param>
    /// <param name="registry">The registry the frames belong to; a new one when <see langword="null"/>.</param>
    /// <returns>All frames in order.</returns>
    internal static List<Frame> ReadAll(IFrameSource source, FrameInterfaceRegistry? registry = null)
    {
        registry ??= new();
        source.Start(registry.RegisterSource(source), registry);
        List<Frame> frames = [];
        while (source.NextFrame() is Frame frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Generates Random frames.</summary>
    /// <param name="count">Number of frames.</param>
    /// <param name="seed">Generator seed.</param>
    /// <param name="registry">The registry the frames belong to (the stack's, when the frames are parsed later).</param>
    /// <param name="mode">Frame mode; UDP over IPv4 by default.</param>
    /// <returns>The frames.</returns>
    internal static List<Frame> Generate(int count, ulong seed, FrameInterfaceRegistry? registry = null,
        RandomFrameMode mode = RandomFrameMode.UdpIPv4)
    {
        using RandomFrameSource source = new(count, seed, mode);
        return ReadAll(source, registry);
    }

    /// <summary>Writes the same Random frames as BLF, PCAPNG, ASC, and legacy PCAP into <paramref name="folder"/>.</summary>
    /// <param name="folder">Per-test folder.</param>
    /// <param name="count">Number of frames.</param>
    /// <param name="seed">Generator seed.</param>
    /// <returns>The paths and the frames.</returns>
    internal static Trace Write(TempFolder folder, int count, ulong seed)
    {
        List<Frame> frames = Generate(count, seed);

        string blf = folder.FilePath("trace.blf");
        using (BlfExporter exporter = BlfExporter.CreateBuilder().ToFile(blf).Build())
        {
            _Feed(exporter, frames);
        }

        string pcapng = folder.FilePath("trace.pcapng");
        using (PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(pcapng).Build())
        {
            _Feed(exporter, frames);
        }

        string asc = folder.FilePath("trace.asc");
        using (AscExporter exporter = AscExporter.CreateBuilder().ToFile(asc).Build())
        {
            _Feed(exporter, frames);
        }

        string pcap = folder.FilePath("trace.pcap");
        LegacyPcapWriter.Write(pcap, frames);
        return new Trace(blf, pcapng, asc, pcap, frames);
    }

    #endregion

    #region Private Helpers

    /// <summary>Feeds all frames and finishes the exporter.</summary>
    private static void _Feed(IFrameListener listener, List<Frame> frames)
    {
        foreach (Frame frame in frames)
        {
            listener.OnFrame(frame);
        }

        listener.OnFinish();
    }

    #endregion
}
