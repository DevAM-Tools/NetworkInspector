// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Base of the exporter-rework scenarios: every <see cref="Run"/> parses <see cref="FrameCount"/> export-mix frames into one
/// recycled packet and hands each packet (or each frame, for frame exporters) to a fresh exporter, then finishes it.
/// <para>
/// <b>Why parse inside Run:</b> the exporter rework reduces parse work (tree-free parses for selected fields). The older
/// <see cref="ExportScenarioBase{TItem}"/> scenarios export pre-parsed packets and can neither show that gain nor a regression
/// in the parse+export path. Scenarios that include the parse are the only fair reference for the later steps, so they exist
/// on today's code first and are frozen as the baseline before any behavior changes.
/// </para>
/// <para>
/// <b>Packet ids:</b> every iteration reuses the ids 0 … <see cref="FrameCount"/> − 1, so iterations after the first are
/// replays of the stack's id bookkeeping. A replay does the same parse work as a first parse for the exporter, and it keeps
/// the stack's bookkeeping bounded across thousands of iterations (fresh ids per iteration would grow it without limit).
/// </para>
/// <para>
/// <b>Output:</b> stream formats write into one reused <see cref="MemoryStream"/>; file formats (DuckDB, Parquet) write into a
/// per-scenario directory below the temp path, which <see cref="Dispose"/> deletes. The disk cost of the file formats is part
/// of the measurement, as it is for a user, and is the reason these scenarios are noisier than the stream formats.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Not thread-safe; the runner calls <see cref="Setup"/>, <see cref="Run"/>, and
/// <see cref="Cleanup"/> sequentially on one thread.</para>
/// <para><b>Parse options:</b> the loop uses the options the CLI export loop uses today (build mode, no cache, no index, no
/// observer), so the numbers are the cost a user of the previous version pays. The rework scenarios of later steps derive
/// their options from the exporter's parse plan instead.</para>
/// </remarks>
internal abstract class ExportPipelineScenarioBase : IProfilingScenario, IDisposable
{
    #region Fields

    /// <summary>
    /// Frames per iteration. Large enough for several batches of the batching exporters (50 000 packets each), so batch
    /// hand-over and writer-thread effects are part of every iteration and not hidden by a single final flush.
    /// </summary>
    protected const int FrameCount = 100_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private PacketFilter? _Filter;

    // The filter in its observer form, for scenarios that parse by plan: it runs inside the parse and is read per packet.
    private FilterObserver? _FilterObserver;
    private Packet? _Recycle;
    private IPacketListener? _LastExporter;
    private int _Delivered;

    #endregion

    #region Properties

    /// <summary>Reused output stream of stream formats; <see langword="null"/> for file formats.</summary>
    protected MemoryStream? Stream { get; private set; }

    /// <summary>Output directory of file formats; empty for stream formats.</summary>
    protected string OutputDirectory { get; private set; } = string.Empty;

    /// <summary>Whether the scenario parses frames (packet exporters) or hands frames on unparsed (frame exporters).</summary>
    protected abstract bool ParsesFrames { get; }

    /// <summary>Filter expression, or <see langword="null"/> for an unfiltered scenario.</summary>
    protected virtual string? FilterExpression => null;

    /// <summary>
    /// Whether the packet loop parses by plan: one <see cref="ParsePlan"/> from the demand of the filter (as an observer) and of the
    /// exporter, and the plan's options for every parse, as `ni export` does after the rework. The default keeps the loop of the
    /// previous version (build mode, no observer), so the scenarios frozen as the baseline keep measuring what they measured.
    /// </summary>
    protected virtual bool UsePlan => false;

    /// <summary>Whether the format writes files (DuckDB, Parquet) instead of a stream.</summary>
    protected virtual bool WritesFiles => false;

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => ParsesFrames
        ? "packets"
        : "frames";

    #endregion

    #region Exporter creation

    /// <summary>Creates the exporter of one iteration; packet exporters return an <see cref="IPacketListener"/>.</summary>
    /// <param name="stack">The stack that parses the frames.</param>
    protected abstract IPacketListener? CreatePacketExporter(Stack stack);

    /// <summary>Creates the exporter of one iteration; frame exporters return an <see cref="IFrameListener"/>.</summary>
    /// <param name="stack">The stack that created the frames.</param>
    protected virtual IFrameListener? CreateFrameExporter(Stack stack) => null;

    /// <summary>
    /// Number of packets the exporter wrote, or <see langword="null"/> when the scenario does not verify it. The filtered
    /// scenarios override this so that <see cref="Cleanup"/> can prove that the filter kept exactly 25 % of the packets.
    /// </summary>
    /// <param name="exporter">The exporter of the last iteration, already finished.</param>
    protected virtual int? GetWrittenCount(IPacketListener exporter) => null;

    #endregion

    #region Lifecycle

    /// <inheritdoc/>
    public void Setup()
    {
        // The full stack: the export mix contains Ethernet/IPv6/UDP and SocketCAN frames.
        _Stack = StackHelper.CreateFullStack();
        _Frames = SampleFrames.CreateExportMix(FrameCount, _Stack);

        if (FilterExpression is { } expression)
        {
            if (UsePlan)
            {
                // The tree-free filter of the rework: compiled as an observer, run inside the parse, read per packet.
                FilterObserverOptions observerOptions = new();
                _FilterObserver = PacketFilter.Compile(expression, _Stack, in observerOptions).Value;
            }
            else
            {
                // The tree filter as `ni export` uses it today: compiled once, evaluated on the built tree, without an index.
                // An index would be stale after the first iteration because replays never write one.
                _Filter = PacketFilter.Compile(expression, _Stack).Value;
            }
        }

        if (WritesFiles)
        {
            OutputDirectory = Path.Combine(Path.GetTempPath(), "ni-profiling", Name);
            Directory.CreateDirectory(OutputDirectory);
        }
        else
        {
            // Pre-sized so the stream does not grow inside the timed loop; the formats write a few MiB per iteration.
            Stream = new MemoryStream(64 * 1024 * 1024);
        }
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stream?.SetLength(0);
        _Delivered = 0;
        if (!ParsesFrames)
        {
            _RunFrames();
            return;
        }

        _RunPackets();
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        // A filtered scenario must keep exactly one packet in four: otherwise the number would measure another workload.
        // The check runs once, after the last iteration, so it costs nothing in the timed loop.
        if (FilterExpression is not null && _LastExporter is not null)
        {
            int expected = FrameCount / 4;
            int? written = GetWrittenCount(_LastExporter);
            if (_Delivered != expected || (written is not null && written != expected))
            {
                throw new InvalidOperationException(
                    Invariant($"{Name}: the filter kept {_Delivered} packets and the exporter wrote {written}, expected {expected}."));
            }
        }

        Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _LastExporter = null;
        _Recycle = null;
        _Frames = null;
        _Filter = null;
        _FilterObserver = null;
        Stream?.Dispose();
        Stream = null;
        _Stack?.Dispose();
        _Stack = null;

        if (OutputDirectory.Length > 0 && Directory.Exists(OutputDirectory))
        {
            Directory.Delete(OutputDirectory, recursive: true);
            OutputDirectory = string.Empty;
        }
    }

    #endregion

    #region Private helpers

    private void _RunPackets()
    {
        Stack stack = _Stack!; // Invariant: Setup ran.
        Frame[] frames = _Frames!; // Invariant: Setup ran.
        IPacketListener exporter = CreatePacketExporter(stack)
            ?? throw new InvalidOperationException(Invariant($"{Name}: a packet scenario must create a packet exporter."));

        // One recycled packet, a new packet only when the recycle is refused. Without UsePlan the options are those of `ni export`
        // before the rework (build mode, nothing else); with it they come from the parse plan of the consumers.
        ParseOptions options = _CreateOptions(exporter, stack);
        for (int i = 0; i < frames.Length; i++)
        {
            PacketId id = i;
            Packet packet;
            if (_Recycle is not null && Packet.TryParse(_Recycle, id, stack, frames[i], in options, out _))
            {
                packet = _Recycle;
            }
            else if (Packet.TryParse(id, stack, frames[i], in options, out Packet? created, out ParseFailure failure))
            {
                packet = created!; // Invariant: TryParse returned true.
            }
            else
            {
                throw new InvalidOperationException(Invariant($"{Name}: frame {i} was not parsed: {failure}"));
            }

            _Recycle = packet;
            if (_Filter is not null && (!_Filter.TryIsMatch(packet, out bool matched, out _) || !matched))
            {
                continue;
            }

            // The observer form of the filter has already looked at the packet during the parse; its verdict is read here.
            if (_FilterObserver is not null && (!_FilterObserver.TryReadMatch(out bool observed, out _) || !observed))
            {
                continue;
            }

            _Delivered++;
            if (!exporter.OnPacket(packet))
            {
                break;
            }
        }

        // OnFinish before Dispose: the writer of a batching exporter must have written everything when the run ends, so
        // the measured time includes the last flush as it does for a user.
        exporter.OnFinish();
        (exporter as IDisposable)?.Dispose();
        _LastExporter = exporter;
    }

    /// <summary>
    /// The parse options of one iteration: the default build-mode options, or (with <see cref="UsePlan"/>) the options of the
    /// plan made from the filter's and the exporter's demands. The filter comes first so that its verdict is complete when the
    /// exporter's packet is committed.
    /// </summary>
    private ParseOptions _CreateOptions(IPacketListener exporter, Stack stack)
    {
        if (!UsePlan)
        {
            return new();
        }

        if (exporter is not IParseDemandSource source)
        {
            throw new InvalidOperationException(Invariant($"{Name}: a plan scenario needs an exporter that states its parse demand."));
        }

        ParseDemand[] demands = _FilterObserver is null
            ? [source.ParseDemand]
            : [_FilterObserver.ParseDemand, source.ParseDemand];
        if (!ParsePlan.TryCreate(stack, demands, out ParsePlan? plan, out ParsePlanError error))
        {
            throw new InvalidOperationException(Invariant($"{Name}: the parse plan was refused: {error.Message}"));
        }

        return plan.Options;
    }

    /// <summary>Two selected fields (<c>udp.srcport</c>, <c>udp.dstport</c>) of the export mix: the content of the <c>-selected</c> scenarios.</summary>
    /// <param name="stack">The stack that resolves the names.</param>
    /// <returns>The selected-fields content.</returns>
    protected static ExportContent CreateSelectedContent(Stack stack)
    {
        FieldRequest[] requests = [new FieldRequest("udp.srcport"), new FieldRequest("udp.dstport")];
        if (!FieldSelection.TryCreate(stack, requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(Invariant($"The scenario selection was refused: {error.Message}"));
        }

        return new ExportContent { Shape = ExportShape.SelectedFields, Fields = selection };
    }

    private void _RunFrames()
    {
        Frame[] frames = _Frames!; // Invariant: Setup ran.
        IFrameListener exporter = CreateFrameExporter(_Stack!)
            ?? throw new InvalidOperationException(Invariant($"{Name}: a frame scenario must create a frame exporter."));

        for (int i = 0; i < frames.Length; i++)
        {
            _Delivered++;
            if (!exporter.OnFrame(frames[i]))
            {
                break;
            }
        }

        exporter.OnFinish();
        (exporter as IDisposable)?.Dispose();
    }

    #endregion
}
