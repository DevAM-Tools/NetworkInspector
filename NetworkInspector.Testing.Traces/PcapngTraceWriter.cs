// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// Writes the frames of a recipe to a PCAPNG file: numbers the frames from 0 and stamps them with the base time of
/// <see cref="TraceRecipe.BaseNanos"/> plus an offset. Disposing without <see cref="Finish"/> abandons the file (the kit
/// deletes it). Not thread-safe; one writer per recipe run.
/// </summary>
internal sealed class PcapngTraceWriter : IDisposable
{
    #region Fields

    private readonly FrameInterfaceRegistry _Registry = new();

    private readonly PcapngExporter _Exporter;

    private readonly LinkType _LinkType;

    private int _Written;

    #endregion

    #region Lifecycle

    /// <summary>Creates the file.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="linkType">The link type of every frame.</param>
    internal PcapngTraceWriter(string path, LinkType linkType)
    {
        _LinkType = linkType;
        _Exporter = PcapngExporter.CreateBuilder().ToFile(path).Build();
    }

    /// <summary>Releases the file handle.</summary>
    public void Dispose() => _Exporter.Dispose();

    #endregion

    #region Public API

    /// <summary>Number of frames written so far.</summary>
    internal int Written => _Written;

    /// <summary>Appends one frame.</summary>
    /// <param name="nanosAfterBase">Time of the frame in nanoseconds after <see cref="TraceRecipe.BaseNanos"/> (may be negative).</param>
    /// <param name="bytes">The frame bytes; the writer does not keep a reference after the call.</param>
    internal void Add(long nanosAfterBase, byte[] bytes)
    {
        Frame frame = Frame.Create(new FrameId(_Written), Timestamp.FromNanos(TraceRecipe.BaseNanos + nanosAfterBase), bytes, _LinkType,
            FrameInterfaceId.Invalid, _Registry).Value;
        _Exporter.OnFrame(frame);
        _Written++;
    }

    /// <summary>Completes the file (block lengths and the end of the section).</summary>
    internal void Finish() => _Exporter.OnFinish();

    #endregion
}
