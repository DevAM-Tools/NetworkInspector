// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that exports pre-generated IPv6/UDP frames as PCAPNG
/// into a <see cref="MemoryStream"/> (no disk I/O) on every <see cref="Run"/> call.
///
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Pcapng.PcapngExporter.OnFrame"/> serialisation.
/// Frame generation happens in <see cref="Setup"/>.
/// </para>
/// </summary>
internal sealed class ExportPcapngScenario : IProfilingScenario, IDisposable
{
    /// <summary>Number of frames exported per <see cref="Run"/> call.</summary>
    private const int BatchSize = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private MemoryStream? _Stream;

    /// <inheritdoc/>
    public string Name => "export-pcapng";

    /// <inheritdoc/>
    public string Description =>
        $"Export {BatchSize:N0} IPv6/UDP frames as PCAPNG → MemoryStream per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(BatchSize, _Stack);
        _Stream = new MemoryStream(4 * 1024 * 1024); // 4 MiB initial capacity
    }

    /// <inheritdoc/>
    public void Run()
    {
        MemoryStream ms = _Stream!;
        ms.SetLength(0);

        using Exporters.Pcapng.PcapngExporter exporter = Exporters.Pcapng.PcapngExporter
            .CreateBuilder()
            .ToStream(ms)
            .Build();

        foreach (Frame frame in _Frames!)
        {
            if (!exporter.OnFrame(frame))
            {
                break;
            }
        }

        exporter.OnFinish();
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Stream?.Dispose();
        _Stream = null;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
    }
}
